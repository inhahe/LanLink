using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LanLink;

public sealed class AppSettings
{
    public string       NodeId         { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string       DisplayName    { get; set; } = Environment.MachineName;
    public string       DownloadFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanLink");
    public int          Port           { get; set; } = 48656;
    public List<string> SavedRemotes   { get; set; } = new();
    public bool         RunOnStartup   { get; set; }
    public bool         StartMinimized { get; set; }

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
                if (s is not null) return s;
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
