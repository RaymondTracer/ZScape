using System.Text;
using ZScape.Models;
using ZScape.Services;
using ZScape.UI;
using ZScape.Utilities;

namespace ZScape;

/// <summary>
/// Main application form for ZScape.
/// </summary>
public partial class MainForm : Form
{
    private readonly ServerBrowserService _browserService = new();
    private readonly LoggingService _logger = LoggingService.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly WadManager _wadManager = WadManager.Instance;
    private readonly NotificationService _notificationService = NotificationService.Instance;
    private readonly ScreenshotMonitorService _screenshotMonitor = ScreenshotMonitorService.Instance;
    private readonly BindingSource _serverBindingSource = new();
    private List<ServerInfo> _allServers = [];
    private List<ServerInfo> _filteredServers = []; // Virtual mode data source
    private ServerInfo? _selectedServer;
    private string? _selectedServerAddress; // For selection preservation
    private Bitmap? _lockIcon; // Cached lock icon for passworded servers
    private Bitmap? _starIcon; // Cached filled star icon for favorites
    private Bitmap? _emptyStarIcon; // Cached empty star icon
    private int _sortColumnIndex = 2; // Default to Players column
    private bool _sortAscending = false; // Default descending (most players first)
    private bool _suppressSelectionEvents = false; // Suppress auto-selection during grid updates
    private DateTime _lastUiUpdate = DateTime.MinValue;
    private readonly TimeSpan _uiUpdateThrottle = TimeSpan.FromMilliseconds(AppConstants.UiIntervals.UiUpdateThrottleMs);
    private bool _isInitializing = true; // Prevent saving during initialization
    private ServerFilter _currentFilter = new();
    private List<ServerFilter> _filterPresets = [];
    
    // Server alert system
    private System.Windows.Forms.Timer? _alertTimer;
    private readonly Dictionary<string, ServerAlertState> _serverAlertStates = new();
    
    // Update notification
    private Panel? _updateNotificationPanel;

    public MainForm()
    {
        InitializeComponent();
        InitializeDataGridView();
        InitializeGameModeFilter();
        ApplyDarkTheme();
        DarkModeHelper.ApplyDarkTitleBar(this);
        SubscribeToEvents();
        // LoadSettings is called in OnLoad after controls are fully initialized
    }

    private void InitializeDataGridView()
    {
        serverListView.AutoGenerateColumns = false;
        serverListView.VirtualMode = true;
        serverListView.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        serverListView.CellValueNeeded += ServerListView_CellValueNeeded;
        serverListView.RowPrePaint += ServerListView_RowPrePaint;
        serverListView.CellClick += ServerListView_CellClick;
        serverContextMenu.Opening += ServerContextMenu_Opening;
        serverListView.Columns.Clear();

        // Favorite star column (fixed width)
        serverListView.Columns.Add(new DataGridViewImageColumn
        {
            Name = "Favorite",
            HeaderText = "",
            Width = AppConstants.ServerListColumns.FavoriteWidth,
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            Resizable = DataGridViewTriState.False,
            Visible = _settings.Settings.ShowFavoritesColumn,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None
        });

        // Lock icon for passworded servers (fixed width)
        serverListView.Columns.Add(new DataGridViewImageColumn
        {
            Name = "Icon",
            HeaderText = "",
            Width = AppConstants.ServerListColumns.IconWidth,
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            Resizable = DataGridViewTriState.False,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None
        });

        // Server Name - takes most of the space
        serverListView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = "Server Name",
            DataPropertyName = "Name",
            MinimumWidth = AppConstants.ServerListColumns.NameMinWidth,
            FillWeight = 40
        });

        serverListView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Players",
            HeaderText = "Players",
            MinimumWidth = AppConstants.ServerListColumns.PlayersMinWidth,
            FillWeight = 6
        });

        serverListView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Ping",
            HeaderText = "Ping",
            DataPropertyName = "Ping",
            MinimumWidth = AppConstants.ServerListColumns.PingMinWidth,
            FillWeight = 5
        });

        serverListView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Map",
            HeaderText = "Map",
            DataPropertyName = "Map",
            MinimumWidth = AppConstants.ServerListColumns.MapMinWidth,
            FillWeight = 10
        });

        serverListView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "GameMode",
            HeaderText = "Mode",
            MinimumWidth = AppConstants.ServerListColumns.GameModeMinWidth,
            FillWeight = 7
        });

        serverListView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "IWAD",
            HeaderText = "IWAD",
            DataPropertyName = "IWAD",
            MinimumWidth = AppConstants.ServerListColumns.IwadMinWidth,
            FillWeight = 10
        });

        serverListView.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Address",
            HeaderText = "Address",
            DataPropertyName = "Address",
            MinimumWidth = AppConstants.ServerListColumns.AddressMinWidth,
            FillWeight = 12
        });
    }

    private void InitializeGameModeFilter()
    {
        gameModeComboBox.Items.Clear();
        gameModeComboBox.Items.Add("All Modes");
        
        foreach (GameModeType mode in Enum.GetValues<GameModeType>())
        {
            if (mode != GameModeType.Unknown)
            {
                var gm = GameMode.FromType(mode);
                gameModeComboBox.Items.Add(gm.Name);
            }
        }
        
        gameModeComboBox.SelectedIndex = 0;
    }

    private void ApplyDarkTheme()
    {
        DarkTheme.Apply(this);
        
        // Apply to specific controls that need extra attention
        serverInfoTextBox.BackColor = DarkTheme.TertiaryBackground;
        serverInfoTextBox.ForeColor = DarkTheme.TextPrimary;
        
        logTextBox.BackColor = DarkTheme.TertiaryBackground;
        logTextBox.ForeColor = DarkTheme.TextPrimary;

        playerListView.BackColor = DarkTheme.SecondaryBackground;
        playerListView.ForeColor = DarkTheme.TextPrimary;
        
        wadsListView.BackColor = DarkTheme.SecondaryBackground;
        wadsListView.ForeColor = DarkTheme.TextPrimary;
    }

    private void SubscribeToEvents()
    {
        _browserService.RefreshStarted += BrowserService_RefreshStarted;
        _browserService.RefreshProgress += BrowserService_RefreshProgress;
        _browserService.RefreshCompleted += BrowserService_RefreshCompleted;
        _browserService.ServerUpdated += BrowserService_ServerUpdated;
        
        _logger.LogAdded += Logger_LogAdded;
        
        // Alert notification click handler
        _notificationService.AlertClicked += NotificationService_AlertClicked;
        
        // Initialize alert timer
        InitializeAlertTimer();
        
        // Start screenshot monitoring if enabled
        _screenshotMonitor.StartMonitoring();
        
        // Subscribe to update service events
        UpdateService.Instance.UpdateReady += UpdateService_UpdateReady;
        UpdateService.Instance.UpdateAvailable += UpdateService_UpdateAvailable;
        UpdateService.Instance.IsApplicationBusy = () => _browserService.IsRefreshing;
        UpdateService.Instance.GetServerState = () => _browserService.GetServerState();
    }
    
    private void UpdateService_UpdateAvailable(object? sender, UpdateAvailableEventArgs e)
    {
        if (InvokeRequired)
        {
            Invoke(() => UpdateService_UpdateAvailable(sender, e));
            return;
        }
        
        // Only show dialog in CheckOnly mode (CheckAndDownload mode uses the notification bar)
        var settings = SettingsService.Instance.Settings;
        if (settings.UpdateBehavior == UpdateBehavior.CheckOnly)
        {
            var result = MessageBox.Show(
                $"A new version (v{e.NewVersion}) is available!\n\n" +
                $"Current version: v{e.CurrentVersion}\n\n" +
                "Would you like to view the release page?",
                "Update Available",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            
            if (result == DialogResult.Yes)
            {
                UpdateService.Instance.OpenReleasePage();
            }
        }
    }
    
    private void UpdateService_UpdateReady(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            Invoke(() => UpdateService_UpdateReady(sender, e));
            return;
        }
        
        ShowUpdateNotification();
    }
    
    private void ShowUpdateNotification()
    {
        if (_updateNotificationPanel != null) return; // Already showing
        
        _updateNotificationPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = Color.FromArgb(40, 80, 120),
            Padding = new Padding(10, 5, 10, 5)
        };
        
        var message = new Label
        {
            Text = $"Update available: v{UpdateService.Instance.LatestVersion}",
            ForeColor = Color.White,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Left,
            Font = new Font(Font.FontFamily, 9.5f)
        };
        
        var installButton = new Button
        {
            Text = "Install && Restart",
            Dock = DockStyle.Right,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(60, 120, 180)
        };
        installButton.FlatAppearance.BorderColor = Color.White;
        installButton.Click += (_, _) =>
        {
            var result = MessageBox.Show(
                "This will close ZScape and install the update. Continue?",
                "Install Update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            
            if (result == DialogResult.Yes)
            {
                UpdateService.Instance.InstallUpdate();
            }
        };
        
        var laterButton = new Button
        {
            Text = "Later",
            Dock = DockStyle.Right,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 10, 0)
        };
        laterButton.FlatAppearance.BorderSize = 0;
        laterButton.Click += (_, _) => HideUpdateNotification();
        
        var releaseNotesButton = new Button
        {
            Text = "Release Notes",
            Dock = DockStyle.Right,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.LightGray,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 10, 0)
        };
        releaseNotesButton.FlatAppearance.BorderSize = 0;
        releaseNotesButton.Click += (_, _) => UpdateService.Instance.OpenReleasePage();
        
        _updateNotificationPanel.Controls.Add(message);
        _updateNotificationPanel.Controls.Add(laterButton);
        _updateNotificationPanel.Controls.Add(releaseNotesButton);
        _updateNotificationPanel.Controls.Add(installButton);
        
        Controls.Add(_updateNotificationPanel);
        _updateNotificationPanel.BringToFront();
    }
    
    private void HideUpdateNotification()
    {
        if (_updateNotificationPanel != null)
        {
            Controls.Remove(_updateNotificationPanel);
            _updateNotificationPanel.Dispose();
            _updateNotificationPanel = null;
        }
    }
    
    private void InitializeAlertTimer()
    {
        _alertTimer = new System.Windows.Forms.Timer();
        _alertTimer.Tick += AlertTimer_Tick;
        UpdateAlertTimerInterval();
        
        // Start timer if alerts are enabled
        if (_settings.Settings.EnableFavoriteServerAlerts || _settings.Settings.EnableManualServerAlerts)
        {
            _alertTimer.Start();
        }
    }
    
    private void UpdateAlertTimerInterval()
    {
        if (_alertTimer != null)
        {
            _alertTimer.Interval = Math.Max(1000, _settings.Settings.AlertCheckIntervalSeconds * 1000);
        }
    }
    
    private async void AlertTimer_Tick(object? sender, EventArgs e)
    {
        // Only check when window is not focused (background monitoring)
        if (Form.ActiveForm == this)
            return;
        
        var settings = _settings.Settings;
        if (!settings.EnableFavoriteServerAlerts && !settings.EnableManualServerAlerts)
            return;
        
        await CheckServersForAlertsAsync();
    }
    
    private async Task CheckServersForAlertsAsync()
    {
        var settings = _settings.Settings;
        var minPlayers = settings.AlertMinPlayers;
        var alertsToShow = new List<(ServerInfo Server, ServerAlertType Type)>();
        
        foreach (var server in _browserService.Servers.Where(s => s.IsOnline && s.IsQueried))
        {
            var address = $"{server.Address}:{server.Port}";
            var hasMinPlayers = server.CurrentPlayers >= minPlayers;
            
            // Check if this is a favorite or manual server
            var isFavorite = settings.FavoriteServers.Contains(address);
            var isManual = settings.ManualServers.Any(m => m.FullAddress == address);
            
            if (!isFavorite && !isManual)
                continue;
            
            // Get or create state tracking
            if (!_serverAlertStates.TryGetValue(address, out var state))
            {
                state = new ServerAlertState();
                _serverAlertStates[address] = state;
            }
            
            var shouldAlert = false;
            ServerAlertType alertType = ServerAlertType.Favorite;
            
            // Check favorite alert
            if (isFavorite && settings.EnableFavoriteServerAlerts && hasMinPlayers)
            {
                if (!state.WasOnlineWithPlayers)
                {
                    shouldAlert = true;
                    alertType = ServerAlertType.Favorite;
                }
            }
            
            // Check manual server alert (if not already alerted as favorite)
            if (!shouldAlert && isManual && settings.EnableManualServerAlerts && hasMinPlayers)
            {
                if (!state.WasOnlineWithPlayers)
                {
                    shouldAlert = true;
                    alertType = ServerAlertType.Manual;
                }
            }
            
            // Update state
            state.WasOnlineWithPlayers = hasMinPlayers;
            
            if (shouldAlert)
            {
                alertsToShow.Add((server, alertType));
            }
        }
        
        // Show alerts
        if (alertsToShow.Count > 0)
        {
            if (alertsToShow.Count == 1)
            {
                _notificationService.ShowServerAlert(alertsToShow[0].Server, alertsToShow[0].Type);
            }
            else
            {
                _notificationService.ShowMultipleServersAlert(alertsToShow);
            }
        }
        
        // Trigger a background refresh of favorites/manual servers
        await RefreshAlertServersAsync();
    }
    
    private async Task RefreshAlertServersAsync()
    {
        var settings = _settings.Settings;
        var endpointsToRefresh = new List<System.Net.IPEndPoint>();
        
        // Collect favorite addresses
        if (settings.EnableFavoriteServerAlerts)
        {
            foreach (var fav in settings.FavoriteServers)
            {
                if (TryParseEndpoint(fav, out var ep))
                {
                    endpointsToRefresh.Add(ep);
                }
            }
        }
        
        // Collect manual server addresses
        if (settings.EnableManualServerAlerts)
        {
            foreach (var manual in settings.ManualServers)
            {
                if (System.Net.IPAddress.TryParse(manual.Address, out var ip))
                {
                    endpointsToRefresh.Add(new System.Net.IPEndPoint(ip, manual.Port));
                }
            }
        }
        
        // Deduplicate
        var uniqueEndpoints = endpointsToRefresh
            .GroupBy(e => e.ToString())
            .Select(g => g.First())
            .ToList();
        
        // Query each endpoint
        foreach (var ep in uniqueEndpoints.Take(10)) // Limit to prevent hammering
        {
            try
            {
                await _browserService.RefreshServerAsync(ep);
            }
            catch
            {
                // Ignore individual failures
            }
        }
    }
    
    private static bool TryParseEndpoint(string address, out System.Net.IPEndPoint endpoint)
    {
        endpoint = null!;
        var parts = address.Split(':');
        if (parts.Length != 2)
            return false;
        
        if (!System.Net.IPAddress.TryParse(parts[0], out var ip))
            return false;
        
        if (!int.TryParse(parts[1], out var port))
            return false;
        
        endpoint = new System.Net.IPEndPoint(ip, port);
        return true;
    }
    
    private void NotificationService_AlertClicked(object? sender, ServerAlertEventArgs e)
    {
        // Bring window to front and select the server
        BeginInvoke(() =>
        {
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            
            // Find and select the server in the list
            var address = $"{e.Server.Address}:{e.Server.Port}";
            for (int i = 0; i < _filteredServers.Count; i++)
            {
                var server = _filteredServers[i];
                if ($"{server.Address}:{server.Port}" == address)
                {
                    serverListView.ClearSelection();
                    serverListView.Rows[i].Selected = true;
                    serverListView.FirstDisplayedScrollingRowIndex = i;
                    DisplayServerDetails(server);
                    break;
                }
            }
        });
    }

    private void LoadSettings()
    {
        var settings = _settings.Settings;
        
        // Window position and size
        if (settings.WindowX >= 0 && settings.WindowY >= 0)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(settings.WindowX, settings.WindowY);
        }
        if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
        {
            Size = new Size(settings.WindowWidth, settings.WindowHeight);
        }
        if (settings.WindowMaximized)
        {
            WindowState = FormWindowState.Maximized;
        }
        
        // Column widths (only for fixed-width columns - Fill columns auto-adjust)
        foreach (var kvp in settings.ColumnWidths)
        {
            if (serverListView.Columns.Contains(kvp.Key))
            {
                var col = serverListView.Columns[kvp.Key];
                if (col != null && col.AutoSizeMode == DataGridViewAutoSizeColumnMode.None)
                {
                    col.Width = kvp.Value;
                }
            }
        }
        
        // Sorting
        _sortColumnIndex = settings.SortColumnIndex;
        _sortAscending = settings.SortAscending;
        
        // Filters
        hideEmptyCheckBox.Checked = settings.HideEmpty;
        hideBotOnlyCheckBox.Checked = settings.TreatBotOnlyAsEmpty;
        hideFullCheckBox.Checked = settings.HideFull;
        hidePasswordedCheckBox.Checked = settings.HidePassworded;
        if (settings.GameModeFilterIndex >= 0 && settings.GameModeFilterIndex < gameModeComboBox.Items.Count)
        {
            gameModeComboBox.SelectedIndex = settings.GameModeFilterIndex;
        }
        searchBox.Text = settings.SearchText;
        
        // View settings
        verboseMenuItem.Checked = settings.VerboseMode;
        _logger.VerboseMode = settings.VerboseMode;
        hexDumpMenuItem.Checked = settings.ShowHexDumps;
        _logger.ShowHexDumps = settings.ShowHexDumps;
        showLogPanelMenuItem.Checked = settings.ShowLogPanel;
        logPanel.Visible = settings.ShowLogPanel;
        
        // Settings menu
        refreshOnLaunchMenuItem.Checked = settings.RefreshOnLaunch;
        autoRefreshMenuItem.Checked = settings.AutoRefresh;
        autoRefreshFavoritesOnlyMenuItem.Checked = settings.AutoRefreshFavoritesOnly;
        autoRefreshFavoritesOnlyMenuItem.Enabled = !settings.AutoRefresh;
        autoRefreshTimer.Enabled = settings.AutoRefresh;
        if (settings.AutoRefreshIntervalMinutes > 0)
        {
            autoRefreshTimer.Interval = settings.AutoRefreshIntervalMinutes * 60000;
        }
        
        // Splitter positions - apply safely within bounds
        if (settings.MainSplitterDistance > 0 && settings.MainSplitterDistance < mainSplitContainer.Height - 100)
        {
            try { mainSplitContainer.SplitterDistance = settings.MainSplitterDistance; } catch { }
        }
        // DetailsSplitterDistance no longer used (TableLayoutPanel doesn't have splitters)

        // Advanced filter
        if (settings.CurrentFilter != null)
        {
            _currentFilter = settings.CurrentFilter.Clone();
            // Sync toolbar with advanced filter
            hideEmptyCheckBox.Checked = !_currentFilter.ShowEmpty;
            hideBotOnlyCheckBox.Checked = _currentFilter.TreatBotOnlyAsEmpty;
            hideFullCheckBox.Checked = !_currentFilter.ShowFull;
            hidePasswordedCheckBox.Checked = _currentFilter.PasswordedServers == FilterMode.Hide;
        }
        _filterPresets = settings.FilterPresets?.Select(p => p.Clone()).ToList() ?? [];
        
        // Favorites settings
        showFavoritesOnlyCheckBox.Checked = settings.ShowFavoritesOnly;
        if (serverListView.Columns.Contains("Favorite"))
        {
            serverListView.Columns["Favorite"]!.Visible = settings.ShowFavoritesColumn;
        }

        // First-time setup wizard - show if settings file doesn't exist
        if (!_settings.SettingsFileExists)
        {
            using var setupDialog = new UI.FirstTimeSetupDialog();
            if (setupDialog.ShowDialog(this) != DialogResult.OK)
            {
                // User cancelled - exit the application
                Environment.Exit(0);
                return;
            }
            // Reload settings after setup
            settings = _settings.Settings;
        }

        // WAD settings - auto-detect search paths if not configured
        var searchPaths = settings.WadSearchPaths ?? [];
        if (searchPaths.Count == 0)
        {
            var detectedWadPaths = AutoDetectWadSearchPaths();
            if (detectedWadPaths.Count > 0)
            {
                searchPaths = detectedWadPaths;
                settings.WadSearchPaths = detectedWadPaths;
                _logger.Info($"Auto-detected WAD folders: {string.Join(", ", detectedWadPaths)}");
            }
        }
        _wadManager.SetSearchPaths(searchPaths);
        _wadManager.DownloadPath = !string.IsNullOrEmpty(settings.WadDownloadPath) 
            ? settings.WadDownloadPath 
            : Path.Combine(AppContext.BaseDirectory, "WADs");
        
        // Set executable folders for WAD discovery (highest priority search locations)
        var exePaths = new List<string>();
        if (!string.IsNullOrEmpty(settings.ZandronumPath))
            exePaths.Add(settings.ZandronumPath);
        // Also check testing versions folder for any executables
        if (!string.IsNullOrEmpty(settings.ZandronumTestingPath) && Directory.Exists(settings.ZandronumTestingPath))
        {
            try
            {
                foreach (var exeFile in Directory.EnumerateFiles(settings.ZandronumTestingPath, "*.exe", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(exeFile).Contains("zandronum", StringComparison.OrdinalIgnoreCase))
                    {
                        exePaths.Add(exeFile);
                    }
                }
            }
            catch { /* Ignore access errors */ }
        }
        
        // Auto-detect Zandronum if not configured
        if (exePaths.Count == 0)
        {
            var detectedPath = AutoDetectZandronumPath();
            if (!string.IsNullOrEmpty(detectedPath))
            {
                exePaths.Add(detectedPath);
                settings.ZandronumPath = detectedPath;
                _logger.Info($"Auto-detected Zandronum: {detectedPath}");
            }
        }
        
        _wadManager.SetExecutableFolders(exePaths);
        _wadManager.RefreshCache();
    }
    
    /// <summary>
    /// Attempts to auto-detect Zandronum installation path.
    /// </summary>
    private static string? AutoDetectZandronumPath()
    {
        var commonPaths = new[]
        {
            // Common installation locations
            @"C:\Zandronum\zandronum.exe",
            @"C:\Games\Zandronum\zandronum.exe",
            @"C:\Program Files\Zandronum\zandronum.exe",
            @"C:\Program Files (x86)\Zandronum\zandronum.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zandronum", "zandronum.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zandronum", "zandronum.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Zandronum", "zandronum.exe"),
            // Check relative to this app
            Path.Combine(AppContext.BaseDirectory, "zandronum.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "zandronum.exe"),
        };
        
        foreach (var path in commonPaths)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch { /* Ignore path resolution errors */ }
        }
        
        return null;
    }
    
    /// <summary>
    /// Attempts to auto-detect common WAD folder locations.
    /// </summary>
    private static List<string> AutoDetectWadSearchPaths()
    {
        var foundPaths = new List<string>();
        var documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        
        var commonPaths = new[]
        {
            // Common WAD folder locations
            Path.Combine(documentsFolder, "WADs"),
            Path.Combine(documentsFolder, "Doom", "WADs"),
            Path.Combine(documentsFolder, "Zandronum", "WADs"),
            Path.Combine(documentsFolder, "Doom"),
            @"C:\Zandronum",
            @"C:\Games\Zandronum",
            @"C:\Games\Doom",
            @"C:\Doom",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zandronum"),
            // Check relative to this app
            Path.Combine(AppContext.BaseDirectory, "WADs"),
        };
        
        foreach (var path in commonPaths)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (Directory.Exists(fullPath) && !foundPaths.Contains(fullPath))
                {
                    // Check if folder contains any WAD files
                    var hasWads = Directory.EnumerateFiles(fullPath, "*.*", SearchOption.TopDirectoryOnly)
                        .Any(f => ZScape.Utilities.WadExtensions.IsSupportedExtension(Path.GetExtension(f)));
                    if (hasWads)
                    {
                        foundPaths.Add(fullPath);
                    }
                }
            }
            catch { /* Ignore path resolution errors */ }
        }
        
        return foundPaths;
    }

    private void SaveSettings()
    {
        // Don't save during initialization - would overwrite with defaults
        if (_isInitializing) return;

        var settings = _settings.Settings;
        
        // Window state
        if (WindowState == FormWindowState.Normal)
        {
            settings.WindowX = Location.X;
            settings.WindowY = Location.Y;
            settings.WindowWidth = Size.Width;
            settings.WindowHeight = Size.Height;
        }
        settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        
        // Column widths (only for fixed-width columns - Fill columns auto-adjust)
        settings.ColumnWidths.Clear();
        foreach (DataGridViewColumn col in serverListView.Columns)
        {
            if (col.AutoSizeMode == DataGridViewAutoSizeColumnMode.None)
            {
                settings.ColumnWidths[col.Name] = col.Width;
            }
        }
        
        // Sorting
        settings.SortColumnIndex = _sortColumnIndex;
        settings.SortAscending = _sortAscending;
        
        // Filters
        settings.HideEmpty = hideEmptyCheckBox.Checked;
        settings.TreatBotOnlyAsEmpty = hideBotOnlyCheckBox.Checked;
        settings.HideFull = hideFullCheckBox.Checked;
        settings.HidePassworded = hidePasswordedCheckBox.Checked;
        settings.GameModeFilterIndex = gameModeComboBox.SelectedIndex;
        settings.SearchText = searchBox.Text;
        
        // View settings
        settings.VerboseMode = verboseMenuItem.Checked;
        settings.ShowHexDumps = hexDumpMenuItem.Checked;
        settings.ShowLogPanel = showLogPanelMenuItem.Checked;
        
        // Settings menu
        settings.RefreshOnLaunch = refreshOnLaunchMenuItem.Checked;
        settings.AutoRefresh = autoRefreshMenuItem.Checked;
        settings.AutoRefreshFavoritesOnly = autoRefreshFavoritesOnlyMenuItem.Checked;
        
        // Splitter positions
        settings.MainSplitterDistance = mainSplitContainer.SplitterDistance;
        // DetailsSplitterDistance no longer used (TableLayoutPanel doesn't have splitters)

        // Advanced filter - sync toolbar state first
        _currentFilter.ShowEmpty = !hideEmptyCheckBox.Checked;
        _currentFilter.TreatBotOnlyAsEmpty = hideBotOnlyCheckBox.Checked;
        _currentFilter.ShowFull = !hideFullCheckBox.Checked;
        _currentFilter.PasswordedServers = hidePasswordedCheckBox.Checked ? FilterMode.Hide : FilterMode.DontCare;
        settings.CurrentFilter = _currentFilter.Clone();
        settings.FilterPresets = _filterPresets.Select(p => p.Clone()).ToList();
        
        // Favorites settings
        settings.ShowFavoritesOnly = showFavoritesOnlyCheckBox.Checked;
        if (serverListView.Columns.Contains("Favorite"))
        {
            settings.ShowFavoritesColumn = serverListView.Columns["Favorite"]!.Visible;
        }

        // WAD settings
        settings.WadSearchPaths = _wadManager.SearchPaths.ToList();
        settings.WadDownloadPath = _wadManager.DownloadPath;
        
        _settings.Save();
    }

    #region Event Handlers - Menu

    private void RefreshMenuItem_Click(object? sender, EventArgs e)
    {
        _ = RefreshServersAsync();
    }

    private void StopMenuItem_Click(object? sender, EventArgs e)
    {
        _browserService.CancelRefresh();
    }

    private void ExitMenuItem_Click(object? sender, EventArgs e)
    {
        Close();
    }

    private void FetchWadsMenuItem_Click(object? sender, EventArgs e)
    {
        using var dialog = new FetchWadsDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.WadNames.Count == 0)
            return;
        
        var settings = _settings.Settings;
        var downloadPath = settings.WadDownloadPath;
        
        if (string.IsNullOrEmpty(downloadPath))
        {
            downloadPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZScape", "Downloads");
            Directory.CreateDirectory(downloadPath);
        }
        
        // Create WadInfo objects for each WAD name
        var wadsToDownload = dialog.WadNames.Select(name => new WadInfo(name)).ToList();
        
        // Create downloader with configured sources
        using var downloader = new WadDownloader(
            dialog.UseWadHostingSites ? settings.DownloadSites : new List<string>());
        downloader.IdgamesEnabled = dialog.UseIdgames;
        downloader.WebSearchEnabled = dialog.UseWebSearch;
        
        // Show download dialog
        using var downloadDialog = new WadDownloadDialog(wadsToDownload, downloadPath, downloader);
        downloadDialog.ShowDialog(this);
        
        _logger.Info($"Fetch WADs completed");
    }

    private void VerboseMenuItem_Click(object? sender, EventArgs e)
    {
        _logger.VerboseMode = verboseMenuItem.Checked;
        _logger.Info($"Verbose mode {(_logger.VerboseMode ? "enabled" : "disabled")}");
        SaveSettings();
    }

    private void HexDumpMenuItem_Click(object? sender, EventArgs e)
    {
        _logger.ShowHexDumps = hexDumpMenuItem.Checked;
        _logger.Info($"Hex dump display {(_logger.ShowHexDumps ? "enabled" : "disabled")}");
        SaveSettings();
    }

    private void ShowLogPanelMenuItem_Click(object? sender, EventArgs e)
    {
        logPanel.Visible = showLogPanelMenuItem.Checked;
        SaveSettings();
    }

    private void AutoRefreshMenuItem_Click(object? sender, EventArgs e)
    {
        autoRefreshTimer.Enabled = autoRefreshMenuItem.Checked;
        // Disable favorites-only option when auto refresh is on (it includes favorites anyway)
        autoRefreshFavoritesOnlyMenuItem.Enabled = !autoRefreshMenuItem.Checked;
        _logger.Info($"Auto refresh {(autoRefreshTimer.Enabled ? "enabled (5 min interval)" : "disabled")}");
        SaveSettings();
    }

    private void AutoRefreshFavoritesOnlyMenuItem_Click(object? sender, EventArgs e)
    {
        SaveSettings();
    }

    private void RefreshOnLaunchMenuItem_Click(object? sender, EventArgs e)
    {
        SaveSettings();
    }

    private void WadBrowserMenuItem_Click(object? sender, EventArgs e)
    {
        using var dialog = new WadBrowserDialog();
        dialog.ShowDialog(this);
        
        // Refresh display if server is selected (WAD status may have changed)
        if (_selectedServer != null)
        {
            DisplayServerDetails(_selectedServer);
        }
    }
    
    private void TestingVersionsMenuItem_Click(object? sender, EventArgs e)
    {
        using var dialog = new TestingVersionManagerDialog();
        dialog.ShowDialog(this);
    }

    private void SettingsMenuItem_Click(object? sender, EventArgs e)
    {
        OpenSettingsDialog();
    }
    
    private void OpenSettingsDialog(string? initialCategory = null)
    {
        using var dialog = new UnifiedSettingsDialog(
            _wadManager.SearchPaths,
            _wadManager.DownloadPath,
            _settings.Settings.DownloadSites.Count > 0 
                ? _settings.Settings.DownloadSites 
                : WadDownloader.DefaultSites,
            initialCategory);
        
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var settings = _settings.Settings;
            
            // Update WAD manager with new settings
            _wadManager.SetSearchPaths(dialog.SearchPaths);
            _wadManager.DownloadPath = dialog.WadDownloadPath;
            _settings.Settings.DownloadSites = dialog.DownloadSites;
            
            // Update executable folders for WAD discovery
            var exePaths = new List<string>();
            if (!string.IsNullOrEmpty(settings.ZandronumPath))
                exePaths.Add(settings.ZandronumPath);
            if (!string.IsNullOrEmpty(settings.ZandronumTestingPath) && Directory.Exists(settings.ZandronumTestingPath))
            {
                try
                {
                    foreach (var exeFile in Directory.EnumerateFiles(settings.ZandronumTestingPath, "*.exe", SearchOption.AllDirectories))
                    {
                        if (Path.GetFileName(exeFile).Contains("zandronum", StringComparison.OrdinalIgnoreCase))
                        {
                            exePaths.Add(exeFile);
                        }
                    }
                }
                catch { /* Ignore access errors */ }
            }
            _wadManager.SetExecutableFolders(exePaths);
            
            _wadManager.RefreshCache();
            SaveSettings();
            _logger.Info("Settings updated.");
            
            // Update favorites column visibility
            if (serverListView.Columns.Contains("Favorite"))
            {
                serverListView.Columns["Favorite"]!.Visible = settings.ShowFavoritesColumn;
            }
            
            // Restart screenshot monitoring with new settings
            _screenshotMonitor.StopMonitoring();
            _screenshotMonitor.StartMonitoring();
        }
    }
    
    private async Task ReconnectFromHistoryAsync(ConnectionHistoryEntry entry)
    {
        // Try to find the server in current list
        var address = entry.FullAddress;
        var server = _browserService.Servers.FirstOrDefault(
            s => s.Address == entry.Address && s.Port == entry.Port);
        
        if (server != null)
        {
            // Server is in the list - use normal launch flow
            await LaunchServerAsync(server);
        }
        else
        {
            // Server not in list - try to query it first
            _logger.Info($"Querying server {address} for reconnect...");
            try
            {
                var endpoint = new System.Net.IPEndPoint(
                    System.Net.IPAddress.Parse(entry.Address), entry.Port);
                await _browserService.RefreshServerAsync(endpoint);
                
                // Check if we got the server
                server = _browserService.Servers.FirstOrDefault(
                    s => s.Address == entry.Address && s.Port == entry.Port);
                
                if (server != null && server.IsOnline)
                {
                    await LaunchServerAsync(server);
                }
                else
                {
                    MessageBox.Show(this, 
                        $"Server {entry.ServerName} ({address}) is offline or unreachable.",
                        "Server Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to query server for reconnect: {ex.Message}");
                MessageBox.Show(this, 
                    $"Failed to connect to {entry.ServerName} ({address}):\n{ex.Message}",
                    "Connection Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void DownloadWadsMenuItem_Click(object? sender, EventArgs e)
    {
        if (_selectedServer == null) return;
        
        var missing = _wadManager.GetMissingWadsForServer(_selectedServer);
        if (missing.Count == 0)
        {
            MessageBox.Show(this, "All required WADs are available.", "WADs Found", 
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        
        var sites = _settings.Settings.DownloadSites.Count > 0 
            ? _settings.Settings.DownloadSites 
            : WadDownloader.DefaultSites;
        
        using var downloader = new WadDownloader(sites);
        using var dialog = new WadDownloadDialog(missing, _wadManager.DownloadPath, downloader);
        dialog.ShowDialog(this);
        
        // Refresh cache after downloads
        _wadManager.RefreshCache();
    }

    private void AboutMenuItem_Click(object? sender, EventArgs e)
    {
        using var aboutBox = new Form
        {
            Text = "About ZScape",
            Size = new Size(AppConstants.DialogSizes.AboutDialogWidth, AppConstants.DialogSizes.AboutDialogHeight),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = DarkTheme.PrimaryBackground,
            ForeColor = DarkTheme.TextPrimary
        };

        var label = new Label
        {
            Text = "ZScape\n\n" +
                   "Version 1.0.0\n\n" +
                   "A modern server browser for Zandronum.\n\n" +
                   "Protocol implementation based on Doomseeker.\n" +
                   "Built with .NET 10 and WinForms.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = DarkTheme.TextPrimary
        };

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Dock = DockStyle.Bottom,
            Height = 35
        };
        DarkTheme.ApplyToButton(okButton);

        aboutBox.Controls.Add(label);
        aboutBox.Controls.Add(okButton);
        aboutBox.AcceptButton = okButton;
        aboutBox.ShowDialog(this);
    }

    #endregion

    #region Event Handlers - Toolbar

    private void SearchBox_TextChanged(object? sender, EventArgs e)
    {
        ApplyFilters();
    }

    private void FilterChanged(object? sender, EventArgs e)
    {
        // Update button appearance based on state
        UpdateFilterButtonStates();
        ApplyFilters();
        SaveSettings();
    }

    private void UpdateFilterButtonStates()
    {
        hideEmptyCheckBox.Checked = hideEmptyCheckBox.Checked;
        hideFullCheckBox.Checked = hideFullCheckBox.Checked;
        hidePasswordedCheckBox.Checked = hidePasswordedCheckBox.Checked;
    }

    #endregion

    #region Event Handlers - Server List

    private void ServerListView_SelectionChanged(object? sender, EventArgs e)
    {
        // Ignore selection changes during grid population (prevents auto-selection during refresh)
        if (_suppressSelectionEvents) return;
        
        int selectedIndex = -1;

        if (serverListView.SelectedRows.Count > 0)
        {
            selectedIndex = serverListView.SelectedRows[0].Index;
        }
        else if (serverListView.CurrentRow != null)
        {
            selectedIndex = serverListView.CurrentRow.Index;
        }

        // Virtual mode: get server from filtered list by index
        if (selectedIndex >= 0 && selectedIndex < _filteredServers.Count)
        {
            var server = _filteredServers[selectedIndex];
            
            // Only update if selection changed to avoid flickering
            string newAddress = server.Address + ":" + server.Port;
            if (_selectedServerAddress != newAddress)
            {
                _selectedServer = server;
                _selectedServerAddress = newAddress;
                DisplayServerDetails(server);
                DisplayWadList(server);
                DisplayPlayerList(server);
            }
        }
        else
        {
            // No selection - clear panels
            _selectedServer = null;
            _selectedServerAddress = null;
            serverInfoTextBox.Clear();
            wadsListView.Items.Clear();
            wadsLabel.Text = "WADs";
            AdjustWadsListColumn();
            playerListView.Items.Clear();
            playerListLabel.Text = "Players (0)";
            AdjustPlayerListColumns();
        }
    }

    private async void ServerListView_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && e.RowIndex < _filteredServers.Count)
        {
            var server = _filteredServers[e.RowIndex];
            _selectedServer = server;
            await LaunchServerAsync(server);
        }
    }

    private void ServerListView_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex == _sortColumnIndex)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumnIndex = e.ColumnIndex;
            _sortAscending = true;
        }

        SortServers();
        ApplyFilters();
        SaveSettings();
    }

    private void ServerListView_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        // Skip if clicking on header row
        if (e.RowIndex < 0 || e.RowIndex >= _filteredServers.Count) return;
        
        // Right-click: select row before context menu opens
        if (e.Button == MouseButtons.Right)
        {
            serverListView.ClearSelection();
            serverListView.Rows[e.RowIndex].Selected = true;
            if (serverListView.Rows[e.RowIndex].Cells.Count > 0)
                serverListView.CurrentCell = serverListView.Rows[e.RowIndex].Cells[0];
        }
        // Middle-click: refresh individual server
        else if (e.Button == MouseButtons.Middle)
        {
            var server = _filteredServers[e.RowIndex];
            _ = RefreshSingleServerAsync(server);
        }
    }

    #endregion

    #region Event Handlers - Context Menu

    private void CopyConnectMenuItem_Click(object? sender, EventArgs e)
    {
        if (_selectedServer != null)
        {
            var fullCommand = GameLauncher.Instance.GetFullConnectCommand(_selectedServer);
            
            if (!string.IsNullOrEmpty(fullCommand))
            {
                Clipboard.SetText(fullCommand);
                _logger.Info($"Copied full connect command ({fullCommand.Length} chars)");
            }
            else
            {
                // Fallback to simple command if exe not found
                string connectCommand = $"zandronum.exe -connect {_selectedServer.Address}";
                Clipboard.SetText(connectCommand);
                _logger.Info($"Copied (simple): {connectCommand}");
            }
        }
    }

    private void CopyAddressMenuItem_Click(object? sender, EventArgs e)
    {
        if (_selectedServer != null)
        {
            Clipboard.SetText(_selectedServer.Address);
            _logger.Info($"Copied: {_selectedServer.Address}");
        }
    }

    private void RefreshServerMenuItem_Click(object? sender, EventArgs e)
    {
        if (_selectedServer != null)
        {
            _ = RefreshSingleServerAsync(_selectedServer);
        }
    }

    private async void ConnectMenuItem_Click(object? sender, EventArgs e)
    {
        if (_selectedServer != null)
        {
            await LaunchServerAsync(_selectedServer);
        }
    }

    private async Task LaunchServerAsync(ServerInfo server)
    {
        var launcher = GameLauncher.Instance;
        
        // Check if executable is configured
        if (!launcher.IsExecutableConfigured(server))
        {
            var exeType = server.IsTestingServer ? "Testing versions folder" : "Stable executable";
            var result = MessageBox.Show(
                $"Zandronum {exeType} is not configured.\n\nWould you like to configure it now in Settings?",
                "Zandronum Path Not Set",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            
            if (result == DialogResult.Yes)
            {
                OpenSettingsDialog("General");
            }
            return;
        }
        
        // For testing servers, check if the specific version is installed
        if (server.IsTestingServer && !launcher.IsTestingVersionInstalled(server))
        {
            var versionFolder = launcher.GetTestingVersionFolder(server);
            var hasArchive = !string.IsNullOrEmpty(server.TestingArchive);
            
            if (hasArchive)
            {
                var result = MessageBox.Show(
                    $"Testing version '{server.GameVersion}' is not installed.\n\n" +
                    $"Would you like to download it now?\n\n" +
                    $"It will be installed to:\n{versionFolder}",
                    "Testing Version Not Found",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                
                if (result == DialogResult.Yes)
                {
                    // Download with progress - await properly to keep UI responsive
                    if (!await DownloadTestingVersionAsync(server))
                    {
                        return; // Download failed
                    }
                }
                else
                {
                    return; // User cancelled
                }
            }
            else
            {
                MessageBox.Show(
                    $"Testing version '{server.GameVersion}' is not installed and no download URL is available.\n\n" +
                    $"Please manually install to:\n{versionFolder}",
                    "Testing Version Not Found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }
        
        // Check for missing WADs
        var (allFound, missingWads) = launcher.CheckRequiredWads(server);
        if (!allFound)
        {
            var wadList = string.Join("\n", missingWads.Take(10).Select(w => w.Name));
            if (missingWads.Count > 10)
                wadList += $"\n... and {missingWads.Count - 10} more";
            
            var result = MessageBox.Show(
                $"The following WAD files are missing:\n\n{wadList}\n\nWould you like to download them?",
                "Missing WADs",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);
            
            if (result == DialogResult.Yes)
            {
                // Start WAD download - include expected hash for verification
                var wadInfos = missingWads.Select(w => new WadInfo(w.Name, w.Hash)).ToList();
                var sites = _settings.Settings.DownloadSites.Count > 0 
                    ? _settings.Settings.DownloadSites 
                    : WadDownloader.DefaultSites;
                using var downloader = new WadDownloader(sites);
                using var downloadDialog = new WadDownloadDialog(wadInfos, _wadManager.DownloadPath, downloader, autoCloseOnComplete: true);
                downloadDialog.ShowDialog(this);
                
                // Refresh WAD cache after downloads
                _wadManager.RefreshCache();
                
                // Re-check after download
                var (nowFound, stillMissing) = launcher.CheckRequiredWads(server);
                if (!nowFound)
                {
                    var stillMissingList = string.Join(", ", stillMissing.Take(5).Select(w => w.Name));
                    MessageBox.Show(
                        $"Some WADs are still missing: {stillMissingList}\n\nCannot connect to server.",
                        "Still Missing WADs",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }
            }
            else if (result == DialogResult.Cancel)
            {
                return;
            }
            // If No, proceed anyway (user may have WADs in other paths)
        }
        
        // Verify WAD hashes match server expectations with progress dialog
        List<GameLauncher.WadHashMismatch> hashMismatches = [];
        
        if (server.PWADs.Any(p => !string.IsNullOrEmpty(p.Hash)))
        {
            var concurrency = SettingsService.Instance.Settings.HashVerificationConcurrency;
            bool isConcurrent = concurrency != 1;
            int displaySlots = isConcurrent ? Math.Min(concurrency == 0 ? 8 : concurrency, 8) : 1;
            // Sequential: compact dialog with status/file/progress/count
            // Concurrent: panels start at Y=98, each is 22px, plus ~40px padding
            int dialogHeight = isConcurrent ? 140 + (displaySlots * 22) : 180;
            
            var progressForm = new Form
            {
                Text = "Verifying WAD Hashes",
                Size = new Size(500, dialogHeight),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = DarkTheme.PrimaryBackground
            };
            
            DarkModeHelper.ApplyDarkTitleBar(progressForm);
            
            using (progressForm)
            {
            
            // For sequential: "Hashing filename.pk3... (100 / 200 MB)"
            // For concurrent: hidden
            var statusLabel = new Label
            {
                Text = "Initializing...",
                Location = new Point(20, 20),
                Size = new Size(440, 25),
                ForeColor = DarkTheme.TextPrimary,
                Font = new Font("Segoe UI", 10f),
                Visible = !isConcurrent
            };
            
            // For sequential: current file name
            // For concurrent: "Concurrent mode (N files)"
            var fileLabel = new Label
            {
                Text = isConcurrent ? $"Concurrent mode ({(concurrency == 0 ? "unlimited" : concurrency.ToString())} files)" : "",
                Location = new Point(20, isConcurrent ? 20 : 50),
                Size = new Size(440, 20),
                ForeColor = isConcurrent ? DarkTheme.TextPrimary : DarkTheme.TextSecondary,
                Font = new Font("Segoe UI", isConcurrent ? 10f : 9f)
            };
            
            var progressBar = new ProgressBar
            {
                Location = new Point(20, isConcurrent ? 45 : 80),
                Size = new Size(440, 25),
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 100
            };
            
            var countLabel = new Label
            {
                Text = "0 / 0",
                Location = new Point(20, isConcurrent ? 75 : 115),
                Size = new Size(200, 20),
                ForeColor = DarkTheme.TextSecondary,
                Font = new Font("Segoe UI", 9f)
            };
            
            var fileSizeLabel = new Label
            {
                Text = "",
                Location = new Point(240, isConcurrent ? 75 : 115),
                Size = new Size(220, 20),
                ForeColor = DarkTheme.TextSecondary,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.TopRight
            };
            
            progressForm.Controls.AddRange([statusLabel, fileLabel, progressBar, countLabel, fileSizeLabel]);
            
            // Force control handles to be created
            _ = progressForm.Handle;
            foreach (Control c in progressForm.Controls)
            {
                _ = c.Handle;
            }
            
            // Create per-file progress panels for concurrent mode (text left, bar right)
            var fileProgressPanels = new List<(Panel Container, ProgressBar Bar, Label Text)>();
            if (isConcurrent)
            {
                int panelStartY = 98; // Start after countLabel (at Y=75, height ~20)
                for (int i = 0; i < displaySlots; i++)
                {
                    var panel = new Panel
                    {
                        Location = new Point(20, panelStartY + (i * 22)),
                        Size = new Size(440, 20),
                        BackColor = DarkTheme.PrimaryBackground
                    };
                    
                    var label = new Label
                    {
                        Location = new Point(0, 2),
                        Size = new Size(330, 16),
                        ForeColor = DarkTheme.TextPrimary,
                        BackColor = DarkTheme.PrimaryBackground,
                        Font = new Font("Segoe UI", 9f)
                    };
                    
                    var miniBar = new ProgressBar
                    {
                        Location = new Point(335, 0),
                        Size = new Size(105, 18),
                        Minimum = 0,
                        Maximum = 100,
                        Style = ProgressBarStyle.Continuous
                    };
                    
                    panel.Controls.Add(label);
                    panel.Controls.Add(miniBar);
                    
                    fileProgressPanels.Add((panel, miniBar, label));
                    progressForm.Controls.Add(panel);
                    _ = panel.Handle;
                    _ = miniBar.Handle;
                    _ = label.Handle;
                }
            }
            
            var progress = new Progress<GameLauncher.HashVerificationProgress>(p =>
            {
                statusLabel.Text = p.Status;
                
                if (!isConcurrent)
                {
                    fileLabel.Text = p.CurrentFile;
                }
                
                // Show file-level progress when hashing a single file, overall progress otherwise
                if (!isConcurrent && p.FileSize > 0 && p.BytesProcessed > 0 && p.BytesProcessed < p.FileSize)
                {
                    // During file hashing, show file progress
                    progressBar.Value = Math.Min(100, p.FilePercentComplete);
                }
                else
                {
                    // Overall progress
                    progressBar.Value = Math.Min(100, p.OverallPercentComplete);
                }
                
                countLabel.Text = $"{p.CurrentIndex} / {p.TotalFiles}";
                
                // Show per-file progress if concurrent mode
                if (isConcurrent && p.FileProgress != null && p.FileProgress.Count > 0)
                {
                    var inProgress = p.FileProgress
                        .Where(kv => kv.Value.BytesProcessed > 0 && kv.Value.BytesProcessed < kv.Value.TotalBytes)
                        .Select(kv => new { 
                            Key = kv.Key, 
                            Value = kv.Value, 
                            Percent = kv.Value.TotalBytes > 0 ? (int)(kv.Value.BytesProcessed * 100 / kv.Value.TotalBytes) : 0 
                        })
                        .OrderByDescending(x => x.Percent)
                        .Take(displaySlots)
                        .ToList();
                    
                    // Update per-file panels with mini progress bars
                    for (int i = 0; i < fileProgressPanels.Count; i++)
                    {
                        var (panel, miniBar, label) = fileProgressPanels[i];
                        if (i < inProgress.Count)
                        {
                            var item = inProgress[i];
                            var fileName = Path.GetFileName(item.Key);
                            var processed = item.Value.BytesProcessed / 1024.0 / 1024.0;
                            var total = item.Value.TotalBytes / 1024.0 / 1024.0;
                            miniBar.Value = Math.Min(100, item.Percent);
                            label.Text = $"{fileName}: {item.Percent}% ({processed:F1} / {total:F1} MB)";
                            panel.Visible = true;
                        }
                        else
                        {
                            panel.Visible = false;
                        }
                    }
                    
                    fileSizeLabel.Text = $"{inProgress.Count} file(s) hashing";
                }
                else if (p.FileSize > 0)
                {
                    fileSizeLabel.Text = $"{p.BytesProcessed / 1024.0 / 1024.0:F2} / {p.FileSize / 1024.0 / 1024.0:F2} MB";
                }
                else
                {
                    fileSizeLabel.Text = "";
                }
            });
            
            // Enable double buffering via reflection (protected property)
            typeof(Form).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(progressForm, true);
            
            var cts = new CancellationTokenSource();
            
            progressForm.Shown += async (s, e) =>
            {
                // Force complete repaint of all controls
                foreach (Control c in progressForm.Controls)
                {
                    c.Refresh();
                }
                progressForm.Refresh();
                Application.DoEvents();
                await Task.Delay(100); // Give Windows time to paint
                Application.DoEvents();
                
                try
                {
                    hashMismatches = await launcher.VerifyWadHashesAsync(server, progress, cts.Token);
                }
                finally
                {
                    if (!progressForm.IsDisposed)
                        progressForm.Close();
                }
            };
            
            progressForm.ShowDialog(this);
            }
        }
        
        if (hashMismatches.Count > 0)
        {
            var mismatchList = string.Join("\n", hashMismatches.Take(5).Select(m => 
                $"  {m.WadName}" + (m.MatchingVersionPath != null ? " (version available)" : " (needs download)")));
            if (hashMismatches.Count > 5)
                mismatchList += $"\n  ... and {hashMismatches.Count - 5} more";
            
            var canResolve = hashMismatches.Any(m => m.MatchingVersionPath != null);
            var needsDownload = hashMismatches.Any(m => m.MatchingVersionPath == null);
            
            var message = $"The following WADs have different versions than the server expects:\n\n{mismatchList}\n\n";
            if (canResolve && needsDownload)
                message += "Some versions can be swapped locally, others need downloading.\nProceed?";
            else if (canResolve)
                message += "The correct versions are available locally and will be swapped.\nProceed?";
            else
                message += "The correct versions need to be downloaded.\nProceed?";
            
            var result = MessageBox.Show(
                message,
                "WAD Version Mismatch",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);
            
            if (result == DialogResult.Cancel)
                return;
            
            if (result == DialogResult.Yes)
            {
                // Resolve mismatches (swap local versions, archive mismatched files)
                var wadsToDownload = launcher.ResolveHashMismatches(hashMismatches);
                
                if (wadsToDownload.Count > 0)
                {
                    // Download the WADs that couldn't be resolved locally
                    var wadInfos = wadsToDownload.Select(w => new WadInfo(w)).ToList();
                    var sites = _settings.Settings.DownloadSites.Count > 0 
                        ? _settings.Settings.DownloadSites 
                        : WadDownloader.DefaultSites;
                    using var downloader = new WadDownloader(sites);
                    using var downloadDialog = new WadDownloadDialog(wadInfos, _wadManager.DownloadPath, downloader, autoCloseOnComplete: true);
                    downloadDialog.ShowDialog(this);
                    
                    // Refresh WAD cache after downloads
                    _wadManager.RefreshCache();
                }
            }
            // If No, proceed anyway with current WADs
        }
        
        // Prompt for passwords if needed
        string? connectPassword = null;
        string? joinPassword = null;
        
        if (server.IsPassworded)
        {
            connectPassword = PromptForPassword("Connect Password", "This server requires a password to connect:");
            if (connectPassword == null) return; // User cancelled
        }
        
        if (server.RequiresJoinPassword)
        {
            joinPassword = PromptForPassword("Join Password", "This server requires a password to join the game:");
            if (joinPassword == null) return; // User cancelled
        }
        
        // Launch the game
        if (!launcher.LaunchGame(server, connectPassword, joinPassword))
        {
            // Error event will have been raised
        }
    }

    private async Task<bool> DownloadTestingVersionAsync(ServerInfo server)
    {
        var launcher = GameLauncher.Instance;
        
        // Create a simple progress dialog
        using var progressForm = new Form
        {
            Text = $"Downloading {server.GameVersion}",
            Width = 400,
            Height = 150,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = DarkTheme.PrimaryBackground,
            ControlBox = false
        };
        
        DarkModeHelper.ApplyDarkTitleBar(progressForm);

        var label = new Label
        {
            Text = "Starting download...",
            Left = 15,
            Top = 20,
            Width = 360,
            ForeColor = DarkTheme.TextPrimary,
            AutoSize = false
        };

        var progressBar = new ProgressBar
        {
            Left = 15,
            Top = 50,
            Width = 355,
            Height = 25,
            Style = ProgressBarStyle.Continuous
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            Left = 145,
            Top = 85,
            Width = 100,
            Height = 28,
            BackColor = DarkTheme.SecondaryBackground,
            ForeColor = DarkTheme.TextPrimary,
            FlatStyle = FlatStyle.Flat
        };

        var cancelled = false;
        cancelButton.Click += (s, e) => { cancelled = true; progressForm.Close(); };

        progressForm.Controls.Add(label);
        progressForm.Controls.Add(progressBar);
        progressForm.Controls.Add(cancelButton);

        // Subscribe to progress updates
        void OnProgress(object? sender, (string Message, int Progress) e)
        {
            if (progressForm.InvokeRequired)
            {
                progressForm.BeginInvoke(() => OnProgress(sender, e));
                return;
            }
            label.Text = e.Message;
            progressBar.Value = Math.Min(100, Math.Max(0, e.Progress));
        }

        launcher.DownloadProgress += OnProgress;

        var downloadTask = launcher.DownloadTestingBuildAsync(server);
        
        // Show dialog non-modally and pump messages while downloading
        progressForm.Show(this);
        
        while (!downloadTask.IsCompleted && !cancelled)
        {
            Application.DoEvents();
            await Task.Delay(50);
        }

        launcher.DownloadProgress -= OnProgress;
        progressForm.Close();

        if (cancelled)
        {
            return false;
        }

        var result = await downloadTask;
        
        if (!result)
        {
            MessageBox.Show(
                "Failed to download testing version. Check the log for details.",
                "Download Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        return result;
    }

    private string? PromptForPassword(string title, string message)
    {
        using var dialog = new Form
        {
            Text = title,
            Width = 350,
            Height = 150,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = DarkTheme.PrimaryBackground
        };

        var label = new Label
        {
            Text = message,
            Left = 15,
            Top = 15,
            Width = 300,
            ForeColor = DarkTheme.TextPrimary
        };
        
        var textBox = new TextBox
        {
            Left = 15,
            Top = 45,
            Width = 300,
            UseSystemPasswordChar = true,
            BackColor = DarkTheme.SecondaryBackground,
            ForeColor = DarkTheme.TextPrimary
        };
        
        var okButton = new Button
        {
            Text = "OK",
            Left = 160,
            Top = 75,
            Width = 75,
            DialogResult = DialogResult.OK,
            BackColor = DarkTheme.AccentColor,
            ForeColor = DarkTheme.TextPrimary,
            FlatStyle = FlatStyle.Flat
        };
        
        var cancelButton = new Button
        {
            Text = "Cancel",
            Left = 240,
            Top = 75,
            Width = 75,
            DialogResult = DialogResult.Cancel,
            BackColor = DarkTheme.SecondaryBackground,
            ForeColor = DarkTheme.TextPrimary,
            FlatStyle = FlatStyle.Flat
        };

        dialog.Controls.AddRange([label, textBox, okButton, cancelButton]);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        return dialog.ShowDialog(this) == DialogResult.OK ? textBox.Text : null;
    }

    #endregion

    #region Event Handlers - Browser Service

    private void BrowserService_RefreshStarted(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            Invoke(() => BrowserService_RefreshStarted(sender, e));
            return;
        }

        // Clear existing servers before refresh
        _allServers.Clear();
        _filteredServers.Clear();
        serverListView.RowCount = 0;
        UpdateStatusBar();

        refreshButton.Enabled = false;
        refreshMenuItem.Enabled = false;
        stopButton.Enabled = true;
        stopMenuItem.Enabled = true;
        progressBar.Visible = true;
        progressBar.Value = 0;
        statusLabel.Text = "Refreshing...";
    }

    private void BrowserService_RefreshProgress(object? sender, int progress)
    {
        if (InvokeRequired)
        {
            Invoke(() => BrowserService_RefreshProgress(sender, progress));
            return;
        }

        progressBar.Value = Math.Min(progress, 100);
        statusLabel.Text = $"Querying servers... {progress}%";
    }

    private void BrowserService_RefreshCompleted(object? sender, RefreshCompletedEventArgs e)
    {
        if (InvokeRequired)
        {
            Invoke(() => BrowserService_RefreshCompleted(sender, e));
            return;
        }

        refreshButton.Enabled = true;
        refreshMenuItem.Enabled = true;
        stopButton.Enabled = false;
        stopMenuItem.Enabled = false;
        progressBar.Visible = false;

        if (e.Success)
        {
            statusLabel.Text = "Ready";
            UpdateServerList();
            RefreshSelectedServerDetails(); // Update details panel once after refresh completes
        }
        else
        {
            statusLabel.Text = $"Error: {e.Error}";
        }
    }

    private void BrowserService_ServerUpdated(object? sender, ServerInfo server)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => BrowserService_ServerUpdated(sender, server));
            return;
        }

        // Throttle UI updates to prevent flickering
        var now = DateTime.UtcNow;
        if (now - _lastUiUpdate < _uiUpdateThrottle)
            return;
        _lastUiUpdate = now;

        // Update the server list live as servers are queried
        _allServers = _browserService.Servers.ToList();
        SortServers();
        ApplyFilters();
    }

    private void Logger_LogAdded(object? sender, LogEntry entry)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Logger_LogAdded(sender, entry));
            return;
        }

        AppendLogEntry(entry);
    }

    private void AutoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!_browserService.IsRefreshing)
        {
            if (_settings.Settings.AutoRefreshFavoritesOnly)
            {
                _ = RefreshFavoriteServersAsync();
            }
            else
            {
                _ = RefreshServersAsync();
            }
        }
    }

    #endregion

    #region Server Operations

    private async Task RefreshServersAsync()
    {
        try
        {
            await _browserService.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Refresh failed: {ex.Message}");
        }
    }
    
    private async Task RefreshFavoriteServersAsync()
    {
        try
        {
            await _browserService.RefreshFavoritesAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Favorites refresh failed: {ex.Message}");
        }
    }

    private async Task RefreshSingleServerAsync(ServerInfo server)
    {
        try
        {
            _logger.Info($"Refreshing {server.Address}...");
            await _browserService.RefreshServerAsync(server);
            UpdateServerList();
            RefreshSelectedServerDetails(); // Update details panel, player list, and WAD list
            _logger.Success($"Server {server.Name} refreshed");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to refresh server: {ex.Message}");
        }
    }

    private void UpdateServerList()
    {
        _allServers = _browserService.Servers.ToList();
        SortServers();
        ApplyFilters();
    }

    private void SortServers()
    {
        if (_sortColumnIndex < 0) return;

        // Column indices: 0=Favorite, 1=Icon, 2=Name, 3=Players, 4=Ping, 5=Map, 6=Mode, 7=IWAD, 8=Address
        _allServers = _sortColumnIndex switch
        {
            2 => _sortAscending 
                ? _allServers.OrderBy(s => s.Name).ToList()
                : _allServers.OrderByDescending(s => s.Name).ToList(),
            // Sort by players: prioritize human players, then total players as tiebreaker
            3 => _sortAscending 
                ? _allServers.OrderByDescending(s => s.HumanPlayerCount)
                             .ThenByDescending(s => s.CurrentPlayers).ToList()
                : _allServers.OrderBy(s => s.HumanPlayerCount)
                             .ThenBy(s => s.CurrentPlayers).ToList(),
            4 => _sortAscending 
                ? _allServers.OrderBy(s => s.Ping).ToList()
                : _allServers.OrderByDescending(s => s.Ping).ToList(),
            5 => _sortAscending 
                ? _allServers.OrderBy(s => s.Map).ToList()
                : _allServers.OrderByDescending(s => s.Map).ToList(),
            6 => _sortAscending 
                ? _allServers.OrderBy(s => s.GameMode.Name).ToList()
                : _allServers.OrderByDescending(s => s.GameMode.Name).ToList(),
            7 => _sortAscending 
                ? _allServers.OrderBy(s => s.IWAD).ToList()
                : _allServers.OrderByDescending(s => s.IWAD).ToList(),
            8 => _sortAscending 
                ? _allServers.OrderBy(s => s.Address).ToList()
                : _allServers.OrderByDescending(s => s.Address).ToList(),
            _ => _allServers
        };
    }

    private List<ServerInfo> SortServers(List<ServerInfo> servers)
    {
        if (_sortColumnIndex < 0) return servers;

        // Column indices: 0=Favorite, 1=Icon, 2=Name, 3=Players, 4=Ping, 5=Map, 6=Mode, 7=IWAD, 8=Address
        return _sortColumnIndex switch
        {
            2 => _sortAscending 
                ? servers.OrderBy(s => s.Name).ToList()
                : servers.OrderByDescending(s => s.Name).ToList(),
            // Sort by players: prioritize human players, then total players as tiebreaker
            3 => _sortAscending 
                ? servers.OrderBy(s => s.HumanPlayerCount)
                         .ThenBy(s => s.CurrentPlayers).ToList()
                : servers.OrderByDescending(s => s.HumanPlayerCount)
                         .ThenByDescending(s => s.CurrentPlayers).ToList(),
            4 => _sortAscending 
                ? servers.OrderBy(s => s.Ping).ToList()
                : servers.OrderByDescending(s => s.Ping).ToList(),
            5 => _sortAscending 
                ? servers.OrderBy(s => s.Map).ToList()
                : servers.OrderByDescending(s => s.Map).ToList(),
            6 => _sortAscending 
                ? servers.OrderBy(s => s.GameMode.Name).ToList()
                : servers.OrderByDescending(s => s.GameMode.Name).ToList(),
            7 => _sortAscending 
                ? servers.OrderBy(s => s.IWAD).ToList()
                : servers.OrderByDescending(s => s.IWAD).ToList(),
            8 => _sortAscending 
                ? servers.OrderBy(s => s.Address).ToList()
                : servers.OrderByDescending(s => s.Address).ToList(),
            _ => servers
        };
    }

    private void AdvancedFilterButton_Click(object? sender, EventArgs e)
    {
        // Sync toolbar state to filter before opening dialog
        _currentFilter.ShowEmpty = !hideEmptyCheckBox.Checked;
        _currentFilter.TreatBotOnlyAsEmpty = hideBotOnlyCheckBox.Checked;
        _currentFilter.ShowFull = !hideFullCheckBox.Checked;
        _currentFilter.PasswordedServers = hidePasswordedCheckBox.Checked ? FilterMode.Hide : FilterMode.DontCare;

        using var dialog = new ServerFilterDialog(_currentFilter, _filterPresets);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _currentFilter = dialog.Filter;
            _filterPresets = dialog.Presets;

            _logger.Info($"Advanced filter applied. Include countries: {string.Join(",", _currentFilter.IncludeCountries)}, Exclude countries: {string.Join(",", _currentFilter.ExcludeCountries)}");

            // Sync toolbar with new filter settings
            hideEmptyCheckBox.Checked = !_currentFilter.ShowEmpty;
            hideBotOnlyCheckBox.Checked = _currentFilter.TreatBotOnlyAsEmpty;
            hideFullCheckBox.Checked = !_currentFilter.ShowFull;
            hidePasswordedCheckBox.Checked = _currentFilter.PasswordedServers == FilterMode.Hide;

            ApplyFilters();
            SaveSettings();
        }
    }

    private void HistoryButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new ConnectionHistoryDialog();
        dialog.ReconnectRequested += async (s, entry) =>
        {
            dialog.DialogResult = DialogResult.Cancel;
            dialog.Close();
            await ReconnectFromHistoryAsync(entry);
        };
        dialog.ShowDialog(this);
    }

    private void ApplyFilters()
    {
        // Sync quick toolbar filters to the current filter
        _currentFilter.ShowEmpty = !hideEmptyCheckBox.Checked;
        _currentFilter.TreatBotOnlyAsEmpty = hideBotOnlyCheckBox.Checked;
        _currentFilter.ShowFull = !hideFullCheckBox.Checked;
        _currentFilter.PasswordedServers = hidePasswordedCheckBox.Checked ? FilterMode.Hide : FilterMode.DontCare;
        if (!string.IsNullOrWhiteSpace(searchBox.Text))
        {
            _currentFilter.ServerNameFilter = searchBox.Text;
        }
        else
        {
            _currentFilter.ServerNameFilter = string.Empty;
        }

        // Handle game mode combo box as a quick filter
        GameModeType? modeFilter = null;
        if (gameModeComboBox.SelectedIndex > 0)
        {
            var modes = Enum.GetValues<GameModeType>().Where(m => m != GameModeType.Unknown).ToArray();
            if (gameModeComboBox.SelectedIndex - 1 < modes.Length)
            {
                modeFilter = modes[gameModeComboBox.SelectedIndex - 1];
            }
        }
        
        // Check favorites filter
        bool showFavoritesOnly = showFavoritesOnlyCheckBox.Checked;
        var favorites = _settings.Settings.FavoriteServers;

        // Apply advanced filter
        var filtered = _allServers.Where(s =>
        {
            // Skip servers pending refresh - data may be stale
            if (s.IsRefreshPending)
                return false;
            
            // Favorites filter
            if (showFavoritesOnly)
            {
                var serverKey = $"{s.Address}:{s.Port}";
                if (!favorites.Contains(serverKey))
                    return false;
            }
            
            // First check the advanced filter
            if (!_currentFilter.Matches(s))
                return false;

            // Then apply game mode combo box filter (on top of advanced filter)
            if (modeFilter.HasValue && s.GameMode.Type != modeFilter.Value)
                return false;

            return true;
        }).ToList();

        _logger.Verbose($"Filter applied: {_allServers.Count} total, {filtered.Count} after filter");

        // Sort filtered list
        filtered = SortServers(filtered);

        PopulateServerGrid(filtered);
        UpdateStatusBar();
    }

    private void PopulateServerGrid(List<ServerInfo> servers)
    {
        // Preserve selection
        string? previousSelection = _selectedServerAddress;
        
        // Suppress selection events during grid update
        _suppressSelectionEvents = true;
        
        try
        {
            // Store the filtered data for virtual mode
            _filteredServers = servers;
            
            // Update row count (virtual mode doesn't use Rows.Add)
            // RowCount must be at least 1 in virtual mode, so set to 0 only after disabling virtual mode temporarily
            if (servers.Count == 0)
            {
                serverListView.Rows.Clear();
            }
            else
            {
                serverListView.RowCount = servers.Count;
            }
            
            // Invalidate to redraw with new data
            serverListView.Invalidate();
            
            // Restore or clear selection
            if (!string.IsNullOrEmpty(previousSelection))
            {
                RestoreSelection(previousSelection);
                // Note: Don't refresh details here - it causes flash during live updates
                // Details will be refreshed when RefreshCompleted fires or when user clicks
            }
            else
            {
                // No previous selection - ensure nothing is selected
                serverListView.ClearSelection();
                serverListView.CurrentCell = null;
            }
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }
    
    private void RefreshSelectedServerDetails()
    {
        if (_selectedServerAddress == null) return;
        
        // Find the currently selected server in the updated list
        var server = _filteredServers.FirstOrDefault(s => s.Address + ":" + s.Port == _selectedServerAddress);
        if (server != null)
        {
            _selectedServer = server;
            DisplayServerDetails(server);
            DisplayWadList(server);
            DisplayPlayerList(server);
        }
    }
    
    private void RestoreSelection(string serverAddress)
    {
        for (int i = 0; i < _filteredServers.Count; i++)
        {
            var server = _filteredServers[i];
            if (server.Address + ":" + server.Port == serverAddress)
            {
                serverListView.ClearSelection();
                if (i < serverListView.RowCount)
                {
                    serverListView.Rows[i].Selected = true;
                    serverListView.CurrentCell = serverListView.Rows[i].Cells[1]; // Name column
                }
                return;
            }
        }
    }
    
    private void ServerListView_CellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _filteredServers.Count)
            return;
        
        var server = _filteredServers[e.RowIndex];
        var columnName = serverListView.Columns[e.ColumnIndex].Name;
        var serverKey = $"{server.Address}:{server.Port}";
        
        e.Value = columnName switch
        {
            "Favorite" => IsFavorite(serverKey) ? _starIcon ??= CreateStarIcon(true) : _emptyStarIcon ??= CreateStarIcon(false),
            "Icon" => server.IsPassworded ? _lockIcon ??= CreateLockIcon() : null,
            "Name" => server.Name,
            "Players" => server.BotCount > 0
                ? $"{server.HumanPlayerCount}+{server.BotCount}/{server.MaxPlayers}"
                : $"{server.CurrentPlayers}/{server.MaxPlayers}",
            "Ping" => server.Ping,
            "Map" => server.Map,
            "GameMode" => server.GameMode.ShortName,
            "IWAD" => server.IWAD,
            "Address" => server.Address,
            _ => null
        };
    }
    
    private bool IsFavorite(string serverAddress)
    {
        return _settings.Settings.FavoriteServers.Contains(serverAddress);
    }
    
    private void ToggleFavorite(ServerInfo server)
    {
        var serverKey = $"{server.Address}:{server.Port}";
        var favorites = _settings.Settings.FavoriteServers;
        
        if (favorites.Contains(serverKey))
        {
            favorites.Remove(serverKey);
            _logger.Info($"Removed from favorites: {server.Name}");
        }
        else
        {
            favorites.Add(serverKey);
            _logger.Info($"Added to favorites: {server.Name}");
        }
        
        _settings.Save();
        serverListView.Invalidate();
    }
    
    private void ServerListView_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        // Handle clicking on the favorites star column
        if (e.RowIndex >= 0 && e.RowIndex < _filteredServers.Count && 
            e.ColumnIndex >= 0 && serverListView.Columns[e.ColumnIndex].Name == "Favorite")
        {
            ToggleFavorite(_filteredServers[e.RowIndex]);
        }
    }
    
    private void ServerContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Update the toggle favorite menu item text based on current state
        if (_selectedServer != null)
        {
            var serverKey = $"{_selectedServer.Address}:{_selectedServer.Port}";
            toggleFavoriteMenuItem.Text = IsFavorite(serverKey) 
                ? "Remove from Favorites" 
                : "Add to Favorites";
        }
    }
    
    private void ToggleFavoriteMenuItem_Click(object? sender, EventArgs e)
    {
        if (_selectedServer != null)
        {
            ToggleFavorite(_selectedServer);
        }
    }
    
    private void AddServerMenuItem_Click(object? sender, EventArgs e)
    {
        using var dialog = new AddServerDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.ServerAddress))
        {
            var entry = new ManualServerEntry
            {
                Address = dialog.ServerAddress,
                Port = dialog.ServerPort,
                IsFavorite = dialog.AddAsFavorite
            };
            
            // Add to manual servers list
            var manualServers = _settings.Settings.ManualServers;
            if (!manualServers.Any(s => s.FullAddress == entry.FullAddress))
            {
                manualServers.Add(entry);
                _logger.Info($"Added manual server: {entry.FullAddress}");
            }
            
            // Also add to favorites if requested
            if (dialog.AddAsFavorite)
            {
                _settings.Settings.FavoriteServers.Add(entry.FullAddress);
            }
            
            _settings.Save();
            
            // Refresh to query the new server
            _ = RefreshManualServerAsync(entry);
        }
    }
    
    private async Task RefreshManualServerAsync(ManualServerEntry entry)
    {
        try
        {
            var endpoint = new System.Net.IPEndPoint(
                System.Net.IPAddress.Parse(entry.Address), 
                entry.Port);
            
            await _browserService.RefreshServerAsync(endpoint);
            UpdateServerList();
            RefreshSelectedServerDetails(); // Update details panel if this server is selected
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to query manual server {entry.FullAddress}: {ex.Message}");
        }
    }
    
    private void ServerListView_RowPrePaint(object? sender, DataGridViewRowPrePaintEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _filteredServers.Count)
            return;
        
        var server = _filteredServers[e.RowIndex];
        var row = serverListView.Rows[e.RowIndex];
        
        // Apply row coloring based on server state
        if (server.IsFull)
        {
            row.DefaultCellStyle.BackColor = DarkTheme.FullServerRow;
        }
        else if (server.IsEmpty)
        {
            row.DefaultCellStyle.BackColor = DarkTheme.EmptyServerRow;
        }
        else if (server.IsPassworded)
        {
            row.DefaultCellStyle.BackColor = DarkTheme.PasswordedServerRow;
        }
        else
        {
            row.DefaultCellStyle.BackColor = DarkTheme.SecondaryBackground;
        }
    }

    private Bitmap CreateLockIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        using var pen = new Pen(DarkTheme.WarningColor, 1.5f);
        using var brush = new SolidBrush(DarkTheme.WarningColor);
        
        // Draw lock shape
        g.DrawArc(pen, 4, 2, 7, 8, 180, 180);
        g.FillRectangle(brush, 3, 7, 10, 7);
        
        return bmp;
    }
    
    private Bitmap CreateStarIcon(bool filled)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        
        // Star points
        var points = new PointF[10];
        float centerX = 8, centerY = 8;
        float outerRadius = 7, innerRadius = 3;
        for (int i = 0; i < 10; i++)
        {
            float radius = (i % 2 == 0) ? outerRadius : innerRadius;
            float angle = (float)(i * Math.PI / 5 - Math.PI / 2);
            points[i] = new PointF(
                centerX + radius * (float)Math.Cos(angle),
                centerY + radius * (float)Math.Sin(angle));
        }
        
        if (filled)
        {
            using var brush = new SolidBrush(Color.Gold);
            g.FillPolygon(brush, points);
            using var pen = new Pen(Color.DarkGoldenrod, 1f);
            g.DrawPolygon(pen, points);
        }
        else
        {
            using var pen = new Pen(DarkTheme.TextSecondary, 1f);
            g.DrawPolygon(pen, points);
        }
        
        return bmp;
    }

    private void DisplayServerDetails(ServerInfo server)
    {
        serverInfoTextBox.Clear();
        
        AppendText($"Name:     {server.Name}\n");
        AppendText($"Address:  {server.Address}\n");
        AppendText($"Map:      {server.Map}\n");
        AppendText($"Mode:     {server.GameMode.Name}\n");
        
        // Show detailed player breakdown
        if (server.BotCount > 0)
            AppendText($"Players:  {server.HumanPlayerCount} humans + {server.BotCount} bots / {server.MaxPlayers}\n");
        else
            AppendText($"Players:  {server.CurrentPlayers}/{server.MaxPlayers}\n");
        
        // IWAD with availability indicator
        AppendText("IWAD:     ");
        AppendWadWithStatus(server.IWAD);
        AppendText("\n");
        
        AppendText($"Version:  {server.GameVersion}\n");
        
        if (server.IsPassworded)
            AppendText("Status:   Password Protected\n");
        if (server.IsSecure)
            AppendText("Security: Enabled\n");
        
        // Verbose details
        if (_logger.VerboseMode)
        {
            AppendText("\n--- Verbose Info ---\n");
            AppendText($"Ping:           {server.Ping} ms\n");
            AppendText($"Max Clients:    {server.MaxClients}\n");
            AppendText($"Skill:          {server.Skill}\n");
            AppendText($"Frag Limit:     {server.FragLimit}\n");
            AppendText($"Time Limit:     {server.TimeLimit} min\n");
            AppendText($"Team Damage:    {server.TeamDamage:P0}\n");
            AppendText($"Instagib:       {server.Instagib}\n");
            AppendText($"Buckshot:       {server.Buckshot}\n");
            
            if (!string.IsNullOrEmpty(server.Website))
                AppendText($"Website:        {server.Website}\n");
            if (!string.IsNullOrEmpty(server.Email))
                AppendText($"Contact:        {server.Email}\n");
            if (!string.IsNullOrEmpty(server.Country))
                AppendText($"Country:        {server.Country}\n");
        }
    }
    
    private void AppendText(string text)
    {
        serverInfoTextBox.SelectionColor = DarkTheme.TextPrimary;
        serverInfoTextBox.AppendText(text);
    }
    
    private void AppendWadWithStatus(string wadName)
    {
        if (string.IsNullOrEmpty(wadName))
            return;
            
        var wadPath = _wadManager.FindWad(wadName);
        bool isAvailable = wadPath != null;
        bool isForbidden = WadManager.IsForbiddenWad(wadName);
        
        // Choose color based on status
        if (isAvailable)
        {
            serverInfoTextBox.SelectionColor = Color.LimeGreen;
            serverInfoTextBox.AppendText("[OK] ");
        }
        else if (isForbidden)
        {
            serverInfoTextBox.SelectionColor = Color.Yellow;
            serverInfoTextBox.AppendText("[IWAD] ");
        }
        else
        {
            serverInfoTextBox.SelectionColor = Color.Tomato;
            serverInfoTextBox.AppendText("[MISSING] ");
        }
        
        serverInfoTextBox.SelectionColor = DarkTheme.TextPrimary;
        serverInfoTextBox.AppendText(wadName);
    }

    private void DisplayPlayerList(ServerInfo server)
    {
        playerListView.Items.Clear();
        
        foreach (var player in server.Players)
        {
            var item = new ListViewItem(DoomColorCodes.StripColorCodes(player.Name));
            item.SubItems.Add(player.Score.ToString());
            item.SubItems.Add(player.Ping.ToString());
            
            string team = player.Team >= 0 && player.Team < server.Teams.Length 
                ? server.Teams[player.Team].Name 
                : "-";
            item.SubItems.Add(team);
            
            // Color spectators differently
            if (player.IsSpectator)
            {
                item.ForeColor = DarkTheme.TextSecondary;
            }
            
            // Mark bots
            if (player.IsBot)
            {
                item.Text += " [BOT]";
                item.ForeColor = DarkTheme.TextDisabled;
            }
            
            playerListView.Items.Add(item);
        }

        playerListLabel.Text = $"Players ({server.Players.Count})";
        
        // Recalculate column widths after populating
        AdjustPlayerListColumns();
    }
    
    private void DisplayWadList(ServerInfo server)
    {
        wadsListView.BeginUpdate();
        wadsListView.Items.Clear();
        
        // Reset column width to client area to clear any horizontal scroll
        if (wadsListView.Columns.Count > 0)
        {
            wadsListView.Columns[0].Width = wadsListView.ClientSize.Width;
        }
        
        // Add IWAD first
        if (!string.IsNullOrEmpty(server.IWAD))
        {
            var iwadItem = new ListViewItem(server.IWAD);
            var iwadPath = _wadManager.FindWad(server.IWAD);
            bool iwadAvailable = iwadPath != null;
            bool iwadForbidden = WadManager.IsForbiddenWad(server.IWAD);
            
            if (iwadAvailable)
            {
                iwadItem.ForeColor = Color.LimeGreen;
                iwadItem.Text = "[OK] " + server.IWAD;
            }
            else if (iwadForbidden)
            {
                iwadItem.ForeColor = Color.Yellow;
                iwadItem.Text = "[MISSING IWAD] " + server.IWAD;
            }
            else
            {
                iwadItem.ForeColor = Color.Tomato;
                iwadItem.Text = "[MISSING] " + server.IWAD;
            }
            wadsListView.Items.Add(iwadItem);
        }
        
        // Add PWADs
        foreach (var pwad in server.PWADs)
        {
            var item = new ListViewItem(pwad.Name);
            var wadPath = _wadManager.FindWad(pwad.Name);
            bool isAvailable = wadPath != null;
            
            if (isAvailable)
            {
                item.ForeColor = Color.LimeGreen;
                item.Text = "[OK] " + pwad.Name;
            }
            else
            {
                item.ForeColor = Color.Tomato;
                item.Text = "[MISSING] " + pwad.Name;
            }
            wadsListView.Items.Add(item);
        }
        
        int totalWads = (string.IsNullOrEmpty(server.IWAD) ? 0 : 1) + server.PWADs.Count;
        wadsLabel.Text = $"WADs ({totalWads})";
        
        // Adjust column width after populating
        AdjustWadsListColumn();
        wadsListView.EndUpdate();
    }
    
    private void WadsListView_Resize(object? sender, EventArgs e)
    {
        // Only expand to fill - don't shrink below content width
        if (wadsListView == null || !wadsListView.IsHandleCreated) return;
        if (wadsListView.Columns.Count == 0) return;
        
        int availableWidth = wadsListView.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
        if (wadsListView.Columns[0].Width < availableWidth)
        {
            wadsListView.Columns[0].Width = availableWidth;
        }
    }
    
    private void AdjustWadsListColumn()
    {
        if (wadsListView == null || !wadsListView.IsHandleCreated) return;
        if (wadsListView.Columns.Count == 0) return;
        
        int availableWidth = wadsListView.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
        
        if (wadsListView.Items.Count > 0)
        {
            // Measure required width for all items
            int requiredWidth = 0;
            using (var g = wadsListView.CreateGraphics())
            {
                foreach (ListViewItem item in wadsListView.Items)
                {
                    var size = TextRenderer.MeasureText(g, item.Text, wadsListView.Font);
                    requiredWidth = Math.Max(requiredWidth, size.Width + 20);
                }
            }
            
            // Use the larger of required width or available space
            int newWidth = Math.Max(requiredWidth, availableWidth);
            
            // Reset scroll by temporarily toggling Scrollable
            if (newWidth > availableWidth)
            {
                wadsListView.Scrollable = false;
                wadsListView.Columns[0].Width = newWidth;
                wadsListView.Scrollable = true;
            }
            else
            {
                wadsListView.Columns[0].Width = newWidth;
            }
        }
        else
        {
            // No items - fill available width
            wadsListView.Columns[0].Width = Math.Max(100, availableWidth);
        }
    }
    
    private void OnWadsListViewSelectionChanged(object? sender, ListViewItemSelectionChangedEventArgs e)
    {
        // Prevent selection while still allowing scrolling and preserving colors
        if (e.IsSelected && e.Item != null)
        {
            e.Item.Selected = false;
        }
    }
    
    private void PlayerListView_Resize(object? sender, EventArgs e)
    {
        AdjustPlayerListColumns();
    }
    
    private void AdjustPlayerListColumns()
    {
        if (playerListView == null || !playerListView.IsHandleCreated) return;
        if (playerListView.Columns.Count < 4) return;
        
        // Fixed widths for Score, Ping, Team
        const int scoreWidth = 50;
        const int pingWidth = 50;
        const int teamWidth = 50;
        
        // Account for vertical scrollbar if items exceed visible area
        int scrollBarWidth = 0;
        if (playerListView.Items.Count > 0)
        {
            int visibleItems = playerListView.ClientSize.Height / (playerListView.Items[0].Bounds.Height > 0 ? playerListView.Items[0].Bounds.Height : 16);
            if (playerListView.Items.Count > visibleItems)
            {
                scrollBarWidth = SystemInformation.VerticalScrollBarWidth;
            }
        }
        
        int availableWidth = playerListView.ClientSize.Width - scoreWidth - pingWidth - teamWidth - scrollBarWidth;
        int nameWidth = Math.Max(60, availableWidth);
        
        // Set all column widths
        playerNameColumn.Width = nameWidth;
        playerScoreColumn.Width = scoreWidth;
        playerPingColumn.Width = pingWidth;
        playerTeamColumn.Width = teamWidth;
    }

    private void UpdateStatusBar()
    {
        var onlineCount = _browserService.OnlineServers;
        var humanPlayers = _browserService.TotalHumanPlayers;
        var botCount = _browserService.TotalBots;
        
        serverCountLabel.Text = $"Servers: {onlineCount}";
        playerCountLabel.Text = botCount > 0
            ? $"Players: {humanPlayers} (+{botCount} bots)"
            : $"Players: {humanPlayers}";
    }

    #endregion

    #region Logging

    private void AppendLogEntry(LogEntry entry)
    {
        if (logTextBox.TextLength > 50000)
        {
            logTextBox.Text = logTextBox.Text[25000..];
        }

        Color color = entry.Level switch
        {
            LogLevel.Error => DarkTheme.ErrorColor,
            LogLevel.Warning => DarkTheme.WarningColor,
            LogLevel.Success => DarkTheme.SuccessColor,
            LogLevel.Verbose => DarkTheme.TextSecondary,
            _ => DarkTheme.TextPrimary
        };

        string prefix = entry.Level switch
        {
            LogLevel.Error => "[ERR] ",
            LogLevel.Warning => "[WRN] ",
            LogLevel.Success => "[OK]  ",
            LogLevel.Verbose => "[VRB] ",
            _ => "[INF] "
        };

        logTextBox.SelectionStart = logTextBox.TextLength;
        logTextBox.SelectionLength = 0;
        logTextBox.SelectionColor = DarkTheme.TextSecondary;
        logTextBox.AppendText($"{entry.Timestamp:HH:mm:ss} ");
        logTextBox.SelectionColor = color;
        logTextBox.AppendText($"{prefix}{entry.Message}\n");
        logTextBox.ScrollToCaret();
    }

    #endregion

    #region Form Lifecycle

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        
        // Load settings after controls are fully ready
        LoadSettings();
        
        // Initialize list column widths
        AdjustPlayerListColumns();
        AdjustWadsListColumn();
        
        // Now allow saving settings
        _isInitializing = false;
        
        // Ensure no server is selected on startup
        serverListView.ClearSelection();
        serverListView.CurrentCell = null;
        _selectedServer = null;
        _selectedServerAddress = null;
        
        _logger.Info("ZScape started");
        
        if (_settings.Settings.RefreshOnLaunch)
        {
            _logger.Info("Auto-refreshing server list...");
            _ = RefreshServersAsync();
        }
        else
        {
            _logger.Info("Press F5 or click Refresh to load servers");
        }
        
        // Check for updates in the background
        _ = CheckForUpdatesAsync();
    }
    
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            // Clean up any old update files from previous updates
            UpdateService.Instance.CleanupOldUpdates();
            
            // Check if we just restarted after an update and need to resume querying
            var savedState = UpdateService.Instance.LoadServerState();
            if (savedState != null)
            {
                _logger.Info($"Detected post-update restart to v{UpdateService.Instance.CurrentVersion}, resuming server query...");
                await _browserService.ResumeFromStateAsync(savedState);
            }
            
            // Perform automatic update check if enabled
            await UpdateService.Instance.PerformAutoUpdateCheckAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning($"Update check failed: {ex.Message}");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveSettings();
        _alertTimer?.Stop();
        _alertTimer?.Dispose();
        _browserService.CancelRefresh();
        _browserService.Dispose();
        _notificationService.Dispose();
        _screenshotMonitor.Dispose();
        base.OnFormClosing(e);
    }

    #endregion
}

/// <summary>
/// Tracks the alert state for a server to avoid duplicate alerts.
/// </summary>
internal class ServerAlertState
{
    /// <summary>
    /// Whether the server was online with the minimum player count in the last check.
    /// Used to detect transitions from offline/empty to online/populated.
    /// </summary>
    public bool WasOnlineWithPlayers { get; set; }
}
