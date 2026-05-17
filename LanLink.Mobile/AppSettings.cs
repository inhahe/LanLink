namespace LanLink;

/// <summary>
/// Settings backed by MAUI Preferences (key-value store persisted across launches).
/// </summary>
public sealed class AppSettings
{
    public string NodeId         { get; set; }
    public string DisplayName    { get; set; }
    public string DownloadFolder { get; set; }
    public int    Port           { get; set; }
    public List<string> SavedRemotes { get; set; } = new();

    public AppSettings()
    {
        NodeId      = Preferences.Get("NodeId", Guid.NewGuid().ToString("N")[..12]);
        DisplayName = Preferences.Get("DisplayName", DeviceInfo.Name ?? "Android");
        Port        = Preferences.Get("Port", 48656);

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
    }

    public static AppSettings Load() => new();
}
