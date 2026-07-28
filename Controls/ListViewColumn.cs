using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZScape.Controls;

/// <summary>
/// Defines a column for use with <see cref="ResizableListView"/>.
/// Supports simple text binding via <see cref="BindingPath"/>, or fully
/// custom cell content via <see cref="CellContentFactory"/>.
/// </summary>
public class ListViewColumn
{
    /// <summary>
    /// Stable identifier used for persisted visibility, widths, and sorting.
    /// When omitted, the control derives a key from <see cref="Header"/>.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>Column header text.</summary>
    public string Header { get; set; } = "";

    /// <summary>Initial column width in pixels. Use 0 for star-sized.</summary>
    public double Width { get; set; }

    /// <summary>If true, this column gets remaining space (star-sized).</summary>
    public bool IsStar { get; set; }

    /// <summary>Minimum column width in pixels.</summary>
    public double MinWidth { get; set; } = 30;

    /// <summary>
    /// Property path for simple text binding (e.g. "Name", "SizeDisplay").
    /// Ignored if <see cref="CellContentFactory"/> is set.
    /// </summary>
    public string? BindingPath { get; set; }

    /// <summary>Text trimming mode for simple text cells.</summary>
    public TextTrimming TextTrimming { get; set; } = TextTrimming.None;

    /// <summary>Foreground brush for simple text cells. Null uses default.</summary>
    public IBrush? Foreground { get; set; }

    /// <summary>Horizontal alignment for simple text cells.</summary>
    public HorizontalAlignment ContentAlignment { get; set; } = HorizontalAlignment.Left;

    /// <summary>Padding for simple text cells.</summary>
    public Thickness CellPadding { get; set; } = new(4, 0);

    /// <summary>
    /// If true, this column has no header button and no splitter (e.g. icon columns).
    /// The header text is rendered as a plain TextBlock instead.
    /// </summary>
    public bool IsFixedWidth { get; set; }

    /// <summary>
    /// Whether the user can show or hide this column from the header context menu.
    /// Essential identity columns should set this to false.
    /// </summary>
    public bool CanUserHide { get; set; } = true;

    /// <summary>
    /// Initial visibility before a persisted preference is applied.
    /// Optional columns should set this to false.
    /// </summary>
    public bool IsVisibleByDefault { get; set; } = true;

    /// <summary>
    /// Factory to create custom cell content for complex columns.
    /// Receives the data context and returns a Control to display.
    /// When set, <see cref="BindingPath"/> is ignored.
    /// </summary>
    public System.Func<Control>? CellContentFactory { get; set; }

    /// <summary>
    /// Enables the reusable multi-column sorting UI without requiring a legacy
    /// per-column click handler. Shift+click appends a tie-breaker and
    /// Ctrl+click removes one.
    /// </summary>
    public bool CanSort { get; set; }

    /// <summary>
    /// When true, clicking this column for the first time sorts descending
    /// instead of ascending (e.g. "Players" column where highest-first is expected).
    /// Only relevant when <see cref="CanSort"/> is set.
    /// </summary>
    public bool DefaultSortDescending { get; set; }
}
