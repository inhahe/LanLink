namespace LanLink;

/// <summary>
/// Settings backed by MAUI Preferences (key-value store persisted across launches).
/// </summary>
public sealed class AppSettings : ILanLinkSettings
{
    public string NodeId         { get; set; }
    public string DisplayName    { get; set; }
    public string DownloadFolder { get; set; }
    public int    Port           { get; set; }
    public List<string> SavedRemotes { get; set; } = new();

    /// <summary>
    /// Accept inbound TCP connections from addresses outside the local network.
    /// Off by default: only LAN / loopback peers may connect in.  (Outgoing
    /// "remote address" connections you initiate are always allowed.)
    /// </summary>
    public bool AcceptExternalConnections { get; set; }

    /// <summary>
    /// Hide Android's recycle bin from the file browser and from directory
    /// sends.  When you delete a photo, Android renames it
    /// <c>.trashed-&lt;purge-timestamp&gt;-&lt;original&gt;</c> and keeps it for ~30 days.
    /// LanLink walks the real filesystem, so without this a folder send happily
    /// ships hundreds of megabytes of photos the user believes they deleted.
    /// On by default.
    /// </summary>
    public bool HideTrashedFiles { get; set; }

    /// <summary>
    /// Name filter for directory sends, or null when the user wants everything.
    /// </summary>
    public Func<string, bool>? TrashFilter
        => HideTrashedFiles ? IsAndroidTrash : null;

    /// <summary>Android's trash naming: <c>.trashed-1791704103-IMG_0001.jpg</c>.</summary>
    public static bool IsAndroidTrash(string name)
        => name.StartsWith(".trashed-", StringComparison.Ordinal);

    public AppSettings()
    {
        NodeId      = Preferences.Get("NodeId", Guid.NewGuid().ToString("N")[..12]);
        DisplayName = Preferences.Get("DisplayName", DeviceInfo.Name ?? "Android");
        Port        = Preferences.Get("Port", 37656);
        AcceptExternalConnections = Preferences.Get("AcceptExternalConnections", false);
        HideTrashedFiles          = Preferences.Get("HideTrashedFiles", true);

        // Default download location: app-specific external storage (visible in file managers).
#if ANDROID
        DownloadFolder = Preferences.Get("DownloadFolder",
            Path.Combine(
                Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath
                    ?? FileSystem.AppDataDirectory,
                "LanLink"));
#else
        DownloadFolder = Preferences.Get("DownloadFolder",
            Path.Combine(FileSystem.AppDataDirectory, "LanLink"));
#endif

        // Persist NodeId immediately so it stays stable across launches.
        if (!Preferences.ContainsKey("NodeId"))
            Preferences.Set("NodeId", NodeId);
    }

    public void Save()
    {
        Preferences.Set("NodeId", NodeId);
        Preferences.Set("DisplayName", DisplayName);
        Preferences.Set("DownloadFolder", DownloadFolder);
        Preferences.Set("Port", Port);
        Preferences.Set("AcceptExternalConnections", AcceptExternalConnections);
        Preferences.Set("HideTrashedFiles", HideTrashedFiles);
    }

    public static AppSettings Load() => new();
}
