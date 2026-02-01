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
    private AppSettings _settings;
    private bool _isLoading;

    public AppSettings Settings => _settings;

    public event EventHandler? SettingsChanged;

    private SettingsService()
    {
        // Save settings in the same directory as the executable (portable mode)
        var exeDirectory = AppContext.BaseDirectory;
        _settingsPath = Path.Combine(exeDirectory, "settings.json");
        _settings = new AppSettings();
        
        Load();
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
        
        // Find existing entry
        var existing = _settings.ConnectionHistory.FirstOrDefault(
            h => h.FullAddress.Equals(fullAddress, StringComparison.OrdinalIgnoreCase));
        
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
            _settings.ConnectionHistory.Insert(0, new ConnectionHistoryEntry
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
        _settings.ConnectionHistory = _settings.ConnectionHistory
            .OrderByDescending(h => h.LastConnected)
            .Take(_settings.MaxHistoryEntries)
            .ToList();
        
        Save();
    }
    
    /// <summary>
    /// Clears all connection history.
    /// </summary>
    public void ClearConnectionHistory()
    {
        _settings.ConnectionHistory.Clear();
        Save();
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
    public bool ShowLogPanel { get; set; } = true;

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
    
    /// <summary>Default max threads for new/unknown domains.</summary>
    public int DefaultMaxThreads { get; set; } = 32;
    
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
    
    /// <summary>
    /// Domain-specific thread settings for downloads.
    /// Tracks optimal thread counts per domain.
    /// </summary>
    public Dictionary<string, DomainSettings> DomainThreadSettings { get; set; } = new();
    
    // Favorites system
    /// <summary>Set of favorite server addresses (IP:Port format).</summary>
    public HashSet<string> FavoriteServers { get; set; } = [];
    
    /// <summary>Manually added server addresses for servers not from master list.</summary>
    public List<ManualServerEntry> ManualServers { get; set; } = [];
    
    /// <summary>Whether to show the favorites column in the server list.</summary>
    public bool ShowFavoritesColumn { get; set; } = true;
    
    /// <summary>Whether to show only favorite servers.</summary>
    public bool ShowFavoritesOnly { get; set; }
    
    // Server Alerts
    /// <summary>Enable alerts when favorite servers come online with players.</summary>
    public bool EnableFavoriteServerAlerts { get; set; }
    
    /// <summary>Enable alerts when manually added servers come online with players.</summary>
    public bool EnableManualServerAlerts { get; set; }
    
    /// <summary>Minimum number of players to trigger an alert (0 = any players).</summary>
    public int AlertMinPlayers { get; set; } = 1;
    
    /// <summary>Interval in seconds between alert checks when window is not focused.</summary>
    public int AlertCheckIntervalSeconds { get; set; } = 60;
    
    // Connection History
    /// <summary>Recent server connection history.</summary>
    public List<ConnectionHistoryEntry> ConnectionHistory { get; set; } = [];
    
    /// <summary>Maximum number of history entries to keep.</summary>
    public int MaxHistoryEntries { get; set; } = 50;
    
    // Screenshot consolidation
    /// <summary>Enable automatic screenshot consolidation from testing versions.</summary>
    public bool EnableScreenshotMonitoring { get; set; }
    
    /// <summary>
    /// Path where screenshots should be consolidated.
    /// If empty, defaults to "Screenshots" folder in the Zandronum root directory.
    /// </summary>
    public string ScreenshotConsolidationPath { get; set; } = string.Empty;
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
