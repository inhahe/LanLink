using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace LanLink;

public partial class MainWindow : Window
{
    private readonly AppSettings     _settings;
    private readonly NetworkManager  _network;
    private readonly TransferManager _transfer;
    private readonly MessageStore    _store;

    private readonly ObservableCollection<Peer>     _peers = new();
    private readonly ObservableCollection<LogEntry> _log   = new();

    private Peer? _selectedPeer;

    // Active transfer progress entries keyed by transfer-id.
    // Updated in-place so the activity log shows live percentages.
    private readonly Dictionary<string, LogEntry> _progressEntries = new();

    // Pending message log entries keyed by PendingMessage.Id for delivery-status updates.
    private readonly Dictionary<string, LogEntry> _pendingEntries = new();

    // System tray
    private Forms.NotifyIcon? _trayIcon;
    private bool _reallyClosing;  // true only when user picks Exit (tray or IPC)
    private bool _cleanedUp;      // guards double dispose of network/tray

    // Prevent double-init (Initialize() can be called from App or from Loaded)
    private bool _initialized;

    // ------------------------------------------------------------------ ctor

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _store    = MessageStore.Load();
        _network  = new NetworkManager(_settings);
        _transfer = new TransferManager(_network, _settings);

        PeerList.ItemsSource    = _peers;
        ActivityLog.ItemsSource = _log;

        WireEvents();
        CreateTrayIcon();
        LoadPersistedState();

        Loaded += OnLoaded;
    }

    // ------------------------------------------------------------------ public init (supports --minimized)

    /// <summary>
    /// Start networking and background services.  Called from App.OnStartup
    /// when launched with --minimized (window never shown), or from the Loaded
    /// event in the normal case.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Directory.CreateDirectory(_settings.DownloadFolder);
        EnsureFirewallRules();

        try
        {
            _network.Start();
            AddLog($"LanLink started as \"{_settings.DisplayName}\"  " +
                   $"(port {_settings.Port})");
            AddLog($"Downloads go to  {_settings.DownloadFolder}");
        }
        catch (Exception ex)
        {
            AddLog($"Failed to start: {ex.Message}", LogLevel.Error);
            AddLog("The port may already be in use. Try a different port in Settings.",
                   LogLevel.Error);
        }
    }

    // ------------------------------------------------------------------ startup / shutdown

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Initialize();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing)
        {
            // Minimize to tray instead of closing.
            e.Cancel = true;
            Hide();
            return;
        }

        // Actually exiting — clean up.
        CleanupAndDispose();
        base.OnClosing(e);
    }

    /// <summary>
    /// Force a real exit.  Used by the single-instance IPC "exit" command
    /// (<c>LanLink.exe --exit</c>) so an installer can shut LanLink down before
    /// replacing the executable.  Works whether or not the window was ever shown.
    /// </summary>
    public void RequestExit()
    {
        _reallyClosing = true;

        if (IsVisible)
        {
            // Closing a visible window runs OnClosing → CleanupAndDispose.
            Close();
        }
        else
        {
            // A window started with --minimized was never shown, so closing it
            // won't raise Closing — clean up directly.
            CleanupAndDispose();
        }

        Application.Current.Shutdown();
    }

    private void CleanupAndDispose()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        try { _trayIcon?.Dispose(); } catch { }
        _trayIcon = null;
        try { _network.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------ system tray

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show LanLink", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _trayIcon = new Forms.NotifyIcon
        {
            Text             = "LanLink",
            Icon             = GetAppIcon(),
            ContextMenuStrip = menu,
            Visible          = true
        };

        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp() => RequestExit();

    private static System.Drawing.Icon GetAppIcon()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (exePath is not null)
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null) return icon;
            }
        }
        catch { }

        return SystemIcons.Application;
    }

    // ------------------------------------------------------------------ firewall

    /// <summary>
    /// Check whether our firewall rules exist.  If not, create them via an
    /// elevated cmd script (the user will see one UAC prompt on first run).
    /// </summary>
    private void EnsureFirewallRules()
    {
        int port = _settings.Port;

        if (FirewallRuleExists("LanLink TCP"))
            return;   // already set up

        AddLog("First run \u2014 adding Windows Firewall rules\u2026");

        string script = Path.Combine(Path.GetTempPath(), $"lanlink_fw_{port}.cmd");
        try
        {
            File.WriteAllText(script,
                "@echo off\r\n" +
                $"netsh advfirewall firewall add rule name=\"LanLink TCP\" dir=in action=allow protocol=TCP localport={port} profile=any >nul 2>&1\r\n" +
                $"netsh advfirewall firewall add rule name=\"LanLink UDP\" dir=in action=allow protocol=UDP localport={port} profile=any >nul 2>&1\r\n");

            var psi = new ProcessStartInfo(script)
            {
                Verb            = "runas",       // triggers UAC
                UseShellExecute = true,
                WindowStyle     = ProcessWindowStyle.Hidden
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(15_000);

            AddLog("Firewall rules added.");
        }
        catch (Win32Exception)
        {
            // User cancelled the UAC prompt.
            AddLog("UAC prompt was cancelled \u2014 firewall rules were NOT added.", LogLevel.Error);
            AddLog("LAN discovery will not work until you allow port " +
                   $"{port} through Windows Firewall.", LogLevel.Error);
        }
        catch (Exception ex)
        {
            AddLog($"Could not add firewall rules: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            try { File.Delete(script); } catch { }
        }
    }

    private static bool FirewallRuleExists(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh",
                $"advfirewall firewall show rule name=\"{name}\"")
            {
                CreateNoWindow        = true,
                UseShellExecute       = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            var proc = Process.Start(psi);
            string output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit(5_000);
            // "No rules match" is printed when the rule doesn't exist.
            return !output.Contains("No rules match", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("No rules", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;  // can't check \u2014 don't pester the user
        }
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
                Text     = sm.IsSent ? $"You \u2192 {sm.PeerName}:  {sm.Text}"
                                     : $"{sm.PeerName}:  {sm.Text}",
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
                Text      = $"You \u2192 {pm.TargetName}:  {pm.Text}",
                Delivery  = DeliveryStatus.Pending,
                PendingId = pm.Id
            };
            _log.Add(entry);
            _pendingEntries[pm.Id] = entry;
        }

        if (_log.Count > 0)
            ActivityLog.ScrollIntoView(_log[^1]);
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

    // ------------------------------------------------------------------ event wiring

    private void WireEvents()
    {
        _network.PeerAdded += peer =>
            Dispatcher.BeginInvoke(() =>
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
            Dispatcher.BeginInvoke(() =>
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
            Dispatcher.BeginInvoke(() =>
            {
                // Don't remove from the list — just mark disconnected.
                var p = _peers.FirstOrDefault(x => x.NodeId == nodeId);
                if (p is not null)
                    p.IsConnected = false;
            });

        _network.Log   += msg => AddLog(msg, LogLevel.Info);
        _transfer.Log  += msg => AddLog(msg, LogLevel.Transfer);

        _transfer.ProgressUpdate += OnProgressUpdate;

        _transfer.TextReceived += (fromId, text) =>
        {
            string name = _network.Peers.TryGetValue(fromId, out var p) ? p.Name : fromId;
            AddLog($"{name}:  {text}", LogLevel.TextReceived);

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

        _transfer.FileReceived += (fromId, path) =>
        {
            // already logged via ProgressUpdate — nothing extra needed
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

    // ------------------------------------------------------------------ activity log

    private LogEntry AddLog(string text, LogLevel level = LogLevel.Info,
                            DeliveryStatus delivery = DeliveryStatus.None)
    {
        if (!Dispatcher.CheckAccess())
        {
            LogEntry? result = null;
            Dispatcher.Invoke(() => result = AddLog(text, level, delivery));
            return result!;
        }

        var entry = new LogEntry { Text = text, Level = level, Delivery = delivery };
        _log.Add(entry);

        // keep the log from growing without bound
        while (_log.Count > 2000)
            _log.RemoveAt(0);

        ActivityLog.ScrollIntoView(_log[^1]);
        return entry;
    }

    // ------------------------------------------------------------------ transfer progress

    private void OnProgressUpdate(string transferId, string text, bool isDone)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnProgressUpdate(transferId, text, isDone));
            return;
        }

        if (_progressEntries.TryGetValue(transferId, out var entry))
        {
            // Update existing entry in-place.
            entry.Text = text;
        }
        else
        {
            // Create a new log entry and track it.
            entry = new LogEntry { Text = text, Level = LogLevel.Transfer };
            _progressEntries[transferId] = entry;
            _log.Add(entry);

            while (_log.Count > 2000)
                _log.RemoveAt(0);
        }

        if (isDone)
            _progressEntries.Remove(transferId);

        ActivityLog.ScrollIntoView(_log[^1]);
    }

    // ------------------------------------------------------------------ peer selection / removal

    private void PeerList_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedPeer = PeerList.SelectedItem as Peer;
        UpdateSendControls();

        // Only allow removing disconnected peers.
        RemovePeerMenuItem.IsEnabled = _selectedPeer is not null && !_selectedPeer.IsConnected;
    }

    private void RemovePeer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer is null || _selectedPeer.IsConnected) return;

        string nodeId = _selectedPeer.NodeId;

        // Collect pending-message IDs for this peer before removing from store.
        var pendingIds = _store.GetPendingFor(nodeId).Select(pm => pm.Id).ToHashSet();

        // Remove pending log entries from the activity log.
        foreach (string id in pendingIds)
        {
            if (_pendingEntries.Remove(id, out var entry))
                _log.Remove(entry);
        }

        // Remove from UI list.
        _peers.Remove(_selectedPeer);
        _selectedPeer = null;
        UpdateSendControls();

        // Remove from persisted store.
        _store.KnownPeers.RemoveAll(p => p.NodeId == nodeId);
        _store.PendingMessages.RemoveAll(p => p.TargetNodeId == nodeId);
        _store.Save();
    }

    private void UpdateSendControls()
    {
        bool hasPeer    = _selectedPeer is not null;
        bool isOnline   = hasPeer && _selectedPeer!.IsConnected;

        // Text can always be sent (queued if offline).
        MessageBox.IsEnabled   = hasPeer;
        SendTextBtn.IsEnabled  = hasPeer;

        // Files/folders require an active connection.
        SendFileBtn.IsEnabled   = isOnline;
        SendFolderBtn.IsEnabled = isOnline;

        if (!hasPeer)
            SelectedPeerLabel.Text = "Select a peer to send";
        else if (isOnline)
            SelectedPeerLabel.Text = $"Send to {_selectedPeer!.Name}:";
        else
            SelectedPeerLabel.Text = $"Send to {_selectedPeer!.Name} (offline \u2014 messages will queue):";
    }

    // ------------------------------------------------------------------ send text

    private async void SendText_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer is null) return;
        string text = MessageBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        MessageBox.Clear();

        if (_selectedPeer.IsConnected)
        {
            // Peer is online — send immediately.
            try
            {
                await _transfer.SendTextAsync(_selectedPeer.NodeId, text);
                AddLog($"You \u2192 {_selectedPeer.Name}:  {text}", LogLevel.TextSent,
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

            var entry = AddLog($"You \u2192 {_selectedPeer.Name}:  {text}", LogLevel.TextSent,
                               DeliveryStatus.Pending);
            entry.PendingId = pm.Id;
            _pendingEntries[pm.Id] = entry;
        }
    }

    private void MessageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendText_Click(sender, e);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ copy message

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (ActivityLog.SelectedItem is LogEntry entry)
            Clipboard.SetText(entry.Text);
    }

    // ------------------------------------------------------------------ send file

    private async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer is null) return;

        var dlg = new OpenFileDialog
        {
            Title       = "Select file(s) to send",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        foreach (string file in dlg.FileNames)
        {
            try
            {
                await _transfer.SendFileAsync(_selectedPeer.NodeId, file);
            }
            catch (Exception ex)
            {
                AddLog($"Failed to send {Path.GetFileName(file)}: {ex.Message}", LogLevel.Error);
            }
        }
    }

    // ------------------------------------------------------------------ send folder

    private async void SendFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer is null) return;

        var dlg = new OpenFolderDialog { Title = "Select folder to send" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await _transfer.SendDirectoryAsync(_selectedPeer.NodeId, dlg.FolderName);
        }
        catch (Exception ex)
        {
            AddLog($"Failed to send folder: {ex.Message}", LogLevel.Error);
        }
    }

    // ------------------------------------------------------------------ drag & drop

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = (_selectedPeer is not null && e.Data.GetDataPresent(DataFormats.FileDrop))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_selectedPeer is null)
        {
            AddLog("Select a peer first before dropping files.", LogLevel.Error);
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        foreach (string path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                    await _transfer.SendDirectoryAsync(_selectedPeer.NodeId, path);
                else if (File.Exists(path))
                    await _transfer.SendFileAsync(_selectedPeer.NodeId, path);
            }
            catch (Exception ex)
            {
                AddLog($"Failed: {ex.Message}", LogLevel.Error);
            }
        }
    }

    // ------------------------------------------------------------------ connect remote

    private async void ConnectRemote_Click(object sender, RoutedEventArgs e)
    {
        await DoConnectRemoteAsync();
    }

    private void RemoteBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = DoConnectRemoteAsync();
            e.Handled = true;
        }
    }

    private async Task DoConnectRemoteAsync()
    {
        string addr = RemoteAddressBox.Text.Trim();
        if (string.IsNullOrEmpty(addr)) return;

        string host;
        int port = _settings.Port;

        // Parse  host:port  or  host  (default port).
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
            RemoteAddressBox.Clear();
    }

    // ------------------------------------------------------------------ settings

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            AddLog("Settings saved (port changes require restart).");
        }
    }
}
