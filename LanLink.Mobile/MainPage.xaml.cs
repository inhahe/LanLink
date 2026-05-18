using System.Collections.ObjectModel;

namespace LanLink;

public partial class MainPage : ContentPage
{
    private readonly AppSettings     _settings;
    private readonly NetworkManager  _network;
    private readonly TransferManager _transfer;
    private readonly MessageStore    _store;

    private readonly ObservableCollection<Peer>     _peers = new();
    private readonly ObservableCollection<LogEntry> _log   = new();
    private readonly Dictionary<string, LogEntry>   _progressEntries = new();
    private readonly Dictionary<string, LogEntry>   _pendingEntries  = new();

    private Peer? _selectedPeer;

    public MainPage()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _store    = MessageStore.Load();
        _network  = new NetworkManager(_settings);
        _transfer = new TransferManager(_network, _settings);

        PeerListView.ItemsSource    = _peers;
        ActivityLogView.ItemsSource = _log;

        // Register value converters programmatically (keeps XAML simple).
        Resources.Add("BoolToColorConverter", new BoolToColorConverter());
        Resources.Add("LevelToColorConverter", new LevelToColorConverter());
        Resources.Add("InvertBoolConverter", new InvertBoolConverter());
        Resources.Add("ConnectedToTextColorConverter", new ConnectedToTextColorConverter());

        WireEvents();
        LoadPersistedState();
    }

    // ------------------------------------------------------------------ startup

    private bool _started;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;

        // Give Android a moment to finish initialising the network stack.
        await Task.Delay(500);
        StartNetwork();
    }

    private void StartNetwork()
    {
        Directory.CreateDirectory(_settings.DownloadFolder);

        try
        {
            _network.Start();
            AddLog($"LanLink started as \"{_settings.DisplayName}\" (port {_settings.Port})");
            AddLog($"Downloads: {_settings.DownloadFolder}");
            PeerEmptyLabel.Text = "Searching for peers on LAN\u2026";
        }
        catch (Exception ex)
        {
            AddLog($"Failed to start: {ex.Message}", LogLevel.Error);
            PeerEmptyLabel.Text = $"Network failed: {ex.Message}";
            PeerEmptyLabel.TextColor = Color.FromArgb("#DC2626");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _network.Dispose();
    }

    // ------------------------------------------------------------------ persisted state

    private void LoadPersistedState()
    {
        // Restore known peers (all start disconnected).
        foreach (var sp in _store.KnownPeers)
        {
            var peer = new Peer
            {
                NodeId      = sp.NodeId,
                Name        = sp.Name,
                Endpoint    = sp.Endpoint,
                IsConnected = false,
                RouteType   = RouteType.Lan
            };
            _peers.Add(peer);
        }

        // Restore message history.
        foreach (var sm in _store.Messages)
        {
            var level = sm.IsSent ? LogLevel.TextSent : LogLevel.TextReceived;
            var entry = new LogEntry
            {
                Time     = sm.Time,
                Level    = level,
                Text     = sm.IsSent ? $"You \u2192 {sm.PeerName}: {sm.Text}"
                                     : $"{sm.PeerName}: {sm.Text}",
                Delivery = sm.IsSent ? DeliveryStatus.Delivered : DeliveryStatus.None
            };
            _log.Add(entry);
        }

        // Restore pending messages (show as pending in log).
        foreach (var pm in _store.PendingMessages)
        {
            var entry = new LogEntry
            {
                Time      = pm.Time,
                Level     = LogLevel.TextSent,
                Text      = $"You \u2192 {pm.TargetName}: {pm.Text}",
                Delivery  = DeliveryStatus.Pending,
                PendingId = pm.Id
            };
            _log.Add(entry);
            _pendingEntries[pm.Id] = entry;
        }
    }

    private void PersistPeer(Peer peer)
    {
        _store.UpsertPeer(new SavedPeer
        {
            NodeId   = peer.NodeId,
            Name     = peer.Name,
            Endpoint = peer.Endpoint
        });
        _store.Save();
    }

    // ------------------------------------------------------------------ events

    private void WireEvents()
    {
        _network.PeerAdded += peer =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var existing = _peers.FirstOrDefault(p => p.NodeId == peer.NodeId);
                if (existing is not null)
                {
                    // Peer reconnected — update existing entry.
                    existing.Name        = peer.Name;
                    existing.IsConnected = peer.IsConnected;
                    existing.RouteType   = peer.RouteType;
                    existing.Endpoint    = peer.Endpoint;
                    existing.Hops        = peer.Hops;
                    existing.LastSeen    = peer.LastSeen;
                }
                else
                {
                    _peers.Add(peer);
                }

                PersistPeer(peer);

                // Flush pending messages if this peer just connected.
                if (peer.IsConnected)
                    _ = FlushPendingAsync(peer.NodeId);
            });

        _network.PeerUpdated += peer =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Sync our UI peer object if NetworkManager uses a different instance.
                var existing = _peers.FirstOrDefault(p => p.NodeId == peer.NodeId);
                if (existing is not null && !ReferenceEquals(existing, peer))
                {
                    existing.Name        = peer.Name;
                    existing.IsConnected = peer.IsConnected;
                    existing.RouteType   = peer.RouteType;
                    existing.Endpoint    = peer.Endpoint;
                    existing.Hops        = peer.Hops;
                    existing.LastSeen    = peer.LastSeen;
                }

                PersistPeer(peer);

                // Flush pending messages on reconnect.
                if (peer.IsConnected)
                    _ = FlushPendingAsync(peer.NodeId);
            });

        _network.PeerRemoved += nodeId =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Don't remove from the list — just mark disconnected.
                var p = _peers.FirstOrDefault(x => x.NodeId == nodeId);
                if (p is not null)
                    p.IsConnected = false;
            });

        _network.Log  += msg => AddLog(msg);
        _transfer.Log += msg => AddLog(msg, LogLevel.Transfer);

        _transfer.ProgressUpdate += OnProgressUpdate;

        _transfer.TextReceived += (fromId, text) =>
        {
            string name = _network.Peers.TryGetValue(fromId, out var p) ? p.Name : fromId;
            AddLog($"{name}: {text}", LogLevel.TextReceived);

            // Persist received message.
            _store.AddMessage(new SavedMessage
            {
                Time       = DateTime.Now,
                PeerNodeId = fromId,
                PeerName   = name,
                Text       = text,
                IsSent     = false
            });
            _store.Save();
        };
    }

    // ------------------------------------------------------------------ pending message flush

    private async Task FlushPendingAsync(string nodeId)
    {
        var pending = _store.GetPendingFor(nodeId);
        if (pending.Count == 0) return;

        foreach (var pm in pending)
        {
            try
            {
                await _transfer.SendTextAsync(pm.TargetNodeId, pm.Text);

                // Update log entry to show delivered.
                if (_pendingEntries.TryGetValue(pm.Id, out var entry))
                {
                    entry.Delivery = DeliveryStatus.Delivered;
                    _pendingEntries.Remove(pm.Id);
                }

                // Move from pending to delivered in store.
                _store.RemovePending(pm.Id);
                _store.AddMessage(new SavedMessage
                {
                    Time       = pm.Time,
                    PeerNodeId = pm.TargetNodeId,
                    PeerName   = pm.TargetName,
                    Text       = pm.Text,
                    IsSent     = true
                });
            }
            catch (Exception ex)
            {
                AddLog($"Failed to deliver pending message to {pm.TargetName}: {ex.Message}",
                       LogLevel.Error);
            }
        }

        _store.Save();
    }

    // ------------------------------------------------------------------ progress

    private void OnProgressUpdate(string transferId, string text, bool isDone)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_progressEntries.TryGetValue(transferId, out var entry))
            {
                entry.Text = text;
            }
            else
            {
                entry = new LogEntry { Text = text, Level = LogLevel.Transfer };
                _progressEntries[transferId] = entry;
                _log.Add(entry);
                TrimLog();
            }

            if (isDone)
                _progressEntries.Remove(transferId);
        });
    }

    // ------------------------------------------------------------------ log

    private LogEntry AddLog(string text, LogLevel level = LogLevel.Info,
                            DeliveryStatus delivery = DeliveryStatus.None)
    {
        var entry = new LogEntry { Text = text, Level = level, Delivery = delivery };

        if (MainThread.IsMainThread)
        {
            _log.Add(entry);
            TrimLog();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _log.Add(entry);
                TrimLog();
            });
        }

        return entry;
    }

    private void TrimLog()
    {
        while (_log.Count > 500)
            _log.RemoveAt(0);
    }

    // ------------------------------------------------------------------ peer selection

    private void PeerList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedPeer = e.CurrentSelection.FirstOrDefault() as Peer;
        UpdateSendControls();
    }

    private void UpdateSendControls()
    {
        bool hasPeer  = _selectedPeer is not null;
        bool isOnline = hasPeer && _selectedPeer!.IsConnected;

        // Text can always be sent (queued if offline).
        MessageEntry.IsEnabled = hasPeer;
        SendTextBtn.IsEnabled  = hasPeer;

        // Files require an active connection.
        SendFilesBtn.IsEnabled = isOnline;

        if (!hasPeer)
            SendToLabel.Text = "Select a peer";
        else if (isOnline)
            SendToLabel.Text = $"Send to {_selectedPeer!.Name}:";
        else
            SendToLabel.Text = $"Send to {_selectedPeer!.Name} (offline \u2014 messages will queue):";
    }

    // ------------------------------------------------------------------ remove peer (swipe)

    private void RemovePeer_Invoked(object? sender, EventArgs e)
    {
        Peer? peer = null;
        if (sender is SwipeItem swipeItem)
            peer = swipeItem.CommandParameter as Peer;

        if (peer is null || peer.IsConnected) return;

        string nodeId = peer.NodeId;

        // Collect pending-message IDs for this peer before removing from store.
        var pendingIds = _store.GetPendingFor(nodeId).Select(pm => pm.Id).ToHashSet();

        // Remove pending log entries from the activity log.
        foreach (string id in pendingIds)
        {
            if (_pendingEntries.Remove(id, out var entry))
                _log.Remove(entry);
        }

        // Remove from UI list.
        _peers.Remove(peer);
        if (_selectedPeer == peer)
        {
            _selectedPeer = null;
            UpdateSendControls();
        }

        // Remove from persisted store.
        _store.KnownPeers.RemoveAll(p => p.NodeId == nodeId);
        _store.PendingMessages.RemoveAll(p => p.TargetNodeId == nodeId);
        _store.Save();
    }

    // ------------------------------------------------------------------ copy message (long-press context menu)

    private async void CopyMessage_Clicked(object? sender, EventArgs e)
    {
        LogEntry? entry = null;
        if (sender is MenuFlyoutItem item)
            entry = item.CommandParameter as LogEntry;

        if (entry is null) return;

        await Clipboard.SetTextAsync(entry.Text);
    }

    // ------------------------------------------------------------------ send text

    private async void SendText_Clicked(object? sender, EventArgs e)
    {
        await DoSendTextAsync();
    }

    private async void MessageEntry_Completed(object? sender, EventArgs e)
    {
        await DoSendTextAsync();
    }

    private async Task DoSendTextAsync()
    {
        if (_selectedPeer is null) return;
        string text = MessageEntry.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;

        MessageEntry.Text = "";

        if (_selectedPeer.IsConnected)
        {
            // Peer is online — send immediately.
            try
            {
                await _transfer.SendTextAsync(_selectedPeer.NodeId, text);
                AddLog($"You \u2192 {_selectedPeer.Name}: {text}", LogLevel.TextSent,
                       DeliveryStatus.Delivered);

                _store.AddMessage(new SavedMessage
                {
                    Time       = DateTime.Now,
                    PeerNodeId = _selectedPeer.NodeId,
                    PeerName   = _selectedPeer.Name,
                    Text       = text,
                    IsSent     = true
                });
                _store.Save();
            }
            catch (Exception ex)
            {
                AddLog($"Send failed: {ex.Message}", LogLevel.Error);
            }
        }
        else
        {
            // Peer is offline — queue for delivery when they reconnect.
            var pm = new PendingMessage
            {
                Id           = Guid.NewGuid().ToString("N")[..8],
                Time         = DateTime.Now,
                TargetNodeId = _selectedPeer.NodeId,
                TargetName   = _selectedPeer.Name,
                Text         = text
            };
            _store.AddPending(pm);
            _store.Save();

            var entry = AddLog($"You \u2192 {_selectedPeer.Name}: {text}", LogLevel.TextSent,
                               DeliveryStatus.Pending);
            entry.PendingId = pm.Id;
            _pendingEntries[pm.Id] = entry;
        }
    }

    // ------------------------------------------------------------------ send files

    private async void SendFiles_Clicked(object? sender, EventArgs e)
    {
        if (_selectedPeer is null) return;

        try
        {
            var results = await FilePicker.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Select files to send"
            });

            foreach (var file in results)
            {
                try
                {
                    await _transfer.SendFileAsync(_selectedPeer.NodeId, file.FullPath);
                }
                catch (Exception ex)
                {
                    AddLog($"Failed: {ex.Message}", LogLevel.Error);
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is not TaskCanceledException)
                AddLog($"Picker error: {ex.Message}", LogLevel.Error);
        }
    }

    // ------------------------------------------------------------------ connect remote

    private async void ConnectRemote_Clicked(object? sender, EventArgs e)
    {
        await DoConnectRemoteAsync();
    }

    private async void RemoteEntry_Completed(object? sender, EventArgs e)
    {
        await DoConnectRemoteAsync();
    }

    private async Task DoConnectRemoteAsync()
    {
        string addr = RemoteEntry.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(addr)) return;

        string host;
        int port = _settings.Port;

        int lastColon = addr.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(addr[(lastColon + 1)..], out int p))
        {
            host = addr[..lastColon];
            port = p;
        }
        else
        {
            host = addr;
        }

        AddLog($"Connecting to {host}:{port}\u2026");
        bool ok = await _network.ConnectToAsync(host, port);
        if (ok)
            RemoteEntry.Text = "";
    }

    // ------------------------------------------------------------------ settings

    private async void Settings_Clicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(SettingsPage));
    }
}

// ===========================================================================
//  Value converters
// ===========================================================================

public class BoolToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
        => (value is true) ? Colors.LimeGreen : Colors.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

public class LevelToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
    {
        return value switch
        {
            LogLevel.TextReceived => Color.FromArgb("#2563EB"),
            LogLevel.TextSent     => Color.FromArgb("#16A34A"),
            LogLevel.Transfer     => Color.FromArgb("#7C3AED"),
            LogLevel.Error        => Color.FromArgb("#DC2626"),
            _                     => Color.FromArgb("#6B7280")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

public class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
        => value is not true;
}

public class ConnectedToTextColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
        => (value is true) ? Color.FromArgb("#111827") : Color.FromArgb("#9CA3AF");

    public object ConvertBack(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

