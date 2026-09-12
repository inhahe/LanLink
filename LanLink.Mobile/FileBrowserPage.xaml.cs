using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LanLink;

public enum BrowserMode
{
    /// <summary>Multi-select files.</summary>
    Files,
    /// <summary>Navigate to a folder and send the whole thing.</summary>
    Folder
}

// ===========================================================================
//  One row in the browser
// ===========================================================================

public sealed class BrowserEntry : INotifyPropertyChanged
{
    public string   Name        { get; init; } = "";
    public string   FullPath    { get; init; } = "";
    public bool     IsDirectory { get; init; }
    public long     Size        { get; init; }
    public DateTime Modified    { get; init; }

    /// <summary>Set for the storage-roots screen ("Internal storage", etc.).</summary>
    public string? SubtitleOverride { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Notify(); Notify(nameof(Trailing)); }
    }

    /// <summary>Folder / page emoji.</summary>
    public string Icon => IsDirectory ? "📁" : "📄";

    public string Detail =>
        SubtitleOverride
        ?? (IsDirectory
                ? "Folder"
                : $"{TransferManager.FormatSize(Size)}  ·  {Modified:yyyy-MM-dd HH:mm}");

    /// <summary>Chevron for a drill-down row, check mark for a selected file.</summary>
    public string Trailing => IsDirectory ? "›" : (IsSelected ? "✓" : "");

    public static BrowserEntry ForDirectory(DirectoryInfo d) => new()
    {
        Name = d.Name, FullPath = d.FullName, IsDirectory = true
    };

    public static BrowserEntry ForFile(FileInfo f) => new()
    {
        Name        = f.Name,
        FullPath    = f.FullName,
        IsDirectory = false,
        Size        = TryGet(() => f.Length, 0L),
        Modified    = TryGet(() => f.LastWriteTime, DateTime.MinValue)
    };

    private static T TryGet<T>(Func<T> get, T fallback)
    {
        try { return get(); } catch { return fallback; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

// ===========================================================================
//  The browser page
// ===========================================================================

/// <summary>
/// An in-app file/folder browser over real filesystem paths.
///
/// Android's system picker (SAF) hands back <c>content://</c> URIs rather than
/// paths, and only surfaces whichever DocumentsProviders the OEM chooses to
/// show -- on a Samsung device that can be nothing but Google Drive.  Because
/// <see cref="TransferManager.SendDirectoryAsync"/> walks a real directory tree,
/// a SAF-picked folder would also have to be copied into the cache before it
/// could be sent.  Browsing the filesystem ourselves avoids both problems, at
/// the cost of needing <see cref="StoragePermission"/>.
/// </summary>
public partial class FileBrowserPage : ContentPage
{
    private readonly BrowserMode _mode;
    private readonly TaskCompletionSource<List<string>?> _result = new();
    private readonly ObservableCollection<BrowserEntry>  _entries = new();

    /// <summary>Selected file paths, kept across directory navigation.</summary>
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    /// <summary>Volume roots -- "up" from one of these returns to the roots screen.</summary>
    private readonly HashSet<string> _volumeRoots = new(StringComparer.Ordinal);

    /// <summary>null means the storage-roots screen rather than a directory.</summary>
    private string? _currentDir;

    private FileBrowserPage(BrowserMode mode)
    {
        InitializeComponent();
        _mode = mode;

        EntryList.ItemsSource = _entries;

        TitleLabel.Text        = mode == BrowserMode.Files ? "Select files" : "Select a folder";
        SelectAllBtn.IsVisible = mode == BrowserMode.Files;

        Load(null);
    }

    /// <summary>
    /// Shows the browser modally.  Returns the chosen paths, or null if the user
    /// cancelled.  In <see cref="BrowserMode.Folder"/> the list holds exactly one
    /// directory path.
    /// </summary>
    public static async Task<List<string>?> PickAsync(Page host, BrowserMode mode)
    {
        var page = new FileBrowserPage(mode);
        await host.Navigation.PushModalAsync(page);
        return await page._result.Task;
    }

    // ------------------------------------------------------------------ listing

    private void Load(string? dir)
    {
        _currentDir = dir;
        _entries.Clear();

        if (dir is null)
        {
            foreach (var root in GetRoots())
                _entries.Add(root);

            PathLabel.Text  = "Storage";
            UpBtn.IsEnabled = false;
            EmptyLabel.Text = "No storage volumes found.";
        }
        else
        {
            PathLabel.Text  = Prettify(dir);
            UpBtn.IsEnabled = true;

            try
            {
                var di = new DirectoryInfo(dir);

                foreach (var d in di.EnumerateDirectories()
                                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    _entries.Add(BrowserEntry.ForDirectory(d));

                foreach (var f in di.EnumerateFiles()
                                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var entry = BrowserEntry.ForFile(f);
                    entry.IsSelected = _selected.Contains(entry.FullPath);
                    _entries.Add(entry);
                }

                EmptyLabel.Text = "This folder is empty.";
            }
            catch (Exception ex)
            {
                EmptyLabel.Text = $"Can't read this folder.\n\n{ex.Message}";
            }
        }

        UpdateFooter();
    }

    /// <summary>
    /// Storage volumes plus shortcuts to the folders people actually want.
    /// Populates <see cref="_volumeRoots"/> as a side effect.
    /// </summary>
    private List<BrowserEntry> GetRoots()
    {
        var roots = new List<BrowserEntry>();
        _volumeRoots.Clear();

        void AddVolume(string label, string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            string full = Path.TrimEndingDirectorySeparator(path);
            if (!_volumeRoots.Add(full)) return;

            roots.Add(new BrowserEntry
            {
                Name = label, FullPath = full, IsDirectory = true, SubtitleOverride = full
            });
        }

        void AddShortcut(string label, string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            string full = Path.TrimEndingDirectorySeparator(path);
            if (roots.Any(r => r.FullPath == full)) return;

            roots.Add(new BrowserEntry
            {
                Name = label, FullPath = full, IsDirectory = true, SubtitleOverride = "Shortcut"
            });
        }

#if ANDROID
        string internalRoot =
            Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";
        AddVolume("Internal storage", internalRoot);

        // Removable volumes: GetExternalFilesDirs returns
        // <volume>/Android/data/<pkg>/files on every mounted volume, so trimming
        // at /Android/data/ yields the volume root.  There is no public API that
        // hands back the roots directly.
        try
        {
            var dirs = Android.App.Application.Context.GetExternalFilesDirs(null);
            if (dirs is not null)
            {
                int n = 0;
                foreach (var d in dirs)
                {
                    string? p = d?.AbsolutePath;
                    if (string.IsNullOrEmpty(p)) continue;

                    int idx = p.IndexOf("/Android/data/", StringComparison.Ordinal);
                    if (idx <= 0) continue;

                    string volume = p[..idx];
                    if (volume == internalRoot) continue;

                    n++;
                    AddVolume(n == 1 ? "SD card" : $"SD card {n}", volume);
                }
            }
        }
        catch { /* no removable storage, or the OEM blocks the query */ }

        AddShortcut("Downloads", Path.Combine(internalRoot, "Download"));
        AddShortcut("Camera",    Path.Combine(internalRoot, "DCIM"));
        AddShortcut("Pictures",  Path.Combine(internalRoot, "Pictures"));
        AddShortcut("Documents", Path.Combine(internalRoot, "Documents"));
#else
        AddVolume("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
#endif

        // Wherever LanLink drops received files -- the obvious place to forward
        // something straight back out again.
        AddShortcut("LanLink downloads", AppSettings.Load().DownloadFolder);

        return roots;
    }

    private static string Prettify(string path)
    {
#if ANDROID
        string internalRoot =
            Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";
        if (path.StartsWith(internalRoot, StringComparison.Ordinal))
            return "Internal storage" + path[internalRoot.Length..];
#endif
        return path;
    }

    // ------------------------------------------------------------------ navigation

    private void Up_Clicked(object? sender, EventArgs e) => GoUp();

    private void GoUp()
    {
        if (_currentDir is null) return;

        if (_volumeRoots.Contains(Path.TrimEndingDirectorySeparator(_currentDir)))
        {
            Load(null);
            return;
        }

        var parent = Directory.GetParent(_currentDir);
        Load(parent?.FullName);
    }

    private void Entry_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not BrowserEntry entry) return;

        if (entry.IsDirectory)
        {
            Load(entry.FullPath);
            return;
        }

        if (_mode == BrowserMode.Folder)
        {
            Toasts.Show("Open a folder, then tap \"Send this folder\"");
            return;
        }

        entry.IsSelected = !entry.IsSelected;
        if (entry.IsSelected) _selected.Add(entry.FullPath);
        else                  _selected.Remove(entry.FullPath);

        UpdateFooter();
    }

    private void SelectAll_Clicked(object? sender, EventArgs e)
    {
        var files = _entries.Where(x => !x.IsDirectory).ToList();
        if (files.Count == 0) return;

        // If everything here is already selected, the button means "none".
        bool selectAll = files.Any(f => !f.IsSelected);

        foreach (var f in files)
        {
            f.IsSelected = selectAll;
            if (selectAll) _selected.Add(f.FullPath);
            else           _selected.Remove(f.FullPath);
        }

        UpdateFooter();
    }

    // ------------------------------------------------------------------ footer

    private void UpdateFooter()
    {
        if (_mode == BrowserMode.Files)
        {
            int count = _selected.Count;
            ConfirmBtn.Text      = count > 0 ? $"Send {count}" : "Send";
            ConfirmBtn.IsEnabled = count > 0;
            StatusLabel.Text     = count == 0
                ? "Tap files to select them"
                : $"{count} file{(count == 1 ? "" : "s")} selected";
        }
        else
        {
            bool inFolder = _currentDir is not null;
            ConfirmBtn.Text      = "Send this folder";
            ConfirmBtn.IsEnabled = inFolder;
            StatusLabel.Text     = inFolder
                ? $"Sends {Path.GetFileName(Path.TrimEndingDirectorySeparator(_currentDir!))} and everything in it"
                : "Open the folder you want to send";
        }
    }

    // ------------------------------------------------------------------ finish

    private async void Confirm_Clicked(object? sender, EventArgs e)
    {
        List<string> picked = _mode == BrowserMode.Files
            ? _selected.ToList()
            : new List<string> { _currentDir! };

        if (picked.Count == 0) return;

        await Navigation.PopModalAsync();
        _result.TrySetResult(picked);
    }

    private async void Cancel_Clicked(object? sender, EventArgs e) => await CancelAsync();

    private async Task CancelAsync()
    {
        await Navigation.PopModalAsync();
        _result.TrySetResult(null);
    }

    /// <summary>
    /// Hardware back walks up the tree first -- closing the whole browser on the
    /// first back press after drilling three levels down would be infuriating.
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        if (_currentDir is not null)
        {
            GoUp();
            return true;
        }

        _ = CancelAsync();
        return true;
    }
}
