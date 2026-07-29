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
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ZScape.Services;

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
/// One level in a list view's ordered sort chain.
/// </summary>
public sealed record ListViewSortDescriptor(
    int ColumnIndex,
    string ColumnKey,
    bool Ascending);

/// <summary>
/// Event args for sort requests from column header clicks.
/// </summary>
public class ListViewSortEventArgs : EventArgs
{
    /// <summary>The logical column index that was clicked.</summary>
    public int ColumnIndex { get; }

    /// <summary>The clicked column's resulting direction.</summary>
    public bool Ascending { get; }

    /// <summary>
    /// Complete ordered sort chain. The first entry is the primary sort;
    /// later entries are tie-breakers.
    /// </summary>
    public IReadOnlyList<ListViewSortDescriptor> SortDescriptors { get; }

    public ListViewSortEventArgs(int columnIndex, bool ascending)
        : this(
            columnIndex,
            ascending,
            [new ListViewSortDescriptor(columnIndex, columnIndex.ToString(CultureInfo.InvariantCulture), ascending)])
    {
    }

    public ListViewSortEventArgs(
        int columnIndex,
        bool ascending,
        IReadOnlyList<ListViewSortDescriptor> sortDescriptors)
    {
        ColumnIndex = columnIndex;
        Ascending = ascending;
        SortDescriptors = sortDescriptors;
    }
}

/// <summary>
/// Event args for a user-driven column visibility change.
/// </summary>
public sealed class ListViewColumnVisibilityChangedEventArgs : EventArgs
{
    public int ColumnIndex { get; }
    public string ColumnKey { get; }
    public bool IsVisible { get; }

    public ListViewColumnVisibilityChangedEventArgs(
        int columnIndex,
        string columnKey,
        bool isVisible)
    {
        ColumnIndex = columnIndex;
        ColumnKey = columnKey;
        IsVisible = isVisible;
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
    private const double ScrollAlignmentTolerance = 0.5;

    // Dark theme fallback colors (used when ThemeService resources aren't loaded yet)
    private static readonly IBrush HeaderBackgroundFallback = new SolidColorBrush(Color.Parse("#2D2D30"));
    private static readonly IBrush HeaderBorderBrushFallback = new SolidColorBrush(Color.Parse("#3F3F46"));
    private static readonly IBrush EvenRowBrush = ThemeService.GetBrush("RowEvenBrush", "#1E1E1E");
    private static readonly IBrush OddRowBrush = ThemeService.GetBrush("RowOddBrush", "#252526");

    private static IBrush HeaderBackground => ThemeService.GetBrush("TertiaryBackgroundBrush", "#2D2D30");
    private static IBrush HeaderBorderBrush => ThemeService.GetBrush("BorderBrush", "#3F3F46");

    private readonly DockPanel _root;
    private readonly Border _headerBorder;
    private readonly Grid _headerGrid;
    private readonly ScrollViewer _scrollViewer;
    private readonly ItemsControl _itemsControl;
    private readonly List<ListViewColumn> _columns = [];
    private readonly List<int> _columnGridIndices = [];
    private readonly List<(GridLength width, double minWidth)> _originalColumnDefs = [];
    private readonly List<(GridLength width, double minWidth)> _lastVisibleColumnDefs = [];
    private readonly List<Control> _headerCells = [];
    private readonly List<bool> _columnVisibility = [];
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
    private readonly List<ListViewSortDescriptor> _sortDescriptors = [];
    private readonly List<TextBlock> _sortIndicators = [];
    private KeyModifiers _pendingHeaderModifiers;

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
    /// Fired after a user shows or hides a column from the header menu.
    /// </summary>
    public event EventHandler<ListViewColumnVisibilityChangedEventArgs>? ColumnVisibilityChanged;

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
    public IBrush SelectedRowBrush { get; set; } = ThemeService.GetBrush("RowSelectedBrush", "#094771");

    /// <summary>
    /// Brush used for the hovered (non-selected) row.
    /// </summary>
    public IBrush HoverRowBrush { get; set; } = ThemeService.GetBrush("RowHoverBrush", "#2A2D2E");

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
    /// Gets the complete primary-to-last tie-breaker sort chain.
    /// </summary>
    public IReadOnlyList<ListViewSortDescriptor> SortDescriptors => _sortDescriptors;

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
        _scrollViewer = new ScrollViewer
        {
            Background = Brushes.Transparent,
            AllowAutoHide = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
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

        if (string.IsNullOrWhiteSpace(column.Key))
        {
            var baseKey = string.IsNullOrWhiteSpace(column.Header)
                ? $"column-{_columns.Count}"
                : new string(column.Header
                    .Trim()
                    .ToLowerInvariant()
                    .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
                    .ToArray())
                    .Trim('-');

            if (string.IsNullOrEmpty(baseKey))
                baseKey = $"column-{_columns.Count}";

            var key = baseKey;
            var suffix = 2;
            while (_columns.Any(existing => existing.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                key = $"{baseKey}-{suffix++}";
            column.Key = key;
        }
        else if (_columns.Any(existing =>
                     existing.Key.Equals(column.Key, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Duplicate list column key '{column.Key}'.", nameof(column));
        }

        _columns.Add(column);
        _columnVisibility.Add(column.IsVisibleByDefault);
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
        _lastVisibleColumnDefs.Clear();
        for (int i = 0; i < _columnGridIndices.Count; i++)
        {
            var gridCol = _columnGridIndices[i];
            var colDef = _headerGrid.ColumnDefinitions[gridCol];
            _originalColumnDefs.Add((colDef.Width, colDef.MinWidth));
            _lastVisibleColumnDefs.Add((colDef.Width, colDef.MinWidth));
        }

        BuildHeaderContextMenu();

        // Enable keyboard navigation
        Focusable = true;
        KeyDown += HandleKeyDown;

        BuildScrollLayout();

        for (var i = 0; i < _columnVisibility.Count; i++)
            ApplyColumnVisibility(i, _columnVisibility[i], raiseEvent: false);
    }

    private void BuildHeaderContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) => PopulateHeaderContextMenu(menu);
        PopulateHeaderContextMenu(menu);
        _headerBorder.ContextMenu = menu;
    }

    private void PopulateHeaderContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();

        var autoResizeItem = new MenuItem { Header = "Auto Size Visible Columns" };
        autoResizeItem.Click += (_, _) => AutoResizeColumns();
        menu.Items.Add(autoResizeItem);

        var resetItem = new MenuItem { Header = "Reset Column Layout" };
        resetItem.Click += (_, _) =>
        {
            ResetColumnWidths();
            for (var i = 0; i < _columns.Count; i++)
                SetColumnVisible(i, _columns[i].IsVisibleByDefault);
        };
        menu.Items.Add(resetItem);

        var sortableColumns = _columns
            .Select((column, index) => (column, index))
            .Where(item => item.column.CanSort && !item.column.IsFixedWidth)
            .ToList();

        if (sortableColumns.Count > 0)
        {
            menu.Items.Add(new Separator());
            var advancedSortItem = new MenuItem { Header = "Advanced Sorting..." };
            advancedSortItem.Click += async (_, _) =>
                await ShowAdvancedSortDialogAsync(sortableColumns);
            menu.Items.Add(advancedSortItem);
        }

        if (_sortDescriptors.Count > 0)
        {
            var clearSortItem = new MenuItem { Header = "Clear Sorting" };
            clearSortItem.Click += (_, _) => ClearSortDescriptors();
            menu.Items.Add(clearSortItem);
        }

        var configurableColumns = _columns
            .Select((column, index) => (column, index))
            .Where(item => item.column.CanUserHide && !string.IsNullOrWhiteSpace(item.column.Header))
            .ToList();

        if (configurableColumns.Count > 0)
        {
            menu.Items.Add(new Separator());
            var columnsMenu = new MenuItem { Header = "Columns" };
            foreach (var (column, index) in configurableColumns)
            {
                var columnItem = new MenuItem
                {
                    Header = column.Header,
                    Icon = IsColumnVisible(index) ? new TextBlock { Text = "\u2713" } : null
                };
                columnItem.Click += (_, _) => SetColumnVisible(index, !IsColumnVisible(index));
                columnsMenu.Items.Add(columnItem);
            }
            menu.Items.Add(columnsMenu);
        }

        if (sortableColumns.Count > 0)
        {
            var sortHelp = new MenuItem { Header = "Shortcut Info..." };
            sortHelp.Click += async (_, _) =>
                await ShowSortingShortcutInfoAsync();
            menu.Items.Add(new Separator());
            menu.Items.Add(sortHelp);
        }
    }

    private async Task ShowSortingShortcutInfoAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var dialog = new Window
        {
            Title = "Sorting Shortcuts",
            Width = 470,
            Height = 300,
            MinWidth = 420,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var content = new StackPanel { Spacing = 5 };
        content.Children.Add(new TextBlock
        {
            Text = "Header sorting shortcuts",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        content.Children.Add(CreateSortingShortcutRow(
            "Click",
            "Make the column the primary sort. Click it again to reverse the direction."));
        content.Children.Add(CreateSortingShortcutRow(
            "Shift + Click",
            "Add a tie-breaker, or reverse that sort level."));
        content.Children.Add(CreateSortingShortcutRow(
            "Ctrl + Click",
            "Remove that column from the current sort chain."));
        content.Children.Add(CreateSortingShortcutRow(
            "Advanced Sorting",
            "Add, remove, reorder, and edit every sort level in one place."));

        var okButton = new Button
        {
            Content = "OK",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        okButton.Classes.Add("accent");
        okButton.Click += (_, _) => dialog.Close();
        content.Children.Add(okButton);

        dialog.Content = new Border
        {
            Padding = new Thickness(16),
            Child = content
        };
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Escape or Key.Enter))
                return;

            dialog.Close();
            e.Handled = true;
        };
        dialog.Opened += (_, _) => okButton.Focus();

        await dialog.ShowDialog(owner);
    }

    private static Control CreateSortingShortcutRow(
        string shortcut,
        string explanation)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("125,*"),
            Margin = new Thickness(0, 3)
        };
        row.Children.Add(new TextBlock
        {
            Text = shortcut,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Top
        });

        var explanationText = new TextBlock
        {
            Text = explanation,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(explanationText, 1);
        row.Children.Add(explanationText);
        return row;
    }

    private async Task ShowAdvancedSortDialogAsync(
        IReadOnlyList<(ListViewColumn column, int index)> sortableColumns)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var columnOptions = sortableColumns
            .Select(item => new AdvancedSortColumnOption(
                item.index,
                item.column.Key,
                item.column.Header,
                item.column.DefaultSortDescending,
                IsColumnVisible(item.index)))
            .ToArray();

        var dialog = new AdvancedSortDialog(
            columnOptions,
            _sortDescriptors.ToArray());
        await dialog.ShowDialog(owner);
        if (dialog.Confirmed)
            SetSortDescriptors(dialog.Result, raiseEvent: true);
    }

    private void BuildScrollLayout()
    {
        // A dedicated vertical bar sits beside both header and rows. This keeps
        // the header and row viewport exactly the same width when the bar appears.
        _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scrollViewer.AllowAutoHide = false;

        Content = null;

        Control contentHost = _root;
        if (_overflowMode != ListViewOverflowMode.Fill)
        {
            var outerScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = _overflowMode == ListViewOverflowMode.Scroll
                    ? ScrollBarVisibility.Visible
                    : ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                AllowAutoHide = false,
                Content = _root
            };

            outerScroll.PropertyChanged += (_, e) =>
            {
                if (e.Property != ScrollViewer.ViewportProperty
                    && e.Property != ScrollViewer.BoundsProperty)
                {
                    return;
                }

                var viewportWidth = outerScroll.Viewport.Width;
                if (viewportWidth > 0)
                    _root.MinWidth = viewportWidth;
            };
            contentHost = outerScroll;
        }

        var verticalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            IsVisible = false,
            AllowAutoHide = false
        };

        var syncing = false;
        _scrollViewer.PropertyChanged += (_, e) =>
        {
            if (syncing)
                return;

            if (e.Property == ScrollViewer.ExtentProperty
                || e.Property == ScrollViewer.ViewportProperty
                || e.Property == ScrollViewer.OffsetProperty)
            {
                syncing = true;
                var maximum = Math.Max(
                    0,
                    _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
                verticalScrollBar.Maximum = maximum;
                verticalScrollBar.ViewportSize = _scrollViewer.Viewport.Height;
                verticalScrollBar.Value = _scrollViewer.Offset.Y;
                verticalScrollBar.IsVisible = maximum > 0;
                syncing = false;
            }

            if (e.Property == ScrollViewer.OffsetProperty)
                ClearStaleHoverAfterScroll();
        };

        verticalScrollBar.PropertyChanged += (_, e) =>
        {
            if (syncing || e.Property != RangeBase.ValueProperty)
                return;

            syncing = true;
            _scrollViewer.Offset = _scrollViewer.Offset.WithY(verticalScrollBar.Value);
            syncing = false;
        };

        var layoutGrid = new Grid();
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(contentHost, 0);
        Grid.SetColumn(verticalScrollBar, 1);
        layoutGrid.Children.Add(contentHost);
        layoutGrid.Children.Add(verticalScrollBar);
        Content = layoutGrid;
    }

    private void ClearStaleHoverAfterScroll()
    {
        if (_hoveredRow == null)
            return;

        var oldHover = _hoveredRow;
        _hoveredRow = null;
        if (oldHover.Background == HoverRowBrush)
            oldHover.Background = GetBaseRowBrush(oldHover);
        RowPointerExited?.Invoke(
            this,
            new ListViewRowEventArgs(oldHover.DataContext, oldHover));
    }

    private void BuildHeaderGrid()
    {
        _headerGrid.ColumnDefinitions.Clear();
        _headerGrid.Children.Clear();
        _columnGridIndices.Clear();
        _headerCells.Clear();
        _sortIndicators.Clear();

        for (int i = 0; i < _columns.Count; i++)
        {
            var col = _columns[i];
            _columnGridIndices.Add(i);
            var colDef = col.IsStar
                ? new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = col.MinWidth }
                : new ColumnDefinition(new GridLength(col.Width)) { MinWidth = col.MinWidth };
            _headerGrid.ColumnDefinitions.Add(colDef);

            var headerHost = new Grid
            {
                ClipToBounds = true
            };

            var sortable = col.CanSort && !col.IsFixedWidth;
            if (sortable)
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

                var logicalIndex = i;
                var btn = new Button
                {
                    Content = headerPanel,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Classes = { "headerButton" }
                };
                ToolTip.SetTip(
                    btn,
                    "Click to sort. Shift+click adds a tie-breaker; Ctrl+click removes it.");
                // Button handles PointerPressed internally, so a normal event
                // subscription can miss the modifiers. Listen on the tunnel and
                // include already-handled events so Shift/Ctrl clicks are reliable.
                btn.AddHandler(
                    PointerPressedEvent,
                    (_, e) => _pendingHeaderModifiers = e.KeyModifiers,
                    RoutingStrategies.Tunnel,
                    handledEventsToo: true);
                btn.Click += (_, _) =>
                {
                    var modifiers = _pendingHeaderModifiers;
                    _pendingHeaderModifiers = KeyModifiers.None;
                    HandleSortClick(logicalIndex, modifiers);
                };
                headerHost.Children.Add(btn);
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
                headerHost.Children.Add(txt);
                _sortIndicators.Add(null!);
            }

            if (!col.IsFixedWidth)
            {
                headerHost.Children.Add(CreateResizeHandle(i));
            }

            Grid.SetColumn(headerHost, i);
            _headerGrid.Children.Add(headerHost);
            _headerCells.Add(headerHost);
        }
    }

    private Control CreateResizeHandle(int logicalColumnIndex)
    {
        var handle = new Border
        {
            Width = 5,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var dragging = false;
        var dragStartX = 0d;
        var dragStartWidth = 0d;

        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
                return;

            dragging = true;
            dragStartX = e.GetPosition(_headerGrid).X;
            dragStartWidth = _headerGrid.ColumnDefinitions[logicalColumnIndex].ActualWidth;
            e.Pointer.Capture(handle);
            e.Handled = true;
        };

        handle.PointerMoved += (_, e) =>
        {
            if (!dragging)
                return;

            var delta = e.GetPosition(_headerGrid).X - dragStartX;
            var newWidth = Math.Max(
                _columns[logicalColumnIndex].MinWidth,
                dragStartWidth + delta);
            _headerGrid.ColumnDefinitions[logicalColumnIndex].Width =
                new GridLength(newWidth);
            e.Handled = true;
        };

        handle.PointerReleased += (_, e) =>
        {
            if (!dragging)
                return;

            dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };

        handle.DoubleTapped += (_, e) =>
        {
            AutoResizeColumn(logicalColumnIndex);
            e.Handled = true;
        };

        return handle;
    }

    private void BuildItemTemplate()
    {
        // Build the column definitions list to reuse in each row
        var colDefs = new List<(GridLength width, double minWidth)>();
        for (int i = 0; i < _columns.Count; i++)
        {
            var col = _columns[i];
            colDefs.Add(col.IsStar
                ? (new GridLength(1, GridUnitType.Star), col.MinWidth)
                : (new GridLength(col.Width), col.MinWidth));
        }

        // Capture references for the closure
        var columns = _columns;
        var rowHeight = RowHeight;
        var rowBaseBackgroundPath = RowBaseBackgroundPath;
        var rowHeightPath = RowHeightPath;
        var suppressHandCursor = SuppressHandCursor;

        _itemsControl.ItemsPanel = new FuncTemplate<Panel?>(() =>
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

            // The header ColumnDefinitions are the single source of truth. Rows
            // bind to the same logical GridLengths and constraints (including
            // star sizing), avoiding a second independent pixel-rounding pass.
            var grid = new Grid
            {
                Name = "RowGrid",
                // Width is bound to the header grid below. Explicitly anchor the
                // fixed-width row grid so any spare viewport space is kept on the
                // right instead of being split across both sides by layout.
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Bind(
                WidthProperty,
                new Binding("Bounds.Width")
                {
                    Source = _headerGrid,
                    Mode = BindingMode.OneWay
                });

            var headerCols = _headerGrid.ColumnDefinitions;
            if (headerCols.Count > 0 && headerCols.Count == colDefs.Count)
            {
                for (int ci = 0; ci < headerCols.Count; ci++)
                {
                    var rowColumn = new ColumnDefinition();
                    rowColumn.Bind(
                        ColumnDefinition.WidthProperty,
                        new Binding(nameof(ColumnDefinition.Width))
                        {
                            Source = headerCols[ci],
                            Mode = BindingMode.OneWay
                        });
                    rowColumn.Bind(
                        ColumnDefinition.MinWidthProperty,
                        new Binding(nameof(ColumnDefinition.MinWidth))
                        {
                            Source = headerCols[ci],
                            Mode = BindingMode.OneWay
                        });
                    grid.ColumnDefinitions.Add(rowColumn);
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
            for (int i = 0; i < columns.Count; i++)
            {
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

                Grid.SetColumn(cellControl, i);
                grid.Children.Add(cellControl);
            }

            border.Child = grid;
            return border;
        });
    }

    /// <summary>
    /// Invalidates realized rows so their direct header-width bindings are
    /// re-evaluated. Width copying is intentionally not used.
    /// </summary>
    public void SyncColumnWidths()
    {
        foreach (var container in _itemsControl.GetRealizedContainers())
        {
            var rowGrid = container.FindDescendantOfType<Grid>();
            if (rowGrid?.Name == "RowGrid")
                rowGrid.InvalidateMeasure();
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

        if (!IsColumnVisible(logicalColumnIndex)
            && logicalColumnIndex < _lastVisibleColumnDefs.Count)
        {
            _lastVisibleColumnDefs[logicalColumnIndex] =
                (width, _lastVisibleColumnDefs[logicalColumnIndex].minWidth);
            return;
        }

        _headerGrid.ColumnDefinitions[gridCol].Width = width;
        if (logicalColumnIndex < _lastVisibleColumnDefs.Count)
        {
            _lastVisibleColumnDefs[logicalColumnIndex] =
                (width, _headerGrid.ColumnDefinitions[gridCol].MinWidth);
        }
    }

    /// <summary>
    /// Returns the current visible width, or the width retained while a column
    /// is hidden.
    /// </summary>
    public double GetColumnWidth(int logicalColumnIndex)
    {
        var gridColumn = GetGridColumnIndex(logicalColumnIndex);
        if (gridColumn < 0 || gridColumn >= _headerGrid.ColumnDefinitions.Count)
            return 0;

        if (IsColumnVisible(logicalColumnIndex))
        {
            var actualWidth = _headerGrid.ColumnDefinitions[gridColumn].ActualWidth;
            if (actualWidth > 0)
                return actualWidth;
        }

        if (logicalColumnIndex < _lastVisibleColumnDefs.Count)
        {
            var retained = _lastVisibleColumnDefs[logicalColumnIndex].width;
            if (retained.GridUnitType == GridUnitType.Pixel)
                return retained.Value;
        }

        return 0;
    }

    /// <summary>
    /// Shows or hides a logical column. When hidden, the column width and MinWidth
    /// are set to 0 so remaining columns (especially star-sized) fill the freed space.
    /// When shown, its most recent visible width is restored.
    /// </summary>
    public void SetColumnVisible(int logicalColumnIndex, bool visible)
    {
        if (logicalColumnIndex < 0 || logicalColumnIndex >= _columns.Count)
            return;

        if (!_isBuilt)
        {
            _columnVisibility[logicalColumnIndex] = visible;
            return;
        }

        ApplyColumnVisibility(logicalColumnIndex, visible, raiseEvent: true);
    }

    /// <summary>Shows or hides a column using its stable key.</summary>
    public void SetColumnVisible(string columnKey, bool visible)
    {
        var index = FindColumnIndex(columnKey);
        if (index >= 0)
            SetColumnVisible(index, visible);
    }

    /// <summary>Returns whether a logical column is currently visible.</summary>
    public bool IsColumnVisible(int logicalColumnIndex)
    {
        return logicalColumnIndex >= 0
            && logicalColumnIndex < _columnVisibility.Count
            && _columnVisibility[logicalColumnIndex];
    }

    /// <summary>Returns whether a keyed column is currently visible.</summary>
    public bool IsColumnVisible(string columnKey)
    {
        var index = FindColumnIndex(columnKey);
        return index >= 0 && IsColumnVisible(index);
    }

    private int FindColumnIndex(string columnKey)
    {
        for (var i = 0; i < _columns.Count; i++)
        {
            if (_columns[i].Key.Equals(columnKey, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private void ApplyColumnVisibility(int logicalColumnIndex, bool visible, bool raiseEvent)
    {
        var gridCol = GetGridColumnIndex(logicalColumnIndex);
        if (gridCol < 0 || gridCol >= _headerGrid.ColumnDefinitions.Count)
            return;
        if (logicalColumnIndex >= _lastVisibleColumnDefs.Count)
            return;

        var changed = _columnVisibility[logicalColumnIndex] != visible;
        var columnDefinition = _headerGrid.ColumnDefinitions[gridCol];

        if (visible)
        {
            var previous = _lastVisibleColumnDefs[logicalColumnIndex];
            columnDefinition.Width = previous.width;
            columnDefinition.MinWidth = previous.minWidth;
        }
        else
        {
            if (_columnVisibility[logicalColumnIndex])
            {
                _lastVisibleColumnDefs[logicalColumnIndex] =
                    (columnDefinition.Width, columnDefinition.MinWidth);
            }
            columnDefinition.Width = new GridLength(0);
            columnDefinition.MinWidth = 0;
        }

        _columnVisibility[logicalColumnIndex] = visible;
        if (logicalColumnIndex < _headerCells.Count)
            _headerCells[logicalColumnIndex].IsVisible = visible;

        if (changed && raiseEvent)
        {
            ColumnVisibilityChanged?.Invoke(
                this,
                new ListViewColumnVisibilityChangedEventArgs(
                    logicalColumnIndex,
                    _columns[logicalColumnIndex].Key,
                    visible));
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
        _selectedRow = null;

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
                if (Equals(b.DataContext, _selectedItem))
                    _selectedRow = b;
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
    private double GetViewportHeight()
    {
        double boundsHeight = _scrollViewer.Bounds.Height;
        double viewportHeight = _scrollViewer.Viewport.Height;

        if (boundsHeight > 0 && viewportHeight > 0)
            return Math.Min(boundsHeight, viewportHeight);

        return boundsHeight > 0 ? boundsHeight : viewportHeight;
    }

    private Border? FindRealizedRowBorder(object? item)
    {
        if (item == null)
            return null;

        foreach (var container in _itemsControl.GetRealizedContainers())
        {
            Border? border = null;
            if (container is ContentPresenter presenter)
                border = presenter.Child as Border;
            border ??= container.FindDescendantOfType<Border>();

            if (border?.DataContext != null && Equals(border.DataContext, item))
                return border;
        }

        return null;
    }

    private double GetEstimatedItemExtent(int itemCount)
    {
        if (itemCount > 0 && _scrollViewer.Extent.Height > 0)
        {
            var extentPerItem = _scrollViewer.Extent.Height / itemCount;
            if (extentPerItem > 0)
                return extentPerItem;
        }

        return RowHeight;
    }

    private int GetPageSelectionStep(int itemCount)
    {
        double itemExtent = GetEstimatedItemExtent(itemCount);
        double viewportHeight = GetViewportHeight();
        if (itemExtent <= 0 || viewportHeight <= 0)
            return 1;

        int visibleItems = (int)Math.Floor(viewportHeight / itemExtent);
        return Math.Max(1, visibleItems - 1);
    }

    private void SetVerticalOffset(double offset)
    {
        double viewportHeight = GetViewportHeight();
        double maxOffset = Math.Max(0, _scrollViewer.Extent.Height - viewportHeight);
        double clampedOffset = Math.Clamp(offset, 0, maxOffset);

        if (Math.Abs(clampedOffset - _scrollViewer.Offset.Y) <= ScrollAlignmentTolerance)
            return;

        _scrollViewer.Offset = _scrollViewer.Offset.WithY(clampedOffset);
    }

    private void ScrollItemIntoView(int index)
    {
        var items = GetItemsList();
        if (items != null && index >= 0 && index < items.Count)
        {
            var targetItem = items[index];
            var realizedRow = FindRealizedRowBorder(items[index]);
            if (realizedRow != null)
            {
                realizedRow.BringIntoView();
                return;
            }

            double itemExtent = GetEstimatedItemExtent(items.Count);
            if (itemExtent > 0)
            {
                double estimatedTop = index * itemExtent;
                double estimatedBottom = estimatedTop + itemExtent;
                double estimatedViewportHeight = GetViewportHeight();
                double estimatedOffset = _scrollViewer.Offset.Y;

                if (estimatedTop < estimatedOffset)
                    SetVerticalOffset(estimatedTop);
                else if (estimatedBottom > estimatedOffset + estimatedViewportHeight)
                    SetVerticalOffset(estimatedBottom - estimatedViewportHeight);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!Equals(_selectedItem, targetItem))
                    return;

                FindRealizedRowBorder(targetItem)?.BringIntoView();
            }, DispatcherPriority.Loaded);
            return;
        }

        double targetTop = index * RowHeight;
        double targetBottom = targetTop + RowHeight;
        double viewportHeight = GetViewportHeight();
        double currentOffset = _scrollViewer.Offset.Y;

        if (targetTop < currentOffset)
            SetVerticalOffset(targetTop);
        else if (targetBottom > currentOffset + viewportHeight)
            SetVerticalOffset(targetBottom - viewportHeight);
    }

    /// <summary>
    /// Handles keyboard navigation: Up/Down to move selection, PageUp/PageDown to move
    /// by one viewport, Home/End to jump, Shift+Arrow to extend selection in multi-mode,
    /// Enter to activate, Ctrl+A to select all.
    /// </summary>
    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (_selectionMode == ListViewSelectionMode.None) return;

        var items = GetItemsList();
        if (items == null || items.Count == 0) return;

        int currentIndex = _selectedItem != null ? items.IndexOf(_selectedItem) : -1;
        int pageStep = GetPageSelectionStep(items.Count);

        switch (e.Key)
        {
            case Key.Up:
            {
                int newIndex = currentIndex > 0 ? currentIndex - 1 : 0;
                if (newIndex == currentIndex)
                {
                    if (currentIndex >= 0)
                        ScrollItemIntoView(currentIndex);
                    e.Handled = true;
                    break;
                }

                SelectByIndex(items, newIndex, e.KeyModifiers);
                e.Handled = true;
                break;
            }
            case Key.Down:
            {
                int newIndex = currentIndex < items.Count - 1 ? currentIndex + 1 : items.Count - 1;
                if (newIndex == currentIndex)
                {
                    if (currentIndex >= 0)
                        ScrollItemIntoView(currentIndex);
                    e.Handled = true;
                    break;
                }

                SelectByIndex(items, newIndex, e.KeyModifiers);
                e.Handled = true;
                break;
            }
            case Key.PageUp:
            {
                int newIndex = currentIndex >= 0
                    ? Math.Max(0, currentIndex - pageStep)
                    : 0;
                SelectByIndex(items, newIndex, e.KeyModifiers);
                e.Handled = true;
                break;
            }
            case Key.PageDown:
            {
                int newIndex = currentIndex >= 0
                    ? Math.Min(items.Count - 1, currentIndex + pageStep)
                    : 0;
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
    /// Handles a sort header click. A plain click replaces the chain,
    /// Shift+click adds or toggles a tie-breaker, and Ctrl+click removes one.
    /// </summary>
    private void HandleSortClick(int logicalColumnIndex, KeyModifiers modifiers)
    {
        if (logicalColumnIndex < 0 || logicalColumnIndex >= _columns.Count)
            return;

        var existingIndex = _sortDescriptors.FindIndex(
            descriptor => descriptor.ColumnIndex == logicalColumnIndex);

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            if (existingIndex >= 0)
                _sortDescriptors.RemoveAt(existingIndex);
        }
        else if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            if (existingIndex >= 0)
            {
                var existing = _sortDescriptors[existingIndex];
                _sortDescriptors[existingIndex] = existing with
                {
                    Ascending = !existing.Ascending
                };
            }
            else
            {
                _sortDescriptors.Add(CreateDefaultSortDescriptor(logicalColumnIndex));
            }
        }
        else
        {
            var toggleExisting = existingIndex == 0;
            var ascending = toggleExisting
                ? !_sortDescriptors[0].Ascending
                : !_columns[logicalColumnIndex].DefaultSortDescending;

            _sortDescriptors.Clear();
            _sortDescriptors.Add(new ListViewSortDescriptor(
                logicalColumnIndex,
                _columns[logicalColumnIndex].Key,
                ascending));
        }

        SyncLegacySortProperties();
        UpdateSortIndicators();
        var clicked = _sortDescriptors.FirstOrDefault(
            descriptor => descriptor.ColumnIndex == logicalColumnIndex);
        SortRequested?.Invoke(
            this,
            new ListViewSortEventArgs(
                logicalColumnIndex,
                clicked?.Ascending ?? true,
                _sortDescriptors.ToArray()));
    }

    private ListViewSortDescriptor CreateDefaultSortDescriptor(int logicalColumnIndex)
    {
        return new ListViewSortDescriptor(
            logicalColumnIndex,
            _columns[logicalColumnIndex].Key,
            !_columns[logicalColumnIndex].DefaultSortDescending);
    }

    /// <summary>
    /// Replaces the visual sort chain, usually when restoring persisted settings.
    /// Invalid, duplicate, and non-sortable columns are ignored.
    /// </summary>
    public void SetSortDescriptors(
        IEnumerable<ListViewSortDescriptor>? descriptors,
        bool raiseEvent = false)
    {
        _sortDescriptors.Clear();
        if (descriptors != null)
        {
            foreach (var descriptor in descriptors)
            {
                var columnIndex = FindColumnIndex(descriptor.ColumnKey);
                if (columnIndex < 0
                    && descriptor.ColumnIndex >= 0
                    && descriptor.ColumnIndex < _columns.Count)
                {
                    columnIndex = descriptor.ColumnIndex;
                }

                if (columnIndex < 0)
                    continue;

                var column = _columns[columnIndex];
                if (!column.CanSort
                    || _sortDescriptors.Any(item => item.ColumnIndex == columnIndex))
                {
                    continue;
                }

                _sortDescriptors.Add(new ListViewSortDescriptor(
                    columnIndex,
                    column.Key,
                    descriptor.Ascending));
            }
        }

        SyncLegacySortProperties();
        UpdateSortIndicators();
        if (raiseEvent)
            RaiseSortRequested();
    }

    /// <summary>Clears all sorting and notifies the consumer.</summary>
    public void ClearSortDescriptors()
    {
        if (_sortDescriptors.Count == 0)
            return;

        _sortDescriptors.Clear();
        SyncLegacySortProperties();
        UpdateSortIndicators();
        RaiseSortRequested();
    }

    private void RaiseSortRequested()
    {
        SortRequested?.Invoke(
            this,
            new ListViewSortEventArgs(
                _sortColumnIndex,
                _sortAscending,
                _sortDescriptors.ToArray()));
    }

    private void SyncLegacySortProperties()
    {
        if (_sortDescriptors.Count == 0)
        {
            _sortColumnIndex = -1;
            _sortAscending = true;
            return;
        }

        _sortColumnIndex = _sortDescriptors[0].ColumnIndex;
        _sortAscending = _sortDescriptors[0].Ascending;
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

            var priority = _sortDescriptors.FindIndex(
                descriptor => descriptor.ColumnIndex == i);
            if (priority >= 0)
            {
                var descriptor = _sortDescriptors[priority];
                var arrow = descriptor.Ascending ? "\u25B2" : "\u25BC";
                indicator.Text = _sortDescriptors.Count > 1
                    ? $"{priority + 1}{arrow}"
                    : arrow;
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
            _lastVisibleColumnDefs[i] = original;
            if (IsColumnVisible(i))
            {
                _headerGrid.ColumnDefinitions[gridCol].Width = original.width;
                _headerGrid.ColumnDefinitions[gridCol].MinWidth = original.minWidth;
            }
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
        if (!IsColumnVisible(logicalColumnIndex)) return;
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
        _lastVisibleColumnDefs[logicalColumnIndex] =
            (new GridLength(newWidth), col.MinWidth);
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
            if (!IsColumnVisible(i) || _columns[i].IsFixedWidth || _columns[i].IsStar) continue;
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
                if (!IsColumnVisible(i) || _columns[i].IsFixedWidth || _columns[i].IsStar) continue;
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
            if (!IsColumnVisible(i) || _columns[i].IsFixedWidth || _columns[i].IsStar) continue;
            var gridCol = _columnGridIndices[i];
            if (gridCol >= _headerGrid.ColumnDefinitions.Count) continue;

            var newWidth = Math.Max(maxWidths[i], _columns[i].MinWidth);
            _headerGrid.ColumnDefinitions[gridCol].Width = new GridLength(newWidth);
            _lastVisibleColumnDefs[i] =
                (new GridLength(newWidth), _columns[i].MinWidth);
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
