using System.ComponentModel;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using ZScape.Controls;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.Views;

/// <summary>
/// Background, cancellable pre-cache operation for local WAD MD5 values.
/// Hashing stays off the UI thread; this window only receives throttled status
/// updates from <see cref="WadHashCacheService"/>.
/// </summary>
public partial class WadHashCacheDialog : Window
{
    private readonly IReadOnlyList<string> _filePaths;
    private readonly Dictionary<string, WadHashCacheFileItem> _itemsByPath;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _isCaching;
    private bool _hasStarted;

    public ObservableCollection<WadHashCacheFileItem> Files { get; } = [];

    public WadHashCacheDialog()
        : this([])
    {
    }

    public WadHashCacheDialog(IEnumerable<string> filePaths)
    {
        InitializeComponent();

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _filePaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(pathComparer)
            .OrderBy(path => path, pathComparer)
            .ToList();
        _itemsByPath = new Dictionary<string, WadHashCacheFileItem>(pathComparer);

        foreach (var path in _filePaths)
        {
            var item = new WadHashCacheFileItem(path);
            Files.Add(item);
            _itemsByPath[path] = item;
        }

        ConfigureList();
        SummaryText.Text = _filePaths.Count == 0
            ? "No local WAD files were supplied."
            : $"Preparing {_filePaths.Count} local WAD file{(_filePaths.Count == 1 ? string.Empty : "s")}. Existing valid hashes are reused.";
        OverallProgressBar.Value = 0;

        Loaded += async (_, _) => await StartCachingAsync();
        Closing += (_, _) =>
        {
            if (_isCaching)
                _cancellation.Cancel();
        };
    }

    private void ConfigureList()
    {
        CacheListView.SelectionMode = ListViewSelectionMode.None;
        CacheListView.RowHeight = 24;
        CacheListView.SuppressHandCursor = true;
        CacheListView.AddColumn(new ListViewColumn
        {
            Key = "status",
            Header = "Status",
            Width = 118,
            MinWidth = 10,
            CellContentFactory = CreateStatusCell,
            CanSort = false,
            CanUserHide = false
        });
        CacheListView.AddColumn(new ListViewColumn
        {
            Key = "file",
            Header = "File",
            Width = 250,
            MinWidth = 10,
            BindingPath = nameof(WadHashCacheFileItem.FileName),
            TextTrimming = TextTrimming.CharacterEllipsis,
            CanSort = false,
            CanUserHide = false
        });
        CacheListView.AddColumn(new ListViewColumn
        {
            Key = "hash",
            Header = "MD5",
            Width = 275,
            MinWidth = 10,
            BindingPath = nameof(WadHashCacheFileItem.Hash),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.Gray,
            CanSort = false
        });
        CacheListView.Build(ListViewOverflowMode.AutoScroll);
        CacheListView.ItemsSource = Files;
    }

    private static Control CreateStatusCell()
    {
        var text = new TextBlock
        {
            Padding = new Thickness(6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(WadHashCacheFileItem.Status)));
        text.Bind(TextBlock.ForegroundProperty, new Binding(nameof(WadHashCacheFileItem.StatusColor)));
        return text;
    }

    private async Task StartCachingAsync()
    {
        if (_hasStarted)
            return;

        _hasStarted = true;
        if (_filePaths.Count == 0)
        {
            Complete("There were no local WAD files to cache.");
            return;
        }

        if (!WadHashCacheService.Instance.IsEnabled)
        {
            Complete("WAD hash caching is disabled in Preferences.");
            return;
        }

        _isCaching = true;
        StopButton.IsEnabled = true;
        CloseButton.IsEnabled = false;
        StatusText.Text = "Caching hashes in the background...";

        var progress = new Progress<WadHashCacheProgress>(ApplyProgress);
        try
        {
            var summary = await WadHashCacheService.Instance.CacheFilesAsync(
                _filePaths,
                progress,
                _cancellation.Token);

            SummaryText.Text = $"Cached {summary.NewlyCachedCount} file{(summary.NewlyCachedCount == 1 ? string.Empty : "s")}; "
                + $"{summary.AlreadyCachedCount} already valid; {summary.FailedCount} failed.";
            OverallProgressBar.Value = 100;
            Complete(summary.FailedCount == 0
                ? "Hash cache is up to date."
                : "Hash caching completed with failures shown above.");
        }
        catch (OperationCanceledException)
        {
            foreach (var item in Files.Where(item => item.Status == "Queued" || item.Status == "Hashing"))
                item.SetStatus("Stopped", Brushes.Gray);

            Complete("Caching stopped. Completed hashes were kept.");
        }
        catch (Exception ex)
        {
            Complete($"Hash caching failed: {ex.Message}");
        }
    }

    private void ApplyProgress(WadHashCacheProgress progress)
    {
        if (!_itemsByPath.TryGetValue(progress.FilePath, out var item))
            return;

        var (status, color) = progress.Stage switch
        {
            WadHashCacheProgressStage.Hashing => ("Hashing", Brushes.DodgerBlue),
            WadHashCacheProgressStage.Cached => ("Cached", Brushes.LightGreen),
            WadHashCacheProgressStage.AlreadyCached => ("Already cached", Brushes.LightGreen),
            WadHashCacheProgressStage.Failed => ("Failed", Brushes.Tomato),
            _ => ("Queued", Brushes.Gray)
        };

        item.SetStatus(status, color, progress.Hash);
        var current = progress.TotalBytes > 0
            ? $"{progress.FileName} — {FormatUtils.FormatBytes(progress.BytesProcessed)} / {FormatUtils.FormatBytes(progress.TotalBytes)}"
            : progress.FileName;
        CurrentFileText.Text = current;
        ToolTip.SetTip(CurrentFileText, progress.FilePath);

        var inFileProgress = progress.TotalBytes > 0
            ? Math.Clamp((double)progress.BytesProcessed / progress.TotalBytes, 0, 1)
            : 0;
        var totalProgress = progress.TotalFiles > 0
            ? (progress.CompletedFiles + inFileProgress) * 100d / progress.TotalFiles
            : 0;
        OverallProgressBar.Value = Math.Clamp(totalProgress, 0, 100);
        StatusText.Text = $"{Math.Min(progress.CompletedFiles, progress.TotalFiles)}/{progress.TotalFiles} files";
    }

    private void Complete(string status)
    {
        _isCaching = false;
        StopButton.IsEnabled = false;
        CloseButton.IsEnabled = true;
        StatusText.Text = status;
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_isCaching)
            return;

        StopButton.IsEnabled = false;
        StopButton.Content = "Stopping...";
        StatusText.Text = "Stopping after the current read...";
        _cancellation.Cancel();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private static string? NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>One visible row in the background WAD hash-cache window.</summary>
public sealed class WadHashCacheFileItem : INotifyPropertyChanged
{
    private string _status = "Queued";
    private IBrush _statusColor = Brushes.Gray;
    private string _hash = string.Empty;

    public string FullPath { get; }
    public string FileName => Path.GetFileName(FullPath);

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged(nameof(Status));
        }
    }

    public IBrush StatusColor
    {
        get => _statusColor;
        private set
        {
            if (Equals(_statusColor, value))
                return;
            _statusColor = value;
            OnPropertyChanged(nameof(StatusColor));
        }
    }

    public string Hash
    {
        get => _hash;
        private set
        {
            if (_hash == value)
                return;
            _hash = value;
            OnPropertyChanged(nameof(Hash));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WadHashCacheFileItem(string fullPath)
    {
        FullPath = fullPath;
    }

    public void SetStatus(string status, IBrush color, string? hash = null)
    {
        Status = status;
        StatusColor = color;
        if (hash != null)
            Hash = hash;
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
