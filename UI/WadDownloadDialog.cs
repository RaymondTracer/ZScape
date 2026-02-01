using ZScape.Models;
using ZScape.Services;
using ZScape.Utilities;
using System.ComponentModel;

namespace ZScape.UI;

/// <summary>
/// Dialog for downloading missing WAD files.
/// </summary>
public class WadDownloadDialog : Form
{
    private readonly List<WadDownloadTask> _tasks;
    private readonly string _downloadPath;
    private readonly WadDownloader _downloader;
    private readonly CancellationTokenSource _cts = new();
    
    private DataGridView _wadGrid = null!;
    private ProgressBar _overallProgressBar = null!;
    private Label _statusLabel = null!;
    private Button _cancelButton = null!;
    private Button _closeButton = null!;
    private RichTextBox _logTextBox = null!;
    private int _completedCount;
    private int _failedCount;
    private readonly bool _autoCloseOnComplete;
    
    public WadDownloadDialog(List<WadInfo> missingWads, string downloadPath, WadDownloader downloader, bool autoCloseOnComplete = false)
    {
        _autoCloseOnComplete = autoCloseOnComplete;
        _downloadPath = downloadPath;
        _downloader = downloader;
        _tasks = missingWads.Select(w => new WadDownloadTask { Wad = w }).ToList();
        
        InitializeComponent();
        ApplyDarkTheme();
        DarkModeHelper.ApplyDarkTitleBar(this);
        PopulateList();
        
        _downloader.ProgressUpdated += OnProgressUpdated;
        _downloader.DownloadCompleted += OnDownloadCompleted;
        _downloader.LogMessage += OnLogMessage;
    }
    
    private void InitializeComponent()
    {
        Text = "WAD Download";
        Size = new Size(700, 500);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(500, 400);
        
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(10)
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // Status
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); // List
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // Progress
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); // Log
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); // Buttons
        
        // Status label
        _statusLabel = new Label
        {
            Text = $"Downloading {_tasks.Count} file(s)...",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        mainPanel.Controls.Add(_statusLabel, 0, 0);
        
        // WAD grid (DataGridView for consistent styling with server list)
        _wadGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 28
        };
        _wadGrid.Columns.Add("File", "File");
        _wadGrid.Columns.Add("Status", "Status");
        _wadGrid.Columns.Add("Progress", "Progress");
        _wadGrid.Columns.Add("Speed", "Speed");
        _wadGrid.Columns.Add("Threads", "Threads");
        _wadGrid.Columns.Add("Source", "Source");
        _wadGrid.Columns["File"]!.Width = 180;
        _wadGrid.Columns["Status"]!.Width = 90;
        _wadGrid.Columns["Progress"]!.Width = 140;
        _wadGrid.Columns["Speed"]!.Width = 90;
        _wadGrid.Columns["Threads"]!.Width = 60;
        _wadGrid.Columns["Source"]!.Width = 120;
        _wadGrid.Columns["Source"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _wadGrid.CellMouseDown += WadGrid_CellMouseDown;
        mainPanel.Controls.Add(_wadGrid, 0, 1);
        
        // Overall progress bar
        _overallProgressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = _tasks.Count > 0 ? _tasks.Count : 1
        };
        mainPanel.Controls.Add(_overallProgressBar, 0, 2);
        
        // Log text box
        _logTextBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Consolas", 9f)
        };
        mainPanel.Controls.Add(_logTextBox, 0, 3);
        
        // Button panel
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        
        _closeButton = new Button
        {
            Text = "Close",
            Width = 80,
            Enabled = false,
            DialogResult = DialogResult.OK
        };
        _closeButton.Click += (_, _) => Close();
        
        _cancelButton = new Button
        {
            Text = "Cancel",
            Width = 80
        };
        _cancelButton.Click += OnCancelClick;
        
        buttonPanel.Controls.Add(_closeButton);
        buttonPanel.Controls.Add(_cancelButton);
        mainPanel.Controls.Add(buttonPanel, 0, 4);
        
        Controls.Add(mainPanel);
        
        Load += OnFormLoad;
        FormClosing += OnFormClosing;
    }
    
    private void ApplyDarkTheme()
    {
        BackColor = DarkTheme.PrimaryBackground;
        ForeColor = DarkTheme.TextPrimary;
        
        _statusLabel.ForeColor = DarkTheme.TextPrimary;
        
        DarkTheme.ApplyToDataGridView(_wadGrid);
        
        _logTextBox.BackColor = DarkTheme.SecondaryBackground;
        _logTextBox.ForeColor = DarkTheme.TextPrimary;
        
        _cancelButton.BackColor = DarkTheme.SecondaryBackground;
        _cancelButton.ForeColor = DarkTheme.TextPrimary;
        _cancelButton.FlatStyle = FlatStyle.Flat;
        
        _closeButton.BackColor = DarkTheme.AccentColor;
        _closeButton.ForeColor = Color.White;
        _closeButton.FlatStyle = FlatStyle.Flat;
    }
    
    private void PopulateList()
    {
        _wadGrid.Rows.Clear();
        for (int i = 0; i < _tasks.Count; i++)
        {
            var task = _tasks[i];
            var rowIndex = _wadGrid.Rows.Add(
                task.Wad.FileName,
                "Pending",
                "",
                "",
                "1",
                ""
            );
            _wadGrid.Rows[rowIndex].Tag = task;
        }
    }
    
    private async void OnFormLoad(object? sender, EventArgs e)
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
            Log("Download cancelled by user.");
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
        finally
        {
            UpdateFinalStatus();
        }
    }
    
    private void OnProgressUpdated(object? sender, WadDownloadTask task)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnProgressUpdated(sender, task));
            return;
        }
        
        UpdateTaskInGrid(task);
    }
    
    private void OnDownloadCompleted(object? sender, WadDownloadTask task)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnDownloadCompleted(sender, task));
            return;
        }
        
        if (task.Status == WadDownloadStatus.Completed)
        {
            _completedCount++;
            var sizeInfo = task.TotalBytes > 0 ? $" ({FormatBytes(task.TotalBytes)})" : "";
            var threadInfo = task.ThreadCount > 1 ? $" [{task.ThreadCount} threads]" : "";
            Log($"[OK] Downloaded: {task.Wad.FileName}{sizeInfo}{threadInfo}");
        }
        else if (task.Status == WadDownloadStatus.Failed)
        {
            _failedCount++;
            Log($"[FAIL] {task.Wad.FileName}: {task.ErrorMessage}");
        }
        else if (task.Status == WadDownloadStatus.Cancelled)
        {
            Log($"[CANCEL] {task.Wad.FileName}");
        }
        
        _overallProgressBar.Value = Math.Min(_completedCount + _failedCount, _overallProgressBar.Maximum);
        UpdateTaskInGrid(task);
    }
    
    private void UpdateTaskInGrid(WadDownloadTask task)
    {
        foreach (DataGridViewRow row in _wadGrid.Rows)
        {
            if (row.Tag == task)
            {
                row.Cells["Status"].Value = task.Status.ToString();
                row.Cells["Progress"].Value = task.ProgressText;
                row.Cells["Speed"].Value = task.SpeedText;
                row.Cells["Threads"].Value = task.ThreadCount > 1 ? task.ThreadCount.ToString() : "1";
                try { row.Cells["Source"].Value = !string.IsNullOrEmpty(task.SourceUrl) ? new Uri(task.SourceUrl).Host : ""; }
                catch { row.Cells["Source"].Value = task.SourceUrl ?? ""; }
                
                row.DefaultCellStyle.ForeColor = task.Status switch
                {
                    WadDownloadStatus.Completed => DarkTheme.SuccessColor,
                    WadDownloadStatus.Failed => DarkTheme.ErrorColor,
                    WadDownloadStatus.Downloading => DarkTheme.AccentColor,
                    WadDownloadStatus.Searching => DarkTheme.WarningColor,
                    WadDownloadStatus.Queued => DarkTheme.TextSecondary,
                    _ => DarkTheme.TextPrimary
                };
                break;
            }
        }
        SortGridByStatus();
    }
    
    private void SortGridByStatus()
    {
        if (_wadGrid.Rows.Count == 0) return;
        var selectedTag = _wadGrid.CurrentRow?.Tag;
        var firstVisible = _wadGrid.FirstDisplayedScrollingRowIndex;
        
        var rows = _wadGrid.Rows.Cast<DataGridViewRow>().Select((r, i) => (Row: r, Idx: i)).ToList();
        var origOrder = rows.Select(r => r.Row.Tag).ToList();
        
        rows.Sort((a, b) =>
        {
            var tA = a.Row.Tag as WadDownloadTask;
            var tB = b.Row.Tag as WadDownloadTask;
            if (tA == null || tB == null) return 0;
            var sc = GetStatusPriority(tA.Status).CompareTo(GetStatusPriority(tB.Status));
            return sc != 0 ? sc : a.Idx.CompareTo(b.Idx);
        });
        
        if (origOrder.SequenceEqual(rows.Select(r => r.Row.Tag))) return;
        
        _wadGrid.SuspendLayout();
        var data = rows.Select(r => new { r.Row.Tag, Cells = r.Row.Cells.Cast<DataGridViewCell>().Select(c => (object)(c.Value ?? "")).ToArray(), Style = r.Row.DefaultCellStyle.Clone() }).ToList();
        _wadGrid.Rows.Clear();
        foreach (var d in data) { var idx = _wadGrid.Rows.Add(d.Cells); _wadGrid.Rows[idx].Tag = d.Tag; _wadGrid.Rows[idx].DefaultCellStyle = d.Style; }
        if (selectedTag != null) foreach (DataGridViewRow r in _wadGrid.Rows) if (r.Tag == selectedTag) { r.Selected = true; break; }
        if (firstVisible >= 0 && firstVisible < _wadGrid.Rows.Count) _wadGrid.FirstDisplayedScrollingRowIndex = firstVisible;
        _wadGrid.ResumeLayout();
    }
    
    private void WadGrid_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
        {
            _wadGrid.ClearSelection();
            _wadGrid.Rows[e.RowIndex].Selected = true;
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
    
    private void UpdateFinalStatus()
    {
        _statusLabel.Text = $"Complete: {_completedCount} downloaded, {_failedCount} failed";
        _cancelButton.Enabled = false;
        _closeButton.Enabled = true;
        _closeButton.Focus();
        
        // Auto-close if all downloads succeeded and we're in auto-close mode
        if (_autoCloseOnComplete && _failedCount == 0)
        {
            Close();
        }
    }
    
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        _logTextBox.AppendText($"[{timestamp}] {message}\n");
        _logTextBox.ScrollToCaret();
    }
    
    private void OnCancelClick(object? sender, EventArgs e)
    {
        _cts.Cancel();
        _cancelButton.Enabled = false;
        _statusLabel.Text = "Cancelling...";
    }
    
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _cts.Cancel();
        _downloader.ProgressUpdated -= OnProgressUpdated;
        _downloader.DownloadCompleted -= OnDownloadCompleted;
        _downloader.LogMessage -= OnLogMessage;
    }
    
    private void OnLogMessage(object? sender, (WadDownloader.LogLevel Level, string Message) e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnLogMessage(sender, e));
            return;
        }
        
        var (level, message) = e;
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var prefix = level switch
        {
            WadDownloader.LogLevel.Verbose => "[VRB]",
            WadDownloader.LogLevel.Info => "[INF]",
            WadDownloader.LogLevel.Warning => "[WRN]",
            WadDownloader.LogLevel.Error => "[ERR]",
            WadDownloader.LogLevel.Success => "[OK]",
            _ => "[???]"
        };
        
        var color = level switch
        {
            WadDownloader.LogLevel.Verbose => DarkTheme.TextSecondary,
            WadDownloader.LogLevel.Info => DarkTheme.TextPrimary,
            WadDownloader.LogLevel.Warning => DarkTheme.WarningColor,
            WadDownloader.LogLevel.Error => DarkTheme.ErrorColor,
            WadDownloader.LogLevel.Success => DarkTheme.SuccessColor,
            _ => DarkTheme.TextPrimary
        };
        
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.SelectionLength = 0;
        _logTextBox.SelectionColor = color;
        _logTextBox.AppendText($"[{timestamp}] {prefix} {message}\n");
        _logTextBox.ScrollToCaret();
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
    
    private static string FormatBytes(long bytes) => FormatUtils.FormatBytes(bytes);
}
