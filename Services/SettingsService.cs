using System.Text.Json;
using System.Text.Json.Serialization;
using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.Services;

/// <summary>
/// Manages application settings with automatic persistence.
/// </summary>
public class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly string _settingsPath;
    private readonly string _historyPath;
    private readonly string _domainSettingsPath;
    private AppSettings _settings;
    private ConnectionHistoryData _historyData;
    private DomainSettingsData _domainSettingsData;
    private bool _isLoading;

    public AppSettings Settings => _settings;
    
    /// <summary>Connection history entries.</summary>
    public List<ConnectionHistoryEntry> ConnectionHistory => _historyData.Entries;
    
    /// <summary>Domain-specific thread settings for downloads.</summary>
    public Dictionary<string, DomainSettings> DomainThreadSettings => _domainSettingsData.Domains;
    
    /// <summary>Returns true if the settings file exists on disk.</summary>
    public bool SettingsFileExists => File.Exists(_settingsPath);

    public event EventHandler? SettingsChanged;

    private SettingsService()
    {
        // Save settings in the same directory as the executable (portable mode)
        var exeDirectory = AppContext.BaseDirectory;
        _settingsPath = Path.Combine(exeDirectory, "settings.json");
        _historyPath = Path.Combine(exeDirectory, "history.json");
        _domainSettingsPath = Path.Combine(exeDirectory, "domain-settings.json");
        _settings = new AppSettings();
        _historyData = new ConnectionHistoryData();
        _domainSettingsData = new DomainSettingsData();
        
        Load();
        LoadHistory();
        LoadDomainSettings();
    }

    public void Load()
    {
        _isLoading = true;
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonUtils.DefaultOptions);
                if (loaded != null)
                {
                    _settings = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to load settings: {ex.Message}");
        }
        finally
        {
            _isLoading = false;
        }
    }
    
    /// <summary>
    /// Loads connection history from history.json.
    /// Migrates from settings.json if history.json doesn't exist.
    /// </summary>
    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                var json = File.ReadAllText(_historyPath);
                var loaded = JsonSerializer.Deserialize<ConnectionHistoryData>(json, JsonUtils.DefaultOptions);
                if (loaded != null)
                {
                    _historyData = loaded;
                }
            }
            else
            {
                // Migration: check if settings.json has legacy connectionHistory
                MigrateLegacyHistory();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to load history: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Migrates connection history from legacy settings.json format.
    /// </summary>
    private void MigrateLegacyHistory()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            
            var json = File.ReadAllText(_settingsPath);
            using var doc = JsonDocument.Parse(json);
            
            if (doc.RootElement.TryGetProperty("connectionHistory", out var historyElement))
            {
                var entries = JsonSerializer.Deserialize<List<ConnectionHistoryEntry>>(
                    historyElement.GetRawText(), JsonUtils.DefaultOptions);
                
                if (entries != null && entries.Count > 0)
                {
                    _historyData.Entries = entries;
                    SaveHistory();
                    LoggingService.Instance.Info($"Migrated {entries.Count} history entries to history.json");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to migrate legacy history: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Loads domain settings from domain-settings.json.
    /// Migrates from settings.json if domain-settings.json doesn't exist.
    /// </summary>
    private void LoadDomainSettings()
    {
        try
        {
            if (File.Exists(_domainSettingsPath))
            {
                var json = File.ReadAllText(_domainSettingsPath);
                var loaded = JsonSerializer.Deserialize<DomainSettingsData>(json, JsonUtils.DefaultOptions);
                if (loaded != null)
                {
                    _domainSettingsData = loaded;
                }
            }
            else
            {
                // Migration: check if settings.json has legacy domainThreadSettings
                MigrateLegacyDomainSettings();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to load domain settings: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Migrates domain settings from legacy settings.json format.
    /// </summary>
    private void MigrateLegacyDomainSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            
            var json = File.ReadAllText(_settingsPath);
            using var doc = JsonDocument.Parse(json);
            
            if (doc.RootElement.TryGetProperty("domainThreadSettings", out var domainElement))
            {
                var domains = JsonSerializer.Deserialize<Dictionary<string, DomainSettings>>(
                    domainElement.GetRawText(), JsonUtils.DefaultOptions);
                
                if (domains != null && domains.Count > 0)
                {
                    _domainSettingsData.Domains = domains;
                    SaveDomainSettings();
                    LoggingService.Instance.Info($"Migrated {domains.Count} domain settings to domain-settings.json");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to migrate legacy domain settings: {ex.Message}");
        }
    }

    public void Save()
    {
        if (_isLoading) return;

        try
        {
            var json = JsonSerializer.Serialize(_settings, JsonUtils.DefaultOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to save settings: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Saves connection history to history.json.
    /// </summary>
    public void SaveHistory()
    {
        try
        {
            var json = JsonSerializer.Serialize(_historyData, JsonUtils.DefaultOptions);
            File.WriteAllText(_historyPath, json);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to save history: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Saves domain settings to domain-settings.json.
    /// </summary>
    public void SaveDomainSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_domainSettingsData, JsonUtils.DefaultOptions);
            File.WriteAllText(_domainSettingsPath, json);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to save domain settings: {ex.Message}");
        }
    }

    public void NotifySettingChanged()
    {
        Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
    
    /// <summary>
    /// Records a server connection in the history.
    /// Updates existing entry if found, or adds a new one.
    /// </summary>
    public void RecordConnection(string address, int port, string serverName, string? iwad = null, string? gameMode = null)
    {
        var fullAddress = $"{address}:{port}";
        
        // Find existing entry based on tracking mode
        ConnectionHistoryEntry? existing = _settings.HistoryTrackingMode switch
        {
            HistoryTrackingMode.ByServerName => _historyData.Entries.FirstOrDefault(
                h => h.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase)),
            HistoryTrackingMode.Both => _historyData.Entries.FirstOrDefault(
                h => h.FullAddress.Equals(fullAddress, StringComparison.OrdinalIgnoreCase) &&
                     h.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase)),
            _ => _historyData.Entries.FirstOrDefault(
                h => h.FullAddress.Equals(fullAddress, StringComparison.OrdinalIgnoreCase))
        };
        
        if (existing != null)
        {
            // Update existing entry
            existing.LastConnected = DateTime.UtcNow;
            existing.ConnectionCount++;
            existing.ServerName = serverName; // Update name in case it changed
            existing.IWAD = iwad;
            existing.GameMode = gameMode;
        }
        else
        {
            // Add new entry
            _historyData.Entries.Insert(0, new ConnectionHistoryEntry
            {
                Address = address,
                Port = port,
                ServerName = serverName,
                LastConnected = DateTime.UtcNow,
                ConnectionCount = 1,
                IWAD = iwad,
                GameMode = gameMode
            });
        }
        
        // Sort by most recent first
        _historyData.Entries = _historyData.Entries
            .OrderByDescending(h => h.LastConnected)
            .Take(_settings.MaxHistoryEntries)
            .ToList();
        
        SaveHistory();
    }
    
    /// <summary>
    /// Clears all connection history.
    /// </summary>
    public void ClearConnectionHistory()
    {
        _historyData.Entries.Clear();
        SaveHistory();
    }
}

/// <summary>
/// Application settings that are persisted to disk.
/// </summary>
public class AppSettings
{
    // Window state
    public int WindowX { get; set; } = 100;
    public int WindowY { get; set; } = 100;
    public int WindowWidth { get; set; } = 1200;
    public int WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }

    // Column widths
    public Dictionary<string, int> ColumnWidths { get; set; } = new();

    // Sorting
    public int SortColumnIndex { get; set; } = 3; // Default: sort by Players (column 3)
    public bool SortAscending { get; set; } = false; // Default descending (most players first)

    // Filters
    public bool HideEmpty { get; set; }
    public bool TreatBotOnlyAsEmpty { get; set; }
    public bool HideFull { get; set; }
    public bool HidePassworded { get; set; }
    public int GameModeFilterIndex { get; set; }
    public string SearchText { get; set; } = string.Empty;

    // View options
    public bool VerboseMode { get; set; }
    public bool ShowHexDumps { get; set; }
    public bool ShowLogPanel { get; set; }
    public bool VerboseLogging { get; set; }
    public bool ColorizePlayerNames { get; set; } = true;

    // Behavior options
    public bool RefreshOnLaunch { get; set; } = true;
    public bool AutoRefresh { get; set; }
    public int AutoRefreshIntervalMinutes { get; set; } = 5;
    public bool AutoRefreshFavoritesOnly { get; set; }

    // Panel sizes (splitter positions)
    public int MainSplitterDistance { get; set; } = 400;
    public int DetailsSplitterDistance { get; set; } = 400;
    public int LogSplitterDistance { get; set; } = 150;

    // Advanced filter
    public ServerFilter CurrentFilter { get; set; } = new();
    public List<ServerFilter> FilterPresets { get; set; } = [];

    // WAD settings
    public List<string> WadSearchPaths { get; set; } = [];
    public string WadDownloadPath { get; set; } = string.Empty;
    public List<string> DownloadSites { get; set; } = [];
    
    /// <summary>
    /// Number of concurrent hash verifications when joining a server.
    /// 0 = unlimited (all files at once), 1 = sequential, N = max N concurrent.
    /// </summary>
    public int HashVerificationConcurrency { get; set; } = 0; // Default: unlimited
    
    // Download concurrency settings
    /// <summary>Maximum concurrent file downloads in total. 0 = unlimited.</summary>
    public int MaxConcurrentDownloads { get; set; } = 0;
    
    /// <summary>Maximum domains to download from simultaneously. 0 = unlimited.</summary>
    public int MaxConcurrentDomains { get; set; } = 8;
    
    /// <summary>Global maximum threads per file. 0 = no global limit (use per-domain settings).</summary>
    public int MaxThreadsPerFile { get; set; } = 0;
    
    /// <summary>Default initial threads for probing new domains.</summary>
    public int DefaultInitialThreads { get; set; } = 2;
    
    /// <summary>Default minimum segment size in KB for new domains.</summary>
    public int DefaultMinSegmentSizeKb { get; set; } = 256;
    
    // Zandronum executable paths
    public string ZandronumPath { get; set; } = string.Empty;
    public string ZandronumTestingPath { get; set; } = string.Empty;
    
    // Server query settings
    /// <summary>Interval in milliseconds between sending server queries. Lower = faster but more aggressive.</summary>
    public int QueryIntervalMs { get; set; } = 5;
    
    /// <summary>Maximum concurrent server queries. 0 = unlimited.</summary>
    public int MaxConcurrentQueries { get; set; } = 50;
    
    /// <summary>Number of retry attempts for failed server queries.</summary>
    public int QueryRetryAttempts { get; set; } = 2;
    
    /// <summary>Delay in milliseconds between retry attempts.</summary>
    public int QueryRetryDelayMs { get; set; } = 2000;
    
    /// <summary>Number of retry attempts for master server queries.</summary>
    public int MasterServerRetryCount { get; set; } = 3;
    
    /// <summary>Number of consecutive failures before marking a server as offline.</summary>
    public int ConsecutiveFailuresBeforeOffline { get; set; } = 3;
    
    // Favorites system
    /// <summary>Set of favorite server addresses (IP:Port format).</summary>
    public HashSet<string> FavoriteServers { get; set; } = [];
    
    /// <summary>Manually added server addresses for servers not from master list.</summary>
    public List<ManualServerEntry> ManualServers { get; set; } = [];
    
    /// <summary>Whether to show the favorites column in the server list.</summary>
    public bool ShowFavoritesColumn { get; set; } = true;
    
    /// <summary>Whether to show only favorite servers.</summary>
    public bool ShowFavoritesOnly { get; set; }
    
    /// <summary>Row height in pixels for the server list. Range: 18-60.</summary>
    public int ServerListRowHeight { get; set; } = 26;
    
    // Server Alerts
    /// <summary>Enable alerts when favorite servers come online with players.</summary>
    public bool EnableFavoriteServerAlerts { get; set; }
    
    /// <summary>Enable alerts when manually added servers come online with players.</summary>
    public bool EnableManualServerAlerts { get; set; }
    
    /// <summary>Minimum number of players to trigger an alert (0 = any players).</summary>
    public int AlertMinPlayers { get; set; } = 1;
    
    /// <summary>Interval in seconds between alert checks when window is not focused.</summary>
    public int AlertCheckIntervalSeconds { get; set; } = 60;
    
    // Connection History (settings only - actual history is in history.json)
    /// <summary>Maximum number of history entries to keep.</summary>
    public int MaxHistoryEntries { get; set; } = 50;
    
    /// <summary>How to identify unique servers in history: by address or server name.</summary>
    public HistoryTrackingMode HistoryTrackingMode { get; set; } = HistoryTrackingMode.ByAddress;
    
    // Download Dialog
    /// <summary>Behavior of the WAD download dialog after downloads complete.</summary>
    public DownloadDialogBehavior DownloadDialogBehavior { get; set; } = DownloadDialogBehavior.CloseOnSuccess;
    
    // Screenshot consolidation
    /// <summary>Enable automatic screenshot consolidation from testing versions.</summary>
    public bool EnableScreenshotMonitoring { get; set; }
    
    /// <summary>
    /// Path where screenshots should be consolidated.
    /// If empty, defaults to "Screenshots" folder in the Zandronum root directory.
    /// </summary>
    public string ScreenshotConsolidationPath { get; set; } = string.Empty;
    
    // Update settings
    /// <summary>Update behavior mode.</summary>
    public UpdateBehavior UpdateBehavior { get; set; } = UpdateBehavior.CheckAndDownload;
    
    /// <summary>Automatically restart to install updates (only when safe).</summary>
    public bool AutoRestartForUpdates { get; set; } = false;
    
    /// <summary>Update check interval value.</summary>
    public int UpdateCheckIntervalValue { get; set; } = 1;
    
    /// <summary>Unit for update check interval.</summary>
    public UpdateIntervalUnit UpdateCheckIntervalUnit { get; set; } = UpdateIntervalUnit.Days;
    
    /// <summary>Gets the update check interval in hours.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int UpdateCheckIntervalHours => UpdateCheckIntervalUnit switch
    {
        UpdateIntervalUnit.Hours => UpdateCheckIntervalValue,
        UpdateIntervalUnit.Days => UpdateCheckIntervalValue * 24,
        UpdateIntervalUnit.Weeks => UpdateCheckIntervalValue * 24 * 7,
        _ => 24
    };
    
    /// <summary>Last time an update check was performed.</summary>
    public DateTime LastUpdateCheck { get; set; } = DateTime.MinValue;
    
    /// <summary>GitHub repository owner for update checks.</summary>
    public string GitHubOwner { get; set; } = "RaymondTracer";
    
    /// <summary>GitHub repository name for update checks.</summary>
    public string GitHubRepo { get; set; } = "ZScape";
}

/// <summary>
/// Unit for update check intervals.
/// </summary>
public enum UpdateIntervalUnit
{
    Hours = 0,
    Days = 1,
    Weeks = 2
}

/// <summary>
/// Defines how the application handles updates.
/// </summary>
public enum UpdateBehavior
{
    /// <summary>Never check for updates.</summary>
    Disabled = 0,
    
    /// <summary>Check for updates and notify, but don't download automatically.</summary>
    CheckOnly = 1,
    
    /// <summary>Check and download updates, prompt user to install.</summary>
    CheckAndDownload = 2
}

/// <summary>
/// Represents a manually added server entry.
/// </summary>
public class ManualServerEntry
{
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } = 10666;
    public string? CustomName { get; set; }
    public bool IsFavorite { get; set; } = true;
    
    public string FullAddress => $"{Address}:{Port}";
}

/// <summary>
/// Represents a server connection history entry.
/// </summary>
public class ConnectionHistoryEntry
{
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } = 10666;
    public string ServerName { get; set; } = string.Empty;
    public DateTime LastConnected { get; set; } = DateTime.UtcNow;
    public int ConnectionCount { get; set; } = 1;
    public string? IWAD { get; set; }
    public string? GameMode { get; set; }
    
    public string FullAddress => $"{Address}:{Port}";
}

/// <summary>
/// Container for connection history stored in history.json.
/// </summary>
public class ConnectionHistoryData
{
    public List<ConnectionHistoryEntry> Entries { get; set; } = [];
}

/// <summary>
/// Container for domain settings stored in domain-settings.json.
/// </summary>
public class DomainSettingsData
{
    public Dictionary<string, DomainSettings> Domains { get; set; } = new();
}

/// <summary>
/// Defines how connection history identifies unique servers.
/// </summary>
public enum HistoryTrackingMode
{
    /// <summary>Track by server IP:Port address. Different addresses = different entries.</summary>
    ByAddress = 0,
    
    /// <summary>Track by server name. Same name = same entry even if address changes.</summary>
    ByServerName = 1,
    
    /// <summary>Track by both address and name. Records exact entries without merging.</summary>
    Both = 2
}

/// <summary>
/// Defines how the WAD download dialog behaves after downloads complete.
/// </summary>
public enum DownloadDialogBehavior
{
    /// <summary>Always stay open until user closes manually.</summary>
    StayOpen = 0,
    
    /// <summary>Auto-close only if all downloads succeed.</summary>
    CloseOnSuccess = 1,
    
    /// <summary>Auto-close on success only if the application window is focused.</summary>
    CloseOnSuccessIfFocused = 2,
    
    /// <summary>Auto-close on success after a brief delay to show results.</summary>
    CloseOnSuccessAfterDelay = 3,
    
    /// <summary>Always auto-close regardless of success or failure.</summary>
    AlwaysClose = 4
}
