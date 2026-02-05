using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ZScape.Models;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.Views;

/// <summary>
/// Dialog for downloading missing WAD files.
/// Matches the WinForms WadDownloadDialog implementation.
/// </summary>
public partial class WadDownloadDialog : Window
{
    private readonly List<WadDownloadTask> _tasks;
    private readonly string _downloadPath;
    private readonly WadDownloader _downloader;
    private readonly CancellationTokenSource _cts = new();
    private readonly ObservableCollection<WadDisplayItem> _displayItems = new();
    private readonly ObservableCollection<LogEntry> _logEntries = new();
    private readonly bool _isServerJoinContext;
    
    private int _completedCount;
    private int _failedCount;

    public WadDownloadDialog() : this(new List<WadInfo>(), "", new WadDownloader()) { }

    /// <summary>
    /// Creates a new WAD download dialog.
    /// </summary>
    /// <param name="missingWads">List of WADs to download.</param>
    /// <param name="downloadPath">Path where WADs should be saved.</param>
    /// <param name="downloader">The downloader instance to use.</param>
    /// <param name="isServerJoinContext">True if downloading WADs as part of joining a server (enables auto-close behavior).</param>
    public WadDownloadDialog(List<WadInfo> missingWads, string downloadPath, WadDownloader downloader, bool isServerJoinContext = false)
    {
        _isServerJoinContext = isServerJoinContext;
        _downloadPath = downloadPath;
        _downloader = downloader;
        _tasks = missingWads.Select(w => new WadDownloadTask { Wad = w }).ToList();
        
        InitializeComponent();
        
        WadListControl.ItemsSource = _displayItems;
        LogControl.ItemsSource = _logEntries;
        
        // Set up log item template
        LogControl.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LogEntry>((entry, _) =>
        {
            var tb = new TextBlock
            {
                Text = entry?.FullText ?? "",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12
            };
            if (entry != null)
                tb.Foreground = entry.TextColor;
            return tb;
        });
        
        // Populate display items from tasks
        for (int i = 0; i < _tasks.Count; i++)
        {
            var task = _tasks[i];
            _displayItems.Add(new WadDisplayItem(task, i));
        }
        
        StatusLabel.Text = $"Downloading {_tasks.Count} file(s)...";
        OverallProgressBar.Maximum = _tasks.Count > 0 ? _tasks.Count : 1;
        
        // Subscribe to downloader events
        _downloader.ProgressUpdated += OnProgressUpdated;
        _downloader.DownloadCompleted += OnDownloadCompleted;
        _downloader.LogMessage += OnLogMessage;
        
        // Start downloads when window opens
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        
        // Handle Escape key
        KeyDown += OnDialogKeyDown;
    }
    
    private void OnDialogKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            if (CancelButton.IsEnabled)
            {
                CancelButton_Click(sender, e);
            }
            else
            {
                Close();
            }
            e.Handled = true;
        }
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        await StartDownloadsAsync();
    }

    private async Task StartDownloadsAsync()
    {
        try
        {
            await _downloader.DownloadWadsAsync(_tasks, _downloadPath, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log(WadDownloader.LogLevel.Info, "Download cancelled by user.");
        }
        catch (Exception ex)
        {
            Log(WadDownloader.LogLevel.Error, $"Error: {ex.Message}");
        }
        finally
        {
            UpdateFinalStatus();
        }
    }

    private void OnProgressUpdated(object? sender, WadDownloadTask task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTaskDisplay(task);
            SortByStatus();
        });
    }

    private void OnDownloadCompleted(object? sender, WadDownloadTask task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (task.Status == WadDownloadStatus.Completed)
            {
                _completedCount++;
                var sizeInfo = task.TotalBytes > 0 ? $" ({FormatUtils.FormatBytes(task.TotalBytes)})" : "";
                var threadInfo = task.ThreadCount > 1 ? $" [{task.ThreadCount} threads]" : "";
                Log(WadDownloader.LogLevel.Success, $"Downloaded: {task.Wad.FileName}{sizeInfo}{threadInfo}");
            }
            else if (task.Status == WadDownloadStatus.Failed)
            {
                _failedCount++;
                Log(WadDownloader.LogLevel.Error, $"{task.Wad.FileName}: {task.ErrorMessage}");
            }
            else if (task.Status == WadDownloadStatus.Cancelled)
            {
                Log(WadDownloader.LogLevel.Warning, $"Cancelled: {task.Wad.FileName}");
            }
            
            OverallProgressBar.Value = Math.Min(_completedCount + _failedCount, OverallProgressBar.Maximum);
            UpdateTaskDisplay(task);
            SortByStatus();
        });
    }

    private void UpdateTaskDisplay(WadDownloadTask task)
    {
        var item = _displayItems.FirstOrDefault(d => d.Task == task);
        if (item != null)
        {
            item.Refresh();
        }
    }

    private void SortByStatus()
    {
        // Get sorted order
        var sorted = _displayItems.OrderBy(d => GetStatusPriority(d.Task.Status)).ThenBy(d => d.OriginalIndex).ToList();
        
        // Check if order changed
        bool changed = false;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (_displayItems[i] != sorted[i])
            {
                changed = true;
                break;
            }
        }
        
        if (!changed) return;
        
        // Reorder by moving items
        for (int i = 0; i < sorted.Count; i++)
        {
            var currentIndex = _displayItems.IndexOf(sorted[i]);
            if (currentIndex != i)
            {
                _displayItems.Move(currentIndex, i);
            }
        }
        
        // Update row backgrounds after reorder
        for (int i = 0; i < _displayItems.Count; i++)
        {
            _displayItems[i].DisplayIndex = i;
        }
    }

    private static int GetStatusPriority(WadDownloadStatus status) => status switch
    {
        WadDownloadStatus.Failed => 0,
        WadDownloadStatus.Downloading => 1,
        WadDownloadStatus.Queued => 2,
        WadDownloadStatus.Searching => 3,
        WadDownloadStatus.Pending => 4,
        WadDownloadStatus.Completed => 5,
        WadDownloadStatus.AlreadyExists => 6,
        WadDownloadStatus.Cancelled => 7,
        _ => 99
    };

    private async void UpdateFinalStatus()
    {
        StatusLabel.Text = $"Complete: {_completedCount} downloaded, {_failedCount} failed";
        CancelButton.IsEnabled = false;
        CloseButton.IsEnabled = true;
        CloseButton.Focus();
        
        // Only apply auto-close behavior if in server join context
        if (!_isServerJoinContext) return;
        
        var behavior = SettingsService.Instance.Settings.DownloadDialogBehavior;
        var allSucceeded = _failedCount == 0;
        
        switch (behavior)
        {
            case DownloadDialogBehavior.StayOpen:
                // Do nothing, user closes manually
                break;
                
            case DownloadDialogBehavior.CloseOnSuccess:
                if (allSucceeded) Close();
                break;
                
            case DownloadDialogBehavior.CloseOnSuccessIfFocused:
                if (allSucceeded && IsActive)
                    Close();
                break;
                
            case DownloadDialogBehavior.CloseOnSuccessAfterDelay:
                if (allSucceeded)
                {
                    StatusLabel.Text += " - Closing in 3 seconds...";
                    await Task.Delay(3000);
                    Close();
                }
                break;
                
            case DownloadDialogBehavior.AlwaysClose:
                Close();
                break;
        }
    }

    private void OnLogMessage(object? sender, (WadDownloader.LogLevel Level, string Message) e)
    {
        Dispatcher.UIThread.Post(() => Log(e.Level, e.Message));
    }

    private void Log(WadDownloader.LogLevel level, string message)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            Level = level,
            Message = message
        };
        _logEntries.Add(entry);
        
        // Scroll to bottom
        Dispatcher.UIThread.Post(() =>
        {
            LogScrollViewer?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        StatusLabel.Text = "Cancelling...";
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _cts.Cancel();
        _downloader.ProgressUpdated -= OnProgressUpdated;
        _downloader.DownloadCompleted -= OnDownloadCompleted;
        _downloader.LogMessage -= OnLogMessage;
    }

    /// <summary>
    /// Display item wrapper for WadDownloadTask with INotifyPropertyChanged.
    /// </summary>
    public class WadDisplayItem : INotifyPropertyChanged
    {
        private static readonly IBrush EvenRowBrush = new SolidColorBrush(Color.Parse("#1E1E1E"));
        private static readonly IBrush OddRowBrush = new SolidColorBrush(Color.Parse("#252526"));
        private static readonly IBrush SuccessColor = new SolidColorBrush(Color.Parse("#4EC9B0"));
        private static readonly IBrush ErrorColor = new SolidColorBrush(Color.Parse("#F14C4C"));
        private static readonly IBrush WarningColor = new SolidColorBrush(Color.Parse("#CCA700"));
        private static readonly IBrush AccentColor = new SolidColorBrush(Color.Parse("#0078D4"));
        private static readonly IBrush TextSecondary = new SolidColorBrush(Color.Parse("#9D9D9D"));
        private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#CCCCCC"));

        public WadDownloadTask Task { get; }
        public int OriginalIndex { get; }
        
        private int _displayIndex;
        public int DisplayIndex
        {
            get => _displayIndex;
            set { _displayIndex = value; OnPropertyChanged(nameof(RowBackground)); }
        }

        public WadDisplayItem(WadDownloadTask task, int index)
        {
            Task = task;
            OriginalIndex = index;
            _displayIndex = index;
        }

        public string FileName => Task.Wad.FileName;
        
        public string StatusDisplay => Task.Status.ToString();
        
        public string ProgressText => Task.ProgressText;
        
        public string SpeedText => Task.SpeedText;
        
        public string ThreadsDisplay => Task.ThreadCount > 1 ? Task.ThreadCount.ToString() : "1";
        
        public string SourceHost
        {
            get
            {
                if (string.IsNullOrEmpty(Task.SourceUrl)) return "";
                try { return new Uri(Task.SourceUrl).Host; }
                catch { return Task.SourceUrl; }
            }
        }

        public IBrush RowBackground => DisplayIndex % 2 == 0 ? EvenRowBrush : OddRowBrush;

        public IBrush StatusColor => Task.Status switch
        {
            WadDownloadStatus.Completed or WadDownloadStatus.AlreadyExists => SuccessColor,
            WadDownloadStatus.Failed or WadDownloadStatus.Cancelled => ErrorColor,
            WadDownloadStatus.Downloading => AccentColor,
            WadDownloadStatus.Searching => WarningColor,
            WadDownloadStatus.Queued => TextSecondary,
            _ => TextPrimary
        };

        public void Refresh()
        {
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(ThreadsDisplay));
            OnPropertyChanged(nameof(SourceHost));
            OnPropertyChanged(nameof(StatusColor));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// Log entry with color based on log level.
    /// </summary>
    public class LogEntry : INotifyPropertyChanged
    {
        private static readonly IBrush VerboseColor = new SolidColorBrush(Color.Parse("#9D9D9D"));
        private static readonly IBrush InfoColor = new SolidColorBrush(Color.Parse("#CCCCCC"));
        private static readonly IBrush WarningColor = new SolidColorBrush(Color.Parse("#CCA700"));
        private static readonly IBrush ErrorColor = new SolidColorBrush(Color.Parse("#F14C4C"));
        private static readonly IBrush SuccessColor = new SolidColorBrush(Color.Parse("#4EC9B0"));

        public string Timestamp { get; set; } = "";
        public WadDownloader.LogLevel Level { get; set; }
        public string Message { get; set; } = "";

        public string Prefix => Level switch
        {
            WadDownloader.LogLevel.Verbose => "[VRB]",
            WadDownloader.LogLevel.Info => "[INF]",
            WadDownloader.LogLevel.Warning => "[WRN]",
            WadDownloader.LogLevel.Error => "[ERR]",
            WadDownloader.LogLevel.Success => "[OK]",
            _ => "[???]"
        };

        public IBrush TextColor => Level switch
        {
            WadDownloader.LogLevel.Verbose => VerboseColor,
            WadDownloader.LogLevel.Info => InfoColor,
            WadDownloader.LogLevel.Warning => WarningColor,
            WadDownloader.LogLevel.Error => ErrorColor,
            WadDownloader.LogLevel.Success => SuccessColor,
            _ => InfoColor
        };

        public string FullText => $"[{Timestamp}] {Prefix} {Message}";

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
