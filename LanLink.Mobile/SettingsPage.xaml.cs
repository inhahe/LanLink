namespace LanLink;

public partial class SettingsPage : ContentPage
{
    private readonly AppSettings _settings;

    public SettingsPage()
    {
        InitializeComponent();

        _settings = AppSettings.Load();

        NameEntry.Text   = _settings.DisplayName;
        FolderEntry.Text = _settings.DownloadFolder;
        PortEntry.Text   = _settings.Port.ToString();
        NodeIdLabel.Text = _settings.NodeId;
    }

    private async void Save_Clicked(object? sender, EventArgs e)
    {
        string name = NameEntry.Text?.Trim() ?? "";
        _settings.DisplayName = string.IsNullOrEmpty(name)
            ? (DeviceInfo.Name ?? "Android")
            : name;

        if (int.TryParse(PortEntry.Text?.Trim(), out int port) && port > 1024 && port < 65536)
            _settings.Port = port;

        _settings.Save();
        await Shell.Current.GoToAsync("..");
    }

    private async void Cancel_Clicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
