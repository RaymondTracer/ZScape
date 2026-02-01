using System.ComponentModel;
using ZScape.Services;

namespace ZScape.UI;

/// <summary>
/// Progress dialog shown while saving server state before an update restart.
/// Uses background worker for concurrent operation with UI updates.
/// </summary>
public class UpdateProgressDialog : Form
{
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly Button _cancelButton;
    private readonly BackgroundWorker _saveWorker;
    private readonly Func<IProgress<SaveStateProgress>, CancellationToken, Task<bool>> _saveAction;
    private CancellationTokenSource? _cts;
    
    // Thread-safe progress state
    private volatile int _currentProgress;
    private volatile int _totalItems;
    private volatile string _currentStatus = "Preparing...";
    
    public bool SaveSucceeded { get; private set; }
    
    public UpdateProgressDialog(Func<IProgress<SaveStateProgress>, CancellationToken, Task<bool>> saveAction)
    {
        _saveAction = saveAction;
        _saveWorker = new BackgroundWorker
        {
            WorkerReportsProgress = true,
            WorkerSupportsCancellation = true
        };
        
        // Form setup
        Text = "Preparing Update";
        Size = new Size(450, 180);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        
        // Status label
        _statusLabel = new Label
        {
            Text = "Saving server state...",
            Location = new Point(20, 20),
            Size = new Size(400, 20),
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        
        // Detail label
        _detailLabel = new Label
        {
            Text = "Preparing...",
            Location = new Point(20, 45),
            Size = new Size(400, 20),
            Font = new Font("Segoe UI", 9)
        };
        
        // Progress bar
        _progressBar = new ProgressBar
        {
            Location = new Point(20, 75),
            Size = new Size(395, 25),
            Style = ProgressBarStyle.Continuous,
            Minimum = 0,
            Maximum = 100
        };
        
        // Cancel button
        _cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(340, 110),
            Size = new Size(75, 28),
            DialogResult = DialogResult.Cancel
        };
        _cancelButton.Click += CancelButton_Click;
        
        Controls.Add(_statusLabel);
        Controls.Add(_detailLabel);
        Controls.Add(_progressBar);
        Controls.Add(_cancelButton);
        
        CancelButton = _cancelButton;
        
        // Apply dark theme
        DarkTheme.Apply(this);
        DarkTheme.ApplyToControl(_cancelButton);
        
        // Wire up background worker
        _saveWorker.DoWork += SaveWorker_DoWork;
        _saveWorker.ProgressChanged += SaveWorker_ProgressChanged;
        _saveWorker.RunWorkerCompleted += SaveWorker_RunWorkerCompleted;
    }
    
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        
        // Start save operation
        _cts = new CancellationTokenSource();
        _saveWorker.RunWorkerAsync();
    }
    
    private void CancelButton_Click(object? sender, EventArgs e)
    {
        _statusLabel.Text = "Cancelling...";
        _cancelButton.Enabled = false;
        _cts?.Cancel();
    }
    
    private void SaveWorker_DoWork(object? sender, DoWorkEventArgs e)
    {
        try
        {
            var progress = new Progress<SaveStateProgress>(p =>
            {
                _currentProgress = p.Current;
                _totalItems = p.Total;
                _currentStatus = p.Status;
                
                // Report progress to trigger UI update on main thread
                var percent = p.Total > 0 ? (int)((double)p.Current / p.Total * 100) : 0;
                _saveWorker.ReportProgress(percent, p);
            });
            
            // Run the async save operation synchronously on this thread
            var task = _saveAction(progress, _cts?.Token ?? CancellationToken.None);
            task.Wait(_cts?.Token ?? CancellationToken.None);
            
            e.Result = task.Result;
        }
        catch (OperationCanceledException)
        {
            e.Cancel = true;
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            e.Cancel = true;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Error saving state: {ex.Message}");
            e.Result = false;
        }
    }
    
    private void SaveWorker_ProgressChanged(object? sender, ProgressChangedEventArgs e)
    {
        if (e.UserState is SaveStateProgress progress)
        {
            _progressBar.Value = Math.Min(e.ProgressPercentage, 100);
            _detailLabel.Text = $"{progress.Status} ({progress.Current}/{progress.Total})";
        }
    }
    
    private void SaveWorker_RunWorkerCompleted(object? sender, RunWorkerCompletedEventArgs e)
    {
        if (e.Cancelled)
        {
            SaveSucceeded = false;
            DialogResult = DialogResult.Cancel;
        }
        else if (e.Error != null)
        {
            SaveSucceeded = false;
            LoggingService.Instance.Error($"Save error: {e.Error.Message}");
            DialogResult = DialogResult.Abort;
        }
        else
        {
            SaveSucceeded = e.Result is true;
            DialogResult = SaveSucceeded ? DialogResult.OK : DialogResult.Abort;
        }
        
        Close();
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _saveWorker.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Progress data for state save operation.
/// </summary>
public class SaveStateProgress
{
    public int Current { get; init; }
    public int Total { get; init; }
    public string Status { get; init; } = string.Empty;
}
