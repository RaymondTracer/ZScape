using ZScape.Utilities;

namespace ZScape.UI;

/// <summary>
/// Dark theme colors and styling for the application.
/// </summary>
public static class DarkTheme
{
    // Primary colors
    public static readonly Color PrimaryBackground = Color.FromArgb(30, 30, 30);       // #1E1E1E
    public static readonly Color SecondaryBackground = Color.FromArgb(37, 37, 38);     // #252526
    public static readonly Color TertiaryBackground = Color.FromArgb(45, 45, 48);      // #2D2D30
    public static readonly Color BorderColor = Color.FromArgb(62, 62, 66);             // #3E3E42
    
    // Accent colors
    public static readonly Color AccentColor = Color.FromArgb(0, 122, 204);            // #007ACC
    public static readonly Color AccentHover = Color.FromArgb(28, 151, 234);           // #1C97EA
    public static readonly Color SelectionColor = Color.FromArgb(51, 51, 52);          // #333334
    public static readonly Color HighlightColor = Color.FromArgb(62, 62, 64);          // #3E3E40
    
    // Text colors
    public static readonly Color TextPrimary = Color.FromArgb(204, 204, 204);          // #CCCCCC
    public static readonly Color TextSecondary = Color.FromArgb(153, 153, 153);        // #999999
    public static readonly Color TextDisabled = Color.FromArgb(92, 92, 92);            // #5C5C5C
    
    // Status colors
    public static readonly Color SuccessColor = Color.FromArgb(78, 201, 176);          // #4EC9B0
    public static readonly Color WarningColor = Color.FromArgb(220, 220, 170);         // #DCDCAA
    public static readonly Color ErrorColor = Color.FromArgb(241, 76, 76);             // #F14C4C
    public static readonly Color InfoColor = Color.FromArgb(86, 156, 214);             // #569CD6
    
    // Server list specific colors
    public static readonly Color FullServerRow = Color.FromArgb(60, 40, 40);           // Reddish tint
    public static readonly Color EmptyServerRow = Color.FromArgb(40, 40, 40);          // Slightly darker
    public static readonly Color PasswordedServerRow = Color.FromArgb(60, 50, 40);     // Orange tint

    /// <summary>
    /// Applies the dark theme to a form and all its child controls.
    /// </summary>
    public static void Apply(Control control)
    {
        control.BackColor = PrimaryBackground;
        control.ForeColor = TextPrimary;
        
        // Apply dark title bar for Forms on Windows
        if (control is Form form)
        {
            DarkModeHelper.ApplyDarkTitleBar(form);
        }

        foreach (Control child in control.Controls)
        {
            ApplyToControl(child);
        }
    }

    /// <summary>
    /// Applies theme to a specific control based on its type.
    /// </summary>
    public static void ApplyToControl(Control control)
    {
        switch (control)
        {
            case DataGridView dgv:
                ApplyToDataGridView(dgv);
                break;
            case MenuStrip menu:
                ApplyToMenuStrip(menu);
                break;
            case StatusStrip statusStrip:
                ApplyToStatusStrip(statusStrip);
                break;
            case ToolStrip toolStrip:
                ApplyToToolStrip(toolStrip);
                break;
            case TextBox textBox:
                ApplyToTextBox(textBox);
                break;
            case ComboBox comboBox:
                ApplyToComboBox(comboBox);
                break;
            case Button button:
                ApplyToButton(button);
                break;
            case CheckBox checkBox:
                ApplyToCheckBox(checkBox);
                break;
            case Panel panel:
                ApplyToPanel(panel);
                break;
            case SplitContainer split:
                ApplyToSplitContainer(split);
                break;
            case ListView listView:
                ApplyToListView(listView);
                break;
            case Label:
            case GroupBox:
                control.BackColor = Color.Transparent;
                control.ForeColor = TextPrimary;
                break;
            default:
                control.BackColor = PrimaryBackground;
                control.ForeColor = TextPrimary;
                break;
        }

        // Recursively apply to children
        foreach (Control child in control.Controls)
        {
            ApplyToControl(child);
        }
    }

    public static void ApplyToDataGridView(DataGridView dgv)
    {
        dgv.BackgroundColor = SecondaryBackground;
        dgv.GridColor = BorderColor;
        dgv.ForeColor = TextPrimary;
        dgv.BorderStyle = BorderStyle.None;
        dgv.EnableHeadersVisualStyles = false;
        dgv.RowHeadersVisible = false;
        dgv.AllowUserToAddRows = false;
        dgv.AllowUserToDeleteRows = false;
        dgv.AllowUserToResizeRows = false;
        dgv.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        dgv.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;

        dgv.DefaultCellStyle.BackColor = SecondaryBackground;
        dgv.DefaultCellStyle.ForeColor = TextPrimary;
        dgv.DefaultCellStyle.SelectionBackColor = AccentColor;
        dgv.DefaultCellStyle.SelectionForeColor = Color.White;
        dgv.DefaultCellStyle.Padding = new Padding(5, 3, 5, 3);

        dgv.ColumnHeadersDefaultCellStyle.BackColor = TertiaryBackground;
        dgv.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
        dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = TertiaryBackground;
        dgv.ColumnHeadersDefaultCellStyle.Font = new Font(dgv.Font, FontStyle.Bold);
        dgv.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        dgv.ColumnHeadersHeight = 32;

        dgv.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(35, 35, 35);
    }

    public static void ApplyToMenuStrip(MenuStrip menu)
    {
        menu.BackColor = TertiaryBackground;
        menu.ForeColor = TextPrimary;
        menu.Renderer = new DarkMenuRenderer();

        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripMenuItem menuItem)
            {
                ApplyToMenuItem(menuItem);
            }
        }
    }

    private static void ApplyToMenuItem(ToolStripMenuItem menuItem)
    {
        menuItem.BackColor = TertiaryBackground;
        menuItem.ForeColor = TextPrimary;

        foreach (ToolStripItem subItem in menuItem.DropDownItems)
        {
            if (subItem is ToolStripMenuItem subMenuItem)
            {
                ApplyToMenuItem(subMenuItem);
            }
            else if (subItem is ToolStripSeparator separator)
            {
                separator.BackColor = BorderColor;
            }
        }
    }

    public static void ApplyToToolStrip(ToolStrip toolStrip)
    {
        toolStrip.BackColor = TertiaryBackground;
        toolStrip.ForeColor = TextPrimary;
        toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        toolStrip.Renderer = new DarkToolStripRenderer();
    }

    public static void ApplyToTextBox(TextBox textBox)
    {
        textBox.BackColor = TertiaryBackground;
        textBox.ForeColor = TextPrimary;
        textBox.BorderStyle = BorderStyle.FixedSingle;
    }

    public static void ApplyToComboBox(ComboBox comboBox)
    {
        comboBox.BackColor = TertiaryBackground;
        comboBox.ForeColor = TextPrimary;
        comboBox.FlatStyle = FlatStyle.Flat;
    }

    public static void ApplyToButton(Button button)
    {
        button.BackColor = TertiaryBackground;
        button.ForeColor = TextPrimary;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = BorderColor;
        button.FlatAppearance.MouseOverBackColor = HighlightColor;
        button.FlatAppearance.MouseDownBackColor = AccentColor;
        button.UseVisualStyleBackColor = false;
    }

    public static void ApplyToCheckBox(CheckBox checkBox)
    {
        checkBox.BackColor = Color.Transparent;
        checkBox.ForeColor = TextPrimary;
    }

    public static void ApplyToPanel(Panel panel)
    {
        panel.BackColor = SecondaryBackground;
        panel.ForeColor = TextPrimary;
    }

    public static void ApplyToSplitContainer(SplitContainer split)
    {
        split.BackColor = PrimaryBackground;
        split.Panel1.BackColor = SecondaryBackground;
        split.Panel2.BackColor = SecondaryBackground;
    }

    public static void ApplyToListView(ListView listView)
    {
        listView.BackColor = SecondaryBackground;
        listView.ForeColor = TextPrimary;
        listView.BorderStyle = BorderStyle.FixedSingle;
        
        // Enable double buffering to prevent flickering
        EnableDoubleBuffering(listView);
        
        // Owner-draw for proper dark theme rendering
        listView.OwnerDraw = true;
        listView.DrawColumnHeader += ListView_DrawColumnHeader;
        listView.DrawItem += ListView_DrawItem;
        listView.DrawSubItem += ListView_DrawSubItem;
    }
    
    private static void EnableDoubleBuffering(Control control)
    {
        // Use reflection to set the protected DoubleBuffered property
        typeof(Control).GetProperty("DoubleBuffered", 
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
            .SetValue(control, true);
    }
    
    private static void ListView_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var brush = new SolidBrush(TertiaryBackground);
        e.Graphics.FillRectangle(brush, e.Bounds);
        
        using var borderPen = new Pen(BorderColor);
        e.Graphics.DrawLine(borderPen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
        e.Graphics.DrawLine(borderPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        
        using var textBrush = new SolidBrush(TextPrimary);
        var format = new StringFormat
        {
            Alignment = e.Header?.TextAlign switch
            {
                HorizontalAlignment.Center => StringAlignment.Center,
                HorizontalAlignment.Right => StringAlignment.Far,
                _ => StringAlignment.Near
            },
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        
        var textRect = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        e.Graphics.DrawString(e.Header?.Text ?? "", e.Font ?? SystemFonts.DefaultFont, textBrush, textRect, format);
    }
    
    private static void ListView_DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        e.DrawDefault = false;
    }
    
    private static void ListView_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null) return;
        
        // Background
        Color bgColor;
        if (e.Item.Selected)
        {
            bgColor = AccentColor;
        }
        else if (e.ItemIndex % 2 == 1)
        {
            bgColor = Color.FromArgb(35, 35, 35);
        }
        else
        {
            bgColor = SecondaryBackground;
        }
        
        using var brush = new SolidBrush(bgColor);
        e.Graphics.FillRectangle(brush, e.Bounds);
        
        // Text - respect item's ForeColor for custom colored items
        Color textColor;
        if (e.Item.Selected)
        {
            textColor = Color.White;
        }
        else if (e.Item.ForeColor != SystemColors.WindowText && e.Item.ForeColor != TextPrimary)
        {
            // Item has a custom color set - use it
            textColor = e.Item.ForeColor;
        }
        else
        {
            textColor = TextPrimary;
        }
        
        using var textBrush = new SolidBrush(textColor);
        var format = new StringFormat
        {
            Alignment = e.Header?.TextAlign switch
            {
                HorizontalAlignment.Center => StringAlignment.Center,
                HorizontalAlignment.Right => StringAlignment.Far,
                _ => StringAlignment.Near
            },
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        
        var textRect = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        var text = e.SubItem?.Text ?? "";
        e.Graphics.DrawString(text, e.Item.Font ?? SystemFonts.DefaultFont, textBrush, textRect, format);
    }

    public static void ApplyToStatusStrip(StatusStrip statusStrip)
    {
        statusStrip.BackColor = TertiaryBackground;
        statusStrip.ForeColor = TextPrimary;
        statusStrip.Renderer = new DarkToolStripRenderer();
    }
}

/// <summary>
/// Custom renderer for dark-themed menus.
/// </summary>
public class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        Color color = e.Item.Selected 
            ? DarkTheme.HighlightColor 
            : DarkTheme.TertiaryBackground;
        
        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, rect);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? DarkTheme.TextPrimary : DarkTheme.TextDisabled;
        base.OnRenderItemText(e);
    }
}

/// <summary>
/// Custom renderer for dark-themed toolstrips.
/// </summary>
public class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    public DarkToolStripRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        Color color;
        Color? borderColor = null;

        // Check if this is a checked button (toggle button)
        bool isChecked = e.Item is ToolStripButton button && button.Checked;

        if (e.Item.Pressed)
        {
            color = DarkTheme.AccentColor;
        }
        else if (isChecked)
        {
            // Checked/toggled state - use accent color with slightly darker shade
            color = Color.FromArgb(0, 90, 158); // Darker blue
            borderColor = DarkTheme.AccentColor;
        }
        else if (e.Item.Selected)
        {
            color = DarkTheme.HighlightColor;
        }
        else
        {
            color = DarkTheme.TertiaryBackground;
        }

        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, rect);
        
        // Draw border for checked buttons
        if (borderColor.HasValue)
        {
            using var pen = new Pen(borderColor.Value, 1);
            e.Graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(DarkTheme.TertiaryBackground);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }
}

/// <summary>
/// Custom color table for dark theme.
/// </summary>
public class DarkColorTable : ProfessionalColorTable
{
    public override Color MenuBorder => DarkTheme.BorderColor;
    public override Color MenuItemBorder => DarkTheme.BorderColor;
    public override Color MenuItemSelected => DarkTheme.HighlightColor;
    public override Color MenuItemSelectedGradientBegin => DarkTheme.HighlightColor;
    public override Color MenuItemSelectedGradientEnd => DarkTheme.HighlightColor;
    public override Color MenuItemPressedGradientBegin => DarkTheme.AccentColor;
    public override Color MenuItemPressedGradientEnd => DarkTheme.AccentColor;
    public override Color MenuStripGradientBegin => DarkTheme.TertiaryBackground;
    public override Color MenuStripGradientEnd => DarkTheme.TertiaryBackground;
    public override Color ToolStripDropDownBackground => DarkTheme.SecondaryBackground;
    public override Color ImageMarginGradientBegin => DarkTheme.SecondaryBackground;
    public override Color ImageMarginGradientMiddle => DarkTheme.SecondaryBackground;
    public override Color ImageMarginGradientEnd => DarkTheme.SecondaryBackground;
    public override Color SeparatorDark => DarkTheme.BorderColor;
    public override Color SeparatorLight => DarkTheme.BorderColor;
    public override Color StatusStripGradientBegin => DarkTheme.TertiaryBackground;
    public override Color StatusStripGradientEnd => DarkTheme.TertiaryBackground;
}
