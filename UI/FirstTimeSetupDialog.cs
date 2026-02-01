using ZScape.Services;

namespace ZScape.UI;

/// <summary>
/// First-time setup wizard shown on initial application launch.
/// Configures essential paths and update preferences.
/// </summary>
public class FirstTimeSetupDialog : Form
{
    private readonly SettingsService _settings = SettingsService.Instance;
    private const string DownloadFolderPrefix = "[Download Folder] ";
    
    // Path controls
    private TextBox _zandronumPathTextBox = null!;
    private TextBox _testingFolderTextBox = null!;
    private ListBox _wadPathsListBox = null!;
    private TextBox _wadDownloadPathTextBox = null!;
    private Button _removeWadPathButton = null!;
    
    // Update controls
    private ComboBox _updateBehaviorComboBox = null!;
    private CheckBox _autoRestartCheckBox = null!;
    private NumericUpDown _updateIntervalValue = null!;
    private ComboBox _updateIntervalUnit = null!;
    private ComboBox _updatePresets = null!;
    
    public FirstTimeSetupDialog()
    {
        InitializeComponent();
        LoadDefaults();
    }
    
    private void InitializeComponent()
    {
        Text = "ZScape - First Time Setup";
        Size = new Size(600, 620);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        
        var mainPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20)
        };
        
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 10,
            AutoSize = false
        };
        
        // Row styles
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Welcome
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // Subtitle
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Zand header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Zand path
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Testing header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));  // Testing path + hint
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // WAD header
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // WAD panel (expands)
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // Update header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));  // Update panel
        
        // Welcome header
        var header = new Label
        {
            Text = "Welcome to ZScape!",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 5)
        };
        layout.Controls.Add(header, 0, 0);
        
        var subtitle = new Label
        {
            Text = "Let's configure the essential settings to get you started.",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        };
        layout.Controls.Add(subtitle, 0, 1);
        
        // Zandronum Executable Section
        var zandHeader = new Label
        {
            Text = "Zandronum Executable",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 3)
        };
        layout.Controls.Add(zandHeader, 0, 2);
        
        var zandPanel = CreatePathPanel(out _zandronumPathTextBox, "Browse...", "Executable files (*.exe)|*.exe", true);
        layout.Controls.Add(zandPanel, 0, 3);
        
        // Testing Versions Folder
        var testingHeader = new Label
        {
            Text = "Testing Versions Folder (optional)",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 3)
        };
        layout.Controls.Add(testingHeader, 0, 4);
        
        var testingContainer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0)
        };
        testingContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        testingContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        
        var testingPathPanel = CreateFolderPanel(out _testingFolderTextBox);
        testingContainer.Controls.Add(testingPathPanel, 0, 0);
        
        var testingHint = UIHelpers.CreateHintLabel(UIHelpers.TestingFolderHint);
        testingContainer.Controls.Add(testingHint, 0, 1);
        
        layout.Controls.Add(testingContainer, 0, 5);
        
        // WAD Settings Section
        var wadHeader = new Label
        {
            Text = "WAD Settings",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 3)
        };
        layout.Controls.Add(wadHeader, 0, 6);
        
        var wadPanel = CreateWadSettingsPanel();
        layout.Controls.Add(wadPanel, 0, 7);
        
        // Update Settings Section
        var updateHeader = new Label
        {
            Text = "Update Settings",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 3)
        };
        layout.Controls.Add(updateHeader, 0, 8);
        
        var updatePanel = CreateUpdateSettingsPanel();
        layout.Controls.Add(updatePanel, 0, 9);
        
        // Buttons panel
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 45,
            Padding = new Padding(15, 5, 15, 5)
        };
        
        var finishButton = new Button
        {
            Text = "Finish Setup",
            Size = new Size(120, 32),
            Margin = new Padding(0)
        };
        finishButton.Click += FinishButton_Click;
        
        var exitButton = new Button
        {
            Text = "Exit",
            Size = new Size(80, 32),
            Margin = new Padding(0, 0, 10, 0)
        };
        exitButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        
        buttonPanel.Controls.Add(finishButton);
        buttonPanel.Controls.Add(exitButton);
        
        mainPanel.Controls.Add(layout);
        Controls.Add(mainPanel);
        Controls.Add(buttonPanel);
        
        AcceptButton = finishButton;
        
        // Apply dark theme
        DarkTheme.Apply(this);
        DarkTheme.ApplyToControl(finishButton);
        DarkTheme.ApplyToControl(exitButton);
    }
    
    private Panel CreatePathPanel(out TextBox textBox, string buttonText, string filter, bool isExe = false)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Height = 30,
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        
        textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 5, 0)
        };
        
        var browseButton = new Button
        {
            Text = buttonText,
            Dock = DockStyle.Fill
        };
        
        var tb = textBox;
        browseButton.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Filter = filter,
                Title = "Select Zandronum Executable"
            };
            
            if (!string.IsNullOrEmpty(tb.Text) && File.Exists(tb.Text))
                dialog.InitialDirectory = Path.GetDirectoryName(tb.Text);
            
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                tb.Text = dialog.FileName;
                
                // Auto-set download folder to exe directory if not already set
                if (isExe && string.IsNullOrEmpty(_wadDownloadPathTextBox.Text))
                {
                    var zandDir = Path.GetDirectoryName(dialog.FileName);
                    if (!string.IsNullOrEmpty(zandDir))
                    {
                        _wadDownloadPathTextBox.Text = zandDir;
                        UpdateDownloadFolderDisplay();
                    }
                }
            }
        };
        
        panel.Controls.Add(textBox, 0, 0);
        panel.Controls.Add(browseButton, 1, 0);
        DarkTheme.ApplyToControl(browseButton);
        
        return panel;
    }
    
    private Panel CreateFolderPanel(out TextBox textBox)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Height = 30,
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        
        textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 5, 0)
        };
        
        var browseButton = new Button
        {
            Text = "Browse...",
            Dock = DockStyle.Fill
        };
        
        var tb = textBox;
        browseButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select Folder",
                UseDescriptionForTitle = true
            };
            
            if (!string.IsNullOrEmpty(tb.Text) && Directory.Exists(tb.Text))
                dialog.InitialDirectory = tb.Text;
            
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                tb.Text = dialog.SelectedPath;
            }
        };
        
        panel.Controls.Add(textBox, 0, 0);
        panel.Controls.Add(browseButton, 1, 0);
        DarkTheme.ApplyToControl(browseButton);
        
        return panel;
    }
    
    private Panel CreateWadSettingsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));   // Label
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // ListBox
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));   // Download label
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // Download path
        
        // WAD search paths label
        var searchLabel = new Label
        {
            Text = "WAD Search Folders (first entry is also the download folder):",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2)
        };
        panel.Controls.Add(searchLabel, 0, 0);
        panel.SetColumnSpan(searchLabel, 2);
        
        // ListBox for paths
        _wadPathsListBox = new ListBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 5, 0)
        };
        _wadPathsListBox.SelectedIndexChanged += (_, _) =>
        {
            // Can't remove the download folder (index 0)
            _removeWadPathButton.Enabled = _wadPathsListBox.SelectedIndex > 0;
        };
        panel.Controls.Add(_wadPathsListBox, 0, 1);
        
        // Buttons for list management
        var buttonStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Margin = new Padding(0)
        };
        
        var addButton = new Button { Text = "Add...", Width = 80, Height = 28 };
        addButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select WAD Folder",
                UseDescriptionForTitle = true
            };
            
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                var path = dialog.SelectedPath;
                // Check if already in list (ignoring the download folder prefix)
                var existingPaths = _wadPathsListBox.Items.Cast<string>()
                    .Select(p => p.StartsWith(DownloadFolderPrefix) ? p[DownloadFolderPrefix.Length..] : p);
                    
                if (!existingPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    _wadPathsListBox.Items.Add(path);
                }
            }
        };
        
        _removeWadPathButton = new Button { Text = "Remove", Width = 80, Height = 28, Enabled = false };
        _removeWadPathButton.Click += (_, _) =>
        {
            if (_wadPathsListBox.SelectedIndex > 0)
            {
                _wadPathsListBox.Items.RemoveAt(_wadPathsListBox.SelectedIndex);
            }
        };
        
        buttonStack.Controls.Add(addButton);
        buttonStack.Controls.Add(_removeWadPathButton);
        DarkTheme.ApplyToControl(addButton);
        DarkTheme.ApplyToControl(_removeWadPathButton);
        panel.Controls.Add(buttonStack, 1, 1);
        
        // Download folder label
        var downloadLabel = new Label
        {
            Text = "WAD Download Folder:",
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 2)
        };
        panel.Controls.Add(downloadLabel, 0, 2);
        panel.SetColumnSpan(downloadLabel, 2);
        
        // Download path row
        var downloadPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        downloadPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        downloadPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        
        _wadDownloadPathTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 5, 0)
        };
        _wadDownloadPathTextBox.TextChanged += (_, _) => UpdateDownloadFolderDisplay();
        
        var browseDownloadButton = new Button { Text = "Browse...", Dock = DockStyle.Fill };
        browseDownloadButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select Download Folder",
                UseDescriptionForTitle = true
            };
            
            if (!string.IsNullOrEmpty(_wadDownloadPathTextBox.Text))
                dialog.InitialDirectory = _wadDownloadPathTextBox.Text;
            
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                _wadDownloadPathTextBox.Text = dialog.SelectedPath;
            }
        };
        
        downloadPanel.Controls.Add(_wadDownloadPathTextBox, 0, 0);
        downloadPanel.Controls.Add(browseDownloadButton, 1, 0);
        DarkTheme.ApplyToControl(browseDownloadButton);
        
        panel.Controls.Add(downloadPanel, 0, 3);
        panel.SetColumnSpan(downloadPanel, 2);
        
        return panel;
    }
    
    private void UpdateDownloadFolderDisplay()
    {
        var downloadPath = _wadDownloadPathTextBox.Text.Trim();
        if (string.IsNullOrEmpty(downloadPath))
            return;
            
        var displayText = DownloadFolderPrefix + downloadPath;
        
        if (_wadPathsListBox.Items.Count == 0)
        {
            _wadPathsListBox.Items.Add(displayText);
        }
        else
        {
            _wadPathsListBox.Items[0] = displayText;
        }
    }
    
    private Panel CreateUpdateSettingsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            AutoSize = true,
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        
        // Update behavior row
        panel.Controls.Add(new Label
        {
            Text = "Update behavior:",
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0)
        }, 0, 0);
        
        _updateBehaviorComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 320,
            Margin = new Padding(0, 2, 0, 0)
        };
        _updateBehaviorComboBox.Items.AddRange([
            "Disabled - Never check for updates",
            "Notify Only - Check but don't download",
            "Auto Download - Check and download automatically"
        ]);
        _updateBehaviorComboBox.SelectedIndex = 2;
        panel.Controls.Add(_updateBehaviorComboBox, 1, 0);
        
        // Check interval row
        panel.Controls.Add(new Label
        {
            Text = "Check every:",
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0)
        }, 0, 1);
        
        var intervalContainer = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0)
        };
        
        var applyingPreset = false;
        
        _updatePresets = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120,
            Margin = new Padding(0, 2, 0, 0)
        };
        _updatePresets.Items.AddRange(["Custom:", "Every 6 hours", "Once a day", "Once a week", "Once a month"]);
        _updatePresets.SelectedIndex = 2; // Default to "Once a day"
        _updatePresets.SelectedIndexChanged += (_, _) =>
        {
            if (_updatePresets.SelectedIndex == 0) return; // Custom selected, don't change values
            applyingPreset = true;
            switch (_updatePresets.SelectedIndex)
            {
                case 1:
                    _updateIntervalValue.Value = 6;
                    _updateIntervalUnit.SelectedIndex = 0;
                    break;
                case 2:
                    _updateIntervalValue.Value = 1;
                    _updateIntervalUnit.SelectedIndex = 1;
                    break;
                case 3:
                    _updateIntervalValue.Value = 1;
                    _updateIntervalUnit.SelectedIndex = 2;
                    break;
                case 4:
                    _updateIntervalValue.Value = 4;
                    _updateIntervalUnit.SelectedIndex = 2;
                    break;
            }
            applyingPreset = false;
        };
        intervalContainer.Controls.Add(_updatePresets);
        
        _updateIntervalValue = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 99,
            Value = 1,
            Width = 55,
            Margin = new Padding(10, 2, 0, 0)
        };
        intervalContainer.Controls.Add(_updateIntervalValue);
        
        _updateIntervalUnit = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 70,
            Margin = new Padding(5, 2, 0, 0)
        };
        _updateIntervalUnit.Items.AddRange(["Hours", "Days", "Weeks"]);
        _updateIntervalUnit.SelectedIndex = 1;
        intervalContainer.Controls.Add(_updateIntervalUnit);
        
        // When user manually changes interval, switch to "Custom:"
        _updateIntervalValue.ValueChanged += (_, _) => { if (!applyingPreset) _updatePresets.SelectedIndex = 0; };
        _updateIntervalUnit.SelectedIndexChanged += (_, _) => { if (!applyingPreset) _updatePresets.SelectedIndex = 0; };
        
        panel.Controls.Add(intervalContainer, 1, 1);
        
        // Auto-restart checkbox row
        _autoRestartCheckBox = new CheckBox
        {
            Text = "Automatically restart when an update is ready",
            AutoSize = true,
            Checked = false,
            Margin = new Padding(0, 3, 0, 0)
        };
        panel.Controls.Add(_autoRestartCheckBox, 1, 2);
        
        return panel;
    }
    
    private void LoadDefaults()
    {
        // Try to auto-detect Zandronum
        var detectedPath = AutoDetectZandronumPath();
        if (!string.IsNullOrEmpty(detectedPath))
        {
            _zandronumPathTextBox.Text = detectedPath;
            
            // Set default WAD download folder to Zandronum directory
            var zandDir = Path.GetDirectoryName(detectedPath);
            if (!string.IsNullOrEmpty(zandDir))
            {
                _wadDownloadPathTextBox.Text = zandDir;
            }
        }
        
        // Load any existing settings
        var settings = _settings.Settings;
        if (!string.IsNullOrEmpty(settings.ZandronumPath))
            _zandronumPathTextBox.Text = settings.ZandronumPath;
        if (!string.IsNullOrEmpty(settings.ZandronumTestingPath))
            _testingFolderTextBox.Text = settings.ZandronumTestingPath;
        if (!string.IsNullOrEmpty(settings.WadDownloadPath))
            _wadDownloadPathTextBox.Text = settings.WadDownloadPath;
        
        // Note: We don't auto-discover WAD paths - let user add them manually
        
        // Ensure download folder is shown first
        UpdateDownloadFolderDisplay();
    }
    
    private static string? AutoDetectZandronumPath()
    {
        var commonPaths = new[]
        {
            @"C:\Zandronum\zandronum.exe",
            @"C:\Games\Zandronum\zandronum.exe",
            @"C:\Program Files\Zandronum\zandronum.exe",
            @"C:\Program Files (x86)\Zandronum\zandronum.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zandronum", "zandronum.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zandronum", "zandronum.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Zandronum", "zandronum.exe"),
            Path.Combine(AppContext.BaseDirectory, "zandronum.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "zandronum.exe"),
        };
        
        foreach (var path in commonPaths)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch { }
        }
        
        return null;
    }
    
    private void FinishButton_Click(object? sender, EventArgs e)
    {
        // Validate Zandronum exe path
        var zandPath = _zandronumPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(zandPath))
        {
            MessageBox.Show("Please specify a path to the Zandronum executable.", 
                "Zandronum Path Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        
        if (!File.Exists(zandPath))
        {
            MessageBox.Show($"The specified Zandronum executable was not found:\n{zandPath}", 
                "File Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        
        var settings = _settings.Settings;
        
        // Save paths
        settings.ZandronumPath = zandPath;
        
        if (!string.IsNullOrWhiteSpace(_testingFolderTextBox.Text))
            settings.ZandronumTestingPath = _testingFolderTextBox.Text.Trim();
        
        // Save WAD paths - extract from listbox, stripping download folder prefix
        var wadPaths = new List<string>();
        foreach (var item in _wadPathsListBox.Items)
        {
            var path = item?.ToString() ?? "";
            if (path.StartsWith(DownloadFolderPrefix))
                path = path[DownloadFolderPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(path) && !wadPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                wadPaths.Add(path);
        }
        settings.WadSearchPaths = wadPaths;
        
        if (!string.IsNullOrWhiteSpace(_wadDownloadPathTextBox.Text))
            settings.WadDownloadPath = _wadDownloadPathTextBox.Text.Trim();
        
        // Save update settings
        settings.UpdateBehavior = (UpdateBehavior)_updateBehaviorComboBox.SelectedIndex;
        settings.UpdateCheckIntervalValue = (int)_updateIntervalValue.Value;
        settings.UpdateCheckIntervalUnit = (UpdateIntervalUnit)_updateIntervalUnit.SelectedIndex;
        settings.AutoRestartForUpdates = _autoRestartCheckBox.Checked;
        
        _settings.Save();
        
        DialogResult = DialogResult.OK;
        Close();
    }
}
