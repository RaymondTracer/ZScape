namespace ZScape.Services;

/// <summary>
/// Manages domain-specific thread configuration with persistence.
/// Remembers successful thread counts and automatically dials back when issues occur.
/// Settings are stored in settings.json.
/// </summary>
public class DomainThreadConfig
{
    private static readonly Lazy<DomainThreadConfig> _instance = new(() => new DomainThreadConfig());
    
    public static DomainThreadConfig Instance => _instance.Value;
    
    private readonly object _lock = new();
    private readonly LoggingService _logger = LoggingService.Instance;
    
    /// <summary>
    /// Gets the domain settings dictionary from the main settings.
    /// </summary>
    private Dictionary<string, DomainSettings> DomainSettings => 
        SettingsService.Instance.Settings.DomainThreadSettings;

    private DomainThreadConfig()
    {
        _logger.Verbose($"Domain thread config initialized: {DomainSettings.Count} domains");
    }

    /// <summary>
    /// Gets the configured thread count for a domain, or null if not configured.
    /// </summary>
    public int? GetThreadCount(string domain)
    {
        domain = NormalizeDomain(domain);
        lock (_lock)
        {
            if (DomainSettings.TryGetValue(domain, out var settings))
            {
                return settings.MaxThreads;
            }
            return null;
        }
    }

    /// <summary>
    /// Gets the full settings for a domain, or null if not configured.
    /// </summary>
    public DomainSettings? GetSettings(string domain)
    {
        domain = NormalizeDomain(domain);
        lock (_lock)
        {
            return DomainSettings.TryGetValue(domain, out var settings) ? settings : null;
        }
    }

    /// <summary>
    /// Gets effective thread settings for a domain, applying global defaults where needed.
    /// This is the primary method for determining download thread configuration.
    /// </summary>
    /// <returns>Tuple of (maxThreads, initialThreads, minSegmentSizeKb, shouldProbe, adaptiveLearning)</returns>
    public (int MaxThreads, int InitialThreads, int MinSegmentSizeKb, bool ShouldProbe, bool AdaptiveLearning) 
        GetEffectiveThreadSettings(string domain)
    {
        domain = NormalizeDomain(domain);
        var globalSettings = SettingsService.Instance.Settings;
        
        lock (_lock)
        {
            if (DomainSettings.TryGetValue(domain, out var settings))
            {
                // Domain has been seen before - use its settings
                int maxThreads = settings.MaxThreads > 0 
                    ? settings.MaxThreads 
                    : 32; // Fallback, probing will discover actual limit
                
                // Apply global cap if set
                if (globalSettings.MaxThreadsPerFile > 0)
                    maxThreads = Math.Min(maxThreads, globalSettings.MaxThreadsPerFile);
                
                int initialThreads = settings.InitialThreads > 0 
                    ? settings.InitialThreads 
                    : globalSettings.DefaultInitialThreads;
                    
                int minSegmentSizeKb = settings.MinSegmentSizeKb > 0 
                    ? settings.MinSegmentSizeKb 
                    : globalSettings.DefaultMinSegmentSizeKb;
                
                // Only probe if adaptive learning is enabled AND max threads seems low
                bool shouldProbe = settings.AdaptiveLearning && settings.MaxThreads < 4 && settings.MaxThreads > 0;
                
                return (maxThreads, initialThreads, minSegmentSizeKb, shouldProbe, settings.AdaptiveLearning);
            }
            else
            {
                // New/unknown domain - probing will discover actual limit
                int maxThreads = 32; // Starting point, probing will adjust
                
                // Apply global cap if set
                if (globalSettings.MaxThreadsPerFile > 0)
                    maxThreads = Math.Min(maxThreads, globalSettings.MaxThreadsPerFile);
                
                return (
                    maxThreads,
                    globalSettings.DefaultInitialThreads,
                    globalSettings.DefaultMinSegmentSizeKb,
                    true, // shouldProbe for new domains
                    true  // adaptiveLearning default
                );
            }
        }
    }

    /// <summary>
    /// Updates the thread count for a domain after a successful probe/download.
    /// Only updates if AdaptiveLearning is enabled for the domain.
    /// </summary>
    public void UpdateThreadCount(string domain, int threadCount, bool wasSuccessful)
    {
        domain = NormalizeDomain(domain);
        lock (_lock)
        {
            if (!DomainSettings.TryGetValue(domain, out var settings))
            {
                settings = new DomainSettings { MaxThreads = threadCount };
                DomainSettings[domain] = settings;
            }

            if (wasSuccessful)
            {
                settings.SuccessCount++;
                
                // Only update thread count if adaptive learning is enabled
                if (settings.AdaptiveLearning && threadCount > settings.MaxThreads)
                {
                    _logger.Verbose($"Domain {domain}: Updated thread count from {settings.MaxThreads} to {threadCount}");
                    settings.MaxThreads = threadCount;
                    settings.LastUpdated = DateTime.UtcNow;
                }
            }
            else
            {
                settings.FailureCount++;
            }

            SettingsService.Instance.Save();
        }
    }

    /// <summary>
    /// Reduces the thread count for a domain after encountering issues.
    /// Returns the new reduced thread count. Only reduces if AdaptiveLearning is enabled.
    /// </summary>
    public int ReduceThreadCount(string domain, int currentThreads)
    {
        domain = NormalizeDomain(domain);
        int reducedCount = Math.Max(1, currentThreads / 2);

        lock (_lock)
        {
            if (!DomainSettings.TryGetValue(domain, out var settings))
            {
                settings = new DomainSettings();
                DomainSettings[domain] = settings;
            }

            settings.FailureCount++;
            
            // Only reduce thread count if adaptive learning is enabled
            if (settings.AdaptiveLearning)
            {
                settings.MaxThreads = reducedCount;
                settings.LastUpdated = DateTime.UtcNow;
                settings.Notes = $"Reduced from {currentThreads} due to connection issues";
                _logger.Warning($"Domain {domain}: Reduced threads from {currentThreads} to {reducedCount}");
            }
            else
            {
                // Keep using current max, just log the failure
                reducedCount = settings.MaxThreads;
                _logger.Verbose($"Domain {domain}: Connection issue (adaptive learning disabled, keeping {reducedCount} threads)");
            }
            
            SettingsService.Instance.Save();
        }

        return reducedCount;
    }

    /// <summary>
    /// Gets all configured domains and their settings.
    /// </summary>
    public IReadOnlyDictionary<string, DomainSettings> GetAllSettings()
    {
        lock (_lock)
        {
            return new Dictionary<string, DomainSettings>(DomainSettings);
        }
    }

    private static string NormalizeDomain(string domain)
    {
        if (Uri.TryCreate(domain, UriKind.Absolute, out var uri))
        {
            domain = uri.Host;
        }
        return domain.ToLowerInvariant();
    }
}

/// <summary>
/// Settings for a specific domain.
/// </summary>
public class DomainSettings
{
    /// <summary>Maximum concurrent threads per file for this domain. 0 = use global default.</summary>
    public int MaxThreads { get; set; } = 0;
    
    /// <summary>Maximum concurrent file downloads from this domain. 0 = unlimited.</summary>
    public int MaxConcurrentDownloads { get; set; } = 0;
    
    /// <summary>Initial thread count when probing a new domain.</summary>
    public int InitialThreads { get; set; } = 2;
    
    /// <summary>Minimum bytes per download segment (KB). Smaller = more threads for small files.</summary>
    public int MinSegmentSizeKb { get; set; } = 256;
    
    /// <summary>Enable adaptive learning (probing and auto-backoff on failures).</summary>
    public bool AdaptiveLearning { get; set; } = true;
    
    /// <summary>Whether this domain was manually configured by user.</summary>
    public bool IsUserConfigured { get; set; }
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public string? Notes { get; set; }
}
