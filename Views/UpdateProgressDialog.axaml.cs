using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;
using ZScape.Services;

namespace ZScape.Views;

public partial class UpdateProgressDialog : Window
{
    private readonly Func<IProgress<SaveStateProgress>, CancellationToken, Task<bool>> _saveAction;
    private readonly string _failureDescription;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public bool SaveSucceeded { get; private set; }
    public bool WasCancelled { get; private set; }
    public Exception? OperationError { get; private set; }

    // Required for XAML runtime loader
    public UpdateProgressDialog() : this((_, _) => Task.FromResult(true)) { }

    public UpdateProgressDialog(
        Func<IProgress<SaveStateProgress>, CancellationToken, Task<bool>> saveAction,
        string title = "Preparing Update",
        string status = "Saving server state...",
        string failureDescription = "saving state")
    {
        InitializeComponent();
        _saveAction = saveAction;
        _failureDescription = failureDescription;
        Title = title;
        StatusLabel.Text = status;
        
        // Handle Escape key
        KeyDown += OnDialogKeyDown;
        
        Opened += OnOpened;
    }
    
    private void OnDialogKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape && CancelButton.IsEnabled)
        {
            CancelButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        _cts = new CancellationTokenSource();
        _isRunning = true;
        var cancellationToken = _cts.Token;
        
        var progress = new Progress<SaveStateProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_isRunning || cancellationToken.IsCancellationRequested)
                    return;
                var percent = p.Total > 0 ? (int)((double)p.Current / p.Total * 100) : 0;
                ProgressBar.IsIndeterminate = p.Total <= 0;
                ProgressBar.Value = Math.Min(percent, 100);
                DetailLabel.Text = p.Total > 0 ? $"{p.Status} ({p.Current}/{p.Total})" : p.Status;
            });
        });

        try
        {
            SaveSucceeded = await Task.Run(async () =>
            {
                var succeeded = await _saveAction(progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return succeeded;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            SaveSucceeded = false;
            WasCancelled = true;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Error {_failureDescription}: {ex.Message}");
            SaveSucceeded = false;
            OperationError = ex;
        }
        finally
        {
            _isRunning = false;
            _cts.Dispose();
            _cts = null;
            Close(SaveSucceeded);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        CancelOperation();
    }

    public void CancelOperation()
    {
        StatusLabel.Text = "Cancelling...";
        CancelButton.IsEnabled = false;
        _cts?.Cancel();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_isRunning)
        {
            e.Cancel = true;
            CancelOperation();
        }
        base.OnClosing(e);
    }
}
