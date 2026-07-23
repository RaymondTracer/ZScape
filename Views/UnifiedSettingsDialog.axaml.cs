using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ZScape.Controls;
using ZScape.Models;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.Views;

public partial class UnifiedSettingsDialog : Window
{
    private const string DownloadFolderPrefix = "[Download Folder] ";
    
    private readonly SettingsService _settingsService;
    private AppSettings Settings => _settingsService.Settings;
    private ObservableCollection<string> _favorites = new();
    private ObservableCollection<TextMatchRule> _favoriteNameRules = new();
    private ObservableCollection<TextMatchRule> _hiddenServerRules = new();
    private ObservableCollection<string> _manualServers = new();
    private ObservableCollection<string> _wadPaths = new();
    private ObservableCollection<string> _downloadSites = new();
    private ObservableCollection<string> _skippedOptionalPwads = new();
    private ObservableCollection<DomainThreadDisplay> _domainConfigs = new();
    private string _lastValidDownloadPath = string.Empty;
    private bool _updatingIntervalControls;
    private bool _updatingAutoRefreshControls;

    public bool SettingsChanged { get; private set; }
    public event EventHandler<int>? RowHeightPreviewChanged;

    public UnifiedSettingsDialog()
    {
        InitializeComponent();
        _settingsService = SettingsService.Instance;
        
        CategoryList.SelectionChanged += CategoryList_SelectionChanged;
        FavoriteNameRulesListBox.SelectionChanged += (_, _) => UpdateFavoriteRuleButtons();
        HiddenServerRulesListBox.SelectionChanged += (_, _) => UpdateFavoriteRuleButtons();
        WadPathsListBox.SelectionChanged += WadPathsListBox_SelectionChanged;
        SkippedOptionalPwadsListBox.SelectionChanged += SkippedOptionalPwadsListBox_SelectionChanged;
        WadDownloadPathTextBox.TextChanged += WadDownloadPathTextBox_TextChanged;
        ZandronumPathTextBox.TextChanged += ZandronumPathTextBox_TextChanged;
        
        // Live preview for row height changes
        RowHeightNumeric.ValueChanged += (_, newValue) => RowHeightPreviewChanged?.Invoke(this, newValue);
        
        // Sync update interval preset and manual controls
        UpdateIntervalPresets.SelectionChanged += UpdateIntervalPresets_SelectionChanged;
        UpdateCheckIntervalValue.ValueChanged += (_, _) => UpdateInterval_ManualChanged();
        UpdateCheckIntervalUnit.SelectionChanged += (_, _) => UpdateInterval_ManualChanged();

        // Sync full and favorites auto-refresh controls
        AutoRefreshIntervalNumeric.ValueChanged += (_, newValue) => AutoRefreshIntervalNumeric_ValueChanged(newValue);
        AutoRefreshFavoritesIntervalNumeric.ValueChanged += (_, newValue) => AutoRefreshFavoritesIntervalNumeric_ValueChanged(newValue);
        UseFullRefreshTimerForFavoritesCheckBox.IsCheckedChanged += (_, _) =>
            UseFullRefreshTimerForFavoritesChanged(UseFullRefreshTimerForFavoritesCheckBox.IsChecked == true);
        
        // Handle Escape/Enter keys
        KeyDown += OnDialogKeyDown;
        
        SetupDomainListView();
        LoadSettings();
    }
    
    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelButton_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            // Only trigger OK if not focused on a multi-line textbox or editable control
            if (FocusManager?.GetFocusedElement() is not TextBox textBox || !textBox.AcceptsReturn)
            {
                OkButton_Click(sender, e);
                e.Handled = true;
            }
        }
    }

    private void SetupDomainListView()
    {
        DomainListView.AlternatingRowColors = true;
        DomainListView.RowHeight = 28;
        DomainListView.SuppressHandCursor = true;

        // Domain name (editable text)
        DomainListView.AddColumn(new ListViewColumn
        {
            Header = "Domain",
            Width = 140,
            MinWidth = 80,
            IsFixedWidth = true,
            CellContentFactory = () =>
            {
                var tb = new TextBox { Margin = new Thickness(4, 0) };
                tb.Classes.Add("editCell");
                tb.Bind(TextBox.TextProperty, new Binding("Domain") { Mode = BindingMode.TwoWay });
                return tb;
            }
        });

        // Threads (editable number)
        DomainListView.AddColumn(new ListViewColumn
        {
            Header = "Threads",
            Width = 55,
            MinWidth = 40,
            IsFixedWidth = true,
            CellContentFactory = () =>
            {
                var tb = new TextBox();
                tb.Classes.Add("editCellNum");
                tb.Bind(TextBox.TextProperty, new Binding("MaxThreads") { Mode = BindingMode.TwoWay });
                return tb;
            }
        });

        // Seg KB (editable number)
        DomainListView.AddColumn(new ListViewColumn
        {
            Header = "Seg KB",
            Width = 55,
            MinWidth = 40,
            IsFixedWidth = true,
            CellContentFactory = () =>
            {
                var tb = new TextBox();
                tb.Classes.Add("editCellNum");
                tb.Bind(TextBox.TextProperty, new Binding("MinSegmentSizeKb") { Mode = BindingMode.TwoWay });
                return tb;
            }
        });

        // Adaptive (checkbox)
        DomainListView.AddColumn(new ListViewColumn
        {
            Header = "Adaptive",
            Width = 55,
            MinWidth = 40,
            IsFixedWidth = true,
            CellContentFactory = () =>
            {
                var cb = new CheckBox
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                cb.Bind(CheckBox.IsCheckedProperty, new Binding("AdaptiveLearning") { Mode = BindingMode.TwoWay });
                return cb;
            }
        });

        DomainListView.RowGotFocus += DomainListView_RowGotFocus;
        DomainListView.Build(ListViewOverflowMode.AutoScroll);
    }

    private void CategoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedIndex >= 0)
        {
            ContentTabControl.SelectedIndex = CategoryList.SelectedIndex;
        }
    }

    private void WadPathsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = WadPathsListBox.SelectedIndex;
        var count = _wadPaths.Count;
        
        // Index 0 is the download folder - cannot be removed or moved
        RemoveWadPathButton.IsEnabled = index > 0;
        MoveUpWadPathButton.IsEnabled = index > 1;  // Can't move to position 0
        MoveDownWadPathButton.IsEnabled = index > 0 && index < count - 1;
        SetAsDownloadButton.IsEnabled = index > 0;  // Can promote any non-download path
    }
    
    private void WadDownloadPathTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        // Update the first item in the list to reflect the new download folder path
        if (_wadPaths.Count > 0)
        {
            var newPath = WadDownloadPathTextBox.Text?.Trim() ?? "";
            _wadPaths[0] = DownloadFolderPrefix + newPath;
        }
    }
    
    private void ZandronumPathTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        // Update watermarks when Zandronum path changes
        UpdatePathWatermarks();
    }
    
    /// <summary>
    /// Updates the placeholder/watermark text on path TextBoxes to show the resolved default paths.
    /// </summary>
    private void UpdatePathWatermarks()
    {
        // Create a temporary settings object with current UI values to calculate defaults
        var tempSettings = new AppSettings
        {
            ZandronumPath = ZandronumPathTextBox.Text ?? string.Empty
        };
        
        // Update Testing Versions watermark
        var testingDefault = PathResolver.GetDefaultTestingVersionsPath(tempSettings);
        ZandronumTestingPathTextBox.Watermark = testingDefault ?? "Configure Zandronum path first";
        
        // Update Screenshots watermark
        var screenshotsDefault = PathResolver.GetDefaultScreenshotsPath(tempSettings);
        ScreenshotPathTextBox.Watermark = screenshotsDefault ?? "Configure Zandronum path first";
    }

    private void LoadSettings()
    {
        // General
        ZandronumPathTextBox.Text = Settings.ZandronumPath;
        ZandronumTestingPathTextBox.Text = Settings.ZandronumTestingPath;
        HashConcurrencyNumeric.Value = Settings.HashVerificationConcurrency;
        ColorizePlayerNamesCheckBox.IsChecked = Settings.ColorizePlayerNames;
        RowHeightNumeric.Value = Settings.ServerListRowHeight;
        ScreenshotMonitorCheckBox.IsChecked = Settings.EnableScreenshotMonitoring;
        ScreenshotPathTextBox.Text = Settings.ScreenshotConsolidationPath;

        // Favorites
        _favorites = new ObservableCollection<string>(Settings.FavoriteServers);
        FavoritesListBox.ItemsSource = _favorites;
        _favoriteNameRules = new ObservableCollection<TextMatchRule>(Settings.FavoriteServerNameRules.Select(rule => rule.Clone()));
        FavoriteNameRulesListBox.ItemsSource = _favoriteNameRules;
        _hiddenServerRules = new ObservableCollection<TextMatchRule>(Settings.HiddenServerNameRules.Select(rule => rule.Clone()));
        HiddenServerRulesListBox.ItemsSource = _hiddenServerRules;
        _manualServers = new ObservableCollection<string>(Settings.ManualServers.Select(m => m.FullAddress));
        ManualServersListBox.ItemsSource = _manualServers;
        PopulateFavoriteStarClickBehaviorComboBox();
        UpdateFavoriteRuleButtons();
        
        EnableFavoriteAlertsCheckBox.IsChecked = Settings.EnableFavoriteServerAlerts;
        EnableManualAlertsCheckBox.IsChecked = Settings.EnableManualServerAlerts;
        PopulateAlertNotificationModeComboBox();
        PopulateCustomNotificationCornerComboBox();
        CustomNotificationDurationNumeric.Value = Settings.CustomNotificationDurationSeconds;
        ShowFavoritesColumnCheckBox.IsChecked = Settings.ShowFavoritesColumn;
        AlertMinPlayersNumeric.Value = Settings.AlertMinPlayers;
        AlertIntervalNumeric.Value = Settings.AlertCheckIntervalSeconds;

        // WAD Paths - Download folder is always first with prefix
        _wadPaths = new ObservableCollection<string>();
        var downloadPath = Settings.WadDownloadPath;
        if (!string.IsNullOrEmpty(downloadPath))
        {
            _wadPaths.Add(DownloadFolderPrefix + downloadPath);
        }
        else
        {
            _wadPaths.Add(DownloadFolderPrefix + "(not set)");
        }
        // Add other search paths, excluding the download path (it's already first)
        foreach (var path in Settings.WadSearchPaths.Where(p => !p.Equals(downloadPath, StringComparison.OrdinalIgnoreCase)))
        {
            _wadPaths.Add(path);
        }
        WadPathsListBox.ItemsSource = _wadPaths;
        WadDownloadPathTextBox.Text = downloadPath;
        _lastValidDownloadPath = downloadPath;

        // Download Sites - auto-populate with defaults if empty (like WinForms)
        var sitesToLoad = Settings.DownloadSites.Count > 0
            ? Settings.DownloadSites
            : WadDownloader.DefaultSites.ToList();
        _downloadSites = new ObservableCollection<string>(sitesToLoad);
        DownloadSitesListBox.ItemsSource = _downloadSites;

        // Downloads
        PopulateDownloadBehaviorComboBox();
        PopulateOptionalPwadModeComboBox();
        MaxConcurrentDownloadsNumeric.Value = Settings.MaxConcurrentDownloads;
        MaxConcurrentDomainsNumeric.Value = Settings.MaxConcurrentDomains;
        MaxThreadsPerFileNumeric.Value = Settings.MaxThreadsPerFile;
        DefaultMinSegmentKbNumeric.Value = Settings.DefaultMinSegmentSizeKb;
        OptionalPwadModeComboBox.SelectedIndex = AppConstants.OptionalPwadDownloadModeLabels.GetIndex(Settings.OptionalPwadDownloadMode);
        _skippedOptionalPwads = new ObservableCollection<string>(Settings.SkippedOptionalPwads
            .Select(NormalizeOptionalPwadName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        SkippedOptionalPwadsListBox.ItemsSource = _skippedOptionalPwads;
        UpdateSkippedOptionalPwadButtons();

        // Domain Threads
        LoadDomainConfigs();

        // Server Queries
        QueryIntervalMsNumeric.Value = Settings.QueryIntervalMs;
        MaxConcurrentQueriesNumeric.Value = Settings.MaxConcurrentQueries;
        QueryRetryAttemptsNumeric.Value = Settings.QueryRetryAttempts;
        QueryRetryDelayMsNumeric.Value = Settings.QueryRetryDelayMs;
        MasterServerRetryCountNumeric.Value = Settings.MasterServerRetryCount;
        ConsecutiveFailuresNumeric.Value = Settings.ConsecutiveFailuresBeforeOffline;
        LoadAutoRefreshSettings();

        // Updates
        UpdateBehaviorComboBox.SelectedIndex = (int)Settings.UpdateBehavior;
        AutoRestartCheckBox.IsChecked = Settings.AutoRestartForUpdates;
        LoadUpdateInterval();
        
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionInfoLabel.Text = $"Current version: {version?.ToString() ?? "Unknown"}";
        
        // Update watermarks to show resolved default paths
        UpdatePathWatermarks();
    }
    
    private void PopulateDownloadBehaviorComboBox()
    {
        DownloadBehaviorComboBox.Items.Clear();
        foreach (var option in AppConstants.DownloadDialogBehaviorLabels.Options)
        {
            DownloadBehaviorComboBox.Items.Add(new ComboBoxItem { Content = option.Label });
        }
        DownloadBehaviorComboBox.SelectedIndex = AppConstants.DownloadDialogBehaviorLabels.GetIndex(Settings.DownloadDialogBehavior);
    }

    private void PopulateOptionalPwadModeComboBox()
    {
        OptionalPwadModeComboBox.Items.Clear();
        foreach (var option in AppConstants.OptionalPwadDownloadModeLabels.Options)
        {
            OptionalPwadModeComboBox.Items.Add(new ComboBoxItem { Content = option.Label });
        }
        OptionalPwadModeComboBox.SelectedIndex = AppConstants.OptionalPwadDownloadModeLabels.GetIndex(Settings.OptionalPwadDownloadMode);
    }

    private void PopulateAlertNotificationModeComboBox()
    {
        AlertNotificationModeComboBox.Items.Clear();
        foreach (var option in AppConstants.NotificationDisplayModeLabels.Options)
        {
            AlertNotificationModeComboBox.Items.Add(new ComboBoxItem { Content = option.Label });
        }
        AlertNotificationModeComboBox.SelectedIndex = AppConstants.NotificationDisplayModeLabels.GetIndex(Settings.AlertNotificationMode);
    }

    private void PopulateCustomNotificationCornerComboBox()
    {
        CustomNotificationCornerComboBox.Items.Clear();
        foreach (var option in AppConstants.CustomNotificationCornerLabels.Options)
        {
            CustomNotificationCornerComboBox.Items.Add(new ComboBoxItem { Content = option.Label });
        }
        CustomNotificationCornerComboBox.SelectedIndex = AppConstants.CustomNotificationCornerLabels.GetIndex(Settings.CustomNotificationCorner);
    }

    private void PopulateFavoriteStarClickBehaviorComboBox()
    {
        FavoriteStarClickBehaviorComboBox.Items.Clear();
        foreach (var option in AppConstants.FavoriteStarClickBehaviorLabels.Options)
        {
            FavoriteStarClickBehaviorComboBox.Items.Add(new ComboBoxItem { Content = option.Label });
        }
        FavoriteStarClickBehaviorComboBox.SelectedIndex = AppConstants.FavoriteStarClickBehaviorLabels.GetIndex(Settings.FavoriteStarClickBehavior);
    }

    private void LoadDomainConfigs()
    {
        _domainConfigs.Clear();
        foreach (var config in SettingsService.Instance.DomainThreadSettings.OrderBy(k => k.Key))
        {
            var display = new DomainThreadDisplay();
            display.InitializeFromSettings(
                config.Key,
                config.Value.MaxThreads,
                config.Value.MinSegmentSizeKb,
                config.Value.AdaptiveLearning,
                _domainConfigs.Count
            );
            _domainConfigs.Add(display);
        }
        DomainListView.ItemsSource = _domainConfigs;
    }

    private void LoadUpdateInterval()
    {
        _updatingIntervalControls = true;
        try
        {
            var hours = Settings.UpdateCheckIntervalHours;
            if (hours == 6)
            {
                UpdateIntervalPresets.SelectedIndex = 1;
                UpdateCheckIntervalValue.Value = 6;
                UpdateCheckIntervalUnit.SelectedIndex = 0; // Hours
            }
            else if (hours == 24)
            {
                UpdateIntervalPresets.SelectedIndex = 2;
                UpdateCheckIntervalValue.Value = 1;
                UpdateCheckIntervalUnit.SelectedIndex = 1; // Days
            }
            else if (hours == 168)
            {
                UpdateIntervalPresets.SelectedIndex = 3;
                UpdateCheckIntervalValue.Value = 1;
                UpdateCheckIntervalUnit.SelectedIndex = 2; // Weeks
            }
            else if (hours == 720)
            {
                UpdateIntervalPresets.SelectedIndex = 4;
                UpdateCheckIntervalValue.Value = 30;
                UpdateCheckIntervalUnit.SelectedIndex = 1; // Days (approx month)
            }
            else
            {
                UpdateIntervalPresets.SelectedIndex = 0;
                if (hours < 24)
                {
                    UpdateCheckIntervalValue.Value = hours;
                    UpdateCheckIntervalUnit.SelectedIndex = 0;
                }
                else if (hours < 168)
                {
                    UpdateCheckIntervalValue.Value = hours / 24;
                    UpdateCheckIntervalUnit.SelectedIndex = 1;
                }
                else
                {
                    UpdateCheckIntervalValue.Value = hours / 168;
                    UpdateCheckIntervalUnit.SelectedIndex = 2;
                }
            }
        }
        finally
        {
            _updatingIntervalControls = false;
        }
    }

    private void UpdateIntervalPresets_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingIntervalControls) return;
        _updatingIntervalControls = true;
        try
        {
            switch (UpdateIntervalPresets.SelectedIndex)
            {
                case 1: // Every 6 hours
                    UpdateCheckIntervalValue.Value = 6;
                    UpdateCheckIntervalUnit.SelectedIndex = 0;
                    break;
                case 2: // Once a day
                    UpdateCheckIntervalValue.Value = 1;
                    UpdateCheckIntervalUnit.SelectedIndex = 1;
                    break;
                case 3: // Once a week
                    UpdateCheckIntervalValue.Value = 1;
                    UpdateCheckIntervalUnit.SelectedIndex = 2;
                    break;
                case 4: // Once a month
                    UpdateCheckIntervalValue.Value = 30;
                    UpdateCheckIntervalUnit.SelectedIndex = 1;
                    break;
            }
        }
        finally
        {
            _updatingIntervalControls = false;
        }
    }

    private void UpdateInterval_ManualChanged()
    {
        if (_updatingIntervalControls) return;
        _updatingIntervalControls = true;
        try
        {
            var value = UpdateCheckIntervalValue.Value;
            var unit = UpdateCheckIntervalUnit.SelectedIndex;
            
            // Check if it matches a preset
            if (value == 6 && unit == 0)
                UpdateIntervalPresets.SelectedIndex = 1; // Every 6 hours
            else if (value == 1 && unit == 1)
                UpdateIntervalPresets.SelectedIndex = 2; // Once a day
            else if (value == 1 && unit == 2)
                UpdateIntervalPresets.SelectedIndex = 3; // Once a week
            else if (value == 30 && unit == 1)
                UpdateIntervalPresets.SelectedIndex = 4; // Once a month
            else
                UpdateIntervalPresets.SelectedIndex = 0; // Custom
        }
        finally
        {
            _updatingIntervalControls = false;
        }
    }

    private void LoadAutoRefreshSettings()
    {
        _updatingAutoRefreshControls = true;
        try
        {
            AutoRefreshIntervalNumeric.Value = NormalizeAutoRefreshInterval(Settings.AutoRefreshIntervalMinutes);
            UseFullRefreshTimerForFavoritesCheckBox.IsChecked = Settings.AutoRefreshFavoritesUseFullRefreshTimer;
            AutoRefreshFavoritesIntervalNumeric.Value = Settings.AutoRefreshFavoritesUseFullRefreshTimer
                ? AutoRefreshIntervalNumeric.Value
                : NormalizeAutoRefreshInterval(Settings.AutoRefreshFavoritesIntervalMinutes);
            AutoRefreshFavoritesOnlyCheckBox.IsChecked = Settings.AutoRefreshFavoritesOnly;
        }
        finally
        {
            _updatingAutoRefreshControls = false;
        }
    }

    private void AutoRefreshIntervalNumeric_ValueChanged(int newValue)
    {
        if (_updatingAutoRefreshControls)
        {
            return;
        }

        if (UseFullRefreshTimerForFavoritesCheckBox.IsChecked == true)
        {
            _updatingAutoRefreshControls = true;
            try
            {
                AutoRefreshFavoritesIntervalNumeric.Value = newValue;
            }
            finally
            {
                _updatingAutoRefreshControls = false;
            }
        }
    }

    private void AutoRefreshFavoritesIntervalNumeric_ValueChanged(int newValue)
    {
        if (_updatingAutoRefreshControls)
        {
            return;
        }

        if (UseFullRefreshTimerForFavoritesCheckBox.IsChecked == true && newValue != AutoRefreshIntervalNumeric.Value)
        {
            _updatingAutoRefreshControls = true;
            try
            {
                UseFullRefreshTimerForFavoritesCheckBox.IsChecked = false;
            }
            finally
            {
                _updatingAutoRefreshControls = false;
            }
        }
    }

    private void UseFullRefreshTimerForFavoritesChanged(bool isChecked)
    {
        if (_updatingAutoRefreshControls || !isChecked)
        {
            return;
        }

        _updatingAutoRefreshControls = true;
        try
        {
            AutoRefreshFavoritesIntervalNumeric.Value = AutoRefreshIntervalNumeric.Value;
        }
        finally
        {
            _updatingAutoRefreshControls = false;
        }
    }

    private static int NormalizeAutoRefreshInterval(int intervalMinutes)
    {
        return intervalMinutes < 1 ? 5 : intervalMinutes;
    }

    private void SaveSettings()
    {
        // General
        Settings.ZandronumPath = ZandronumPathTextBox.Text ?? "";
        Settings.ZandronumTestingPath = ZandronumTestingPathTextBox.Text ?? "";
        Settings.HashVerificationConcurrency = HashConcurrencyNumeric.Value;
        Settings.ColorizePlayerNames = ColorizePlayerNamesCheckBox.IsChecked ?? true;
        Settings.ServerListRowHeight = RowHeightNumeric.Value;
        Settings.EnableScreenshotMonitoring = ScreenshotMonitorCheckBox.IsChecked ?? false;
        Settings.ScreenshotConsolidationPath = ScreenshotPathTextBox.Text ?? "";

        // Favorites
        Settings.FavoriteServers = _favorites.ToHashSet();
        Settings.FavoriteServerNameRules = NormalizeTextMatchRules(_favoriteNameRules);
        Settings.HiddenServerNameRules = NormalizeTextMatchRules(_hiddenServerRules);
        Settings.ManualServers = _manualServers.Select(addr => {
            var parts = addr.Split(':');
            return new ManualServerEntry {
                Address = parts[0],
                Port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 10666
            };
        }).ToList();
        Settings.FavoriteStarClickBehavior = AppConstants.FavoriteStarClickBehaviorLabels.GetValue(FavoriteStarClickBehaviorComboBox.SelectedIndex);
        Settings.EnableFavoriteServerAlerts = EnableFavoriteAlertsCheckBox.IsChecked ?? false;
        Settings.EnableManualServerAlerts = EnableManualAlertsCheckBox.IsChecked ?? false;
        Settings.AlertNotificationMode = AppConstants.NotificationDisplayModeLabels.GetValue(AlertNotificationModeComboBox.SelectedIndex);
        Settings.CustomNotificationCorner = AppConstants.CustomNotificationCornerLabels.GetValue(CustomNotificationCornerComboBox.SelectedIndex);
        Settings.CustomNotificationDurationSeconds = CustomNotificationDurationNumeric.Value;
        Settings.ShowFavoritesColumn = ShowFavoritesColumnCheckBox.IsChecked ?? false;
        Settings.AlertMinPlayers = AlertMinPlayersNumeric.Value;
        Settings.AlertCheckIntervalSeconds = AlertIntervalNumeric.Value;

        // WAD Paths - Extract paths correctly from the list
        // Index 0 has the "[Download Folder] " prefix, so we get download path from textbox
        // Other items are regular search paths
        var downloadPath = WadDownloadPathTextBox.Text?.Trim() ?? "";
        Settings.WadDownloadPath = downloadPath;
        
        // Build search paths list: download folder first, then other paths
        var searchPaths = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(downloadPath))
        {
            searchPaths.Add(downloadPath);
        }
        // Add remaining paths (skip index 0 which is the prefixed download folder)
        foreach (var path in _wadPaths.Skip(1))
        {
            if (!string.IsNullOrEmpty(path) && !searchPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                searchPaths.Add(path);
            }
        }
        Settings.WadSearchPaths = searchPaths;

        // Download Sites
        Settings.DownloadSites = _downloadSites.ToList();

        // Downloads
        Settings.DownloadDialogBehavior = AppConstants.DownloadDialogBehaviorLabels.GetValue(DownloadBehaviorComboBox.SelectedIndex);
        Settings.OptionalPwadDownloadMode = AppConstants.OptionalPwadDownloadModeLabels.GetValue(OptionalPwadModeComboBox.SelectedIndex);
        Settings.SkippedOptionalPwads = _skippedOptionalPwads
            .Select(NormalizeOptionalPwadName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Settings.MaxConcurrentDownloads = MaxConcurrentDownloadsNumeric.Value;
        Settings.MaxConcurrentDomains = MaxConcurrentDomainsNumeric.Value;
        Settings.MaxThreadsPerFile = MaxThreadsPerFileNumeric.Value;
        Settings.DefaultMinSegmentSizeKb = DefaultMinSegmentKbNumeric.Value;

        // Domain Threads
        SaveDomainConfigs();

        // Server Queries
        Settings.QueryIntervalMs = QueryIntervalMsNumeric.Value;
        Settings.MaxConcurrentQueries = MaxConcurrentQueriesNumeric.Value;
        Settings.QueryRetryAttempts = QueryRetryAttemptsNumeric.Value;
        Settings.QueryRetryDelayMs = QueryRetryDelayMsNumeric.Value;
        Settings.MasterServerRetryCount = MasterServerRetryCountNumeric.Value;
        Settings.ConsecutiveFailuresBeforeOffline = ConsecutiveFailuresNumeric.Value;
        Settings.AutoRefreshIntervalMinutes = AutoRefreshIntervalNumeric.Value;
        Settings.AutoRefreshFavoritesUseFullRefreshTimer = UseFullRefreshTimerForFavoritesCheckBox.IsChecked ?? true;
        Settings.AutoRefreshFavoritesIntervalMinutes = Settings.AutoRefreshFavoritesUseFullRefreshTimer
            ? AutoRefreshIntervalNumeric.Value
            : AutoRefreshFavoritesIntervalNumeric.Value;
        Settings.AutoRefreshFavoritesOnly = AutoRefreshFavoritesOnlyCheckBox.IsChecked ?? false;

        // Updates
        Settings.UpdateBehavior = (UpdateBehavior)(UpdateBehaviorComboBox.SelectedIndex);
        Settings.AutoRestartForUpdates = AutoRestartCheckBox.IsChecked ?? false;
        SaveUpdateInterval();

        _settingsService.Save();
        SettingsChanged = true;
    }

    private void TestCustomNotificationButton_Click(object? sender, RoutedEventArgs e)
    {
        var selectedCorner = AppConstants.CustomNotificationCornerLabels.GetValue(CustomNotificationCornerComboBox.SelectedIndex);
        NotificationService.Instance.ShowCustomPreviewNotification(this, selectedCorner, CustomNotificationDurationNumeric.Value);
    }

    private void SaveDomainConfigs()
    {
        var domainSettings = SettingsService.Instance.DomainThreadSettings;
        domainSettings.Clear();
        foreach (var item in _domainConfigs)
        {
            // Normalize domain to lowercase like WinForms
            string domain = item.Domain.ToLowerInvariant().Trim();
            if (string.IsNullOrWhiteSpace(domain)) continue;
            
            domainSettings[domain] = new DomainSettings
            {
                MaxThreads = item.MaxThreads,
                MinSegmentSizeKb = item.MinSegmentSizeKb,
                AdaptiveLearning = item.AdaptiveLearning
            };
        }
        SettingsService.Instance.SaveDomainSettings();
    }

    private void SaveUpdateInterval()
    {
        var preset = UpdateIntervalPresets.SelectedIndex;
        switch (preset)
        {
            case 1: // 6 hours
                Settings.UpdateCheckIntervalValue = 6;
                Settings.UpdateCheckIntervalUnit = UpdateIntervalUnit.Hours;
                break;
            case 2: // 24 hours (1 day)
                Settings.UpdateCheckIntervalValue = 1;
                Settings.UpdateCheckIntervalUnit = UpdateIntervalUnit.Days;
                break;
            case 3: // 168 hours (1 week)
                Settings.UpdateCheckIntervalValue = 1;
                Settings.UpdateCheckIntervalUnit = UpdateIntervalUnit.Weeks;
                break;
            case 4: // 720 hours (30 days/1 month)
                Settings.UpdateCheckIntervalValue = 30;
                Settings.UpdateCheckIntervalUnit = UpdateIntervalUnit.Days;
                break;
            default:
                Settings.UpdateCheckIntervalValue = UpdateCheckIntervalValue.Value;
                Settings.UpdateCheckIntervalUnit = UpdateCheckIntervalUnit.SelectedIndex switch
                {
                    0 => Services.UpdateIntervalUnit.Hours,
                    1 => Services.UpdateIntervalUnit.Days,
                    2 => Services.UpdateIntervalUnit.Weeks,
                    _ => Services.UpdateIntervalUnit.Days
                };
                break;
        }
    }

    private async void BrowseZandronumPath_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Zandronum Executable",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executable") { Patterns = new[] { "*.exe", "zandronum*" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
            }
        });
        if (files.Count > 0)
        {
            ZandronumPathTextBox.Text = files[0].Path.LocalPath;
        }
    }

    private async void BrowseTestingPath_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Testing Versions Folder",
            AllowMultiple = false
        });
        if (folder.Count > 0)
        {
            ZandronumTestingPathTextBox.Text = folder[0].Path.LocalPath;
        }
    }

    private async void BrowseScreenshotPath_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Screenshots Folder",
            AllowMultiple = false
        });
        if (folder.Count > 0)
        {
            ScreenshotPathTextBox.Text = folder[0].Path.LocalPath;
        }
    }

    private void RemoveFavorite_Click(object? sender, RoutedEventArgs e)
    {
        if (FavoritesListBox.SelectedItem is string favorite)
        {
            _favorites.Remove(favorite);
        }
    }

    private async void AddFavoriteNameRule_Click(object? sender, RoutedEventArgs e)
    {
        var rule = await ShowTextMatchRuleDialogAsync("Add Favorite Name Rule");
        if (rule != null)
        {
            _favoriteNameRules.Add(rule);
        }
    }

    private async void EditFavoriteNameRule_Click(object? sender, RoutedEventArgs e)
    {
        if (FavoriteNameRulesListBox.SelectedItem is not TextMatchRule rule)
        {
            return;
        }

        var editedRule = await ShowTextMatchRuleDialogAsync("Edit Favorite Name Rule", rule);
        if (editedRule == null)
        {
            return;
        }

        var index = _favoriteNameRules.IndexOf(rule);
        if (index >= 0)
        {
            _favoriteNameRules[index] = editedRule;
        }
    }

    private void RemoveFavoriteNameRule_Click(object? sender, RoutedEventArgs e)
    {
        if (FavoriteNameRulesListBox.SelectedItem is TextMatchRule rule)
        {
            _favoriteNameRules.Remove(rule);
        }
    }

    private async void AddHiddenServerRule_Click(object? sender, RoutedEventArgs e)
    {
        var rule = await ShowTextMatchRuleDialogAsync("Add Hidden Server Rule");
        if (rule != null)
        {
            _hiddenServerRules.Add(rule);
        }
    }

    private async void EditHiddenServerRule_Click(object? sender, RoutedEventArgs e)
    {
        if (HiddenServerRulesListBox.SelectedItem is not TextMatchRule rule)
        {
            return;
        }

        var editedRule = await ShowTextMatchRuleDialogAsync("Edit Hidden Server Rule", rule);
        if (editedRule == null)
        {
            return;
        }

        var index = _hiddenServerRules.IndexOf(rule);
        if (index >= 0)
        {
            _hiddenServerRules[index] = editedRule;
        }
    }

    private void RemoveHiddenServerRule_Click(object? sender, RoutedEventArgs e)
    {
        if (HiddenServerRulesListBox.SelectedItem is TextMatchRule rule)
        {
            _hiddenServerRules.Remove(rule);
        }
    }

    private void ClearFavorites_Click(object? sender, RoutedEventArgs e)
    {
        _favorites.Clear();
    }

    private async void AddManualServer_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new AddServerDialog();
        await dialog.ShowDialog(this);
        if (dialog.Confirmed && !string.IsNullOrEmpty(dialog.ServerAddress))
        {
            var addr = $"{dialog.ServerAddress}:{dialog.ServerPort}";
            if (!_manualServers.Contains(addr))
            {
                _manualServers.Add(addr);
            }
            
            // Also add to favorites if checkbox was checked
            if (dialog.AddAsFavorite && !_favorites.Contains(addr))
            {
                _favorites.Add(addr);
            }
        }
    }

    private void RemoveManualServer_Click(object? sender, RoutedEventArgs e)
    {
        if (ManualServersListBox.SelectedItem is string server)
        {
            _manualServers.Remove(server);
        }
    }

    private void UpdateFavoriteRuleButtons()
    {
        EditFavoriteNameRuleButton.IsEnabled = FavoriteNameRulesListBox.SelectedItem is TextMatchRule;
        RemoveFavoriteNameRuleButton.IsEnabled = FavoriteNameRulesListBox.SelectedItem is TextMatchRule;
        EditHiddenServerRuleButton.IsEnabled = HiddenServerRulesListBox.SelectedItem is TextMatchRule;
        RemoveHiddenServerRuleButton.IsEnabled = HiddenServerRulesListBox.SelectedItem is TextMatchRule;
    }

    private async Task<TextMatchRule?> ShowTextMatchRuleDialogAsync(string title, TextMatchRule? existingRule = null)
    {
        var dialog = new TextMatchRuleDialog(title, existingRule);
        await dialog.ShowDialog(this);
        return dialog.Confirmed ? dialog.Rule.Clone() : null;
    }

    private static List<TextMatchRule> NormalizeTextMatchRules(IEnumerable<TextMatchRule> rules)
    {
        var normalizedRules = new List<TextMatchRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            var pattern = rule.Pattern?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var key = $"{rule.Mode}:{pattern}";
            if (!seen.Add(key))
            {
                continue;
            }

            normalizedRules.Add(new TextMatchRule
            {
                Pattern = pattern,
                Mode = rule.Mode
            });
        }

        return normalizedRules;
    }

    private async void AddWadPath_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select WAD Search Path",
            AllowMultiple = false
        });
        if (folder.Count > 0)
        {
            var path = folder[0].Path.LocalPath;
            var downloadPath = WadDownloadPathTextBox.Text?.Trim() ?? "";
            
            // Check if it's the download folder
            if (path.Equals(downloadPath, StringComparison.OrdinalIgnoreCase))
                return; // Already the download folder
            
            // Check if already in list (skip index 0 which has prefix)
            if (!_wadPaths.Skip(1).Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                _wadPaths.Add(path);
            }
        }
    }

    private void RemoveWadPath_Click(object? sender, RoutedEventArgs e)
    {
        // Can only remove paths at index > 0 (not the download folder)
        if (WadPathsListBox.SelectedIndex > 0)
        {
            _wadPaths.RemoveAt(WadPathsListBox.SelectedIndex);
        }
    }

    private void MoveUpWadPath_Click(object? sender, RoutedEventArgs e)
    {
        var idx = WadPathsListBox.SelectedIndex;
        // Can only move up if index > 1 (can't move to or past index 0)
        if (idx > 1)
        {
            var item = _wadPaths[idx];
            _wadPaths.RemoveAt(idx);
            _wadPaths.Insert(idx - 1, item);
            WadPathsListBox.SelectedIndex = idx - 1;
        }
    }

    private void MoveDownWadPath_Click(object? sender, RoutedEventArgs e)
    {
        var idx = WadPathsListBox.SelectedIndex;
        // Can only move down if index > 0 and not at end
        if (idx > 0 && idx < _wadPaths.Count - 1)
        {
            var item = _wadPaths[idx];
            _wadPaths.RemoveAt(idx);
            _wadPaths.Insert(idx + 1, item);
            WadPathsListBox.SelectedIndex = idx + 1;
        }
    }

    private void SetAsDownload_Click(object? sender, RoutedEventArgs e)
    {
        var index = WadPathsListBox.SelectedIndex;
        if (index <= 0) return;
        
        var selectedPath = _wadPaths[index];
        if (string.IsNullOrEmpty(selectedPath)) return;
        
        // Get the current download path before changing
        var oldDownloadPath = WadDownloadPathTextBox.Text?.Trim() ?? "";
        
        // Remove the selected path from its current position (it will become the download folder)
        _wadPaths.RemoveAt(index);
        
        // Set as new download path (this will update item 0 via TextChanged event)
        WadDownloadPathTextBox.Text = selectedPath;
        _lastValidDownloadPath = selectedPath;
        
        // Add the old download folder as a regular search path if valid
        if (!string.IsNullOrEmpty(oldDownloadPath) && 
            Directory.Exists(oldDownloadPath) &&
            !_wadPaths.Skip(1).Any(p => p.Equals(oldDownloadPath, StringComparison.OrdinalIgnoreCase)))
        {
            _wadPaths.Insert(1, oldDownloadPath);
        }
    }

    private async void BrowseDownloadPath_Click(object? sender, RoutedEventArgs e)
    {
        // Get the current download path before browsing
        var oldDownloadPath = WadDownloadPathTextBox.Text?.Trim() ?? "";
        
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select WAD Download Folder",
            AllowMultiple = false
        });
        if (folder.Count > 0)
        {
            var newPath = folder[0].Path.LocalPath;
            WadDownloadPathTextBox.Text = newPath;
            
            // Preserve old download path as a search path if valid and different
            if (!string.IsNullOrEmpty(oldDownloadPath) && 
                Directory.Exists(oldDownloadPath) &&
                !oldDownloadPath.Equals(newPath, StringComparison.OrdinalIgnoreCase) &&
                !_wadPaths.Skip(1).Any(p => p.Equals(oldDownloadPath, StringComparison.OrdinalIgnoreCase)))
            {
                _wadPaths.Insert(1, oldDownloadPath);
            }
            _lastValidDownloadPath = newPath;
        }
    }

    private void AddDefaultSites_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var site in WadDownloader.DefaultSites)
        {
            if (!_downloadSites.Contains(site))
            {
                _downloadSites.Add(site);
            }
        }
    }

    private static string NormalizeOptionalPwadName(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return string.Empty;

        return Path.GetFileName(trimmed);
    }

    private void UpdateSkippedOptionalPwadButtons()
    {
        var hasSelection = SkippedOptionalPwadsListBox.SelectedItem is string;
        UpdateSkippedOptionalPwadButton.IsEnabled = hasSelection;
        RemoveSkippedOptionalPwadButton.IsEnabled = hasSelection;
    }

    private void SkippedOptionalPwadsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SkippedOptionalPwadsListBox.SelectedItem is string selected)
        {
            SkippedOptionalPwadTextBox.Text = selected;
        }

        UpdateSkippedOptionalPwadButtons();
    }

    private void AddSkippedOptionalPwad_Click(object? sender, RoutedEventArgs e)
    {
        var name = NormalizeOptionalPwadName(SkippedOptionalPwadTextBox.Text);
        if (string.IsNullOrEmpty(name))
            return;

        var existing = _skippedOptionalPwads.FirstOrDefault(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SkippedOptionalPwadsListBox.SelectedItem = existing;
            return;
        }

        _skippedOptionalPwads.Add(name);
        SkippedOptionalPwadTextBox.Text = string.Empty;
        SkippedOptionalPwadsListBox.SelectedItem = name;
    }

    private void UpdateSkippedOptionalPwad_Click(object? sender, RoutedEventArgs e)
    {
        if (SkippedOptionalPwadsListBox.SelectedItem is not string selected)
            return;

        var updatedName = NormalizeOptionalPwadName(SkippedOptionalPwadTextBox.Text);
        if (string.IsNullOrEmpty(updatedName))
            return;

        var duplicate = _skippedOptionalPwads.FirstOrDefault(item =>
            !item.Equals(selected, StringComparison.OrdinalIgnoreCase) &&
            item.Equals(updatedName, StringComparison.OrdinalIgnoreCase));
        if (duplicate != null)
        {
            SkippedOptionalPwadsListBox.SelectedItem = duplicate;
            return;
        }

        var index = _skippedOptionalPwads.IndexOf(selected);
        if (index < 0)
            return;

        _skippedOptionalPwads[index] = updatedName;
        SkippedOptionalPwadsListBox.SelectedItem = updatedName;
    }

    private void RemoveSkippedOptionalPwad_Click(object? sender, RoutedEventArgs e)
    {
        if (SkippedOptionalPwadsListBox.SelectedItem is not string selected)
            return;

        _skippedOptionalPwads.Remove(selected);
        SkippedOptionalPwadsListBox.SelectedIndex = -1;
        SkippedOptionalPwadTextBox.Text = string.Empty;
        UpdateSkippedOptionalPwadButtons();
    }

    private void RemoveDownloadSite_Click(object? sender, RoutedEventArgs e)
    {
        if (DownloadSitesListBox.SelectedIndex >= 0)
        {
            _downloadSites.RemoveAt(DownloadSitesListBox.SelectedIndex);
        }
    }

    private void MoveUpSite_Click(object? sender, RoutedEventArgs e)
    {
        var idx = DownloadSitesListBox.SelectedIndex;
        if (idx > 0)
        {
            var item = _downloadSites[idx];
            _downloadSites.RemoveAt(idx);
            _downloadSites.Insert(idx - 1, item);
            DownloadSitesListBox.SelectedIndex = idx - 1;
        }
    }

    private void MoveDownSite_Click(object? sender, RoutedEventArgs e)
    {
        var idx = DownloadSitesListBox.SelectedIndex;
        if (idx >= 0 && idx < _downloadSites.Count - 1)
        {
            var item = _downloadSites[idx];
            _downloadSites.RemoveAt(idx);
            _downloadSites.Insert(idx + 1, item);
            DownloadSitesListBox.SelectedIndex = idx + 1;
        }
    }

    private void AddSite_Click(object? sender, RoutedEventArgs e)
    {
        var url = NewSiteTextBox.Text?.Trim();
        if (!string.IsNullOrEmpty(url))
        {
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;
            if (!_downloadSites.Contains(url))
            {
                _downloadSites.Add(url);
                NewSiteTextBox.Text = "";
            }
        }
    }

    private void AddDomain_Click(object? sender, RoutedEventArgs e)
    {
        var display = new DomainThreadDisplay();
        display.InitializeFromSettings(
            "example.com",
            0,       // MaxThreads: 0 = use global default
            256,     // MinSegmentSizeKb
            true,    // AdaptiveLearning
            _domainConfigs.Count  // Index
        );
        _domainConfigs.Add(display);
    }

    private void RemoveDomain_Click(object? sender, RoutedEventArgs e)
    {
        if (DomainListView.SelectedItem is DomainThreadDisplay selected)
        {
            _domainConfigs.Remove(selected);
            DomainListView.ClearSelection();
            // Update indices
            for (int i = 0; i < _domainConfigs.Count; i++)
            {
                _domainConfigs[i].Index = i;
            }
        }
    }

    private void ResetDomains_Click(object? sender, RoutedEventArgs e)
    {
        _domainConfigs.Clear();
        DomainListView.ClearSelection();
    }

    private void DomainListView_RowGotFocus(object? sender, ListViewRowEventArgs e)
    {
        // When any child control (TextBox, CheckBox) gets focus, select the row
        if (e.DataContext is DomainThreadDisplay domain)
        {
            DomainListView.SelectItem(domain);
        }
    }

    private async void CheckNowButton_Click(object? sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button != null)
            button.IsEnabled = false;
        
        try
        {
            var updateService = UpdateService.Instance;
            var hasUpdate = await updateService.CheckForUpdatesAsync();
            
            if (hasUpdate)
            {
                await ShowMessageDialogAsync(
                    "Update Available",
                    $"A new version ({updateService.LatestVersion}) is available.\n\nCurrent version: {updateService.CurrentVersion}\n\nCheck the main window for update options.");
            }
            else
            {
                await ShowMessageDialogAsync(
                    "No Updates",
                    $"You are running the latest version ({updateService.CurrentVersion}).");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Update check failed: {ex.Message}");
            await ShowMessageDialogAsync("Update Check Failed", $"Could not check for updates: {ex.Message}");
        }
        finally
        {
            if (button != null)
                button.IsEnabled = true;
        }
    }
    
    private async Task ShowMessageDialogAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 15,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button { Content = "OK", Width = 80, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center }
                }
            }
        };
        
        if (dialog.Content is StackPanel sp && sp.Children.LastOrDefault() is Button okBtn)
        {
            okBtn.Click += (_, _) => dialog.Close();
        }
        
        await dialog.ShowDialog(this);
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private class DomainThreadDisplay : System.ComponentModel.INotifyPropertyChanged
    {
        private string _domain = "";
        private int _maxThreads;
        private int _minSegmentSizeKb;
        private bool _adaptiveLearning;
        private int _index;
        
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        
        /// <summary>
        /// Initializes all fields from settings without triggering property setters' side effects.
        /// </summary>
        public void InitializeFromSettings(string domain, int maxThreads,
            int minSegmentSizeKb, bool adaptiveLearning, int index)
        {
            _domain = domain;
            _maxThreads = maxThreads;
            _minSegmentSizeKb = minSegmentSizeKb;
            _adaptiveLearning = adaptiveLearning;
            _index = index;
        }
        
        public string Domain
        {
            get => _domain;
            set { _domain = value; OnPropertyChanged(nameof(Domain)); }
        }
        
        /// <summary>Max threads per file. 0 = use global default.</summary>
        public int MaxThreads
        {
            get => _maxThreads;
            set { _maxThreads = Math.Max(0, value); OnPropertyChanged(nameof(MaxThreads)); }
        }
        
        public int MinSegmentSizeKb
        {
            get => _minSegmentSizeKb;
            set { _minSegmentSizeKb = Math.Clamp(value, 64, 4096); OnPropertyChanged(nameof(MinSegmentSizeKb)); }
        }
        
        public bool AdaptiveLearning
        {
            get => _adaptiveLearning;
            set { _adaptiveLearning = value; OnPropertyChanged(nameof(AdaptiveLearning)); }
        }
        
        public int Index
        {
            get => _index;
            set { _index = value; OnPropertyChanged(nameof(Index)); }
        }
        
        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }
}

public enum UpdateBehaviorMode
{
    Disabled = 0,
    NotifyOnly = 1,
    Download = 2
}
