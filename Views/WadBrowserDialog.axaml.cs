using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ZScape.Controls;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.Views;

/// <summary>
/// View-model for a single WAD file row in the browser list.
/// </summary>
public class WadFileEntry : INotifyPropertyChanged
{
    private string? _cachedHash;
    private bool _isHashCachingExcluded;

    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    public string NameWithExtension => Name + Extension;

    public string SizeDisplay => FormatSize(Size);
    public string ModifiedDisplay => Modified.ToString("yyyy-MM-dd HH:mm");
    public bool IsHashCached => !string.IsNullOrWhiteSpace(CachedHash);
    public bool IsHashCachingExcluded => _isHashCachingExcluded;
    public string CachedMarker => IsHashCachingExcluded ? "Skip" : IsHashCached ? "✓" : string.Empty;
    public IBrush CachedMarkerColor => IsHashCachingExcluded ? Brushes.Gray : Brushes.LightGreen;
    public string? CachedHash
    {
        get => _cachedHash;
        private set
        {
            if (string.Equals(_cachedHash, value, StringComparison.OrdinalIgnoreCase))
                return;

            _cachedHash = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CachedHash)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHashCached)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CachedMarker)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetCachedHash(string? hash) => CachedHash = hash;

    public void SetHashCachingExcluded(bool isExcluded)
    {
        if (_isHashCachingExcluded == isExcluded)
            return;

        _isHashCachingExcluded = isExcluded;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHashCachingExcluded)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CachedMarker)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CachedMarkerColor)));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:N1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):N1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):N2} GB";
    }
}

public partial class WadBrowserDialog : Window
{
    private readonly SettingsService _settings;
    private readonly LoggingService _logger = LoggingService.Instance;
    private readonly List<WadFileEntry> _allWads = new();
    private ObservableCollection<WadFileEntry> _filteredWads = new();
    private bool _isScanning;
    private bool _isRefreshingSelectedHash;
    private bool _isUpdatingHashCacheExclusions;
    private bool _isClosing;
    private CancellationTokenSource? _scanCancellation;
    private MenuItem? _locateFileMenuItem;
    private MenuItem? _calculateHashMenuItem;
    private MenuItem? _toggleHashCachingMenuItem;
    private MenuItem? _deleteSelectedMenuItem;

    private readonly List<ListViewSortDescriptor> _sortDescriptors =
    [
        new(0, "name", true)
    ];

    // For shift-click range selection
    // (Handled by built-in multi-select in ResizableListView)

    public WadBrowserDialog()
    {
        InitializeComponent();
        _settings = SettingsService.Instance;

        // Configure the list view columns
        WadListView.SelectionMode = ListViewSelectionMode.Multi;
        WadListView.AddColumn(new ListViewColumn
        {
            Key = "name", Header = "Name", Width = 250, MinWidth = 10,
            BindingPath = "NameWithExtension",
            TextTrimming = TextTrimming.CharacterEllipsis,
            CellPadding = new Thickness(6, 0),
            CanSort = true,
            CanUserHide = false
        });
        WadListView.AddColumn(new ListViewColumn
        {
            Key = "cached", Header = "Cached", Width = 70, MinWidth = 10,
            CellContentFactory = CreateCachedStatusCell,
            CanSort = true,
            HeaderToolTip = "A check mark means a valid local full MD5 is cached for this unchanged file. Skip means this file is explicitly excluded from hash caching."
        });
        WadListView.AddColumn(new ListViewColumn
        {
            Key = "size", Header = "Size", Width = 80, MinWidth = 10,
            BindingPath = "SizeDisplay",
            CanSort = true
        });
        WadListView.AddColumn(new ListViewColumn
        {
            Key = "hash", Header = "MD5", Width = 270, MinWidth = 10,
            BindingPath = nameof(WadFileEntry.CachedHash),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.Gray,
            CanSort = true,
            IsVisibleByDefault = false,
            HeaderToolTip = "Full cached MD5. Show this optional column from the list header menu."
        });
        WadListView.AddColumn(new ListViewColumn
        {
            Key = "modified", Header = "Modified", Width = 130, MinWidth = 10,
            BindingPath = "ModifiedDisplay",
            CanSort = true
        });
        WadListView.AddColumn(new ListViewColumn
        {
            Key = "path", Header = "Path", Width = 240, IsStar = true, MinWidth = 10,
            BindingPath = "FullPath",
            Foreground = Brushes.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis,
            CanSort = true
        });
        WadListView.Build(ListViewOverflowMode.AutoScroll);
        WadListView.SetSortDescriptors(_sortDescriptors);
        WadListView.SortRequested += WadListView_SortRequested;
        WadListView.ItemsSource = _filteredWads;
        WadListView.SelectionChanged += (_, _) => UpdateActionAvailability();
        WadListView.RowsUpdated += (_, _) => UpdateStats();
        ConfigureWadContextMenu();

        // Wire up row events
        WadListView.RowDoubleTapped += OnWadRowDoubleTapped;

        // Handle Escape key
        KeyDown += OnDialogKeyDown;
        Closing += (_, _) =>
        {
            _isClosing = true;
            _scanCancellation?.Cancel();
        };

        Loaded += async (_, _) => await ScanWadsAsync();
        UpdateActionAvailability();
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    #region Scanning

    private async Task ScanWadsAsync()
    {
        if (_isScanning) return;
        _isScanning = true;
        _isClosing = false;

        using var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        var scanProgress = new Progress<WadFileScanProgress>(UpdateScanProgress);
        var wadPaths = WadManager.Instance.GetSearchRootsInPriorityOrder().ToArray();

        WadListView.ClearSelection();
        ScanProgressBar.IsVisible = true;
        ScanProgressBar.IsIndeterminate = true;
        CancelScanButton.IsEnabled = true;
        StatusLabel.Text = wadPaths.Length == 0
            ? "No valid WAD folders are configured."
            : $"Preparing to scan {wadPaths.Length} WAD folder(s)...";
        ToolTip.SetTip(StatusLabel, wadPaths.Length == 0
            ? "Configure WAD Paths in Settings to browse local WAD files."
            : string.Join(Environment.NewLine, wadPaths));
        RefreshButton.IsEnabled = false;
        CacheAllHashesButton.IsEnabled = false;

        try
        {
            if (wadPaths.Length == 0)
            {
                UpdateStats();
                return;
            }

            var scannedWads = await Task.Run(
                () => ScanWadFiles(wadPaths, scanProgress, scanCancellation.Token),
                scanCancellation.Token);

            if (_isClosing)
                return;

            _allWads.Clear();
            foreach (var entry in scannedWads.Entries)
                _allWads.Add(entry);

            ApplyFilterAndSort();
            UpdateStats();
            StatusLabel.Text = BuildScanCompleteStatus(scannedWads);
            ToolTip.SetTip(StatusLabel, BuildScanDiagnosticsToolTip(scannedWads));

            if (scannedWads.ScanResult.Issues.Count > 0)
            {
                foreach (var issue in scannedWads.ScanResult.Issues.Take(5))
                    _logger.Warning($"WAD Browser skipped {issue.Path}: {issue.Message}");
            }
        }
        catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
        {
            if (!_isClosing)
            {
                StatusLabel.Text = _allWads.Count == 0
                    ? "WAD scan cancelled before any results were loaded."
                    : "WAD scan cancelled; previous results are still shown.";
                ToolTip.SetTip(StatusLabel, null);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"WAD Browser scan failed: {ex.Message}");
            if (!_isClosing)
            {
                StatusLabel.Text = $"WAD scan failed: {ex.Message}";
                ToolTip.SetTip(StatusLabel, ex.ToString());
            }
        }
        finally
        {
            _isScanning = false;
            if (ReferenceEquals(_scanCancellation, scanCancellation))
                _scanCancellation = null;

            if (!_isClosing)
            {
                ScanProgressBar.IsVisible = false;
                CancelScanButton.IsEnabled = false;
                UpdateActionAvailability();
            }
        }
    }

    private static WadBrowserScanResult ScanWadFiles(
        IReadOnlyList<string> wadPaths,
        IProgress<WadFileScanProgress> progress,
        CancellationToken cancellationToken)
    {
        var entries = new List<WadFileEntry>();
        var skippedEntries = 0;
        var hashCache = WadHashCacheService.Instance;

        var scanResult = WadFileScanner.Scan(
            wadPaths,
            filePath =>
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    var entry = new WadFileEntry
                    {
                        Name = Path.GetFileNameWithoutExtension(filePath),
                        Extension = Path.GetExtension(filePath).ToLowerInvariant(),
                        FullPath = filePath,
                        Size = fileInfo.Length,
                        Modified = fileInfo.LastWriteTime
                    };

                    entry.SetHashCachingExcluded(hashCache.IsHashCachingExcluded(filePath));

                    // Most WADs have never been cached. Avoid an unnecessary
                    // Windows file-identity handle open for each of those paths;
                    // TryGetCachedHash still performs the full snapshot proof for
                    // every path that may have a cache entry.
                    if (!entry.IsHashCachingExcluded && hashCache.HasCachedEntryForPath(filePath))
                        entry.SetCachedHash(hashCache.TryGetCachedHash(filePath));

                    entries.Add(entry);
                }
                catch
                {
                    // A file may vanish or become inaccessible after the scanner
                    // yields it. Count it for diagnostics without abandoning the
                    // rest of the WAD folders.
                    skippedEntries++;
                }
            },
            progress,
            cancellationToken);

        return new WadBrowserScanResult(entries, scanResult, skippedEntries);
    }

    private void UpdateScanProgress(WadFileScanProgress progress)
    {
        if (!_isScanning || _isClosing)
            return;

        var rootName = GetFolderDisplayName(progress.CurrentRoot);
        StatusLabel.Text = $"Scanning WAD folder {progress.RootIndex + 1}/{progress.RootCount}: {rootName} "
            + $"· {progress.FilesExamined:N0} files checked · {progress.SupportedFilesFound:N0} WADs found";
        ToolTip.SetTip(
            StatusLabel,
            $"Current root: {progress.CurrentRoot}{Environment.NewLine}"
            + $"Current folder: {progress.CurrentDirectory}{Environment.NewLine}"
            + $"{progress.DirectoriesVisited:N0} folders visited; "
            + $"{progress.SkippedDirectories:N0} inaccessible folder(s) skipped; "
            + $"{progress.SkippedReparsePoints:N0} reparse-point folder(s) skipped.");
    }

    private static string BuildScanCompleteStatus(WadBrowserScanResult scan)
    {
        var elapsed = scan.ScanResult.Elapsed;
        var elapsedText = elapsed.TotalSeconds < 1
            ? "under 1 second"
            : $"{elapsed.TotalSeconds:N1} seconds";
        var status = $"Scan complete: {scan.Entries.Count:N0} WAD file(s) found in {elapsedText}.";

        var skipped = scan.ScanResult.SkippedDirectories + scan.SkippedEntries;
        if (skipped > 0 || scan.ScanResult.SkippedReparsePoints > 0)
        {
            status += $" Skipped {skipped:N0} inaccessible/changed path(s) and "
                + $"{scan.ScanResult.SkippedReparsePoints:N0} reparse-point folder(s).";
        }

        return status;
    }

    private static string? BuildScanDiagnosticsToolTip(WadBrowserScanResult scan)
    {
        if (scan.ScanResult.Issues.Count == 0 && scan.SkippedEntries == 0)
            return null;

        var lines = new List<string>();
        if (scan.SkippedEntries > 0)
            lines.Add($"{scan.SkippedEntries:N0} file(s) changed or became inaccessible while being listed.");
        lines.AddRange(scan.ScanResult.Issues.Take(5).Select(issue => $"{issue.Path}: {issue.Message}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string GetFolderDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "unknown folder";

        var trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmedPath) is { Length: > 0 } name ? name : trimmedPath;
    }

    private void CancelScanButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_scanCancellation == null || _scanCancellation.IsCancellationRequested)
            return;

        _scanCancellation.Cancel();
        CancelScanButton.IsEnabled = false;
        StatusLabel.Text = "Cancelling WAD folder scan...";
    }

    private sealed record WadBrowserScanResult(
        IReadOnlyList<WadFileEntry> Entries,
        WadFileScanResult ScanResult,
        int SkippedEntries);

    #endregion

    #region Filtering and Sorting

    private void ApplyFilterAndSort()
    {
        var searchText = SearchTextBox?.Text?.Trim() ?? "";

        IEnumerable<WadFileEntry> results = _allWads;

        // Text search
        if (!string.IsNullOrEmpty(searchText))
        {
            results = results.Where(w =>
                TextMatchUtility.IsLooseSearchMatch(w.NameWithExtension, searchText) ||
                TextMatchUtility.IsLooseSearchMatch(w.FullPath, searchText));
        }

        IOrderedEnumerable<WadFileEntry>? ordered = null;
        foreach (var descriptor in _sortDescriptors)
        {
            ordered = descriptor.ColumnKey switch
            {
                "name" => ApplyWadSort(results, ordered, wad => wad.Name,
                    descriptor.Ascending, StringComparer.OrdinalIgnoreCase),
                "cached" => ApplyWadSort(results, ordered, wad => wad.IsHashCached,
                    descriptor.Ascending),
                "size" => ApplyWadSort(results, ordered, wad => wad.Size,
                    descriptor.Ascending),
                "modified" => ApplyWadSort(results, ordered, wad => wad.Modified,
                    descriptor.Ascending),
                "path" => ApplyWadSort(results, ordered, wad => wad.FullPath,
                    descriptor.Ascending, StringComparer.OrdinalIgnoreCase),
                "hash" => ApplyWadSort(results, ordered, wad => wad.CachedHash ?? string.Empty,
                    descriptor.Ascending, StringComparer.OrdinalIgnoreCase),
                _ => ordered
            };
        }
        if (ordered != null)
            results = ordered;

        WadListView.UpdateRows(_filteredWads, results,
            wad => OperatingSystem.IsWindows() ? wad.FullPath.ToUpperInvariant() : wad.FullPath);
    }

    private static IOrderedEnumerable<WadFileEntry> ApplyWadSort<TKey>(
        IEnumerable<WadFileEntry> source,
        IOrderedEnumerable<WadFileEntry>? ordered,
        Func<WadFileEntry, TKey> selector,
        bool ascending,
        IComparer<TKey>? comparer = null)
    {
        if (ordered == null)
        {
            return ascending
                ? source.OrderBy(selector, comparer)
                : source.OrderByDescending(selector, comparer);
        }

        return ascending
            ? ordered.ThenBy(selector, comparer)
            : ordered.ThenByDescending(selector, comparer);
    }

    #endregion

    private void WadListView_SortRequested(object? sender, ListViewSortEventArgs e)
    {
        _sortDescriptors.Clear();
        _sortDescriptors.AddRange(e.SortDescriptors);
        ApplyFilterAndSort();
    }

    #region Row Interaction

    private async void OnWadRowDoubleTapped(object? sender, ListViewRowEventArgs e)
    {
        if (e.DataContext is WadFileEntry wad)
            await LocateWadFileAsync(wad);
    }

    #endregion

    #region Stats

    private void UpdateStats()
    {
        var count = _filteredWads.Count;
        var totalSize = _filteredWads.Sum(w => w.Size);

        CountLabel.Text = $"{count} files";
        TotalSizeLabel.Text = FormatSize(totalSize);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:N1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):N1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):N2} GB";
    }

    #endregion

    #region Toolbar Handlers

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        await ScanWadsAsync();
    }

    private async void CacheAllHashesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isScanning || _allWads.Count == 0)
            return;

        if (!WadHashCacheService.Instance.IsEnabled)
        {
            StatusLabel.Text = "Enable WAD hash caching in Preferences before caching files.";
            return;
        }

        var dialog = new WadHashCacheDialog(_allWads.Select(wad => wad.FullPath));
        await dialog.ShowDialog(this);

        var hashCache = WadHashCacheService.Instance;
        foreach (var wad in _allWads)
        {
            wad.SetHashCachingExcluded(hashCache.IsHashCachingExcluded(wad.FullPath));
            wad.SetCachedHash(hashCache.TryGetCachedHash(wad.FullPath));
        }

        ApplyFilterAndSort();
        UpdateStats();
        UpdateActionAvailability();
    }

    private static Control CreateCachedStatusCell()
    {
        var text = new TextBlock
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(WadFileEntry.CachedMarker)));
        text.Bind(TextBlock.ForegroundProperty, new Binding(nameof(WadFileEntry.CachedMarkerColor)));
        return text;
    }

    private async void LocateFileMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var selected = GetSingleSelectedWad();
        if (selected != null)
            await LocateWadFileAsync(selected);
    }

    private async void CalculateHashMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        await CalculateSelectedHashAsync();
    }

    private async void ToggleHashCachingMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        await ToggleSelectedHashCachingAsync();
    }

    private async Task CalculateSelectedHashAsync()
    {
        var selected = GetSingleSelectedWad();
        if (_isScanning || _isRefreshingSelectedHash || selected == null)
            return;

        var hashCache = WadHashCacheService.Instance;
        if (!hashCache.IsEnabled)
        {
            StatusLabel.Text = "Enable WAD hash caching in Preferences before calculating a cached MD5.";
            return;
        }

        if (hashCache.IsHashCachingExcluded(selected.FullPath))
        {
            StatusLabel.Text = $"{selected.NameWithExtension} is excluded from hash caching. Allow caching first to calculate its MD5.";
            return;
        }

        var isRecalculation = !string.IsNullOrWhiteSpace(hashCache.TryGetCachedHash(selected.FullPath));
        _isRefreshingSelectedHash = true;
        StatusLabel.Text = isRecalculation
            ? $"Recalculating cached MD5 for {selected.NameWithExtension}..."
            : $"Calculating MD5 for {selected.NameWithExtension}...";
        UpdateActionAvailability();

        try
        {
            var result = isRecalculation
                ? await hashCache.RefreshHashAsync(
                    selected.FullPath,
                    progress: null,
                    CancellationToken.None)
                : await hashCache.GetHashAsync(
                    selected.FullPath,
                    progress: null,
                    CancellationToken.None);
            var cachedHash = hashCache.TryGetCachedHash(selected.FullPath);
            selected.SetCachedHash(cachedHash);

            if (!result.IsSuccess)
            {
                StatusLabel.Text = $"Could not calculate {selected.NameWithExtension}: "
                    + (result.ErrorMessage ?? "unknown read error");
                return;
            }

            ApplyFilterAndSort();
            UpdateStats();
            StatusLabel.Text = cachedHash == null
                ? $"{selected.NameWithExtension} changed while its MD5 was calculated; no cache entry was saved."
                : isRecalculation
                    ? $"Recalculated cached MD5 for {selected.NameWithExtension}."
                    : $"Calculated and cached MD5 for {selected.NameWithExtension}.";
        }
        catch (Exception ex)
        {
            selected.SetCachedHash(hashCache.TryGetCachedHash(selected.FullPath));
            StatusLabel.Text = $"Could not calculate {selected.NameWithExtension}: {ex.Message}";
        }
        finally
        {
            _isRefreshingSelectedHash = false;
            UpdateActionAvailability();
        }
    }

    private async Task LocateWadFileAsync(WadFileEntry wad)
    {
        var result = await FileManagerService.LocateFileAsync(wad.FullPath);
        StatusLabel.Text = result.Succeeded
            ? $"Located {wad.NameWithExtension}."
            : $"Could not locate {wad.NameWithExtension}: {result.ErrorMessage ?? "unknown error"}";
    }

    private void ConfigureWadContextMenu()
    {
        var contextMenu = new ContextMenu();
        WadListView.RowContextMenuOpening += (_, _) => UpdateActionAvailability();

        _locateFileMenuItem = new MenuItem { Header = "_Locate File" };
        _locateFileMenuItem.Click += LocateFileMenuItem_Click;
        contextMenu.Items.Add(_locateFileMenuItem);

        contextMenu.Items.Add(new Separator());

        _calculateHashMenuItem = new MenuItem
        {
            Header = "Calculate _Hash",
            IsEnabled = false
        };
        _calculateHashMenuItem.Click += CalculateHashMenuItem_Click;
        contextMenu.Items.Add(_calculateHashMenuItem);

        _toggleHashCachingMenuItem = new MenuItem
        {
            Header = "_Don't Cache Hash",
            IsEnabled = false
        };
        _toggleHashCachingMenuItem.Click += ToggleHashCachingMenuItem_Click;
        contextMenu.Items.Add(_toggleHashCachingMenuItem);

        contextMenu.Items.Add(new Separator());

        _deleteSelectedMenuItem = new MenuItem { Header = "_Delete Selected" };
        _deleteSelectedMenuItem.Click += DeleteSelected_Click;
        contextMenu.Items.Add(_deleteSelectedMenuItem);

        WadListView.ContextMenu = contextMenu;
    }

    private WadFileEntry? GetSingleSelectedWad()
    {
        var selected = GetSelectedWads();
        return selected.Count == 1 ? selected[0] : null;
    }

    private List<WadFileEntry> GetSelectedWads() =>
        WadListView.SelectedItems.OfType<WadFileEntry>().ToList();

    private async Task ToggleSelectedHashCachingAsync()
    {
        var selected = GetSelectedWads();
        if (_isScanning || _isRefreshingSelectedHash || _isUpdatingHashCacheExclusions || selected.Count == 0)
            return;

        var hashCache = WadHashCacheService.Instance;
        var shouldExclude = selected.Any(wad => !hashCache.IsHashCachingExcluded(wad.FullPath));
        _isUpdatingHashCacheExclusions = true;
        StatusLabel.Text = shouldExclude
            ? "Updating per-file hash cache preference..."
            : "Allowing selected files to be cached...";
        UpdateActionAvailability();

        try
        {
            await hashCache.SetHashCachingExcludedAsync(
                selected.Select(wad => wad.FullPath),
                shouldExclude);

            foreach (var wad in selected)
            {
                wad.SetHashCachingExcluded(hashCache.IsHashCachingExcluded(wad.FullPath));
                wad.SetCachedHash(hashCache.TryGetCachedHash(wad.FullPath));
            }

            ApplyFilterAndSort();
            UpdateStats();
            var fileDescription = selected.Count == 1
                ? selected[0].NameWithExtension
                : $"{selected.Count} selected WAD files";
            StatusLabel.Text = shouldExclude
                ? $"{fileDescription} will not be hash-cached. Server hash verification remains enabled."
                : $"{fileDescription} can be hash-cached again on the next verification or cache pass.";
            _logger.Info(
                $"{(shouldExclude ? "Excluded" : "Allowed")} {selected.Count} WAD file(s) "
                + $"{(shouldExclude ? "from" : "for")} hash caching.");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not update WAD hash-cache preference: {ex.Message}";
            _logger.Warning($"Could not update WAD hash-cache preference: {ex.Message}");
        }
        finally
        {
            _isUpdatingHashCacheExclusions = false;
            UpdateActionAvailability();
        }
    }

    private void UpdateActionAvailability()
    {
        var selected = GetSelectedWads();
        var selectedCount = selected.Count;
        var hasSingleSelection = selectedCount == 1;
        var hasSelection = selectedCount > 0;
        var isBusy = _isScanning || _isRefreshingSelectedHash || _isUpdatingHashCacheExclusions;
        var hashCache = WadHashCacheService.Instance;
        var hashCachingEnabled = hashCache.IsEnabled;
        var selectedFileIsExcluded = hasSingleSelection
            && hashCache.IsHashCachingExcluded(selected[0].FullPath);
        var allSelectedFilesAreExcluded = hasSelection
            && selected.All(wad => hashCache.IsHashCachingExcluded(wad.FullPath));

        RefreshButton.IsEnabled = !isBusy;
        CacheAllHashesButton.IsEnabled = !isBusy && hashCachingEnabled && _allWads.Count > 0;
        if (_locateFileMenuItem != null)
            _locateFileMenuItem.IsEnabled = !isBusy && hasSingleSelection;
        if (_calculateHashMenuItem != null)
        {
            var hasCachedHash = hasSingleSelection
                && !selectedFileIsExcluded
                && !string.IsNullOrWhiteSpace(hashCache.TryGetCachedHash(selected[0].FullPath));
            _calculateHashMenuItem.IsEnabled = !isBusy
                && hashCachingEnabled
                && hasSingleSelection
                && !selectedFileIsExcluded;
            _calculateHashMenuItem.Header = hasCachedHash
                ? "_Recalculate Hash"
                : "Calculate _Hash";
            ToolTip.SetTip(
                _calculateHashMenuItem,
                !hasSingleSelection
                    ? "Select one WAD file first."
                    : !hashCachingEnabled
                        ? "Enable WAD hash caching in Preferences before calculating a cached MD5."
                        : selectedFileIsExcluded
                            ? "Allow hash caching for this file before calculating a cached MD5."
                            : hasCachedHash
                                ? "Calculate this file's MD5 again and replace its cached value."
                                : "Calculate this file's MD5 and save it in the local hash cache.");
        }
        if (_toggleHashCachingMenuItem != null)
        {
            _toggleHashCachingMenuItem.IsEnabled = !isBusy && hasSelection;
            _toggleHashCachingMenuItem.Header = allSelectedFilesAreExcluded
                ? hasSingleSelection ? "Allow Hash _Caching" : "Allow Hash Caching for _Selected Files"
                : hasSingleSelection ? "_Don't Cache Hash" : "_Don't Cache Selected Hashes";
            ToolTip.SetTip(
                _toggleHashCachingMenuItem,
                allSelectedFilesAreExcluded
                    ? "Allow these files to receive a fresh cached MD5 on a later verification or cache pass."
                    : "Do not reuse or store cached MD5 values for these files. Server hash verification still runs normally.");
        }
        if (_deleteSelectedMenuItem != null)
            _deleteSelectedMenuItem.IsEnabled = !isBusy && hasSelection;
        CancelScanButton.IsEnabled = _isScanning
            && _scanCancellation is { IsCancellationRequested: false };
    }

    private void SearchTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilterAndSort();
        UpdateStats();
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = "";
    }

    #endregion

    #region Delete / Copy / Close

    private async void DeleteSelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = WadListView.SelectedItems.OfType<WadFileEntry>().ToList();
        if (selected.Count == 0) return;

        var msgBox = new Window
        {
            Title = "Confirm Delete",
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 15,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Delete {selected.Count} file(s)? This cannot be undone.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children =
                        {
                            new Button { Content = "Delete", Tag = "yes" },
                            new Button { Content = "Cancel", Tag = "no" }
                        }
                    }
                }
            }
        };

        bool confirmed = false;
        foreach (var btn in ((StackPanel)((StackPanel)msgBox.Content).Children[1]).Children.OfType<Button>())
        {
            btn.Click += (s, _) =>
            {
                confirmed = ((Button)s!).Tag?.ToString() == "yes";
                msgBox.Close();
            };
        }

        await msgBox.ShowDialog(this);

        if (!confirmed) return;

        var deleted = 0;
        foreach (var wad in selected)
        {
            try
            {
                File.Delete(wad.FullPath);
                _allWads.Remove(wad);
                _filteredWads.Remove(wad);
                deleted++;
            }
            catch { }
        }

        WadListView.ClearSelection();

        StatusLabel.Text = $"Deleted {deleted} file(s)";
        UpdateStats();
        UpdateActionAvailability();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion
}
