using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using System;
using System.Collections;
using System.Collections.Generic;

namespace ZScape.Controls;

/// <summary>
/// Controls how the <see cref="ResizableListView"/> handles horizontal overflow
/// when column content exceeds the available viewport width.
/// </summary>
public enum ListViewOverflowMode
{
    /// <summary>
    /// Star-sized columns stretch to fill the viewport. No horizontal scrollbar.
    /// This is the default mode, suitable for most list views.
    /// </summary>
    Fill = 0,

    /// <summary>
    /// Columns keep their declared widths. A horizontal scrollbar is always
    /// visible when content exceeds the viewport width.
    /// Best for wide tables with many fixed-width columns.
    /// </summary>
    Scroll = 1,

    /// <summary>
    /// Columns keep their declared widths. A horizontal scrollbar appears
    /// automatically only when the total column width exceeds the viewport.
    /// </summary>
    AutoScroll = 2
}

/// <summary>
/// Controls whether a <see cref="ResizableListView"/> allows no selection, single
/// selection, or multi-selection (Ctrl+Click, Shift+Click, Shift+Arrow).
/// </summary>
public enum ListViewSelectionMode
{
    /// <summary>No selection highlighting. Rows can still fire RowPressed.</summary>
    None = 0,

    /// <summary>Only one row can be selected at a time (default).</summary>
    Single = 1,

    /// <summary>
    /// Multiple rows can be selected via Ctrl+Click (toggle) or Shift+Click/Arrow (range).
    /// </summary>
    Multi = 2
}

/// <summary>
/// Event args for row interaction events on <see cref="ResizableListView"/>.
/// </summary>
public class ListViewRowEventArgs : EventArgs
{
    public object? DataContext { get; }
    public Border RowBorder { get; }

    public ListViewRowEventArgs(object? dataContext, Border rowBorder)
    {
        DataContext = dataContext;
        RowBorder = rowBorder;
    }
}

/// <summary>
/// Event args for row pointer events (includes key modifiers and pointer info).
/// </summary>
public class ListViewRowPointerEventArgs : ListViewRowEventArgs
{
    public PointerPressedEventArgs PointerArgs { get; }

    public ListViewRowPointerEventArgs(object? dataContext, Border rowBorder, PointerPressedEventArgs pointerArgs)
        : base(dataContext, rowBorder)
    {
        PointerArgs = pointerArgs;
    }
}

/// <summary>
/// Event args for sort requests from column header clicks.
/// </summary>
public class ListViewSortEventArgs : EventArgs
{
    /// <summary>The logical column index that was clicked.</summary>
    public int ColumnIndex { get; }

    /// <summary>True when sorting ascending, false for descending.</summary>
    public bool Ascending { get; }

    public ListViewSortEventArgs(int columnIndex, bool ascending)
    {
        ColumnIndex = columnIndex;
        Ascending = ascending;
    }
}

/// <summary>
/// A reusable list control with a resizable column header, virtualized scrolling,
/// hover/select highlighting, and automatic header-to-row column width syncing.
/// <para>
/// For simple text columns, set <see cref="ListViewColumn.BindingPath"/>.
/// For custom cell content (icons, multi-bindings, etc.), set
/// <see cref="ListViewColumn.CellContentFactory"/> which is called per-row.
/// </para>
/// </summary>
public class ResizableListView : UserControl
{
    // Dark theme colors matching the app theme
    private static readonly IBrush HeaderBackground = new SolidColorBrush(Color.Parse("#2D2D30"));
    private static readonly IBrush HeaderBorderBrush = new SolidColorBrush(Color.Parse("#3F3F46"));
    private static readonly IBrush EvenRowBrush = new SolidColorBrush(Color.Parse("#1E1E1E"));
    private static readonly IBrush OddRowBrush = new SolidColorBrush(Color.Parse("#252526"));

    private readonly DockPanel _root;
    private readonly Border _headerBorder;
    private readonly Grid _headerGrid;
    private readonly ScrollViewer _scrollViewer;
    private readonly ItemsControl _itemsControl;
    private readonly List<ListViewColumn> _columns = [];
    private readonly List<int> _columnGridIndices = [];
    private readonly List<(GridLength width, double minWidth)> _originalColumnDefs = [];
    private ListViewOverflowMode _overflowMode;
    private bool _isBuilt;

    // Selection and highlighting state (always active)
    private ListViewSelectionMode _selectionMode = ListViewSelectionMode.Single;
    private Border? _hoveredRow;
    private Border? _selectedRow;
    private object? _selectedItem;
    private readonly HashSet<object> _selectedItems = [];
    private object? _selectionAnchor;

    // Sort state
    private int _sortColumnIndex = -1;
    private bool _sortAscending = true;
    private readonly List<TextBlock> _sortIndicators = [];

    // Expose internal controls for advanced scenarios
    public Grid HeaderGrid => _headerGrid;
    public Border HeaderBorder => _headerBorder;
    public ScrollViewer ScrollViewer => _scrollViewer;
    public ItemsControl ItemsControl => _itemsControl;

    // Row interaction events
    public event EventHandler<ListViewRowPointerEventArgs>? RowPressed;
    public event EventHandler<ListViewRowEventArgs>? RowDoubleTapped;
    public event EventHandler<ListViewRowEventArgs>? RowPointerEntered;
    public event EventHandler<ListViewRowEventArgs>? RowPointerExited;
    public event EventHandler<ListViewRowEventArgs>? RowGotFocus;

    /// <summary>
    /// Fired when a sortable column header is clicked. Provides the column index
    /// and the new sort direction. The caller is responsible for re-sorting the
    /// data source and updating <see cref="ItemsSource"/>.
    /// </summary>
    public event EventHandler<ListViewSortEventArgs>? SortRequested;

    /// <summary>
    /// Fired whenever the selection set changes (items added or removed).
    /// </summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Row height in pixels. Default 26.</summary>
    public double RowHeight { get; set; } = 26;

    /// <summary>
    /// Optional property path on the row data context that returns an <see cref="IBrush"/>
    /// for the row's base (non-highlighted) background colour. When set, the brush is read
    /// via a binding on each row's <see cref="Border.Tag"/> and used by
    /// <see cref="GetBaseRowBrush"/> as the default colour.
    /// This is layered underneath selection and hover highlighting, enabling semantic
    /// row colours (e.g. red tint for full servers) while still using built-in highlighting.
    /// </summary>
    public string? RowBaseBackgroundPath { get; set; }

    /// <summary>
    /// Property path for the row Height binding (e.g. "RowHeight").
    /// If null, uses the fixed <see cref="RowHeight"/> value.
    /// </summary>
    public string? RowHeightPath { get; set; }

    /// <summary>
    /// Gets the overflow mode that was set when <see cref="Build"/> was called.
    /// </summary>
    public ListViewOverflowMode OverflowMode => _overflowMode;

    /// <summary>
    /// When true, row borders do not show the hand cursor. Useful for editable rows.
    /// </summary>
    public bool SuppressHandCursor { get; set; }

    /// <summary>
    /// Controls the selection behavior. <see cref="ListViewSelectionMode.Single"/> (default)
    /// allows one row at a time. <see cref="ListViewSelectionMode.Multi"/> enables
    /// Ctrl+Click toggle, Shift+Click range, and Shift+Arrow extend. Must be set before
    /// <see cref="Build"/>.
    /// </summary>
    public ListViewSelectionMode SelectionMode
    {
        get => _selectionMode;
        set => _selectionMode = value;
    }

    /// <summary>
    /// Brush used for the currently selected row.
    /// </summary>
    public IBrush SelectedRowBrush { get; set; } = new SolidColorBrush(Color.Parse("#094771"));

    /// <summary>
    /// Brush used for the hovered (non-selected) row.
    /// </summary>
    public IBrush HoverRowBrush { get; set; } = new SolidColorBrush(Color.Parse("#2A2D2E"));

    /// <summary>
    /// Gets the data context of the currently selected row.
    /// </summary>
    public object? SelectedItem => _selectedItem;

    /// <summary>
    /// Gets all currently selected data contexts.
    /// In <see cref="ListViewSelectionMode.Single"/> mode, contains at most one item.
    /// </summary>
    public IReadOnlyCollection<object> SelectedItems => _selectedItems;

    /// <summary>
    /// When true, rows alternate between two subtle background colours for readability.
    /// The alternating colour is used as the base row colour when no
    /// <see cref="RowBaseBackgroundPath"/> is configured, or as a fallback when the
    /// path returns null.
    /// </summary>
    public bool AlternatingRowColors { get; set; }

    /// <summary>
    /// Gets the current sort column index (-1 if no sort is active).
    /// </summary>
    public int SortColumnIndex => _sortColumnIndex;

    /// <summary>
    /// Gets whether the current sort direction is ascending.
    /// </summary>
    public bool SortAscending => _sortAscending;

    /// <summary>
    /// Context menu to attach to the scroll viewer.
    /// </summary>
    public new ContextMenu? ContextMenu
    {
        get => _scrollViewer.ContextMenu;
        set => _scrollViewer.ContextMenu = value;
    }

    public ResizableListView()
    {
        ClipToBounds = true;

        _headerGrid = new Grid();
        _scrollViewer = new ScrollViewer { Background = Brushes.Transparent };
        _itemsControl = new ItemsControl();

        _root = new DockPanel { ClipToBounds = true };

        _headerBorder = new Border
        {
            Background = HeaderBackground,
            BorderBrush = HeaderBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            ClipToBounds = true,
            Child = _headerGrid
        };
        DockPanel.SetDock(_headerBorder, Dock.Top);

        _root.Children.Add(_headerBorder);

        _scrollViewer.Content = _itemsControl;
        _root.Children.Add(_scrollViewer);

        // Sync column widths when header is resized
        _headerGrid.LayoutUpdated += (_, _) => SyncColumnWidths();

        // Layout is finalized in Build() to apply OverflowMode
        Content = _root;
    }

    /// <summary>
    /// Defines the columns for this list view. Must be called before <see cref="Build"/>.
    /// </summary>
    public IReadOnlyList<ListViewColumn> Columns => _columns;

    /// <summary>
    /// Adds a column definition. Must be called before <see cref="Build"/>.
    /// </summary>
    public void AddColumn(ListViewColumn column)
    {
        if (_isBuilt) throw new InvalidOperationException("Cannot add columns after Build() has been called.");
        _columns.Add(column);
    }

    /// <summary>
    /// Sets the items source for the list.
    /// </summary>
    public IEnumerable? ItemsSource
    {
        get => _itemsControl.ItemsSource;
        set => _itemsControl.ItemsSource = value;
    }

    /// <summary>
    /// Builds the header and row template from the column definitions.
    /// Must be called once after all columns are added.
    /// </summary>
    /// <param name="overflowMode">
    /// Determines how horizontal overflow is handled.
    /// <see cref="ListViewOverflowMode.Fill"/>: star columns fill viewport, no scrollbar.
    /// <see cref="ListViewOverflowMode.Scroll"/>: fixed widths, permanent scrollbar.
    /// <see cref="ListViewOverflowMode.AutoScroll"/>: fixed widths, scrollbar on overflow.
    /// </param>
    public void Build(ListViewOverflowMode overflowMode)
    {
        if (_isBuilt) return;
        _isBuilt = true;
        _overflowMode = overflowMode;

        BuildHeaderGrid();
        BuildItemTemplate();

        // Store original column dimensions for show/hide toggling
        _originalColumnDefs.Clear();
        for (int i = 0; i < _columnGridIndices.Count; i++)
        {
            var gridCol = _columnGridIndices[i];
            var colDef = _headerGrid.ColumnDefinitions[gridCol];
            _originalColumnDefs.Add((colDef.Width, colDef.MinWidth));
        }

        // Header right-click context menu for column management
        var resetItem = new MenuItem { Header = "Reset to Default" };
        resetItem.Click += (_, _) => ResetColumnWidths();

        var autoResizeItem = new MenuItem { Header = "Auto Resize" };
        autoResizeItem.Click += (_, _) => AutoResizeColumns();

        _headerBorder.ContextMenu = new ContextMenu
        {
            Items = { resetItem, autoResizeItem }
        };

        // Enable keyboard navigation
        Focusable = true;
        KeyDown += HandleKeyDown;

        // Apply horizontal overflow mode.
        // Scroll/AutoScroll: wrap _root in an outer horizontal-only ScrollViewer.
        // The inner _scrollViewer handles vertical scrolling with its bar HIDDEN,
        // and an external ScrollBar is docked to the viewport's right edge
        // (outside the horizontal scroll) so it never scrolls off-screen.
        if (_overflowMode != ListViewOverflowMode.Fill)
        {
            // Inner: vertical scrolling (hidden bar), no horizontal
            _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;

            // Detach _root from UserControl before re-parenting
            Content = null;

            // Outer horizontal ScrollViewer wraps _root (header + rows scroll together).
            // Auto: appears only when content exceeds viewport, classic (non-overlay) style.
            var outerScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                AllowAutoHide = false, // WinForms-style: always expanded when visible
                Content = _root
            };

            // Keep _root at least as wide as the viewport so star columns fill
            // the visible area instead of collapsing to MinWidth.
            outerScroll.PropertyChanged += (_, e) =>
            {
                if (e.Property == ScrollViewer.ViewportProperty
                    || e.Property == ScrollViewer.BoundsProperty)
                {
                    double vw = outerScroll.Viewport.Width;
                    if (vw > 0)
                        _root.MinWidth = vw;
                }
            };

            // External vertical ScrollBar, docked outside the horizontal scroll
            // so it never scrolls off-screen. Hidden when not needed, classic style.
            var vScrollBar = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                IsVisible = false,
                AllowAutoHide = false // WinForms-style: always expanded when visible
            };

            // Two-way sync between _scrollViewer vertical offset and external bar
            bool syncing = false;
            _scrollViewer.PropertyChanged += (_, e) =>
            {
                if (syncing) return;
                if (e.Property == ScrollViewer.ExtentProperty
                    || e.Property == ScrollViewer.ViewportProperty
                    || e.Property == ScrollViewer.OffsetProperty)
                {
                    syncing = true;
                    double max = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
                    vScrollBar.Maximum = max;
                    vScrollBar.ViewportSize = _scrollViewer.Viewport.Height;
                    vScrollBar.Value = _scrollViewer.Offset.Y;
                    vScrollBar.IsVisible = max > 0;
                    syncing = false;

                    // Clear hover on scroll to prevent stale highlights.
                    // During scroll, PointerExited does not fire for containers
                    // that move out from under the pointer.
                    if (e.Property == ScrollViewer.OffsetProperty && _hoveredRow != null)
                    {
                        var oldHover = _hoveredRow;
                        _hoveredRow = null;
                        // Only undo the hover if the row is actually showing hover colour
                        if (oldHover.Background == HoverRowBrush)
                            oldHover.Background = GetBaseRowBrush(oldHover);
                        RowPointerExited?.Invoke(this, new ListViewRowEventArgs(oldHover.DataContext, oldHover));
                    }
                }
            };
            vScrollBar.PropertyChanged += (_, e) =>
            {
                if (syncing) return;
                if (e.Property == RangeBase.ValueProperty)
                {
                    syncing = true;
                    _scrollViewer.Offset = _scrollViewer.Offset.WithY(vScrollBar.Value);
                    syncing = false;
                }
            };

            // Layout grid: [ outerScroll(*) | vScrollBar(auto) ]
            var layoutGrid = new Grid();
            layoutGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            layoutGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(outerScroll, 0);
            Grid.SetColumn(vScrollBar, 1);
            layoutGrid.Children.Add(outerScroll);
            layoutGrid.Children.Add(vScrollBar);

            Content = layoutGrid;
        }
    }

    private void BuildHeaderGrid()
    {
        _headerGrid.ColumnDefinitions.Clear();
        _headerGrid.Children.Clear();
        _columnGridIndices.Clear();

        int gridCol = 0;
        for (int i = 0; i < _columns.Count; i++)
        {
            var col = _columns[i];

            // Add splitter column before this column (except the first)
            if (i > 0 && !col.IsFixedWidth && !_columns[i - 1].IsFixedWidth)
            {
                _headerGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5)));
                var splitter = new GridSplitter
                {
                    Width = 5,
                    Background = Brushes.Transparent,
                    ResizeDirection = GridResizeDirection.Columns,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext
                };

                // Double-click the splitter to auto-fit the column to its left
                var leftLogical = i - 1;
                splitter.DoubleTapped += (_, e) =>
                {
                    AutoResizeColumn(leftLogical);
                    e.Handled = true;
                };

                Grid.SetColumn(splitter, gridCol);
                _headerGrid.Children.Add(splitter);
                gridCol++;
            }
            else if (i > 0)
            {
                // Fixed-width columns still need a spacer column definition
                // but no splitter
                _headerGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0)));
                gridCol++;
            }

            // Record the grid column index for this logical column
            _columnGridIndices.Add(gridCol);

            // Data column
            var colDef = col.IsStar
                ? new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = col.MinWidth }
                : new ColumnDefinition(new GridLength(col.Width)) { MinWidth = col.MinWidth };
            _headerGrid.ColumnDefinitions.Add(colDef);

            // Header content
            if (col.SortClick != null && !col.IsFixedWidth)
            {
                var headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4
                };
                headerPanel.Children.Add(new TextBlock { Text = col.Header });

                var sortIndicator = new TextBlock
                {
                    Text = "",
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.Gray
                };
                headerPanel.Children.Add(sortIndicator);
                _sortIndicators.Add(sortIndicator);

                var logicalIndex = i; // capture for the closure
                var btn = new Button
                {
                    Content = headerPanel,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Classes = { "headerButton" }
                };
                btn.Click += (s, e) =>
                {
                    // Internal sort state toggle
                    HandleSortClick(logicalIndex);
                    // Also fire the original per-column handler if set
                    col.SortClick?.Invoke(s, e);
                };
                Grid.SetColumn(btn, gridCol);
                _headerGrid.Children.Add(btn);
            }
            else
            {
                var txt = new TextBlock
                {
                    Text = col.Header,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(col.CellPadding.Left, 0, 0, 0),
                    Padding = new Thickness(4)
                };
                Grid.SetColumn(txt, gridCol);
                _headerGrid.Children.Add(txt);
                _sortIndicators.Add(null!); // placeholder to keep indices aligned
            }

            gridCol++;
        }

        // Trailing resize handle column for the last column's right edge.
        // GridSplitters only resize between two columns, so the last column
        // would otherwise have no right-edge drag handle.
        _headerGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5)));
        var trailingHandle = new Border
        {
            Width = 5,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
        };
        Grid.SetColumn(trailingHandle, gridCol);
        _headerGrid.Children.Add(trailingHandle);

        // The trailing handle resizes the last logical column
        int trailingTargetLogical = _columns.Count - 1;
        int trailingTargetGridCol = _columnGridIndices[trailingTargetLogical];

        bool trailingDrag = false;
        double trailingDragStartX = 0;
        double trailingDragStartWidth = 0;

        trailingHandle.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(trailingHandle).Properties.IsLeftButtonPressed)
            {
                trailingDrag = true;
                trailingDragStartX = e.GetPosition(_headerGrid).X;
                trailingDragStartWidth = _headerGrid.ColumnDefinitions[trailingTargetGridCol].ActualWidth;
                e.Pointer.Capture(trailingHandle);
                e.Handled = true;
            }
        };

        trailingHandle.PointerMoved += (s, e) =>
        {
            if (!trailingDrag) return;
            double delta = e.GetPosition(_headerGrid).X - trailingDragStartX;
            double newWidth = Math.Max(_columns[trailingTargetLogical].MinWidth,
                trailingDragStartWidth + delta);
            _headerGrid.ColumnDefinitions[trailingTargetGridCol].Width = new GridLength(newWidth);
            e.Handled = true;
        };

        trailingHandle.PointerReleased += (s, e) =>
        {
            if (trailingDrag)
            {
                trailingDrag = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            }
        };

        // Double-click the trailing handle to auto-fit the last column
        trailingHandle.DoubleTapped += (_, e) =>
        {
            AutoResizeColumn(trailingTargetLogical);
            e.Handled = true;
        };
    }

    private void BuildItemTemplate()
    {
        // Build the column definitions list to reuse in each row
        var colDefs = new List<(GridLength width, double minWidth)>();
        for (int i = 0; i < _columns.Count; i++)
        {
            if (i > 0)
            {
                // Splitter/spacer column
                bool hasSplitter = !_columns[i].IsFixedWidth && !_columns[i - 1].IsFixedWidth;
                colDefs.Add((new GridLength(hasSplitter ? 5 : 0), 0));
            }

            var col = _columns[i];
            colDefs.Add(col.IsStar
                ? (new GridLength(1, GridUnitType.Star), col.MinWidth)
                : (new GridLength(col.Width), col.MinWidth));
        }

        // Trailing spacer to match the header's trailing resize handle column
        colDefs.Add((new GridLength(5), 0));

        // Capture references for the closure
        var columns = _columns;
        var rowHeight = RowHeight;
        var rowBaseBackgroundPath = RowBaseBackgroundPath;
        var rowHeightPath = RowHeightPath;
        var suppressHandCursor = SuppressHandCursor;

        _itemsControl.ItemsPanel = new FuncTemplate<Panel>(() =>
            new VirtualizingStackPanel());

        _itemsControl.ItemTemplate = new FuncDataTemplate<object>((_, _) =>
        {
            var border = new Border
            {
                Padding = new Thickness(0),
                Cursor = suppressHandCursor ? null : new Cursor(StandardCursorType.Hand),
                ClipToBounds = true
            };

            // Row height
            if (rowHeightPath != null)
            {
                border.Bind(HeightProperty, new Binding(rowHeightPath));
            }
            else
            {
                border.Height = rowHeight;
            }

            // Base row colour: bind to Tag so GetBaseRowBrush() can read it.
            // The actual Background is painted imperatively by the highlighting system.
            border.Background = Brushes.Transparent;
            if (rowBaseBackgroundPath != null)
            {
                border.Bind(Border.TagProperty, new Binding(rowBaseBackgroundPath));

                // When the Tag binding resolves (possibly after DataContextChanged),
                // re-evaluate the base colour IF the row is not showing a
                // higher-priority highlight (selection or hover).
                border.PropertyChanged += (s, e) =>
                {
                    if (e.Property != Border.TagProperty) return;
                    if (s is not Border b) return;
                    if (b.Background == SelectedRowBrush || b.Background == HoverRowBrush) return;
                    b.Background = GetBaseRowBrush(b);
                };
            }

            // Row events with built-in highlighting
            border.PointerPressed += (s, e) =>
            {
                if (s is not Border b) return;
                if (_selectionMode != ListViewSelectionMode.None)
                    HandleRowSelection(b, e.KeyModifiers);
                RowPressed?.Invoke(this, new ListViewRowPointerEventArgs(b.DataContext, b, e));
            };
            border.DoubleTapped += (s, e) =>
            {
                if (s is Border b)
                    RowDoubleTapped?.Invoke(this, new ListViewRowEventArgs(b.DataContext, b));
            };
            border.PointerEntered += (s, e) =>
            {
                if (s is not Border b) return;

                // Clean up stale hover from previous row.
                // Only reset the background if it is actually showing
                // HoverRowBrush -- leave selection and semantic colours alone.
                if (_hoveredRow != null && _hoveredRow != b)
                {
                    var oldHover = _hoveredRow;
                    if (oldHover.Background == HoverRowBrush)
                        oldHover.Background = GetBaseRowBrush(oldHover);
                    RowPointerExited?.Invoke(this, new ListViewRowEventArgs(oldHover.DataContext, oldHover));
                }
                _hoveredRow = b;

                // Only apply hover brush if the row isn't already showing
                // a higher-priority highlight (selection).
                if (b.Background != SelectedRowBrush)
                    b.Background = HoverRowBrush;
                RowPointerEntered?.Invoke(this, new ListViewRowEventArgs(b.DataContext, b));
            };
            border.PointerExited += (s, e) =>
            {
                if (s is not Border b) return;
                if (_hoveredRow == b) _hoveredRow = null;
                // Only undo the hover if we actually painted it
                if (b.Background == HoverRowBrush)
                    b.Background = GetBaseRowBrush(b);
                RowPointerExited?.Invoke(this, new ListViewRowEventArgs(b.DataContext, b));
            };
            border.GotFocus += (s, e) =>
            {
                if (s is Border b)
                    RowGotFocus?.Invoke(this, new ListViewRowEventArgs(b.DataContext, b));
            };

            // Handle container recycling for virtualization.
            // Fires synthetic RowPointerExited for the OLD data context so views
            // can respond to stale hover state. Also updates highlighting for selected items.
            // Background is updated immediately (no deferral) to avoid flash frames.
            // If RowBaseBackgroundPath is used, the Tag PropertyChanged handler above
            // will correct the base colour once the binding resolves.
            object? previousDataContext = null;
            border.DataContextChanged += (s, e) =>
            {
                if (s is not Border b) return;

                // Clear stale hover on the old data context
                if (previousDataContext != null && _hoveredRow == b)
                {
                    _hoveredRow = null;
                    RowPointerExited?.Invoke(this, new ListViewRowEventArgs(previousDataContext, b));
                }
                previousDataContext = b.DataContext;

                // Immediate background update -- no deferral.
                // Selected/hovered state takes priority over base colour.
                if (b.DataContext != null && _selectedItems.Contains(b.DataContext))
                {
                    b.Background = SelectedRowBrush;
                    if (b.DataContext == _selectedItem)
                        _selectedRow = b;
                }
                else if (b == _hoveredRow)
                {
                    b.Background = HoverRowBrush;
                }
                else
                {
                    b.Background = GetBaseRowBrush(b);
                    if (b == _selectedRow)
                        _selectedRow = null;
                }
            };

            // Row grid - use pixel widths from the header so Star columns are
            // already resolved. This prevents erratic resizing of newly realized
            // containers during scroll (they would briefly use Star sizing).
            // An explicit Width is set to cap DesiredSize at the header width,
            // preventing a feedback loop where row desired sizes grow _root wider,
            // causing star columns to expand and feeding back into even wider rows.
            double headerW = this._headerGrid.Bounds.Width;
            var grid = new Grid
            {
                Name = "RowGrid",
                VerticalAlignment = VerticalAlignment.Center,
                Width = headerW > 0 ? headerW : double.NaN
            };

            var headerCols = this._headerGrid.ColumnDefinitions;
            if (headerCols.Count > 0 && headerCols.Count == colDefs.Count)
            {
                // Prefer current header actual widths (already layout-resolved)
                for (int ci = 0; ci < headerCols.Count; ci++)
                {
                    double actualW = headerCols[ci].ActualWidth;
                    grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(actualW > 0 ? actualW : colDefs[ci].width.Value))
                        { MinWidth = headerCols[ci].MinWidth });
                }
            }
            else
            {
                foreach (var (width, minWidth) in colDefs)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition(width) { MinWidth = minWidth });
                }
            }

            // Cell content
            int gridCol = 0;
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) gridCol++; // Skip splitter/spacer column

                var col = columns[i];

                Control cellControl;
                if (col.CellContentFactory != null)
                {
                    cellControl = col.CellContentFactory();
                }
                else if (col.BindingPath != null)
                {
                    var tb = new TextBlock
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = col.TextTrimming,
                        Padding = col.CellPadding,
                        HorizontalAlignment = col.ContentAlignment,
                    };

                    if (col.Foreground != null)
                        tb.Foreground = col.Foreground;

                    tb.Bind(TextBlock.TextProperty, new Binding(col.BindingPath));

                    cellControl = tb;
                }
                else
                {
                    cellControl = new TextBlock(); // Empty placeholder
                }

                Grid.SetColumn(cellControl, gridCol);
                grid.Children.Add(cellControl);
                gridCol++;
            }

            border.Child = grid;
            return border;
        });
    }

    /// <summary>
    /// Synchronizes header column widths to all realized row grids.
    /// Called automatically on header LayoutUpdated.
    /// </summary>
    public void SyncColumnWidths()
    {
        var headerCols = _headerGrid.ColumnDefinitions;
        if (headerCols.Count == 0) return;

        double headerWidth = _headerGrid.Bounds.Width;

        foreach (var container in _itemsControl.GetRealizedContainers())
        {
            var rowGrid = container.FindDescendantOfType<Grid>();
            if (rowGrid?.Name == "RowGrid" && rowGrid.ColumnDefinitions.Count == headerCols.Count)
            {
                // Pin the row grid width to the header's actual width.
                // This prevents the row-desired-size feedback loop:
                // row pixel columns can sum to slightly more than the header
                // (sub-pixel rounding), inflating _root, expanding star
                // columns, and compounding on every layout pass.
                if (headerWidth > 0 && (double.IsNaN(rowGrid.Width) || Math.Abs(rowGrid.Width - headerWidth) > 0.5))
                    rowGrid.Width = headerWidth;

                for (int i = 0; i < headerCols.Count; i++)
                {
                    double hw = headerCols[i].ActualWidth;
                    var rd = rowGrid.ColumnDefinitions[i];

                    // Epsilon guard: skip no-op writes to avoid triggering
                    // unnecessary layout passes (LayoutUpdated -> SyncColumnWidths).
                    if (rd.Width.GridUnitType != GridUnitType.Pixel || Math.Abs(rd.Width.Value - hw) > 0.5)
                        rd.Width = new GridLength(hw);

                    double hMin = headerCols[i].MinWidth;
                    if (Math.Abs(rd.MinWidth - hMin) > 0.1)
                        rd.MinWidth = hMin;
                }
            }
        }
    }

    /// <summary>
    /// Gets the internal grid column index for a logical column (0-based).
    /// Useful for direct ColumnDefinitions manipulation.
    /// </summary>
    public int GetGridColumnIndex(int logicalColumnIndex)
    {
        if (logicalColumnIndex < 0 || logicalColumnIndex >= _columnGridIndices.Count)
            return -1;
        return _columnGridIndices[logicalColumnIndex];
    }

    /// <summary>
    /// Changes the width of a logical column at runtime.
    /// The change propagates to all row grids via the column sync.
    /// </summary>
    public void SetColumnWidth(int logicalColumnIndex, GridLength width)
    {
        var gridCol = GetGridColumnIndex(logicalColumnIndex);
        if (gridCol < 0 || gridCol >= _headerGrid.ColumnDefinitions.Count) return;
        _headerGrid.ColumnDefinitions[gridCol].Width = width;
    }

    /// <summary>
    /// Shows or hides a logical column. When hidden, the column width and MinWidth
    /// are set to 0 so remaining columns (especially star-sized) fill the freed space.
    /// When shown, the original width and MinWidth are restored.
    /// </summary>
    public void SetColumnVisible(int logicalColumnIndex, bool visible)
    {
        var gridCol = GetGridColumnIndex(logicalColumnIndex);
        if (gridCol < 0 || gridCol >= _headerGrid.ColumnDefinitions.Count) return;
        if (logicalColumnIndex >= _originalColumnDefs.Count) return;

        var colDef = _headerGrid.ColumnDefinitions[gridCol];

        if (visible)
        {
            var original = _originalColumnDefs[logicalColumnIndex];
            colDef.Width = original.width;
            colDef.MinWidth = original.minWidth;
        }
        else
        {
            colDef.Width = new GridLength(0);
            colDef.MinWidth = 0;
        }

        // Also collapse/restore the adjacent spacer column (before this column)
        if (gridCol > 0)
        {
            var spacerDef = _headerGrid.ColumnDefinitions[gridCol - 1];
            if (!visible)
            {
                spacerDef.Width = new GridLength(0);
            }
            else
            {
                // Restore spacer only if the previous logical column is also visible
                var prevLogical = logicalColumnIndex - 1;
                if (prevLogical >= 0)
                {
                    var prevGridCol = _columnGridIndices[prevLogical];
                    var prevVisible = _headerGrid.ColumnDefinitions[prevGridCol].MinWidth > 0
                        || _headerGrid.ColumnDefinitions[prevGridCol].Width.Value > 0;
                    if (prevVisible)
                    {
                        // Determine original spacer width: 5 if both columns are non-fixed, 0 otherwise
                        bool hasSplitter = !_columns[logicalColumnIndex].IsFixedWidth
                            && !_columns[prevLogical].IsFixedWidth;
                        spacerDef.Width = new GridLength(hasSplitter ? 5 : 0);
                    }
                }
            }
        }

        // Hide/show the header content element for this column
        foreach (var child in _headerGrid.Children)
        {
            if (child is Control ctrl && Grid.GetColumn(ctrl) == gridCol && ctrl is not GridSplitter)
            {
                ctrl.IsVisible = visible;
            }
        }
    }

    /// <summary>
    /// Clears the current selection highlight.
    /// </summary>
    public void ClearSelection()
    {
        _selectedItems.Clear();
        _selectionAnchor = null;
        _selectedRow = null;
        _selectedItem = null;
        UpdateSelectionVisuals();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Programmatically selects the given item, replacing any current selection.
    /// If <paramref name="item"/> is null, the selection is cleared.
    /// Fires <see cref="SelectionChanged"/>.
    /// </summary>
    public void SelectItem(object? item)
    {
        if (item == null)
        {
            ClearSelection();
            return;
        }

        _selectedItems.Clear();
        _selectedItems.Add(item);
        _selectionAnchor = item;
        _selectedItem = item;
        _selectedRow = null; // will be resolved in UpdateSelectionVisuals
        UpdateSelectionVisuals();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handles selection logic for a row press, respecting <see cref="SelectionMode"/>,
    /// Ctrl (toggle) and Shift (range) modifiers.
    /// </summary>
    private void HandleRowSelection(Border border, KeyModifiers modifiers)
    {
        var item = border.DataContext;

        if (_selectionMode == ListViewSelectionMode.Multi && modifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl+Click: toggle item in selection
            if (item != null)
            {
                if (_selectedItems.Contains(item))
                    _selectedItems.Remove(item);
                else
                    _selectedItems.Add(item);
            }
            _selectionAnchor = item;
            _selectedItem = item;
            _selectedRow = border;
            UpdateSelectionVisuals();
        }
        else if (_selectionMode == ListViewSelectionMode.Multi && modifiers.HasFlag(KeyModifiers.Shift))
        {
            // Shift+Click: range select from anchor to clicked item
            if (_selectionAnchor != null && item != null)
            {
                SelectRange(_selectionAnchor, item);
            }
            else if (item != null)
            {
                _selectedItems.Clear();
                _selectedItems.Add(item);
                _selectionAnchor = item;
            }
            _selectedItem = item;
            _selectedRow = border;
            UpdateSelectionVisuals();
        }
        else
        {
            // Normal click (or Single mode): clear all, select one
            _selectedItems.Clear();
            if (item != null) _selectedItems.Add(item);
            _selectionAnchor = item;
            _selectedItem = item;
            _selectedRow = border;
            UpdateSelectionVisuals();
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);

        // Ensure the control has keyboard focus
        Focus();
    }

    /// <summary>
    /// Selects all items between <paramref name="from"/> and <paramref name="to"/> inclusive.
    /// </summary>
    private void SelectRange(object from, object to)
    {
        var items = GetItemsList();
        if (items == null) return;

        int fromIndex = items.IndexOf(from);
        int toIndex = items.IndexOf(to);
        if (fromIndex < 0 || toIndex < 0) return;

        _selectedItems.Clear();
        int start = Math.Min(fromIndex, toIndex);
        int end = Math.Max(fromIndex, toIndex);
        for (int i = start; i <= end; i++)
            _selectedItems.Add(items[i]);
    }

    /// <summary>
    /// Returns the current <see cref="ItemsSource"/> as a materialized list, or null.
    /// </summary>
    private List<object>? GetItemsList()
    {
        if (_itemsControl.ItemsSource == null) return null;
        var list = new List<object>();
        foreach (var item in _itemsControl.ItemsSource)
        {
            if (item != null) list.Add(item);
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Re-paints all realized row containers to reflect the current selection set.
    /// Only the visible (virtualized) rows are touched, so this is efficient.
    /// </summary>
    private void UpdateSelectionVisuals()
    {
        foreach (var container in _itemsControl.GetRealizedContainers())
        {
            Border? b = null;
            if (container is ContentPresenter cp)
                b = cp.Child as Border;
            b ??= container.FindDescendantOfType<Border>();

            if (b == null) continue;

            if (b.DataContext != null && _selectedItems.Contains(b.DataContext))
            {
                b.Background = SelectedRowBrush;
            }
            else if (b == _hoveredRow)
            {
                b.Background = HoverRowBrush;
            }
            else
            {
                b.Background = GetBaseRowBrush(b);
            }
        }
    }

    /// <summary>
    /// Scrolls the vertical <see cref="ScrollViewer"/> so that the row at the given
    /// index is visible. Uses <see cref="RowHeight"/> to compute the scroll offset.
    /// </summary>
    private void ScrollItemIntoView(int index)
    {
        double targetTop = index * RowHeight;
        double targetBottom = targetTop + RowHeight;
        double viewportHeight = _scrollViewer.Viewport.Height;
        double currentOffset = _scrollViewer.Offset.Y;

        if (targetTop < currentOffset)
            _scrollViewer.Offset = _scrollViewer.Offset.WithY(targetTop);
        else if (targetBottom > currentOffset + viewportHeight)
            _scrollViewer.Offset = _scrollViewer.Offset.WithY(targetBottom - viewportHeight);
    }

    /// <summary>
    /// Handles keyboard navigation: Up/Down to move selection, Home/End to jump,
    /// Shift+Arrow to extend selection in multi-mode, Enter to activate, Ctrl+A to select all.
    /// </summary>
    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (_selectionMode == ListViewSelectionMode.None) return;

        var items = GetItemsList();
        if (items == null || items.Count == 0) return;

        int currentIndex = _selectedItem != null ? items.IndexOf(_selectedItem) : -1;

        switch (e.Key)
        {
            case Key.Up:
            {
                int newIndex = currentIndex > 0 ? currentIndex - 1 : 0;
                SelectByIndex(items, newIndex, e.KeyModifiers);
                e.Handled = true;
                break;
            }
            case Key.Down:
            {
                int newIndex = currentIndex < items.Count - 1 ? currentIndex + 1 : items.Count - 1;
                SelectByIndex(items, newIndex, e.KeyModifiers);
                e.Handled = true;
                break;
            }
            case Key.Home:
            {
                SelectByIndex(items, 0, e.KeyModifiers);
                e.Handled = true;
                break;
            }
            case Key.End:
            {
                SelectByIndex(items, items.Count - 1, e.KeyModifiers);
                e.Handled = true;
                break;
            }
            case Key.Enter:
            {
                if (_selectedItem != null && _selectedRow != null)
                {
                    RowDoubleTapped?.Invoke(this, new ListViewRowEventArgs(_selectedItem, _selectedRow));
                    e.Handled = true;
                }
                break;
            }
            case Key.A:
            {
                // Ctrl+A: select all in multi mode
                if (_selectionMode == ListViewSelectionMode.Multi
                    && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    _selectedItems.Clear();
                    foreach (var item in items)
                        _selectedItems.Add(item);
                    _selectedItem = items[^1];
                    UpdateSelectionVisuals();
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                }
                break;
            }
        }
    }

    /// <summary>
    /// Selects the item at the given index, optionally extending the selection
    /// (Shift) in multi-select mode. Scrolls the item into view.
    /// </summary>
    private void SelectByIndex(List<object> items, int index, KeyModifiers modifiers)
    {
        if (index < 0 || index >= items.Count) return;
        var item = items[index];

        if (_selectionMode == ListViewSelectionMode.Multi && modifiers.HasFlag(KeyModifiers.Shift))
        {
            // Extend selection from anchor to new index
            if (_selectionAnchor != null)
            {
                int anchorIndex = items.IndexOf(_selectionAnchor);
                if (anchorIndex >= 0)
                {
                    _selectedItems.Clear();
                    int start = Math.Min(anchorIndex, index);
                    int end = Math.Max(anchorIndex, index);
                    for (int i = start; i <= end; i++)
                        _selectedItems.Add(items[i]);
                }
            }
            else
            {
                _selectedItems.Clear();
                _selectedItems.Add(item);
                _selectionAnchor = item;
            }
        }
        else
        {
            _selectedItems.Clear();
            _selectedItems.Add(item);
            _selectionAnchor = item;
        }

        _selectedItem = item;
        UpdateSelectionVisuals();
        ScrollItemIntoView(index);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Returns the base (non-highlighted) row background for a given border.
    /// Priority order: <see cref="RowBaseBackgroundPath"/> (via border Tag binding),
    /// then <see cref="AlternatingRowColors"/>, then transparent.
    /// </summary>
    private IBrush GetBaseRowBrush(Border border)
    {
        // 1. Check for a data-bound base background via RowBaseBackgroundPath
        if (border.Tag is IBrush baseBrush)
            return baseBrush;

        // 2. Alternating row colours
        if (!AlternatingRowColors) return Brushes.Transparent;

        // Determine the visual index of this container in the ItemsControl
        var panel = _itemsControl.ItemsPanelRoot;
        if (panel is VirtualizingStackPanel vsp)
        {
            int index = vsp.Children.IndexOf(border.Parent as Control ?? border);
            // If the border is wrapped in a ContentPresenter, look for that instead
            if (index < 0)
            {
                foreach (var child in vsp.Children)
                {
                    if (child is ContentPresenter cp && cp.Child == border)
                    {
                        index = vsp.Children.IndexOf(cp);
                        break;
                    }
                }
            }
            // Use first realized index offset to get the absolute position
            int firstIndex = vsp.FirstRealizedIndex;
            if (firstIndex >= 0 && index >= 0)
            {
                int absoluteIndex = firstIndex + index;
                return absoluteIndex % 2 == 0 ? EvenRowBrush : OddRowBrush;
            }
        }
        return Brushes.Transparent;
    }

    /// <summary>
    /// Handles a sort header click: toggles direction or sets new column,
    /// updates visual indicators, and fires <see cref="SortRequested"/>.
    /// </summary>
    private void HandleSortClick(int logicalColumnIndex)
    {
        if (logicalColumnIndex == _sortColumnIndex)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumnIndex = logicalColumnIndex;
            // Check if this column defaults to descending
            _sortAscending = logicalColumnIndex < _columns.Count
                && _columns[logicalColumnIndex].DefaultSortDescending
                ? false : true;
        }

        UpdateSortIndicators();
        SortRequested?.Invoke(this, new ListViewSortEventArgs(_sortColumnIndex, _sortAscending));
    }

    /// <summary>
    /// Updates the visual sort arrow indicators in the header.
    /// Shows an up or down triangle on the active sort column, clears all others.
    /// </summary>
    private void UpdateSortIndicators()
    {
        for (int i = 0; i < _sortIndicators.Count; i++)
        {
            var indicator = _sortIndicators[i];
            if (indicator == null) continue;

            if (i == _sortColumnIndex)
            {
                indicator.Text = _sortAscending ? "\u25B2" : "\u25BC"; // up / down triangle
            }
            else
            {
                indicator.Text = "";
            }
        }
    }

    /// <summary>
    /// Resets all column widths to their original values as defined during column setup.
    /// </summary>
    public void ResetColumnWidths()
    {
        for (int i = 0; i < _columnGridIndices.Count; i++)
        {
            if (i >= _originalColumnDefs.Count) break;
            var gridCol = _columnGridIndices[i];
            if (gridCol >= _headerGrid.ColumnDefinitions.Count) continue;

            var original = _originalColumnDefs[i];
            _headerGrid.ColumnDefinitions[gridCol].Width = original.width;
            _headerGrid.ColumnDefinitions[gridCol].MinWidth = original.minWidth;
        }
    }

    /// <summary>
    /// Auto-resizes non-fixed pixel columns to fit their widest visible content.
    /// Star-sized columns remain star-sized and fill any remaining space.
    /// Fixed-width columns (e.g. icon columns) are not modified.
    /// </summary>
    /// <summary>
    /// Auto-resizes a single column to fit its content.
    /// Works on any non-fixed column including star-sized columns
    /// (which are converted to pixel width on autosize).
    /// </summary>
    public void AutoResizeColumn(int logicalColumnIndex)
    {
        if (logicalColumnIndex < 0 || logicalColumnIndex >= _columns.Count) return;
        var col = _columns[logicalColumnIndex];
        if (col.IsFixedWidth) return;

        var gridColIdx = _columnGridIndices[logicalColumnIndex];
        if (gridColIdx >= _headerGrid.ColumnDefinitions.Count) return;

        double maxWidth = 0;

        // Measure header text width
        if (!string.IsNullOrEmpty(col.Header))
            maxWidth = MeasureTextWidth(col.Header) + 24; // +24 for button padding/chrome

        // Measure content widths from realized rows
        foreach (var container in _itemsControl.GetRealizedContainers())
        {
            var rowGrid = container.FindDescendantOfType<Grid>();
            if (rowGrid?.Name != "RowGrid") continue;

            foreach (var child in rowGrid.Children)
            {
                if (child is not Control ctrl || Grid.GetColumn(ctrl) != gridColIdx) continue;

                double cellWidth;
                if (ctrl is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                {
                    cellWidth = MeasureTextWidth(tb.Text, tb.FontSize > 0 ? tb.FontSize : 12)
                                + tb.Padding.Left + tb.Padding.Right + 8;
                }
                else
                {
                    ctrl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    cellWidth = ctrl.DesiredSize.Width + 8;
                }

                maxWidth = Math.Max(maxWidth, cellWidth);
                break;
            }
        }

        var newWidth = Math.Max(maxWidth, col.MinWidth);
        _headerGrid.ColumnDefinitions[gridColIdx].Width = new GridLength(newWidth);
    }

    /// <summary>
    /// Auto-resizes all non-fixed pixel columns to fit their content.
    /// Star-sized columns are left unchanged. Use <see cref="AutoResizeColumn"/>
    /// for explicit per-column autosize including star columns.
    /// </summary>
    public void AutoResizeColumns()
    {
        var maxWidths = new double[_columns.Count];

        // Measure header text widths for resizable pixel columns
        for (int i = 0; i < _columns.Count; i++)
        {
            if (_columns[i].IsFixedWidth || _columns[i].IsStar) continue;
            var headerText = _columns[i].Header;
            if (!string.IsNullOrEmpty(headerText))
                maxWidths[i] = MeasureTextWidth(headerText) + 24; // +24 for button padding/chrome
        }

        // Measure content widths from realized rows
        foreach (var container in _itemsControl.GetRealizedContainers())
        {
            var rowGrid = container.FindDescendantOfType<Grid>();
            if (rowGrid?.Name != "RowGrid") continue;

            for (int i = 0; i < _columns.Count; i++)
            {
                if (_columns[i].IsFixedWidth || _columns[i].IsStar) continue;
                var gridCol = _columnGridIndices[i];

                foreach (var child in rowGrid.Children)
                {
                    if (child is not Control ctrl || Grid.GetColumn(ctrl) != gridCol) continue;

                    double cellWidth;
                    if (ctrl is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                    {
                        // Measure full text width (ignoring trimming constraints)
                        cellWidth = MeasureTextWidth(tb.Text, tb.FontSize > 0 ? tb.FontSize : 12)
                                    + tb.Padding.Left + tb.Padding.Right + 8;
                    }
                    else
                    {
                        ctrl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        cellWidth = ctrl.DesiredSize.Width + 8;
                    }

                    maxWidths[i] = Math.Max(maxWidths[i], cellWidth);
                    break;
                }
            }
        }

        // Apply calculated widths to resizable pixel columns
        for (int i = 0; i < _columns.Count; i++)
        {
            if (_columns[i].IsFixedWidth || _columns[i].IsStar) continue;
            var gridCol = _columnGridIndices[i];
            if (gridCol >= _headerGrid.ColumnDefinitions.Count) continue;

            var newWidth = Math.Max(maxWidths[i], _columns[i].MinWidth);
            _headerGrid.ColumnDefinitions[gridCol].Width = new GridLength(newWidth);
        }
    }

    /// <summary>
    /// Measures the pixel width of a text string using default font properties.
    /// </summary>
    private static double MeasureTextWidth(string text, double fontSize = 12)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var measure = new TextBlock { Text = text, FontSize = fontSize };
        measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return measure.DesiredSize.Width;
    }
}
