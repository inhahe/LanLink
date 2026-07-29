using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanLink;

/// <summary>How the window should appear when LanLink launches.</summary>
public enum StartupMode
{
    Normal,     // show the window normally
    Minimized,  // start minimized to the taskbar
    Tray        // start hidden — only the system-tray icon is shown
}

public sealed class AppSettings
{
    public string       NodeId         { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string       DisplayName    { get; set; } = Environment.MachineName;
    public string       DownloadFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanLink");
    public int          Port           { get; set; } = 37656;
    public List<string> SavedRemotes   { get; set; } = new();

    /// <summary>Launch LanLink automatically when Windows starts.</summary>
    public bool         RunOnStartup   { get; set; }

    /// <summary>How the window appears on launch (normal / minimized / tray).</summary>
    public StartupMode  StartupMode    { get; set; } = StartupMode.Normal;

    /// <summary>
    /// Accept inbound TCP connections from addresses outside the local network.
    /// Off by default: only LAN / loopback peers may connect in.  (Outgoing
    /// "remote address" connections you initiate are always allowed.)
    /// </summary>
    public bool         AcceptExternalConnections { get; set; }

    /// <summary>
    /// The installer autostart request (a version stamp) that has already been
    /// applied — see <see cref="Autostart.ConsumeInstallerRequest"/>.  Empty
    /// means "no install has asked yet".  Stored so setup's choice is honoured
    /// exactly once and a later change in Settings isn't undone on every launch.
    /// </summary>
    public string       AutostartRequestHandled { get; set; } = "";

    /// <summary>
    /// Legacy flag (pre-StartupMode).  Kept only so old settings files still
    /// deserialize; migrated to <see cref="StartupMode"/> on load.
    /// </summary>
    public bool         StartMinimized { get; set; }

    /// <summary>
    /// True when these settings came from an existing settings.json rather than
    /// from defaults.  Lets first-run logic tell "the user has never configured
    /// anything" from "the user chose the defaults".  Not persisted.
    /// </summary>
    [JsonIgnore]
    public bool         ExistedOnDisk  { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanLink", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s is not null)
                {
                    // Migrate the old boolean "start minimized to tray" flag.
                    if (s.StartMinimized && s.StartupMode == StartupMode.Normal)
                        s.StartupMode = StartupMode.Tray;
                    s.StartMinimized = false;
                    s.ExistedOnDisk  = true;
                    return s;
                }
            }
        }
        catch { /* first run or corrupt file — use defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
