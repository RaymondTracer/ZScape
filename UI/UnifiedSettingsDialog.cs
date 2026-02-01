using System.ComponentModel;
using System.IO;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.UI;

/// <summary>
/// Unified settings dialog with categorized navigation.
/// </summary>
public class UnifiedSettingsDialog : Form
{
    private ListBox _categoryList = null!;
    private Panel _contentPanel = null!;
    private readonly Dictionary<string, Panel> _categoryPanels = new();
    
    // General settings controls
    private TextBox _zandronumPathTextBox = null!;
    private TextBox _zandronumTestingPathTextBox = null!;
    private NumericUpDown _hashConcurrencyNumeric = null!;
    private CheckBox _screenshotMonitorCheckBox = null!;
    private TextBox _screenshotPathTextBox = null!;
    
    // WAD Paths controls
    private ListBox _pathsListBox = null!;
    private TextBox _downloadPathTextBox = null!;
    private Button _removePathButton = null!;
    private Button _moveUpButton = null!;
    private Button _moveDownButton = null!;
    private Button _setAsDownloadButton = null!;
    private System.Windows.Forms.Timer? _downloadPathDebounceTimer;
    private string _lastValidDownloadPath = string.Empty;
    
    // Download Sites controls
    private ListBox _sitesListBox = null!;
    private TextBox _newSiteTextBox = null!;
    
    // Downloads controls
    private NumericUpDown _maxConcurrentDownloads = null!;
    private NumericUpDown _maxConcurrentDomains = null!;
    private NumericUpDown _defaultMaxThreads = null!;
    private NumericUpDown _maxThreadsPerFile = null!;
    private NumericUpDown _defaultInitialThreads = null!;
    private NumericUpDown _defaultMinSegmentKb = null!;
    
    // Domain Threads controls
    private DataGridView _domainGridView = null!;
    private readonly List<DomainSettingsRow> _domainRows = [];
    
    // Server Queries controls
    private NumericUpDown _queryIntervalMs = null!;
    private NumericUpDown _maxConcurrentQueries = null!;
    private NumericUpDown _queryRetryAttempts = null!;
    private NumericUpDown _queryRetryDelayMs = null!;
    private NumericUpDown _masterServerRetryCount = null!;
    private NumericUpDown _consecutiveFailuresBeforeOffline = null!;
    private NumericUpDown _autoRefreshIntervalMinutes = null!;
    private CheckBox _autoRefreshFavoritesOnlyCheckBox = null!;
    
    // Favorites & Servers controls
    private ListBox _favoritesListBox = null!;
    private ListBox _manualServersListBox = null!;
    private CheckBox _enableFavoriteAlertsCheckBox = null!;
    private CheckBox _enableManualAlertsCheckBox = null!;
    private CheckBox _showFavoritesColumnCheckBox = null!;
    private NumericUpDown _alertMinPlayersNumeric = null!;
    private NumericUpDown _alertIntervalNumeric = null!;
    
    private const string DownloadFolderPrefix = "[Download Folder] ";
    
    // Categories
    private const string CategoryGeneral = "General";
    private const string CategoryWadPaths = "WAD Paths";
    private const string CategoryDownloadSites = "Download Sites";
    private const string CategoryDownloads = "Downloads";
    private const string CategoryDomainThreads = "Domain Threads";
    private const string CategoryServerQueries = "Server Queries";
    private const string CategoryFavorites = "Favorites & Servers";
    
    // WAD settings properties for external access
    public List<string> SearchPaths { get; private set; } = [];
    public string WadDownloadPath { get; private set; } = string.Empty;
    public List<string> DownloadSites { get; private set; } = [];
    
    public UnifiedSettingsDialog()
    {
        InitializeComponent();
        ApplyDarkTheme();
        DarkModeHelper.ApplyDarkTitleBar(this);
        LoadSettings();
        FormClosed += (_, _) => _downloadPathDebounceTimer?.Dispose();
    }
    
    public UnifiedSettingsDialog(
        IEnumerable<string>? wadSearchPaths = null,
        string? wadDownloadPath = null,
        IEnumerable<string>? downloadSites = null,
        string? initialCategory = null) : this()
    {
        // Override WAD settings if provided
        if (wadSearchPaths != null)
            SearchPaths = wadSearchPaths.ToList();
        if (wadDownloadPath != null)
            WadDownloadPath = wadDownloadPath;
        if (downloadSites != null)
            DownloadSites = downloadSites.ToList();
        
        PopulateWadControls();
        
        // Select initial category if specified
        if (!string.IsNullOrEmpty(initialCategory))
        {
            int index = _categoryList.Items.IndexOf(initialCategory);
            if (index >= 0)
                _categoryList.SelectedIndex = index;
        }
    }
    
    private void InitializeComponent()
    {
        Text = "Settings";
        Size = new Size(900, 600);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(750, 500);
        
        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10)
        };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        
        // Split container: categories on left, content on right
        var splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            IsSplitterFixed = true
        };
        
        // Category list
        _categoryList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            Font = new Font(Font.FontFamily, 10f)
        };
        _categoryList.Items.AddRange([CategoryGeneral, CategoryFavorites, CategoryWadPaths, CategoryDownloadSites, CategoryDownloads, CategoryDomainThreads, CategoryServerQueries]);
        _categoryList.SelectedIndexChanged += OnCategoryChanged;
        splitContainer.Panel1.Controls.Add(_categoryList);
        
        // Content panel
        _contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 0, 0, 0)
        };
        splitContainer.Panel2.Controls.Add(_contentPanel);
        
        // Create all category panels
        CreateGeneralPanel();
        CreateFavoritesPanel();
        CreateWadPathsPanel();
        CreateDownloadSitesPanel();
        CreateDownloadsPanel();
        CreateDomainThreadsPanel();
        CreateServerQueriesPanel();
        
        // Add all panels to content area (hidden by default)
        foreach (var panel in _categoryPanels.Values)
        {
            panel.Visible = false;
            _contentPanel.Controls.Add(panel);
        }
        
        mainLayout.Controls.Add(splitContainer, 0, 0);
        
        // Button panel
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };
        
        var cancelButton = new Button
        {
            Text = "Cancel",
            Width = 80,
            Height = 30,
            DialogResult = DialogResult.Cancel
        };
        
        var okButton = new Button
        {
            Text = "OK",
            Width = 80,
            Height = 30,
            DialogResult = DialogResult.OK
        };
        okButton.Click += OnOkClick;
        
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(okButton);
        mainLayout.Controls.Add(buttonPanel, 0, 1);
        
        Controls.Add(mainLayout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
        
        // Set splitter distance after layout is added
        splitContainer.SplitterDistance = 160;
        
        // Select first category
        _categoryList.SelectedIndex = 0;
    }
    
    private void OnCategoryChanged(object? sender, EventArgs e)
    {
        var selected = _categoryList.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(selected)) return;
        
        foreach (var kvp in _categoryPanels)
        {
            kvp.Value.Visible = kvp.Key == selected;
        }
    }
    
    #region General Panel
    
    private void CreateGeneralPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 16,
            Padding = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Label
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Stable path
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 15));  // Spacer
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Label
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Testing path
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 15));  // Spacer
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Label
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Hash concurrency
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));  // Info
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));  // Spacer
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Screenshot header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Screenshot checkbox
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Screenshot path label
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Screenshot path
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Fill
        
        // Header
        var header = new Label
        {
            Text = "Zandronum Configuration",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(header, 0, 0);
        
        // Stable path label
        var stableLabel = new Label
        {
            Text = "Zandronum Executable (Stable):",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        layout.Controls.Add(stableLabel, 0, 1);
        
        // Stable path row
        var stablePathPanel = CreatePathBrowseRow(out _zandronumPathTextBox, "Browse...", 
            () => BrowseForExecutable(_zandronumPathTextBox));
        layout.Controls.Add(stablePathPanel, 0, 2);
        
        // Testing path label
        var testingLabel = new Label
        {
            Text = "Testing Versions Folder:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        layout.Controls.Add(testingLabel, 0, 4);
        
        // Testing path row
        var testingPathPanel = CreatePathBrowseRow(out _zandronumTestingPathTextBox, "Browse...", 
            () => BrowseForFolder(_zandronumTestingPathTextBox));
        layout.Controls.Add(testingPathPanel, 0, 5);
        
        // Hash concurrency label
        var hashLabel = new Label
        {
            Text = "Hash Verification Concurrency:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        layout.Controls.Add(hashLabel, 0, 7);
        
        // Hash concurrency row
        var hashPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty
        };
        
        _hashConcurrencyNumeric = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 32,
            Value = 0,
            Width = 60
        };
        hashPanel.Controls.Add(_hashConcurrencyNumeric);
        
        var hashHint = new Label
        {
            Text = "(0 = unlimited/all at once, 1 = sequential)",
            AutoSize = true,
            Padding = new Padding(5, 5, 0, 0)
        };
        hashPanel.Controls.Add(hashHint);
        layout.Controls.Add(hashPanel, 0, 8);
        
        // Info
        var info = new Label
        {
            Text = "Testing versions are automatically downloaded when connecting to testing servers.",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 5, 0, 0)
        };
        layout.Controls.Add(info, 0, 9);
        
        // Screenshot Header
        var screenshotHeader = new Label
        {
            Text = "Screenshot Consolidation",
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        layout.Controls.Add(screenshotHeader, 0, 11);
        
        // Screenshot checkbox
        _screenshotMonitorCheckBox = new CheckBox
        {
            Text = "Automatically move screenshots from testing versions to a single folder",
            Dock = DockStyle.Fill,
            AutoSize = true
        };
        _screenshotMonitorCheckBox.CheckedChanged += ScreenshotMonitorCheckBox_CheckedChanged;
        layout.Controls.Add(_screenshotMonitorCheckBox, 0, 12);
        
        // Screenshot path label
        var screenshotPathLabel = new Label
        {
            Text = "Screenshots Folder (leave empty for default):",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        layout.Controls.Add(screenshotPathLabel, 0, 13);
        
        // Screenshot path row
        var screenshotPathPanel = CreatePathBrowseRow(out _screenshotPathTextBox, "Browse...", 
            () => BrowseForFolder(_screenshotPathTextBox));
        layout.Controls.Add(screenshotPathPanel, 0, 14);
        
        panel.Controls.Add(layout);
        _categoryPanels[CategoryGeneral] = panel;
    }
    
    private void ScreenshotMonitorCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        _screenshotPathTextBox.Enabled = _screenshotMonitorCheckBox.Checked;
    }
    
    #endregion
    
    #region Favorites & Servers Panel
    
    private void CreateFavoritesPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));  // Favorites
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));  // Manual Servers
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 145)); // Alerts & Display Options
        
        // === Favorites Section ===
        var favoritesGroup = new GroupBox
        {
            Text = "Favorite Servers",
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };
        
        var favoritesLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(0)
        };
        favoritesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        favoritesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        favoritesLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));   // Label
        favoritesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // List
        
        var favoritesLabel = new Label
        {
            Text = "Favorited servers (queried with priority during refresh):",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        favoritesLayout.Controls.Add(favoritesLabel, 0, 0);
        favoritesLayout.SetColumnSpan(favoritesLabel, 2);
        
        _favoritesListBox = new ListBox
        {
            Dock = DockStyle.Fill
        };
        favoritesLayout.Controls.Add(_favoritesListBox, 0, 1);
        
        var favButtonStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown
        };
        
        var removeFavoriteButton = new Button { Text = "Remove", Width = 90 };
        removeFavoriteButton.Click += OnRemoveFavoriteClick;
        
        var clearFavoritesButton = new Button { Text = "Clear All", Width = 90 };
        clearFavoritesButton.Click += OnClearFavoritesClick;
        
        favButtonStack.Controls.Add(removeFavoriteButton);
        favButtonStack.Controls.Add(clearFavoritesButton);
        favoritesLayout.Controls.Add(favButtonStack, 1, 1);
        
        favoritesGroup.Controls.Add(favoritesLayout);
        layout.Controls.Add(favoritesGroup, 0, 0);
        
        // === Manual Servers Section ===
        var manualGroup = new GroupBox
        {
            Text = "Manually Added Servers",
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };
        
        var manualLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(0)
        };
        manualLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        manualLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        manualLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));   // Label
        manualLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // List
        manualLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));   // Add row
        
        var manualLabel = new Label
        {
            Text = "Manually added servers (always queried, even if not on master server):",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        manualLayout.Controls.Add(manualLabel, 0, 0);
        manualLayout.SetColumnSpan(manualLabel, 2);
        
        _manualServersListBox = new ListBox
        {
            Dock = DockStyle.Fill
        };
        manualLayout.Controls.Add(_manualServersListBox, 0, 1);
        
        var manualButtonStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown
        };
        
        var addManualButton = new Button { Text = "Add...", Width = 90 };
        addManualButton.Click += OnAddManualServerClick;
        
        var removeManualButton = new Button { Text = "Remove", Width = 90 };
        removeManualButton.Click += OnRemoveManualServerClick;
        
        manualButtonStack.Controls.Add(addManualButton);
        manualButtonStack.Controls.Add(removeManualButton);
        manualLayout.Controls.Add(manualButtonStack, 1, 1);
        
        // Info label
        var infoLabel = new Label
        {
            Text = "Manual servers are queried first during refresh and are never removed from the list.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.Gray
        };
        manualLayout.Controls.Add(infoLabel, 0, 2);
        manualLayout.SetColumnSpan(infoLabel, 2);
        
        manualGroup.Controls.Add(manualLayout);
        layout.Controls.Add(manualGroup, 0, 1);
        
        // === Alerts Section ===
        var alertsGroup = new GroupBox
        {
            Text = "Server Alerts",
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };
        
        var alertsLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        
        _enableFavoriteAlertsCheckBox = new CheckBox
        {
            Text = "Alert when favorite servers come online with players",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 5)
        };
        
        _enableManualAlertsCheckBox = new CheckBox
        {
            Text = "Alert when manually added servers come online with players",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 5)
        };
        
        _showFavoritesColumnCheckBox = new CheckBox
        {
            Text = "Show favorites column in server list",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 5)
        };
        
        var minPlayersPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 5, 0, 5)
        };
        
        var minPlayersLabel = new Label
        {
            Text = "Minimum players to trigger alert:",
            AutoSize = true,
            Margin = new Padding(0, 3, 5, 0)
        };
        
        _alertMinPlayersNumeric = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 64,
            Value = 1,
            Width = 60
        };
        
        minPlayersPanel.Controls.Add(minPlayersLabel);
        minPlayersPanel.Controls.Add(_alertMinPlayersNumeric);
        
        var intervalPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 0)
        };
        
        var intervalLabel = new Label
        {
            Text = "Check interval (seconds):",
            AutoSize = true,
            Margin = new Padding(0, 3, 5, 0)
        };
        
        _alertIntervalNumeric = new NumericUpDown
        {
            Minimum = 30,
            Maximum = 600,
            Value = 60,
            Width = 60
        };
        
        intervalPanel.Controls.Add(intervalLabel);
        intervalPanel.Controls.Add(_alertIntervalNumeric);
        
        alertsLayout.Controls.Add(_enableFavoriteAlertsCheckBox);
        alertsLayout.Controls.Add(_enableManualAlertsCheckBox);
        alertsLayout.Controls.Add(_showFavoritesColumnCheckBox);
        alertsLayout.Controls.Add(minPlayersPanel);
        alertsLayout.Controls.Add(intervalPanel);
        
        alertsGroup.Controls.Add(alertsLayout);
        layout.Controls.Add(alertsGroup, 0, 2);
        
        panel.Controls.Add(layout);
        _categoryPanels[CategoryFavorites] = panel;
    }
    
    private void OnRemoveFavoriteClick(object? sender, EventArgs e)
    {
        if (_favoritesListBox.SelectedIndex >= 0)
        {
            var selected = _favoritesListBox.SelectedItem?.ToString();
            if (!string.IsNullOrEmpty(selected))
            {
                _favoritesListBox.Items.RemoveAt(_favoritesListBox.SelectedIndex);
            }
        }
    }
    
    private void OnClearFavoritesClick(object? sender, EventArgs e)
    {
        if (_favoritesListBox.Items.Count == 0) return;
        
        var result = MessageBox.Show(
            "Are you sure you want to remove all favorite servers?",
            "Clear Favorites",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        
        if (result == DialogResult.Yes)
        {
            _favoritesListBox.Items.Clear();
        }
    }
    
    private void OnAddManualServerClick(object? sender, EventArgs e)
    {
        using var dialog = new AddServerDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.ServerAddress))
        {
            var address = $"{dialog.ServerAddress}:{dialog.ServerPort}";
            
            // Avoid duplicates
            if (!_manualServersListBox.Items.Contains(address))
            {
                _manualServersListBox.Items.Add(address);
                
                // Also add to favorites if requested
                if (dialog.AddAsFavorite && !_favoritesListBox.Items.Contains(address))
                {
                    _favoritesListBox.Items.Add(address);
                }
            }
        }
    }
    
    private void OnRemoveManualServerClick(object? sender, EventArgs e)
    {
        if (_manualServersListBox.SelectedIndex >= 0)
        {
            _manualServersListBox.Items.RemoveAt(_manualServersListBox.SelectedIndex);
        }
    }
    
    #endregion
    
    #region WAD Paths Panel

    private void CreateWadPathsPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Label
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // List
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Download label
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Download path
        
        // Header
        var header = new Label
        {
            Text = "WAD Search Paths",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(header, 0, 0);
        layout.SetColumnSpan(header, 2);
        
        // Paths label
        var pathsLabel = new Label
        {
            Text = "Folders to search for WAD files:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        layout.Controls.Add(pathsLabel, 0, 1);
        layout.SetColumnSpan(pathsLabel, 2);
        
        // Paths list
        _pathsListBox = new ListBox
        {
            Dock = DockStyle.Fill
        };
        _pathsListBox.SelectedIndexChanged += OnPathSelectionChanged;
        layout.Controls.Add(_pathsListBox, 0, 2);
        
        // Buttons
        var buttonStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown
        };
        
        var addButton = new Button { Text = "Add...", Width = 90 };
        addButton.Click += OnAddPathClick;
        
        _removePathButton = new Button { Text = "Remove", Width = 90, Enabled = false };
        _removePathButton.Click += OnRemovePathClick;
        
        _moveUpButton = new Button { Text = "Move Up", Width = 90, Enabled = false };
        _moveUpButton.Click += (_, _) => MovePathItem(-1);
        
        _moveDownButton = new Button { Text = "Move Down", Width = 90, Enabled = false };
        _moveDownButton.Click += (_, _) => MovePathItem(1);
        
        _setAsDownloadButton = new Button { Text = "Set as Download", Width = 90, Enabled = false };
        _setAsDownloadButton.Click += OnSetAsDownloadClick;
        
        buttonStack.Controls.Add(addButton);
        buttonStack.Controls.Add(_removePathButton);
        buttonStack.Controls.Add(_moveUpButton);
        buttonStack.Controls.Add(_moveDownButton);
        buttonStack.Controls.Add(_setAsDownloadButton);
        layout.Controls.Add(buttonStack, 1, 2);
        
        // Download path label
        var downloadLabel = new Label
        {
            Text = "Default folder for downloaded WADs:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        layout.Controls.Add(downloadLabel, 0, 3);
        layout.SetColumnSpan(downloadLabel, 2);
        
        // Download path row
        var downloadPanel = CreatePathBrowseRow(out _downloadPathTextBox, "Browse...", OnBrowseDownloadPath);
        _downloadPathTextBox.TextChanged += OnDownloadPathTextChanged;
        _downloadPathTextBox.KeyDown += OnDownloadPathKeyDown;
        _downloadPathTextBox.Leave += OnDownloadPathLeave;
        layout.Controls.Add(downloadPanel, 0, 4);
        layout.SetColumnSpan(downloadPanel, 2);
        
        panel.Controls.Add(layout);
        _categoryPanels[CategoryWadPaths] = panel;
    }
    
    #endregion
    
    #region Download Sites Panel
    
    private void CreateDownloadSitesPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Label
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // List
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Add label
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Add row
        
        // Header
        var header = new Label
        {
            Text = "WAD Download Sites",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(header, 0, 0);
        layout.SetColumnSpan(header, 2);
        
        // Sites label
        var sitesLabel = new Label
        {
            Text = "WAD download sites (searched in order):",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        layout.Controls.Add(sitesLabel, 0, 1);
        layout.SetColumnSpan(sitesLabel, 2);
        
        // Sites list
        _sitesListBox = new ListBox
        {
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(_sitesListBox, 0, 2);
        
        // Buttons
        var buttonStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown
        };
        
        var addDefaultsButton = new Button { Text = "Add Defaults", Width = 90 };
        addDefaultsButton.Click += OnAddDefaultSites;
        
        var removeButton = new Button { Text = "Remove", Width = 90 };
        removeButton.Click += OnRemoveSiteClick;
        
        var moveUpButton = new Button { Text = "Move Up", Width = 90 };
        moveUpButton.Click += (_, _) => MoveSiteItem(-1);
        
        var moveDownButton = new Button { Text = "Move Down", Width = 90 };
        moveDownButton.Click += (_, _) => MoveSiteItem(1);
        
        buttonStack.Controls.Add(addDefaultsButton);
        buttonStack.Controls.Add(removeButton);
        buttonStack.Controls.Add(moveUpButton);
        buttonStack.Controls.Add(moveDownButton);
        layout.Controls.Add(buttonStack, 1, 2);
        
        // Add label
        var addLabel = new Label
        {
            Text = "Add new site URL:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        layout.Controls.Add(addLabel, 0, 3);
        layout.SetColumnSpan(addLabel, 2);
        
        // Add row
        var addPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        addPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        
        _newSiteTextBox = new TextBox { Dock = DockStyle.Fill };
        addPanel.Controls.Add(_newSiteTextBox, 0, 0);
        
        var addSiteButton = new Button { Text = "Add", Dock = DockStyle.Fill };
        addSiteButton.Click += OnAddSiteClick;
        addPanel.Controls.Add(addSiteButton, 1, 0);
        
        layout.Controls.Add(addPanel, 0, 4);
        layout.SetColumnSpan(addPanel, 2);
        
        panel.Controls.Add(layout);
        _categoryPanels[CategoryDownloadSites] = panel;
    }
    
    #endregion
    
    #region Downloads Panel
    
    private void CreateDownloadsPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));   // Header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));  // Settings grid (6 rows * 32px + padding)
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // Info
        
        // Header
        var header = new Label
        {
            Text = "Download Concurrency",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(header, 0, 0);
        
        // Settings grid - use a 2-column layout with label-control pairs
        var settingsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(0, 10, 0, 0)
        };
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        for (int i = 0; i < 6; i++)
            settingsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        
        var settings = SettingsService.Instance.Settings;
        
        // Row 0: Max Downloads
        settingsGrid.Controls.Add(new Label { Text = "Max Downloads:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 0);
        _maxConcurrentDownloads = new NumericUpDown { Minimum = 0, Maximum = 100, Value = settings.MaxConcurrentDownloads, Width = 80 };
        new ToolTip().SetToolTip(_maxConcurrentDownloads, "Max simultaneous file downloads (0=unlimited)");
        settingsGrid.Controls.Add(_maxConcurrentDownloads, 1, 0);
        
        // Row 1: Max Domains
        settingsGrid.Controls.Add(new Label { Text = "Max Domains:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 1);
        _maxConcurrentDomains = new NumericUpDown { Minimum = 0, Maximum = 50, Value = settings.MaxConcurrentDomains, Width = 80 };
        new ToolTip().SetToolTip(_maxConcurrentDomains, "Max domains to download from simultaneously (0=unlimited)");
        settingsGrid.Controls.Add(_maxConcurrentDomains, 1, 1);
        
        // Row 2: Default Max Threads
        settingsGrid.Controls.Add(new Label { Text = "Default Max Threads:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 2);
        _defaultMaxThreads = new NumericUpDown { Minimum = 1, Maximum = 256, Value = settings.DefaultMaxThreads, Width = 80 };
        new ToolTip().SetToolTip(_defaultMaxThreads, "Default threads per file when domain not configured");
        settingsGrid.Controls.Add(_defaultMaxThreads, 1, 2);
        
        // Row 3: Max Threads/File
        settingsGrid.Controls.Add(new Label { Text = "Max Threads/File:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 3);
        _maxThreadsPerFile = new NumericUpDown { Minimum = 0, Maximum = 1024, Value = settings.MaxThreadsPerFile, Width = 80 };
        new ToolTip().SetToolTip(_maxThreadsPerFile, "Hard cap on threads per file (0=no limit)");
        settingsGrid.Controls.Add(_maxThreadsPerFile, 1, 3);
        
        // Row 4: Default Initial Threads
        settingsGrid.Controls.Add(new Label { Text = "Default Initial Threads:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 4);
        _defaultInitialThreads = new NumericUpDown { Minimum = 1, Maximum = 32, Value = settings.DefaultInitialThreads, Width = 80 };
        new ToolTip().SetToolTip(_defaultInitialThreads, "Starting thread count for probing new domains");
        settingsGrid.Controls.Add(_defaultInitialThreads, 1, 4);
        
        // Row 5: Min Segment KB
        settingsGrid.Controls.Add(new Label { Text = "Min Segment KB:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 5);
        _defaultMinSegmentKb = new NumericUpDown { Minimum = 64, Maximum = 4096, Value = settings.DefaultMinSegmentSizeKb, Width = 80 };
        new ToolTip().SetToolTip(_defaultMinSegmentKb, "Minimum bytes per thread segment");
        settingsGrid.Controls.Add(_defaultMinSegmentKb, 1, 5);
        
        layout.Controls.Add(settingsGrid, 0, 1);
        
        // Info
        var info = new Label
        {
            Text = "These settings control overall download behavior.\n\n" +
                   "Max Downloads: Total simultaneous file downloads (0=unlimited).\n" +
                   "Max Domains: How many servers to download from at once (0=unlimited).\n" +
                   "Default Max/Initial Threads: Used for domains without specific configuration.\n" +
                   "Min Segment KB: Smallest chunk size per thread.\n\n" +
                   "For per-domain settings, use the Domain Threads category.",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 10, 0, 0)
        };
        layout.Controls.Add(info, 0, 2);
        
        panel.Controls.Add(layout);
        _categoryPanels[CategoryDownloads] = panel;
    }
    
    #endregion
    
    #region Domain Threads Panel
    
    private void CreateDomainThreadsPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));   // Header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));   // Description
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // Grid
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // Buttons
        
        // Header
        var header = new Label
        {
            Text = "Domain Thread Settings",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(header, 0, 0);
        
        // Description
        var desc = new Label
        {
            Text = "Configure download thread settings per domain. Adaptive learning automatically probes and adjusts thread counts.\n" +
                   "Double-click a cell to edit. Press Enter to confirm.",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.TopLeft
        };
        layout.Controls.Add(desc, 0, 1);
        
        // Grid
        _domainGridView = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2
        };
        
        // Columns
        _domainGridView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Domain", HeaderText = "Domain", DataPropertyName = "Domain", Width = 140
        });
        _domainGridView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "MaxThreads", HeaderText = "Threads", DataPropertyName = "MaxThreads", Width = 60,
            ToolTipText = "Maximum concurrent threads per file (0=use global default)"
        });
        _domainGridView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "MaxConcurrentDownloads", HeaderText = "DL Lim", DataPropertyName = "MaxConcurrentDownloads", Width = 55,
            ToolTipText = "Max concurrent downloads from this domain (0=unlimited)"
        });
        _domainGridView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "InitialThreads", HeaderText = "Initial", DataPropertyName = "InitialThreads", Width = 50,
            ToolTipText = "Starting thread count for probing (1-32)"
        });
        _domainGridView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "MinSegmentSizeKb", HeaderText = "Seg KB", DataPropertyName = "MinSegmentSizeKb", Width = 55,
            ToolTipText = "Minimum segment size in KB (64-4096)"
        });
        _domainGridView.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "AdaptiveLearning", HeaderText = "Adaptive", DataPropertyName = "AdaptiveLearning", Width = 60,
            ToolTipText = "Enable automatic thread probing and backoff"
        });
        _domainGridView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "SuccessCount", HeaderText = "OK", DataPropertyName = "SuccessCount", Width = 35, ReadOnly = true,
            ToolTipText = "Successful downloads"
        });
        _domainGridView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "FailureCount", HeaderText = "Fail", DataPropertyName = "FailureCount", Width = 35, ReadOnly = true,
            ToolTipText = "Failed downloads"
        });
        _domainGridView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Notes", HeaderText = "Notes", DataPropertyName = "Notes", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        
        _domainGridView.CellValidating += OnDomainCellValidating;
        _domainGridView.CellEndEdit += OnDomainCellEndEdit;
        
        layout.Controls.Add(_domainGridView, 0, 2);
        
        // Buttons
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight
        };
        
        var addButton = new Button { Text = "Add Domain", Width = 100, Height = 28 };
        addButton.Click += OnAddDomainClick;
        
        var removeButton = new Button { Text = "Remove", Width = 80, Height = 28 };
        removeButton.Click += OnRemoveDomainClick;
        
        var resetButton = new Button { Text = "Reset All", Width = 80, Height = 28 };
        resetButton.Click += OnResetDomainsClick;
        
        buttonPanel.Controls.Add(addButton);
        buttonPanel.Controls.Add(removeButton);
        buttonPanel.Controls.Add(resetButton);
        
        layout.Controls.Add(buttonPanel, 0, 3);
        
        panel.Controls.Add(layout);
        _categoryPanels[CategoryDomainThreads] = panel;
    }
    
    #endregion
    
    #region Server Queries Panel
    
    private void CreateServerQueriesPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));   // Header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 260));  // Settings grid (7 rows * 32px + padding)
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // Info
        
        // Header
        var header = new Label
        {
            Text = "Server Query Settings",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(header, 0, 0);
        
        // Settings grid
        var settingsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
            Padding = new Padding(0, 10, 0, 0)
        };
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        for (int i = 0; i < 8; i++)
            settingsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        
        var settings = SettingsService.Instance.Settings;
        
        // Row 0: Query Interval
        settingsGrid.Controls.Add(new Label { Text = "Query Interval (ms):", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 0);
        _queryIntervalMs = new NumericUpDown { Minimum = 1, Maximum = 1000, Value = settings.QueryIntervalMs, Width = 80, Anchor = AnchorStyles.Left };
        new ToolTip().SetToolTip(_queryIntervalMs, "Delay between sending individual server queries (1=aggressive, 60=cautious)");
        settingsGrid.Controls.Add(_queryIntervalMs, 1, 0);
        
        // Row 1: Max Concurrent Queries
        settingsGrid.Controls.Add(new Label { Text = "Max Concurrent Queries:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 1);
        _maxConcurrentQueries = new NumericUpDown { Minimum = 0, Maximum = 500, Value = settings.MaxConcurrentQueries, Width = 80, Anchor = AnchorStyles.Left };
        new ToolTip().SetToolTip(_maxConcurrentQueries, "Maximum servers to query simultaneously (0=all at once)");
        settingsGrid.Controls.Add(_maxConcurrentQueries, 1, 1);
        
        // Row 2: Retry Attempts
        settingsGrid.Controls.Add(new Label { Text = "Retry Attempts:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 2);
        _queryRetryAttempts = new NumericUpDown { Minimum = 1, Maximum = 10, Value = settings.QueryRetryAttempts, Width = 80, Anchor = AnchorStyles.Left };
        new ToolTip().SetToolTip(_queryRetryAttempts, "How many times to retry a failed server query");
        settingsGrid.Controls.Add(_queryRetryAttempts, 1, 2);
        
        // Row 3: Retry Delay
        settingsGrid.Controls.Add(new Label { Text = "Retry Delay (ms):", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 3);
        _queryRetryDelayMs = new NumericUpDown { Minimum = 100, Maximum = 10000, Value = settings.QueryRetryDelayMs, Width = 80, Anchor = AnchorStyles.Left };
        new ToolTip().SetToolTip(_queryRetryDelayMs, "Delay before retrying a failed server query");
        settingsGrid.Controls.Add(_queryRetryDelayMs, 1, 3);
        
        // Row 4: Master Server Retry Count
        settingsGrid.Controls.Add(new Label { Text = "Master Server Retries:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 4);
        _masterServerRetryCount = new NumericUpDown { Minimum = 1, Maximum = 10, Value = settings.MasterServerRetryCount, Width = 80, Anchor = AnchorStyles.Left };
        new ToolTip().SetToolTip(_masterServerRetryCount, "Number of retry attempts when master server query fails");
        settingsGrid.Controls.Add(_masterServerRetryCount, 1, 4);
        
        // Row 5: Consecutive Failures Before Offline
        settingsGrid.Controls.Add(new Label { Text = "Failures Before Offline:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 5);
        _consecutiveFailuresBeforeOffline = new NumericUpDown { Minimum = 1, Maximum = 10, Value = settings.ConsecutiveFailuresBeforeOffline, Width = 80, Anchor = AnchorStyles.Left };
        new ToolTip().SetToolTip(_consecutiveFailuresBeforeOffline, "Number of consecutive query failures before marking a server as offline");
        settingsGrid.Controls.Add(_consecutiveFailuresBeforeOffline, 1, 5);
        
        // Row 6: Auto Refresh Interval
        settingsGrid.Controls.Add(new Label { Text = "Auto Refresh Interval (min):", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AutoSize = false }, 0, 6);
        _autoRefreshIntervalMinutes = new NumericUpDown { Minimum = 1, Maximum = 60, Value = settings.AutoRefreshIntervalMinutes, Width = 80, Anchor = AnchorStyles.Left };
        new ToolTip().SetToolTip(_autoRefreshIntervalMinutes, "How often the server list automatically refreshes when auto-refresh is enabled");
        settingsGrid.Controls.Add(_autoRefreshIntervalMinutes, 1, 6);
        
        // Row 7: Auto Refresh Favorites Only
        _autoRefreshFavoritesOnlyCheckBox = new CheckBox { Text = "Auto-refresh favorites only", Checked = settings.AutoRefreshFavoritesOnly, Enabled = !settings.AutoRefresh, AutoSize = true, Anchor = AnchorStyles.Left };
        new ToolTip().SetToolTip(_autoRefreshFavoritesOnlyCheckBox, "When enabled, auto-refresh will only query favorite servers instead of the full server list");
        settingsGrid.SetColumnSpan(_autoRefreshFavoritesOnlyCheckBox, 2);
        settingsGrid.Controls.Add(_autoRefreshFavoritesOnlyCheckBox, 0, 7);
        
        layout.Controls.Add(settingsGrid, 0, 1);
        
        // Info
        var info = new Label
        {
            Text = "These settings control how servers are queried during refresh.\n\n" +
                   "Query Interval: Delay between sending queries. Lower = faster but more aggressive.\n" +
                   "  - 1-5ms: Very Aggressive (fastest, may overwhelm network)\n" +
                   "  - 5-30ms: Aggressive (recommended for good connections)\n" +
                   "  - 30-60ms: Moderate (balanced)\n" +
                   "  - 60+ms: Cautious (slower but reliable)\n\n" +
                   "Master Server Retries: How many times to retry if master server fails to respond.\n\n" +
                   "Failures Before Offline: Servers are kept in the list but marked offline after\n" +
                   "  this many consecutive failures instead of being removed immediately.",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 10, 0, 0)
        };
        layout.Controls.Add(info, 0, 2);
        
        panel.Controls.Add(layout);
        _categoryPanels[CategoryServerQueries] = panel;
    }
    
    #endregion
    
    #region Utility Methods
    
    private TableLayoutPanel CreatePathBrowseRow(out TextBox textBox, string buttonText, Action onClick)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        
        textBox = new TextBox { Dock = DockStyle.Fill };
        panel.Controls.Add(textBox, 0, 0);
        
        var browseButton = new Button
        {
            Text = buttonText,
            Dock = DockStyle.Fill
        };
        browseButton.Click += (_, _) => onClick();
        panel.Controls.Add(browseButton, 1, 0);
        
        return panel;
    }
    
    private void BrowseForExecutable(TextBox targetTextBox)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select Zandronum Executable",
            Filter = "Zandronum|zandronum.exe;zandronum-*.exe|Executable Files|*.exe|All Files|*.*",
            CheckFileExists = true
        };
        
        if (!string.IsNullOrEmpty(targetTextBox.Text) && File.Exists(targetTextBox.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(targetTextBox.Text);
            dialog.FileName = Path.GetFileName(targetTextBox.Text);
        }
        
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            targetTextBox.Text = dialog.FileName;
        }
    }
    
    private void BrowseForFolder(TextBox targetTextBox)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select Folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        
        if (!string.IsNullOrEmpty(targetTextBox.Text) && Directory.Exists(targetTextBox.Text))
        {
            dialog.InitialDirectory = targetTextBox.Text;
        }
        
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            targetTextBox.Text = dialog.SelectedPath;
        }
    }
    
    #endregion
    
    #region WAD Paths Events
    
    private void OnPathSelectionChanged(object? sender, EventArgs e)
    {
        var index = _pathsListBox.SelectedIndex;
        var count = _pathsListBox.Items.Count;
        
        // Index 0 is the download folder - unremovable and unmovable
        _removePathButton.Enabled = index > 0;
        _moveUpButton.Enabled = index > 1;
        _moveDownButton.Enabled = index > 0 && index < count - 1;
        _setAsDownloadButton.Enabled = index > 0; // Can set any non-download-folder path as download folder
    }
    
    private void OnDownloadPathTextChanged(object? sender, EventArgs e)
    {
        // Update the first item in the list to reflect the new download folder path
        if (_pathsListBox.Items.Count > 0)
        {
            var newPath = _downloadPathTextBox.Text.Trim();
            _pathsListBox.Items[0] = DownloadFolderPrefix + newPath;
        }
        
        // Start debounce timer for validation (allows typing without excess validation)
        _downloadPathDebounceTimer?.Stop();
        _downloadPathDebounceTimer ??= new System.Windows.Forms.Timer { Interval = 500 };
        _downloadPathDebounceTimer.Tick -= OnDownloadPathDebounceElapsed;
        _downloadPathDebounceTimer.Tick += OnDownloadPathDebounceElapsed;
        _downloadPathDebounceTimer.Start();
    }
    
    private void OnDownloadPathKeyDown(object? sender, KeyEventArgs e)
    {
        // Detect paste (Ctrl+V or Shift+Insert) for immediate validation
        if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert))
        {
            // Stop debounce timer and validate immediately after paste completes
            _downloadPathDebounceTimer?.Stop();
            BeginInvoke(ValidateAndPreserveDownloadPath);
        }
    }
    
    private void OnDownloadPathLeave(object? sender, EventArgs e)
    {
        // Validate when focus leaves the text box
        _downloadPathDebounceTimer?.Stop();
        ValidateAndPreserveDownloadPath();
    }
    
    private void OnDownloadPathDebounceElapsed(object? sender, EventArgs e)
    {
        _downloadPathDebounceTimer?.Stop();
        ValidateAndPreserveDownloadPath();
    }
    
    private void ValidateAndPreserveDownloadPath()
    {
        var newPath = _downloadPathTextBox.Text.Trim();
        
        // Only process if the path has actually changed and is valid
        if (!string.IsNullOrEmpty(newPath) && 
            !newPath.Equals(_lastValidDownloadPath, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(newPath))
        {
            // Preserve the old valid path before updating
            if (!string.IsNullOrEmpty(_lastValidDownloadPath))
            {
                PreserveOldDownloadPath(_lastValidDownloadPath);
            }
            _lastValidDownloadPath = newPath;
        }
    }
    
    /// <summary>
    /// Adds the old download path to the search paths list if it's valid and not already present.
    /// </summary>
    private void PreserveOldDownloadPath(string oldPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || !Directory.Exists(oldPath))
            return;
        
        var newPath = _downloadPathTextBox.Text.Trim();
        if (oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
            return;
        
        // Check if already in the list
        if (_pathsListBox.Items.Cast<string>().Skip(1).Any(p => p.Equals(oldPath, StringComparison.OrdinalIgnoreCase)))
            return;
        
        _pathsListBox.Items.Insert(1, oldPath);
    }
    
    private void OnSetAsDownloadClick(object? sender, EventArgs e)
    {
        _downloadPathDebounceTimer?.Stop(); // Stop any pending validation
        
        var index = _pathsListBox.SelectedIndex;
        if (index <= 0) return;
        
        var selectedPath = _pathsListBox.Items[index]?.ToString();
        if (string.IsNullOrEmpty(selectedPath)) return;
        
        // Remove the "[Download Folder] " prefix if present (shouldn't be, but just in case)
        if (selectedPath.StartsWith(DownloadFolderPrefix))
            selectedPath = selectedPath[DownloadFolderPrefix.Length..];
        
        // Get the current download path before changing
        var oldDownloadPath = _downloadPathTextBox.Text.Trim();
        
        // Remove the selected path from its current position (it will become the download folder)
        _pathsListBox.Items.RemoveAt(index);
        
        // Set as new download path
        _downloadPathTextBox.Text = selectedPath;
        _lastValidDownloadPath = selectedPath; // Update tracking to prevent debounce re-preservation
        
        // Add the old download folder as a regular search path
        PreserveOldDownloadPath(oldDownloadPath);
    }
    
    private void OnAddPathClick(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a folder to search for WAD files",
            UseDescriptionForTitle = true
        };
        
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var selectedPath = dialog.SelectedPath;
            var downloadPath = _downloadPathTextBox.Text.Trim();
            
            if (selectedPath.Equals(downloadPath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "This folder is already set as the download folder.",
                    "Duplicate Path", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            
            if (!_pathsListBox.Items.Cast<string>().Any(p => p.Equals(selectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                _pathsListBox.Items.Add(selectedPath);
            }
        }
    }
    
    private void OnRemovePathClick(object? sender, EventArgs e)
    {
        if (_pathsListBox.SelectedIndex > 0)
        {
            _pathsListBox.Items.RemoveAt(_pathsListBox.SelectedIndex);
        }
    }
    
    private void MovePathItem(int direction)
    {
        var index = _pathsListBox.SelectedIndex;
        if (index <= 0) return;
        
        var newIndex = index + direction;
        if (newIndex <= 0 || newIndex >= _pathsListBox.Items.Count) return;
        
        var item = _pathsListBox.Items[index];
        _pathsListBox.Items.RemoveAt(index);
        _pathsListBox.Items.Insert(newIndex, item);
        _pathsListBox.SelectedIndex = newIndex;
    }
    
    private void OnBrowseDownloadPath()
    {
        _downloadPathDebounceTimer?.Stop(); // Stop any pending validation
        
        // Get the current download path before browsing
        var oldDownloadPath = _downloadPathTextBox.Text.Trim();
        
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select default download folder",
            UseDescriptionForTitle = true
        };
        
        if (!string.IsNullOrEmpty(_downloadPathTextBox.Text))
        {
            dialog.SelectedPath = _downloadPathTextBox.Text;
        }
        
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _downloadPathTextBox.Text = dialog.SelectedPath;
            _lastValidDownloadPath = dialog.SelectedPath; // Update tracking to prevent debounce re-preservation
            
            // Add the old download folder as a regular search path
            PreserveOldDownloadPath(oldDownloadPath);
        }
    }
    
    #endregion
    
    #region Download Sites Events
    
    private void OnAddDefaultSites(object? sender, EventArgs e)
    {
        foreach (var site in WadDownloader.DefaultSites)
        {
            if (!_sitesListBox.Items.Contains(site))
            {
                _sitesListBox.Items.Add(site);
            }
        }
    }
    
    private void OnAddSiteClick(object? sender, EventArgs e)
    {
        var url = _newSiteTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(url))
        {
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                url = "https://" + url;
            }
            
            if (Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                if (!_sitesListBox.Items.Contains(url))
                {
                    _sitesListBox.Items.Add(url);
                    _newSiteTextBox.Clear();
                }
            }
            else
            {
                MessageBox.Show(this, "Invalid URL format.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
    
    private void OnRemoveSiteClick(object? sender, EventArgs e)
    {
        if (_sitesListBox.SelectedIndex >= 0)
        {
            _sitesListBox.Items.RemoveAt(_sitesListBox.SelectedIndex);
        }
    }
    
    private void MoveSiteItem(int direction)
    {
        var index = _sitesListBox.SelectedIndex;
        if (index < 0) return;
        
        var newIndex = index + direction;
        if (newIndex < 0 || newIndex >= _sitesListBox.Items.Count) return;
        
        var item = _sitesListBox.Items[index];
        _sitesListBox.Items.RemoveAt(index);
        _sitesListBox.Items.Insert(newIndex, item);
        _sitesListBox.SelectedIndex = newIndex;
    }
    
    #endregion
    
    #region Domain Threads Events
    
    private void OnDomainCellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        string colName = _domainGridView.Columns[e.ColumnIndex].Name;
        string? value = e.FormattedValue?.ToString();
        
        if (colName == "MaxThreads")
        {
            if (!int.TryParse(value, out int v) || v < 0)
            {
                e.Cancel = true;
                _domainGridView.Rows[e.RowIndex].ErrorText = "Max threads must be >= 0";
            }
        }
        else if (colName == "MaxConcurrentDownloads")
        {
            if (!int.TryParse(value, out int v) || v < 0)
            {
                e.Cancel = true;
                _domainGridView.Rows[e.RowIndex].ErrorText = "Must be >= 0";
            }
        }
        else if (colName == "InitialThreads")
        {
            if (!int.TryParse(value, out int v) || v < 1 || v > 32)
            {
                e.Cancel = true;
                _domainGridView.Rows[e.RowIndex].ErrorText = "Must be between 1 and 32";
            }
        }
        else if (colName == "MinSegmentSizeKb")
        {
            if (!int.TryParse(value, out int v) || v < 64 || v > 4096)
            {
                e.Cancel = true;
                _domainGridView.Rows[e.RowIndex].ErrorText = "Must be between 64 and 4096";
            }
        }
        else if (colName == "Domain")
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                e.Cancel = true;
                _domainGridView.Rows[e.RowIndex].ErrorText = "Domain cannot be empty";
            }
        }
    }
    
    private void OnDomainCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        _domainGridView.Rows[e.RowIndex].ErrorText = "";
        
        if (_domainGridView.Columns[e.ColumnIndex].Name == "MaxThreads")
        {
            if (e.RowIndex < _domainRows.Count)
            {
                _domainRows[e.RowIndex].IsUserConfigured = true;
                _domainGridView.InvalidateRow(e.RowIndex);
            }
        }
    }
    
    private void OnAddDomainClick(object? sender, EventArgs e)
    {
        var newRow = new DomainSettingsRow
        {
            Domain = "example.com",
            MaxThreads = 0,
            MaxConcurrentDownloads = 0,
            InitialThreads = 2,
            MinSegmentSizeKb = 256,
            AdaptiveLearning = true,
            IsUserConfigured = true,
            SuccessCount = 0,
            FailureCount = 0,
            Notes = "",
            OriginalDomain = null
        };
        _domainRows.Add(newRow);
        RefreshDomainGrid();
        
        int newRowIndex = _domainRows.Count - 1;
        _domainGridView.ClearSelection();
        _domainGridView.Rows[newRowIndex].Selected = true;
        _domainGridView.CurrentCell = _domainGridView.Rows[newRowIndex].Cells["Domain"];
        _domainGridView.BeginEdit(true);
    }
    
    private void OnRemoveDomainClick(object? sender, EventArgs e)
    {
        if (_domainGridView.SelectedRows.Count > 0)
        {
            int index = _domainGridView.SelectedRows[0].Index;
            if (index >= 0 && index < _domainRows.Count)
            {
                _domainRows.RemoveAt(index);
                RefreshDomainGrid();
            }
        }
    }
    
    private void OnResetDomainsClick(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to reset all domain thread settings?\n\n" +
            "This will clear all learned and configured values.",
            "Confirm Reset",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        
        if (result == DialogResult.Yes)
        {
            _domainRows.Clear();
            RefreshDomainGrid();
        }
    }
    
    private void RefreshDomainGrid()
    {
        _domainGridView.DataSource = null;
        _domainGridView.DataSource = new BindingList<DomainSettingsRow>(_domainRows);
    }
    
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Intercept Enter key when editing domain grid to commit edit
        if (keyData == Keys.Enter && _domainGridView.IsCurrentCellInEditMode)
        {
            _domainGridView.EndEdit();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    
    #endregion
    
    #region Load/Save
    
    private void LoadSettings()
    {
        var settings = SettingsService.Instance.Settings;
        
        // General
        _zandronumPathTextBox.Text = settings.ZandronumPath;
        _hashConcurrencyNumeric.Value = Math.Max(0, Math.Min(32, settings.HashVerificationConcurrency));
        
        if (!string.IsNullOrEmpty(settings.ZandronumTestingPath))
        {
            _zandronumTestingPathTextBox.Text = settings.ZandronumTestingPath;
        }
        else if (!string.IsNullOrEmpty(settings.ZandronumPath) && File.Exists(settings.ZandronumPath))
        {
            var exeDir = Path.GetDirectoryName(settings.ZandronumPath);
            if (!string.IsNullOrEmpty(exeDir))
            {
                _zandronumTestingPathTextBox.Text = Path.Combine(exeDir, "TestingVersions");
            }
        }
        
        // Screenshot settings
        _screenshotMonitorCheckBox.Checked = settings.EnableScreenshotMonitoring;
        _screenshotPathTextBox.Text = settings.ScreenshotConsolidationPath;
        _screenshotPathTextBox.Enabled = settings.EnableScreenshotMonitoring;
        
        // WAD Paths
        SearchPaths = settings.WadSearchPaths.ToList();
        WadDownloadPath = settings.WadDownloadPath;
        PopulateWadControls();
        
        // Download Sites
        DownloadSites = settings.DownloadSites.Count > 0 
            ? settings.DownloadSites.ToList() 
            : WadDownloader.DefaultSites.ToList();
        PopulateSitesControl();
        
        // Domain Threads
        LoadDomainSettings();
        
        // Favorites & Manual Servers
        LoadFavoritesSettings();
    }
    
    private void LoadFavoritesSettings()
    {
        var settings = SettingsService.Instance.Settings;
        
        _favoritesListBox.Items.Clear();
        foreach (var favorite in settings.FavoriteServers.OrderBy(f => f))
        {
            _favoritesListBox.Items.Add(favorite);
        }
        
        _manualServersListBox.Items.Clear();
        foreach (var manual in settings.ManualServers.OrderBy(m => m.FullAddress))
        {
            _manualServersListBox.Items.Add(manual.FullAddress);
        }
        
        // Alert settings
        _enableFavoriteAlertsCheckBox.Checked = settings.EnableFavoriteServerAlerts;
        _enableManualAlertsCheckBox.Checked = settings.EnableManualServerAlerts;
        _showFavoritesColumnCheckBox.Checked = settings.ShowFavoritesColumn;
        _alertMinPlayersNumeric.Value = Math.Clamp(settings.AlertMinPlayers, 0, 64);
        _alertIntervalNumeric.Value = Math.Clamp(settings.AlertCheckIntervalSeconds, 30, 600);
    }
    
    private void PopulateWadControls()
    {
        _pathsListBox.Items.Clear();
        
        if (!string.IsNullOrEmpty(WadDownloadPath))
        {
            _pathsListBox.Items.Add(DownloadFolderPrefix + WadDownloadPath);
        }
        else
        {
            _pathsListBox.Items.Add(DownloadFolderPrefix + "(not set)");
        }
        
        foreach (var path in SearchPaths.Where(p => !p.Equals(WadDownloadPath, StringComparison.OrdinalIgnoreCase)))
        {
            _pathsListBox.Items.Add(path);
        }
        
        _downloadPathTextBox.Text = WadDownloadPath;
        _lastValidDownloadPath = WadDownloadPath; // Initialize for change tracking
    }
    
    private void PopulateSitesControl()
    {
        _sitesListBox.Items.Clear();
        foreach (var site in DownloadSites)
        {
            _sitesListBox.Items.Add(site);
        }
    }
    
    private void LoadDomainSettings()
    {
        _domainRows.Clear();
        var settings = SettingsService.Instance.Settings.DomainThreadSettings;
        
        foreach (var kvp in settings.OrderBy(k => k.Key))
        {
            _domainRows.Add(new DomainSettingsRow
            {
                Domain = kvp.Key,
                MaxThreads = kvp.Value.MaxThreads,
                MaxConcurrentDownloads = kvp.Value.MaxConcurrentDownloads,
                InitialThreads = kvp.Value.InitialThreads,
                MinSegmentSizeKb = kvp.Value.MinSegmentSizeKb,
                AdaptiveLearning = kvp.Value.AdaptiveLearning,
                IsUserConfigured = kvp.Value.IsUserConfigured,
                SuccessCount = kvp.Value.SuccessCount,
                FailureCount = kvp.Value.FailureCount,
                Notes = kvp.Value.Notes ?? "",
                OriginalDomain = kvp.Key
            });
        }
        
        RefreshDomainGrid();
    }
    
    private void OnOkClick(object? sender, EventArgs e)
    {
        // Commit any pending grid edit
        if (_domainGridView.IsCurrentCellInEditMode)
        {
            _domainGridView.EndEdit();
        }
        
        // Validate domains are unique
        var domains = _domainRows.Select(r => r.Domain.ToLowerInvariant()).ToList();
        var duplicates = domains.GroupBy(d => d).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        
        if (duplicates.Count > 0)
        {
            MessageBox.Show(
                $"Duplicate domains found: {string.Join(", ", duplicates)}\n\nPlease ensure all domains are unique.",
                "Validation Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            DialogResult = DialogResult.None;
            return;
        }
        
        SaveSettings();
    }
    
    private void SaveSettings()
    {
        var settings = SettingsService.Instance.Settings;
        
        // General
        settings.ZandronumPath = _zandronumPathTextBox.Text.Trim();
        settings.ZandronumTestingPath = _zandronumTestingPathTextBox.Text.Trim();
        settings.HashVerificationConcurrency = (int)_hashConcurrencyNumeric.Value;
        
        // Screenshot settings
        settings.EnableScreenshotMonitoring = _screenshotMonitorCheckBox.Checked;
        settings.ScreenshotConsolidationPath = _screenshotPathTextBox.Text.Trim();
        
        // Downloads
        settings.MaxConcurrentDownloads = (int)_maxConcurrentDownloads.Value;
        settings.MaxConcurrentDomains = (int)_maxConcurrentDomains.Value;
        settings.DefaultMaxThreads = (int)_defaultMaxThreads.Value;
        settings.MaxThreadsPerFile = (int)_maxThreadsPerFile.Value;
        settings.DefaultInitialThreads = (int)_defaultInitialThreads.Value;
        settings.DefaultMinSegmentSizeKb = (int)_defaultMinSegmentKb.Value;
        
        // WAD Paths (skip index 0 - download folder display)
        SearchPaths = _pathsListBox.Items.Cast<string>().Skip(1).ToList();
        WadDownloadPath = _downloadPathTextBox.Text.Trim();
        settings.WadSearchPaths = SearchPaths;
        settings.WadDownloadPath = WadDownloadPath;
        
        // Download Sites
        DownloadSites = _sitesListBox.Items.Cast<string>().ToList();
        settings.DownloadSites = DownloadSites;
        
        // Domain Threads
        settings.DomainThreadSettings.Clear();
        foreach (var row in _domainRows)
        {
            string domain = row.Domain.ToLowerInvariant().Trim();
            if (!string.IsNullOrWhiteSpace(domain))
            {
                settings.DomainThreadSettings[domain] = new DomainSettings
                {
                    MaxThreads = row.MaxThreads,
                    MaxConcurrentDownloads = row.MaxConcurrentDownloads,
                    InitialThreads = row.InitialThreads,
                    MinSegmentSizeKb = row.MinSegmentSizeKb,
                    AdaptiveLearning = row.AdaptiveLearning,
                    IsUserConfigured = row.IsUserConfigured,
                    SuccessCount = row.SuccessCount,
                    FailureCount = row.FailureCount,
                    Notes = string.IsNullOrWhiteSpace(row.Notes) ? null : row.Notes,
                    LastUpdated = DateTime.UtcNow
                };
            }
        }
        
        // Server Queries
        settings.QueryIntervalMs = (int)_queryIntervalMs.Value;
        settings.MaxConcurrentQueries = (int)_maxConcurrentQueries.Value;
        settings.QueryRetryAttempts = (int)_queryRetryAttempts.Value;
        settings.QueryRetryDelayMs = (int)_queryRetryDelayMs.Value;
        settings.MasterServerRetryCount = (int)_masterServerRetryCount.Value;
        settings.ConsecutiveFailuresBeforeOffline = (int)_consecutiveFailuresBeforeOffline.Value;
        settings.AutoRefreshIntervalMinutes = (int)_autoRefreshIntervalMinutes.Value;
        settings.AutoRefreshFavoritesOnly = _autoRefreshFavoritesOnlyCheckBox.Checked;
        
        // Favorites & Manual Servers
        SaveFavoritesSettings();
        
        SettingsService.Instance.Save();
    }
    
    private void SaveFavoritesSettings()
    {
        var settings = SettingsService.Instance.Settings;
        
        // Update favorites - sync from listbox back to settings
        settings.FavoriteServers.Clear();
        foreach (string favorite in _favoritesListBox.Items)
        {
            settings.FavoriteServers.Add(favorite);
        }
        
        // Update manual servers - need to preserve entries that match
        var currentManualAddresses = _manualServersListBox.Items.Cast<string>().ToHashSet();
        
        // Remove entries no longer in listbox
        settings.ManualServers.RemoveAll(m => !currentManualAddresses.Contains(m.FullAddress));
        
        // Alert settings
        settings.EnableFavoriteServerAlerts = _enableFavoriteAlertsCheckBox.Checked;
        settings.EnableManualServerAlerts = _enableManualAlertsCheckBox.Checked;
        settings.ShowFavoritesColumn = _showFavoritesColumnCheckBox.Checked;
        settings.AlertMinPlayers = (int)_alertMinPlayersNumeric.Value;
        settings.AlertCheckIntervalSeconds = (int)_alertIntervalNumeric.Value;
    }
    
    #endregion
    
    #region Dark Theme
    
    private void ApplyDarkTheme()
    {
        BackColor = DarkTheme.PrimaryBackground;
        ForeColor = DarkTheme.TextPrimary;
        ApplyDarkThemeToControls(Controls);
    }
    
    private void ApplyDarkThemeToControls(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            control.BackColor = control switch
            {
                TextBox or ListBox => DarkTheme.SecondaryBackground,
                Button => DarkTheme.SecondaryBackground,
                DataGridView => DarkTheme.SecondaryBackground,
                SplitContainer => DarkTheme.PrimaryBackground,
                _ => DarkTheme.PrimaryBackground
            };
            control.ForeColor = DarkTheme.TextPrimary;
            
            if (control is Button btn)
            {
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderColor = DarkTheme.AccentColor;
            }
            
            if (control is DataGridView grid)
            {
                grid.BackgroundColor = DarkTheme.SecondaryBackground;
                grid.GridColor = DarkTheme.BorderColor;
                grid.DefaultCellStyle.BackColor = DarkTheme.SecondaryBackground;
                grid.DefaultCellStyle.ForeColor = DarkTheme.TextPrimary;
                grid.DefaultCellStyle.SelectionBackColor = DarkTheme.AccentColor;
                grid.ColumnHeadersDefaultCellStyle.BackColor = DarkTheme.PrimaryBackground;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = DarkTheme.TextPrimary;
                grid.EnableHeadersVisualStyles = false;
            }
            
            if (control is ListBox lb)
            {
                lb.BorderStyle = BorderStyle.FixedSingle;
            }
            
            if (control.HasChildren)
            {
                ApplyDarkThemeToControls(control.Controls);
            }
        }
    }
    
    #endregion
    
    /// <summary>
    /// Row model for domain settings DataGridView binding.
    /// </summary>
    private class DomainSettingsRow
    {
        public string Domain { get; set; } = "";
        public int MaxThreads { get; set; } = 0;
        public int MaxConcurrentDownloads { get; set; } = 0;
        public int InitialThreads { get; set; } = 2;
        public int MinSegmentSizeKb { get; set; } = 256;
        public bool AdaptiveLearning { get; set; } = true;
        public bool IsUserConfigured { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public string Notes { get; set; } = "";
        public string? OriginalDomain { get; set; }
    }
}
