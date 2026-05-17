using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace LanLink;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    private const string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "LanLink";

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        NameBox.Text   = settings.DisplayName;
        FolderBox.Text = settings.DownloadFolder;
        PortBox.Text   = settings.Port.ToString();
        NodeIdBox.Text = settings.NodeId;
        StartupCheck.IsChecked   = settings.RunOnStartup;
        MinimizedCheck.IsChecked = settings.StartMinimized;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select download folder" };
        if (dlg.ShowDialog() == true)
            FolderBox.Text = dlg.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        _settings.DisplayName = string.IsNullOrEmpty(name)
            ? Environment.MachineName
            : name;

        string folder = FolderBox.Text.Trim();
        _settings.DownloadFolder = string.IsNullOrEmpty(folder)
            ? Path.Combine(Environment.GetFolderPath(
                  Environment.SpecialFolder.UserProfile), "Downloads", "LanLink")
            : folder;

        if (int.TryParse(PortBox.Text.Trim(), out int port) && port > 1024 && port < 65536)
            _settings.Port = port;

        _settings.RunOnStartup   = StartupCheck.IsChecked == true;
        _settings.StartMinimized = MinimizedCheck.IsChecked == true;
        ApplyStartupRegistry(_settings.RunOnStartup, _settings.StartMinimized);

        _settings.Save();
        DialogResult = true;
        Close();
    }

    private static void ApplyStartupRegistry(bool enable, bool minimized)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
            if (key is null) return;

            if (enable)
            {
                string exePath = Environment.ProcessPath
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LanLink.exe");
                string value = minimized
                    ? $"\"{exePath}\" --minimized"
                    : $"\"{exePath}\"";
                key.SetValue(AppName, value);
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch { /* non-critical — user can set manually */ }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
