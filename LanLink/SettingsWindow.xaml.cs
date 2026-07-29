using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace LanLink;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        NameBox.Text   = settings.DisplayName;
        FolderBox.Text = settings.DownloadFolder;
        PortBox.Text   = settings.Port.ToString();
        NodeIdBox.Text = settings.NodeId;
        // Reflect what the registry actually says rather than the saved bool:
        // an entry left by an older installer can turn autostart on without
        // settings.json ever knowing, which would leave this checkbox lying.
        StartupCheck.IsChecked   = Autostart.IsEnabled();
        StartModeCombo.SelectedIndex = (int)settings.StartupMode;   // Normal=0, Minimized=1, Tray=2
        ExternalCheck.IsChecked  = settings.AcceptExternalConnections;
    }

    /// <summary>
    /// Set when a machine-wide (HKLM) autostart entry survived because removing
    /// it needs administrator rights.  The caller surfaces this to the user.
    /// </summary>
    public bool MachineWideAutostartRemains { get; private set; }

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

        _settings.RunOnStartup = StartupCheck.IsChecked == true;
        _settings.StartupMode  = (StartupMode)Math.Max(0, StartModeCombo.SelectedIndex);
        _settings.AcceptExternalConnections = ExternalCheck.IsChecked == true;
        MachineWideAutostartRemains =
            !Autostart.Apply(_settings.RunOnStartup, _settings.StartupMode);

        _settings.Save();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
