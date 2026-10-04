using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using ZScape.Services;

namespace ZScape.Controls;

/// <summary>
/// Presents availability separately from editable path strings. Checks run on a
/// worker; row replacement and window closure invalidate late completions.
/// </summary>
internal sealed class WadPathListPresenter : IDisposable
{
    private readonly Window _owner;
    private readonly Func<string, string?> _getPath;
    private readonly Func<string, CancellationToken, Task<bool>> _probe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<PathRow> _visibleRows = [];
    private bool _disposed;

    internal sealed record PathRow(Control Content, TextBlock Status, string? Path)
    {
        public bool IsChecking { get; set; }
    }

    public WadPathListPresenter(Window owner, ListBox list, Func<string, string?> getPath,
        Func<string, CancellationToken, Task<bool>>? probe = null)
    {
        _owner = owner;
        _getPath = getPath;
        _probe = probe ?? WadManager.IsSearchPathAvailableAsync;
        list.ItemTemplate = new FuncDataTemplate<string>((text, _) => CreateRow(text ?? string.Empty).Content);
        owner.Activated += OnActivated;
        owner.Closed += OnClosed;
    }

    internal PathRow CreateRow(string displayedPath)
    {
        var status = new TextBlock
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeService.GetBrush("WarningBrush", "#FFA500")
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = displayedPath });
        content.Children.Add(status);
        var row = new PathRow(content, status, _getPath(displayedPath));
        content.AttachedToVisualTree += (_, _) =>
        {
            if (_disposed) return;
            _visibleRows.Add(row);
            _ = RefreshAsync(row);
        };
        content.DetachedFromVisualTree += (_, _) => _visibleRows.Remove(row);
        return row;
    }

    internal async Task RefreshAsync(PathRow row)
    {
        if (_disposed || row.IsChecking || string.IsNullOrWhiteSpace(row.Path)) return;
        row.IsChecking = true;
        row.Status.Text = "[Checking…]";
        var token = _lifetime.Token;
        try
        {
            var available = await _probe(row.Path, token);
            if (!token.IsCancellationRequested)
                row.Status.Text = available ? string.Empty : "[Unavailable]";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                row.Status.Text = "[Unavailable]";
                LoggingService.Instance.Warning($"Could not check WAD folder {row.Path}: {ex.Message}");
            }
        }
        finally { row.IsChecking = false; }
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        foreach (var row in _visibleRows.ToArray()) _ = RefreshAsync(row);
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Activated -= OnActivated;
        _owner.Closed -= OnClosed;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _visibleRows.Clear();
    }
}
