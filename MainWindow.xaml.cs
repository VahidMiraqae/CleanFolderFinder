using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace CleanFolderFinder;

public partial class MainWindow : Window
{
    private const int MaxRecentFolders = 10;

    private readonly ObservableCollection<FolderItem> _items = new();
    private readonly List<string> _recentFolders = new();

    private static string SettingsFilePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CleanFolderFinder",
            "recent-folders.txt");

    public MainWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _items;
        LoadRecentFolders();
    }

    /// <summary>Loads the persisted recent folders into the editable combo box.</summary>
    private void LoadRecentFolders()
    {
        _recentFolders.Clear();
        RootPathBox.Items.Clear();

        try
        {
            if (File.Exists(SettingsFilePath))
            {
                foreach (string line in File.ReadAllLines(SettingsFilePath))
                {
                    string path = line.Trim();
                    if (path.Length > 0 && !_recentFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        _recentFolders.Add(path);
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        foreach (string path in _recentFolders)
        {
            RootPathBox.Items.Add(path);
        }

        if (_recentFolders.Count > 0)
        {
            RootPathBox.Text = _recentFolders[0];
        }
    }

    /// <summary>Adds the folder to the MRU list (newest first) and persists it.</summary>
    private void AddRecentFolder(string path)
    {
        path = path.Trim();
        if (path.Length == 0)
        {
            return;
        }

        _recentFolders.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _recentFolders.Insert(0, path);

        while (_recentFolders.Count > MaxRecentFolders)
        {
            _recentFolders.RemoveAt(_recentFolders.Count - 1);
        }

        RootPathBox.Items.Clear();
        foreach (string recent in _recentFolders)
        {
            RootPathBox.Items.Add(recent);
        }

        SaveRecentFolders();
    }

    private void SaveRecentFolders()
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(SettingsFilePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllLines(SettingsFilePath, _recentFolders);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a root folder to scan"
        };

        if (dialog.ShowDialog() == true)
        {
            RootPathBox.Text = dialog.FolderName;
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        string root = RootPathBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            MessageBox.Show(this, "Please select a valid root folder.", "Invalid folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        StatusText.Text = "Scanning...";
        AddRecentFolder(root);
        _items.Clear();
        UpdateSelectionInfo();

        List<string> found;
        try
        {
            found = await Task.Run(() => FindBuildFolders(root));
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed: " + ex.Message;
            SetBusy(false);
            return;
        }

        Progress.Maximum = found.Count;
        Progress.Value = 0;

        // Add items first, then compute sizes on a background thread.
        var newItems = new List<FolderItem>();
        foreach (string path in found)
        {
            string kind = System.IO.Path.GetFileName(path).ToLowerInvariant() == "obj" ? "obj" : "bin";
            var item = new FolderItem(path, kind);
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FolderItem.IsSelected))
                {
                    UpdateSelectionInfo();
                }
            };

            _items.Add(item);
            newItems.Add(item);
        }

        StatusText.Text = $"Found {newItems.Count} folder(s). Calculating sizes...";

        foreach (FolderItem item in newItems)
        {
            long size = await Task.Run(() => FolderItem.ComputeSize(item.Path));
            item.SizeBytes = size;
            UpdateStats();
            Progress.Value++;
        }

        Progress.Value = 0;
        StatusText.Text = $"Found {_items.Count} build output folder(s).";
        UpdateSelectionInfo();
        SetBusy(false);
    }

    private void UpdateStats()
    {
        long allSize = _items.Sum(i => Math.Max(0, i.SizeBytes));
        long selectedSize = _items.Where(i => i.IsSelected).Sum(i => Math.Max(0, i.SizeBytes));
        int selectedCount = _items.Count(i => i.IsSelected);

        StatsText.Text =
            $"Found: {_items.Count} folder(s), {FolderItem.FormatSize(allSize)} total   |   " +
            $"Selected: {selectedCount} folder(s), {FolderItem.FormatSize(selectedSize)} total";
    }

    /// <summary>
    /// Recursively finds directories named "bin" or "obj" under <paramref name="root"/>.
    /// When a match is found its subtree is skipped so nested duplicates are not reported.
    /// </summary>
    private static List<string> FindBuildFolders(string root)
    {
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            string name = System.IO.Path.GetFileName(dir);

            // Never descend into node_modules: any bin/obj inside it is ignored.
            if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(dir, root, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(dir);
                continue;
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string sub in subDirs)
            {
                stack.Push(sub);
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (FolderItem item in _items)
        {
            item.IsSelected = true;
        }

        HeaderCheckBox.IsChecked = true;
        UpdateSelectionInfo();
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (FolderItem item in _items)
        {
            item.IsSelected = false;
        }

        HeaderCheckBox.IsChecked = false;
        UpdateSelectionInfo();
    }

    private void HeaderCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool select = HeaderCheckBox.IsChecked == true;
        foreach (FolderItem item in _items)
        {
            item.IsSelected = select;
        }

        UpdateSelectionInfo();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var targets = _items.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "No folders are selected.", "Nothing to delete",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Confirm(targets))
        {
            return;
        }

        await DeleteItemsAsync(targets);
    }

    private async void DeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderItem item })
        {
            return;
        }

        if (!Confirm(new List<FolderItem> { item }))
        {
            return;
        }

        await DeleteItemsAsync(new List<FolderItem> { item });
    }

    private bool Confirm(IReadOnlyCollection<FolderItem> targets)
    {
        string message = targets.Count == 1
            ? $"Delete this folder?\n\n{targets.First().Path}"
            : $"Delete {targets.Count} folders? This cannot be undone.";

        return MessageBox.Show(this, message, "Confirm delete",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private async Task DeleteItemsAsync(IReadOnlyCollection<FolderItem> targets)
    {
        SetBusy(true);
        Progress.Maximum = targets.Count;
        Progress.Value = 0;
        StatusText.Text = "Deleting...";

        int deleted = 0;
        int failed = 0;
        var errors = new List<string>();

        foreach (FolderItem item in targets)
        {
            try
            {
                await Task.Run(() => Directory.Delete(item.Path, recursive: true));
                _items.Remove(item);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{item.Path}: {ex.Message}");
            }

            Progress.Value++;
        }

        Progress.Value = 0;
        SetBusy(false);
        UpdateSelectionInfo();

        StatusText.Text = $"Deleted {deleted} folder(s)." + (failed > 0 ? $" {failed} failed." : string.Empty);

        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "Some folders could not be deleted",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateSelectionInfo()
    {
        int selected = _items.Count(i => i.IsSelected);
        SelectionInfo.Text = $"{selected} selected of {_items.Count}";
        UpdateStats();
    }

    private void SetBusy(bool busy)
    {
        RootPathBox.IsEnabled = !busy;
        ResultsList.IsEnabled = !busy;
    }
}
