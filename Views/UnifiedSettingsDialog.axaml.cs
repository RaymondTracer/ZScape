using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.Views;

public partial class UnifiedSettingsDialog : Window
{
    private const string DownloadFolderPrefix = "[Download Folder] ";
    
    private readonly SettingsService _settingsService;
    private AppSettings Settings => _settingsService.Settings;
    private ObservableCollection<string> _favorites = new();
    private ObservableCollection<string> _manualServers = new();
    private ObservableCollection<string> _wadPaths = new();
    private ObservableCollection<string> _downloadSites = new();
    private ObservableCollection<DomainThreadDisplay> _domainConfigs = new();
    private DomainThreadDisplay? _selectedDomain;
    private string _lastValidDownloadPath = string.Empty;
    private bool _updatingIntervalControls;

    public bool SettingsChanged { get; private set; }
    public event EventHandler<int>? RowHeightPreviewChanged;

    public UnifiedSettingsDialog()
    {
        InitializeComponent();
        _settingsService = SettingsService.Instance;
        
        CategoryList.SelectionChanged += CategoryList_SelectionChanged;
        WadPathsListBox.SelectionChanged += WadPathsListBox_SelectionChanged;
        WadDownloadPathTextBox.TextChanged += WadDownloadPathTextBox_TextChanged;
        ZandronumPathTextBox.TextChanged += ZandronumPathTextBox_TextChanged;
        
        // Live preview for row height changes
        RowHeightNumeric.ValueChanged += (_, newValue) => RowHeightPreviewChanged?.Invoke(this, newValue);
        
        // Sync update interval preset and manual controls
        UpdateIntervalPresets.SelectionChanged += UpdateIntervalPresets_SelectionChanged;
        UpdateCheckIntervalValue.ValueChanged += (_, _) => UpdateInterval_ManualChanged();
        UpdateCheckIntervalUnit.SelectionChanged += (_, _) => UpdateInterval_ManualChanged();
        
        // Handle Escape/Enter keys
        KeyDown += OnDialogKeyDown;
        
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
        _manualServers = new ObservableCollection<string>(Settings.ManualServers.Select(m => m.FullAddress));
        ManualServersListBox.ItemsSource = _manualServers;
        
        EnableFavoriteAlertsCheckBox.IsChecked = Settings.EnableFavoriteServerAlerts;
        EnableManualAlertsCheckBox.IsChecked = Settings.EnableManualServerAlerts;
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
        MaxConcurrentDownloadsNumeric.Value = Settings.MaxConcurrentDownloads;
        MaxConcurrentDomainsNumeric.Value = Settings.MaxConcurrentDomains;
        MaxThreadsPerFileNumeric.Value = Settings.MaxThreadsPerFile;
        DefaultMinSegmentKbNumeric.Value = Settings.DefaultMinSegmentSizeKb;

        // Domain Threads
        LoadDomainConfigs();

        // Server Queries
        QueryIntervalMsNumeric.Value = Settings.QueryIntervalMs;
        MaxConcurrentQueriesNumeric.Value = Settings.MaxConcurrentQueries;
        QueryRetryAttemptsNumeric.Value = Settings.QueryRetryAttempts;
        QueryRetryDelayMsNumeric.Value = Settings.QueryRetryDelayMs;
        MasterServerRetryCountNumeric.Value = Settings.MasterServerRetryCount;
        ConsecutiveFailuresNumeric.Value = Settings.ConsecutiveFailuresBeforeOffline;
        AutoRefreshIntervalNumeric.Value = Settings.AutoRefreshIntervalMinutes;
        AutoRefreshFavoritesOnlyCheckBox.IsChecked = Settings.AutoRefreshFavoritesOnly;

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

    private void LoadDomainConfigs()
    {
        _domainConfigs.Clear();
        foreach (var config in SettingsService.Instance.DomainThreadSettings.OrderBy(k => k.Key))
        {
            var display = new DomainThreadDisplay();
            display.InitializeFromSettings(
                config.Key,
                config.Value.MaxThreads,
                config.Value.MaxConcurrentDownloads,
                config.Value.InitialThreads,
                config.Value.MinSegmentSizeKb,
                config.Value.AdaptiveLearning,
                config.Value.SuccessCount,
                config.Value.FailureCount,
                config.Value.Notes ?? "",
                _domainConfigs.Count
            );
            _domainConfigs.Add(display);
        }
        DomainListControl.ItemsSource = _domainConfigs;
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
        Settings.ManualServers = _manualServers.Select(addr => {
            var parts = addr.Split(':');
            return new ManualServerEntry {
                Address = parts[0],
                Port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 10666
            };
        }).ToList();
        Settings.EnableFavoriteServerAlerts = EnableFavoriteAlertsCheckBox.IsChecked ?? false;
        Settings.EnableManualServerAlerts = EnableManualAlertsCheckBox.IsChecked ?? false;
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
        Settings.AutoRefreshFavoritesOnly = AutoRefreshFavoritesOnlyCheckBox.IsChecked ?? false;

        // Updates
        Settings.UpdateBehavior = (UpdateBehavior)(UpdateBehaviorComboBox.SelectedIndex);
        Settings.AutoRestartForUpdates = AutoRestartCheckBox.IsChecked ?? false;
        SaveUpdateInterval();

        _settingsService.Save();
        SettingsChanged = true;
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
                MaxConcurrentDownloads = item.MaxConcurrentDownloads,
                InitialThreads = item.InitialThreads,
                MinSegmentSizeKb = item.MinSegmentSizeKb,
                AdaptiveLearning = item.AdaptiveLearning,
                SuccessCount = item.SuccessCount,
                FailureCount = item.FailureCount,
                Notes = string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes,
                LastUpdated = DateTime.UtcNow
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
            0,       // MaxConcurrentDownloads: 0 = unlimited
            2,       // InitialThreads
            256,     // MinSegmentSizeKb
            true,    // AdaptiveLearning
            0,       // SuccessCount
            0,       // FailureCount
            "",      // Notes
            _domainConfigs.Count  // Index
        );
        _domainConfigs.Add(display);
    }

    private void RemoveDomain_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedDomain != null)
        {
            _domainConfigs.Remove(_selectedDomain);
            _selectedDomain = null;
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
        _selectedDomain = null;
    }

    private void DomainRow_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is DomainThreadDisplay domain)
        {
            SelectDomainRow(domain);
        }
    }

    private void DomainRow_GotFocus(object? sender, Avalonia.Input.GotFocusEventArgs e)
    {
        // When any child control (TextBox, CheckBox) gets focus, select the row
        if (sender is Border border && border.DataContext is DomainThreadDisplay domain)
        {
            SelectDomainRow(domain);
        }
    }

    private void SelectDomainRow(DomainThreadDisplay domain)
    {
        // Deselect previous
        if (_selectedDomain != null)
            _selectedDomain.IsSelected = false;
        
        // Select new
        _selectedDomain = domain;
        _selectedDomain.IsSelected = true;
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
        private static readonly IBrush EvenRowBrush = new SolidColorBrush(Color.Parse("#1E1E1E"));
        private static readonly IBrush OddRowBrush = new SolidColorBrush(Color.Parse("#252526"));
        private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#094771"));
        
        private string _domain = "";
        private int _maxThreads;
        private int _maxConcurrentDownloads;
        private int _initialThreads;
        private int _minSegmentSizeKb;
        private bool _adaptiveLearning;
        private int _successCount;
        private int _failureCount;
        private string _notes = "";
        private int _index;
        private bool _isSelected;
        
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        
        /// <summary>
        /// Initializes all fields from settings without triggering property setters' side effects.
        /// </summary>
        public void InitializeFromSettings(string domain, int maxThreads, int maxConcurrentDownloads,
            int initialThreads, int minSegmentSizeKb, bool adaptiveLearning,
            int successCount, int failureCount, string notes, int index)
        {
            _domain = domain;
            _maxThreads = maxThreads;
            _maxConcurrentDownloads = maxConcurrentDownloads;
            _initialThreads = initialThreads;
            _minSegmentSizeKb = minSegmentSizeKb;
            _adaptiveLearning = adaptiveLearning;
            _successCount = successCount;
            _failureCount = failureCount;
            _notes = notes;
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
        
        /// <summary>Max concurrent downloads from this domain. 0 = unlimited.</summary>
        public int MaxConcurrentDownloads
        {
            get => _maxConcurrentDownloads;
            set { _maxConcurrentDownloads = Math.Max(0, value); OnPropertyChanged(nameof(MaxConcurrentDownloads)); }
        }
        
        public int InitialThreads
        {
            get => _initialThreads;
            set { _initialThreads = Math.Clamp(value, 1, 32); OnPropertyChanged(nameof(InitialThreads)); }
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
        
        public int SuccessCount
        {
            get => _successCount;
            set { _successCount = value; OnPropertyChanged(nameof(SuccessCount)); }
        }
        
        public int FailureCount
        {
            get => _failureCount;
            set { _failureCount = value; OnPropertyChanged(nameof(FailureCount)); }
        }
        
        public string Notes
        {
            get => _notes;
            set { _notes = value ?? ""; OnPropertyChanged(nameof(Notes)); }
        }
        
        public int Index
        {
            get => _index;
            set { _index = value; OnPropertyChanged(nameof(Index)); OnPropertyChanged(nameof(RowBackground)); }
        }
        
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); OnPropertyChanged(nameof(RowBackground)); }
        }
        
        public IBrush RowBackground => IsSelected ? SelectedBrush : (Index % 2 == 0 ? EvenRowBrush : OddRowBrush);
        
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
