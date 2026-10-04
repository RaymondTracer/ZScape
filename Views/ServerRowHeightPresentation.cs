using Avalonia.Threading;
using ZScape.Controls;

namespace ZScape.Views;

/// <summary>Displays a row-height draft while accepted settings remain available for persistence.</summary>
internal sealed class ServerRowHeightPresentation(Func<int> getAcceptedHeight)
{
    private int? _previewHeight;
    private PreviewBinding? _binding;

    public int RowHeight => _previewHeight ?? getAcceptedHeight();

    public void Apply(ResizableListView list, IEnumerable<ServerViewModel> rows)
    {
        list.RowHeight = RowHeight;
        foreach (var row in rows) row.NotifyRowHeightChanged();
    }

    public IDisposable Bind(UnifiedSettingsDialog dialog, Action updateRows)
    {
        Dispatcher.UIThread.VerifyAccess();
        _binding?.Dispose();
        return _binding = new PreviewBinding(this, dialog, updateRows);
    }

    private sealed class PreviewBinding : IDisposable
    {
        private readonly ServerRowHeightPresentation _owner;
        private readonly UnifiedSettingsDialog _dialog;
        private readonly Action _updateRows;
        private bool _disposed;

        public PreviewBinding(ServerRowHeightPresentation owner, UnifiedSettingsDialog dialog, Action updateRows)
        {
            _owner = owner;
            _dialog = dialog;
            _updateRows = updateRows;
            dialog.RowHeightPreviewChanged += OnHeightChanged;
            dialog.Closed += OnClosed;
        }

        private void OnHeightChanged(object? sender, int height)
        {
            _owner._previewHeight = height;
            _updateRows();
        }

        private void OnClosed(object? sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            Dispatcher.UIThread.VerifyAccess();
            if (_disposed) return;
            _disposed = true;
            _dialog.RowHeightPreviewChanged -= OnHeightChanged;
            _dialog.Closed -= OnClosed;
            _owner._binding = null;
            _owner._previewHeight = null;
            _updateRows();
        }
    }
}
