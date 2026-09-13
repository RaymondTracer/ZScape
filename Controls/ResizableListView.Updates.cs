using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ZScape.Controls;

public partial class ResizableListView
{
    private Action? _pendingRowsUpdate;
    private int _rowsUpdateVersion;

    /// <summary>Raised after row data and selection have been reconciled.</summary>
    public event EventHandler? RowsUpdated;

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _pendingRowsUpdate = null;
        ++_rowsUpdateVersion;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Applies a snapshot by stable row key, preserving selection and the visible
    /// row anchor. Duplicate keys are matched by occurrence. A consumer can reuse
    /// or update an existing view model through reconcile. Snapshot data must not
    /// be mutated while an open context menu defers its application.
    /// </summary>
    public void UpdateRows<T>(ObservableCollection<T> rows, IEnumerable<T> snapshot,
        Func<T, object> key, Func<T, T, T>? reconcile = null) where T : class
    {
        Dispatcher.UIThread.VerifyAccess();
        var incoming = snapshot.ToList();
        if (IsContextMenuOpen)
        {
            // Only the latest snapshot matters, and only this list waits.
            _pendingRowsUpdate = () => UpdateRows(rows, incoming, key, reconcile);
            return;
        }

        _pendingRowsUpdate = null;
        var version = ++_rowsUpdateVersion;
        var oldRows = rows.ToList();
        var oldKeys = GetRowIdentities(oldRows, key);
        var newKeys = GetRowIdentities(incoming, key);
        var oldByKey = oldKeys.Select((identity, index) => (identity, row: oldRows[index]))
            .ToDictionary(pair => pair.identity, pair => pair.row);
        var newByOldItem = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < incoming.Count; i++)
        {
            if (oldByKey.TryGetValue(newKeys[i], out var existing))
            {
                incoming[i] = reconcile?.Invoke(existing, incoming[i]) ?? incoming[i];
                newByOldItem[existing] = incoming[i];
            }
        }

        object? Remap(object? item) => item != null && newByOldItem.TryGetValue(item, out var next)
            ? next : null;
        var selection = _selectedItems.Select(Remap).OfType<object>().ToHashSet();
        var primary = Remap(_selectedItem);
        var anchor = Remap(_selectionAnchor);
        var selectionChanged = !_selectedItems.SetEquals(selection) || !ReferenceEquals(primary, _selectedItem);
        var offset = _scrollViewer.Offset;
        var itemExtent = GetEstimatedItemExtent(oldRows.Count);
        var topIndex = itemExtent > 0 ? (int)Math.Floor(offset.Y / itemExtent) : 0;
        var topKey = topIndex >= 0 && topIndex < oldKeys.Count ? oldKeys[topIndex] : null;

        // Keep matching objects and their containers; avoid collection Reset events.
        var retained = incoming.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!retained.Contains(rows[i])) rows.RemoveAt(i);
        for (var i = 0; i < incoming.Count; i++)
        {
            if (i < rows.Count && ReferenceEquals(rows[i], incoming[i])) continue;
            var existingIndex = rows.IndexOf(incoming[i]);
            if (existingIndex >= 0) rows.Move(existingIndex, i);
            else rows.Insert(i, incoming[i]);
        }

        _selectedItems.Clear();
        _selectedItems.UnionWith(selection);
        _selectedItem = primary;
        _selectionAnchor = anchor;
        _selectedRow = null;
        UpdateSelectionVisuals();

        var newTopIndex = topKey == null ? -1 : newKeys.IndexOf(topKey);
        var targetOffset = newTopIndex < 0 ? offset.Y
            : newTopIndex * itemExtent + offset.Y - topIndex * itemExtent;
        var offsetAfterUpdate = _scrollViewer.Offset;
        Dispatcher.UIThread.Post(() =>
        {
            // A later update, reset, or user scroll takes precedence.
            if (version != _rowsUpdateVersion || _scrollViewer.Offset != offsetAfterUpdate) return;
            SetVerticalOffset(targetOffset);
        }, DispatcherPriority.Loaded);

        if (selectionChanged) SelectionChanged?.Invoke(this, EventArgs.Empty);
        RowsUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Explicitly discards pending updates and interaction state for a new data scope.</summary>
    public void ResetRows<T>(ObservableCollection<T> rows) where T : class
    {
        Dispatcher.UIThread.VerifyAccess();
        _pendingRowsUpdate = null;
        ++_rowsUpdateVersion;
        _rowContextMenu?.Close();
        _headerBorder.ContextMenu?.Close();
        rows.Clear();
        ClearSelection();
        _scrollViewer.Offset = Vector.Zero;
    }

    private void UpdateMenuClosed(object? sender, EventArgs e)
    {
        // Closing one menu can immediately open the other; check after that transition.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsContextMenuOpen) return;
            var pending = _pendingRowsUpdate;
            _pendingRowsUpdate = null;
            pending?.Invoke();
        });
    }

    private sealed record RowIdentity(object Key, int Occurrence);

    private static List<RowIdentity> GetRowIdentities<T>(IEnumerable<T> rows, Func<T, object> key)
    {
        var occurrences = new Dictionary<object, int>();
        var identities = new List<RowIdentity>();
        foreach (var row in rows)
        {
            var value = key(row);
            occurrences.TryGetValue(value, out var occurrence);
            occurrences[value] = occurrence + 1;
            identities.Add(new RowIdentity(value, occurrence));
        }
        return identities;
    }
}
