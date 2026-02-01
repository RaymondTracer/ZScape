using ZScape.Models;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.UI;

/// <summary>
/// Dialog for fetching WAD files by name with configurable download sources.
/// </summary>
public class FetchWadsDialog : Form
{
    private TextBox _wadNamesTextBox = null!;
    private CheckBox _wadHostingSitesCheckBox = null!;
    private CheckBox _idgamesCheckBox = null!;
    private CheckBox _webSearchCheckBox = null!;
    private Button _fetchButton = null!;
    private Button _cancelButton = null!;
    private Label _statusLabel = null!;
    
    /// <summary>
    /// List of WAD file names to fetch (result after dialog closes).
    /// </summary>
    public List<string> WadNames { get; private set; } = new();
    
    /// <summary>
    /// Whether to search WAD hosting sites.
    /// </summary>
    public bool UseWadHostingSites => _wadHostingSitesCheckBox.Checked;
    
    /// <summary>
    /// Whether to search /idgames Archive.
    /// </summary>
    public bool UseIdgames => _idgamesCheckBox.Checked;
    
    /// <summary>
    /// Whether to use web search (DuckDuckGo) as fallback.
    /// </summary>
    public bool UseWebSearch => _webSearchCheckBox.Checked;
    
    public FetchWadsDialog()
    {
        InitializeComponent();
        ApplyDarkTheme();
        DarkModeHelper.ApplyDarkTitleBar(this);
    }
    
    private void InitializeComponent()
    {
        Text = "Fetch WADs";
        Size = new Size(500, 450);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(400, 350);
        MaximizeBox = false;
        
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // Label
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // TextBox
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 110)); // Options
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));  // Buttons
        
        // Instructions label
        var instructionsLabel = new Label
        {
            Text = "Enter WAD file names to fetch (one per line):",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        mainPanel.Controls.Add(instructionsLabel, 0, 0);
        
        // WAD names text box
        _wadNamesTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Font = new Font("Consolas", 9.75f)
        };
        _wadNamesTextBox.TextChanged += WadNamesTextBox_TextChanged;
        mainPanel.Controls.Add(_wadNamesTextBox, 0, 1);
        
        // Options group
        var optionsGroup = new GroupBox
        {
            Text = "Download Sources",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 5, 10, 5)
        };
        
        var optionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true
        };
        
        var siteCount = SettingsService.Instance.Settings.DownloadSites.Count;
        _wadHostingSitesCheckBox = new CheckBox
        {
            Text = siteCount > 0
                ? $"WAD Hosting Sites ({siteCount} configured in Settings)"
                : "WAD Hosting Sites (none configured - add in Settings)",
            Checked = siteCount > 0,
            Enabled = siteCount > 0,
            AutoSize = true,
            Margin = new Padding(3)
        };
        optionsPanel.Controls.Add(_wadHostingSitesCheckBox);
        
        _idgamesCheckBox = new CheckBox
        {
            Text = "/idgames Archive (Doomworld)",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(3)
        };
        optionsPanel.Controls.Add(_idgamesCheckBox);
        
        _webSearchCheckBox = new CheckBox
        {
            Text = "Web Search (DuckDuckGo) - slower fallback",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(3)
        };
        optionsPanel.Controls.Add(_webSearchCheckBox);
        
        optionsGroup.Controls.Add(optionsPanel);
        mainPanel.Controls.Add(optionsGroup, 0, 2);
        
        // Buttons panel
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 5, 0, 0)
        };
        
        _cancelButton = new Button
        {
            Text = "Cancel",
            Size = new Size(80, 30),
            DialogResult = DialogResult.Cancel
        };
        buttonPanel.Controls.Add(_cancelButton);
        
        _fetchButton = new Button
        {
            Text = "Fetch",
            Size = new Size(80, 30),
            Enabled = false,
            Margin = new Padding(0, 0, 10, 0)
        };
        _fetchButton.Click += FetchButton_Click;
        buttonPanel.Controls.Add(_fetchButton);
        
        _statusLabel = new Label
        {
            Text = "Enter at least one WAD name",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 8, 10, 0),
            ForeColor = Color.Gray
        };
        buttonPanel.Controls.Add(_statusLabel);
        
        mainPanel.Controls.Add(buttonPanel, 0, 3);
        
        Controls.Add(mainPanel);
        
        AcceptButton = _fetchButton;
        CancelButton = _cancelButton;
    }
    
    private void WadNamesTextBox_TextChanged(object? sender, EventArgs e)
    {
        var lines = ParseWadNames();
        var count = lines.Count;
        
        _fetchButton.Enabled = count > 0 && (_wadHostingSitesCheckBox.Checked || _idgamesCheckBox.Checked || _webSearchCheckBox.Checked);
        _statusLabel.Text = count == 0 ? "Enter at least one WAD name" : $"{count} WAD(s) to fetch";
    }
    
    private List<string> ParseWadNames()
    {
        var names = new List<string>();
        var lines = _wadNamesTextBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            
            // Keep files without extension - the downloader will try all supported extensions
            // Only validate that if there IS an extension, it's a supported one
            var ext = Path.GetExtension(trimmed);
            if (!string.IsNullOrEmpty(ext) && !Utilities.WadExtensions.IsSupportedExtension(ext))
            {
                // Has an unsupported extension - skip with warning
                continue;
            }
            
            if (!names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(trimmed);
            }
        }
        
        return names;
    }
    
    private static bool HasWadExtension(string name)
    {
        var ext = Path.GetExtension(name);
        return Utilities.WadExtensions.IsSupportedExtension(ext);
    }
    
    private void FetchButton_Click(object? sender, EventArgs e)
    {
        WadNames = ParseWadNames();
        
        if (WadNames.Count == 0)
        {
            MessageBox.Show("Please enter at least one WAD file name.", "No WADs Specified", 
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        
        if (!_wadHostingSitesCheckBox.Checked && !_idgamesCheckBox.Checked && !_webSearchCheckBox.Checked)
        {
            MessageBox.Show("Please enable at least one download source.", "No Sources Selected", 
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        
        DialogResult = DialogResult.OK;
        Close();
    }
    
    private void ApplyDarkTheme()
    {
        BackColor = DarkTheme.PrimaryBackground;
        ForeColor = DarkTheme.TextPrimary;
        
        foreach (Control control in Controls)
        {
            ApplyThemeToControl(control);
        }
    }
    
    private void ApplyThemeToControl(Control control)
    {
        control.BackColor = DarkTheme.PrimaryBackground;
        control.ForeColor = DarkTheme.TextPrimary;
        
        if (control is TextBox textBox)
        {
            textBox.BackColor = DarkTheme.SecondaryBackground;
            textBox.ForeColor = DarkTheme.TextPrimary;
            textBox.BorderStyle = BorderStyle.FixedSingle;
        }
        else if (control is Button button)
        {
            button.BackColor = DarkTheme.SecondaryBackground;
            button.ForeColor = DarkTheme.TextPrimary;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = DarkTheme.BorderColor;
        }
        else if (control is CheckBox checkBox)
        {
            checkBox.ForeColor = DarkTheme.TextPrimary;
        }
        else if (control is GroupBox groupBox)
        {
            groupBox.ForeColor = DarkTheme.TextPrimary;
        }
        
        foreach (Control child in control.Controls)
        {
            ApplyThemeToControl(child);
        }
    }
}
