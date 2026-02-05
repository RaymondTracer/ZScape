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
    private CancellationTokenSource? _cts;

    public bool SaveSucceeded { get; private set; }

    // Required for XAML runtime loader
    public UpdateProgressDialog() : this((_, _) => Task.FromResult(true)) { }

    public UpdateProgressDialog(Func<IProgress<SaveStateProgress>, CancellationToken, Task<bool>> saveAction)
    {
        InitializeComponent();
        _saveAction = saveAction;
        
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
        
        var progress = new Progress<SaveStateProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var percent = p.Total > 0 ? (int)((double)p.Current / p.Total * 100) : 0;
                ProgressBar.Value = Math.Min(percent, 100);
                DetailLabel.Text = $"{p.Status} ({p.Current}/{p.Total})";
            });
        });

        try
        {
            SaveSucceeded = await Task.Run(async () =>
            {
                return await _saveAction(progress, _cts.Token);
            }, _cts.Token);

            Close(SaveSucceeded);
        }
        catch (OperationCanceledException)
        {
            SaveSucceeded = false;
            Close(false);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Error saving state: {ex.Message}");
            SaveSucceeded = false;
            Close(false);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Cancelling...";
        CancelButton.IsEnabled = false;
        _cts?.Cancel();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnClosing(e);
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
