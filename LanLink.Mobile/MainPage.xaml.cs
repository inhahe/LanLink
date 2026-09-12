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
        UpdateSendControls();
    }

    // ------------------------------------------------------------------ startup

    private bool _started;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;

        // The network outlives this page, so tie teardown to the window closing
        // rather than to the page disappearing.  See OnDisappearing below.
        if (Window is not null && !_disposeHooked)
        {
            _disposeHooked = true;
            Window.Destroying += (_, _) =>
            {
                BackgroundKeepAlive.Stop();
                _network.Dispose();
            };
        }

        // Give Android a moment to finish initialising the network stack.
        await Task.Delay(500);
        StartNetwork();

        // Ask for notification permission *after* the network is up, never
        // before.  RequestAsync does not return until the user answers the system
        // dialog, so awaiting it first stalled OnAppearing indefinitely and the
        // listener never bound at all — the app sat there with no sockets.  The
        // permission only governs whether the service's notification is
        // *visible*; the service runs either way, so nothing here is worth
        // delaying startup for.
        try
        {
            await BackgroundKeepAlive.RequestNotificationPermissionAsync();
        }
        catch { /* the indicator is cosmetic; never let it surface as a failure */ }
    }

    private bool _disposeHooked;

    private void StartNetwork()
    {
        Directory.CreateDirectory(_settings.DownloadFolder);

        try
        {
            _network.Start();

            // Only once the listener is actually up — a "sharing" notification
            // over a network that failed to start would be a lie.
            BackgroundKeepAlive.Start();

            AddLog($"LanLink started as \"{_settings.DisplayName}\" (port {_settings.Port})");
            AddLog($"Downloads: {_settings.DownloadFolder}");
            PeerEmptyLabel.Text = "Searching for peers on LAN…";
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

        // Deliberately does NOT dispose the network.
        //
        // OnDisappearing fires whenever this page stops being the visible one:
        // pushing a modal (the file browser), navigating to Settings, the app
        // going to the background, or the screen simply locking.  Disposing here
        // tore down the listener, discovery and every connection — and because
        // OnAppearing is guarded by _started, nothing ever brought it back.  The
        // app looked fine (the peer list still showed the last known state) but
        // had no sockets, so sends failed with "No active connection to
        // next-hop" and no peer could reach us.
        //
        // A LAN sharing app also *wants* to keep listening while backgrounded,
        // so the network's lifetime belongs to the window, not the page.
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
                Text     = sm.IsSent ? $"You → {sm.PeerName}: {sm.Text}"
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
                Text      = $"You → {pm.TargetName}: {pm.Text}",
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
                RefreshIfSelected(peer.NodeId);

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
                RefreshIfSelected(peer.NodeId);

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

                RefreshIfSelected(nodeId);
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

    /// <summary>
    /// Keep the send controls honest when the *selected* peer's connection state
    /// changes underneath us.  Without this, selecting an offline peer that later
    /// connects left the file buttons greyed out until you re-tapped the peer.
    /// </summary>
    private void RefreshIfSelected(string nodeId)
    {
        if (_selectedPeer is not null && _selectedPeer.NodeId == nodeId)
            UpdateSendControls();
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

        // Files and folders require an active connection.
        SendFilesBtn.IsEnabled   = isOnline;
        SendFolderBtn.IsEnabled  = isOnline;
        SendViaAppsBtn.IsEnabled = isOnline;

        // Tap-catchers: a disabled control eats the touch without a sound, so
        // these sit on top and explain why nothing is happening.
        NoPeerBlocker.IsVisible  = !hasPeer;
        OfflineBlocker.IsVisible = hasPeer && !isOnline;

        if (!hasPeer)
        {
            SendToLabel.Text      = "Select a peer above to send to";
            SendToLabel.TextColor = Color.FromArgb("#B45309");
        }
        else if (isOnline)
        {
            SendToLabel.Text      = $"→ Sending to {_selectedPeer!.Name}";
            SendToLabel.TextColor = Color.FromArgb("#15803D");
        }
        else
        {
            SendToLabel.Text      = $"→ Sending to {_selectedPeer!.Name}  "
                                  + "(offline — text will queue, files can't be sent)";
            SendToLabel.TextColor = Color.FromArgb("#B45309");
        }
    }

    // ------------------------------------------------------------------ blocked-tap feedback

    private async void NoPeerBlocker_Tapped(object? sender, TappedEventArgs e)
    {
        Toasts.Show("Pick who you're sending to — tap a peer in the list above.");
        await FlashPeersAsync();
    }

    private void OfflineBlocker_Tapped(object? sender, TappedEventArgs e)
    {
        string name = _selectedPeer?.Name ?? "That peer";
        Toasts.Show($"{name} is offline. Files need a live connection — "
                  + "text messages will queue until it's back.");
    }

    /// <summary>Pulses the peer list so the eye is dragged to where the fix is.</summary>
    private async Task FlashPeersAsync()
    {
        try
        {
            await PeersFrame.FadeTo(0.35, 110);
            await PeersFrame.FadeTo(1.0, 110);
        }
        catch { /* animation cancelled by navigation */ }
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
                AddLog($"You → {_selectedPeer.Name}: {text}", LogLevel.TextSent,
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

            var entry = AddLog($"You → {_selectedPeer.Name}: {text}", LogLevel.TextSent,
                               DeliveryStatus.Pending);
            entry.PendingId = pm.Id;
            _pendingEntries[pm.Id] = entry;
        }
    }

    // ------------------------------------------------------------------ send files / folders

    private async void SendFiles_Clicked(object? sender, EventArgs e)
    {
        if (!HasOnlinePeer()) return;
        if (!await EnsureBrowseAccessAsync()) return;

        var paths = await FileBrowserPage.PickAsync(this, BrowserMode.Files);
        if (paths is null || paths.Count == 0) return;

        await SendPathsAsync(paths);
    }

    private async void SendFolder_Clicked(object? sender, EventArgs e)
    {
        if (!HasOnlinePeer()) return;
        if (!await EnsureBrowseAccessAsync()) return;

        var paths = await FileBrowserPage.PickAsync(this, BrowserMode.Folder);
        if (paths is null || paths.Count == 0) return;

        await SendPathsAsync(paths);
    }

    /// <summary>
    /// The escape hatch: Android's own picker, for content that isn't on the
    /// filesystem at all (Drive, other apps' private storage).  Files only —
    /// SAF hands back opaque content:// URIs, and a folder picked that way would
    /// have to be copied into the cache wholesale before it could be sent.
    /// </summary>
    private async void SendViaApps_Clicked(object? sender, EventArgs e)
    {
        if (!HasOnlinePeer()) return;
        await PickViaSystemPickerAsync();
    }

    private bool HasOnlinePeer()
    {
        if (_selectedPeer is null)
        {
            Toasts.Show("Pick who you're sending to — tap a peer in the list above.");
            return false;
        }

        if (!_selectedPeer.IsConnected)
        {
            Toasts.Show($"{_selectedPeer.Name} is offline — files need a live connection.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Confirms we can enumerate storage, and offers the system picker as a way
    /// out when the user would rather not grant all-files access.
    /// </summary>
    private async Task<bool> EnsureBrowseAccessAsync()
    {
        if (await StoragePermission.HasAccessAsync()) return true;

        // Android 10 and below: an ordinary runtime prompt is enough.
        if (await StoragePermission.TryRequestLegacyAsync()) return true;

        const string openSettings = "Open Settings";
        const string useSystem    = "Use the system picker instead";

        string choice = await DisplayActionSheet(
            "To browse this phone's files and folders, Android needs you to turn on "
            + "\"All files access\" for LanLink.",
            "Cancel", null, openSettings, useSystem);

        if (choice == openSettings)
        {
            StoragePermission.OpenAllFilesAccessSettings();
            AddLog("Turn on \"Allow access to manage all files\", then come back and "
                 + "tap Send Files again.");
        }
        else if (choice == useSystem)
        {
            await PickViaSystemPickerAsync();
        }

        return false;
    }

    private async Task SendPathsAsync(IEnumerable<string> paths)
    {
        var peer = _selectedPeer;
        if (peer is null) return;

        foreach (string path in paths)
        {
            bool   isDir = Directory.Exists(path);
            string label = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

            // Immediate feedback so the user always sees *something* happen after
            // picking — even before the transfer's own progress lines.
            AddLog($"Sending {(isDir ? "folder " : "")}{label} to {peer.Name}…",
                   LogLevel.Transfer);
            try
            {
                if (isDir)
                    await _transfer.SendDirectoryAsync(peer.NodeId, path);
                else
                    await _transfer.SendFileAsync(peer.NodeId, path);
            }
            catch (Exception ex)
            {
                AddLog($"Failed to send {label}: {ex.Message}", LogLevel.Error);
            }
        }
    }

    private async Task PickViaSystemPickerAsync()
    {
        var peer = _selectedPeer;
        if (peer is null) return;

        try
        {
            var results = await FilePicker.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Select files to send"
            });

            // PickMultipleAsync returns null (not an empty list) when cancelled.
            if (results is null) return;

            foreach (var file in results)
            {
                AddLog($"Sending {file.FileName} to {peer.Name}…", LogLevel.Transfer);
                try
                {
                    // On Android the picked file's FullPath is often a content
                    // URI that isn't a real filesystem path, so copy the stream
                    // to a local cache file first and send that.
                    string path = await EnsureLocalPathAsync(file);
                    await _transfer.SendFileAsync(peer.NodeId, path);
                }
                catch (Exception ex)
                {
                    AddLog($"Failed to send {file.FileName}: {ex.Message}", LogLevel.Error);
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is not TaskCanceledException)
                AddLog($"Picker error: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Return a real, readable filesystem path for a picked file.  If the
    /// picker's FullPath already points at a readable file we use it directly;
    /// otherwise (typical for Android content:// URIs) we copy the file's
    /// stream into the app cache and return that path.
    /// </summary>
    private static async Task<string> EnsureLocalPathAsync(FileResult file)
    {
        try
        {
            if (!string.IsNullOrEmpty(file.FullPath) && File.Exists(file.FullPath))
                return file.FullPath;
        }
        catch { /* fall through to copy */ }

        string dest = Path.Combine(FileSystem.CacheDirectory, file.FileName);
        using (var src = await file.OpenReadAsync().ConfigureAwait(false))
        using (var dst = File.Create(dest))
        {
            await src.CopyToAsync(dst).ConfigureAwait(false);
        }
        return dest;
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

        AddLog($"Connecting to {host}:{port}…");
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
