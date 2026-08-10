namespace ZScape.Services;

/// <summary>
/// Manages domain-specific thread configuration with persistence.
/// Remembers successful thread counts and automatically dials back when issues occur.
/// Settings are stored in domain-settings.json.
/// </summary>
public class DomainThreadConfig
{
    private static readonly Lazy<DomainThreadConfig> _instance = new(() => new DomainThreadConfig());
    
    public static DomainThreadConfig Instance => _instance.Value;
    
    private readonly object _lock = new();
    private readonly LoggingService _logger = LoggingService.Instance;
    
    /// <summary>
    /// Gets the domain settings dictionary from the settings service.
    /// </summary>
    private Dictionary<string, DomainSettings> DomainSettings => 
        SettingsService.Instance.DomainThreadSettings;

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
    /// Gets effective per-file thread settings for a domain, applying the global
    /// per-file cap. Call <see cref="GetEffectiveDomainBudgetSettings"/> when
    /// scheduling several files that share one domain's connection budget.
    /// </summary>
    /// <returns>Tuple of (maxThreads, minSegmentSizeKb, shouldProbe, adaptiveLearning)</returns>
    public (int MaxThreads, int MinSegmentSizeKb, bool ShouldProbe, bool AdaptiveLearning) 
        GetEffectiveThreadSettings(string domain)
    {
        var globalSettings = SettingsService.Instance.Settings;
        var effective = GetEffectiveDomainBudgetSettings(domain);
        var maxThreads = effective.MaxThreads;
        if (globalSettings.MaxThreadsPerFile > 0)
        {
            maxThreads = Math.Min(
                maxThreads,
                globalSettings.MaxThreadsPerFile);
        }

        return (
            maxThreads,
            effective.MinSegmentSizeKb,
            effective.ShouldProbe,
            effective.AdaptiveLearning);
    }

    /// <summary>
    /// Gets the aggregate connection budget shared by simultaneous downloads
    /// from a domain. Unlike <see cref="GetEffectiveThreadSettings"/>, the
    /// global per-file cap is not applied to this aggregate budget.
    /// </summary>
    public (int MaxThreads, int MinSegmentSizeKb, bool ShouldProbe, bool AdaptiveLearning)
        GetEffectiveDomainBudgetSettings(string domain)
    {
        domain = NormalizeDomain(domain);
        var globalSettings = SettingsService.Instance.Settings;

        lock (_lock)
        {
            if (!DomainSettings.TryGetValue(domain, out var settings))
            {
                return (
                    32,
                    globalSettings.DefaultMinSegmentSizeKb,
                    true,
                    true);
            }

            var maxThreads = settings.MaxThreads > 0
                ? settings.MaxThreads
                : 32;
            var minSegmentSizeKb = settings.MinSegmentSizeKb > 0
                ? settings.MinSegmentSizeKb
                : globalSettings.DefaultMinSegmentSizeKb;
            var shouldProbe =
                settings.AdaptiveLearning
                && settings.MaxThreads is > 0 and < 4;

            return (
                maxThreads,
                minSegmentSizeKb,
                shouldProbe,
                settings.AdaptiveLearning);
        }
    }

    /// <summary>
    /// Updates the thread count for a domain after a successful probe/download.
    /// Only updates if adaptive learning is enabled for the domain.
    /// </summary>
    public void UpdateThreadCount(string domain, int threadCount)
    {
        domain = NormalizeDomain(domain);
        lock (_lock)
        {
            if (!DomainSettings.TryGetValue(domain, out var settings))
            {
                settings = new DomainSettings { MaxThreads = threadCount };
                DomainSettings[domain] = settings;
            }

            if (settings.AdaptiveLearning && threadCount > settings.MaxThreads)
            {
                _logger.Verbose($"Domain {domain}: Updated thread count from {settings.MaxThreads} to {threadCount}");
                settings.MaxThreads = threadCount;
            }

            SettingsService.Instance.SaveDomainSettings();
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
            
            // Only reduce thread count if adaptive learning is enabled
            if (settings.AdaptiveLearning)
            {
                settings.MaxThreads = reducedCount;
                _logger.Warning($"Domain {domain}: Reduced threads from {currentThreads} to {reducedCount}");
            }
            else
            {
                // Keep using current max, just log the failure
                reducedCount = settings.MaxThreads;
                _logger.Verbose($"Domain {domain}: Connection issue (adaptive learning disabled, keeping {reducedCount} threads)");
            }
            
            SettingsService.Instance.SaveDomainSettings();
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
    /// <summary>
    /// Aggregate connection budget shared by downloads from this domain.
    /// 0 = automatic behavior.
    /// </summary>
    public int MaxThreads { get; set; } = 0;
    
    /// <summary>
    /// Minimum bytes per download segment (KB). 0 = use the global
    /// DefaultMinSegmentSizeKb value.
    /// </summary>
    public int MinSegmentSizeKb { get; set; } = 0;
    
    /// <summary>Enable automatic connection-budget backoff after failures.</summary>
    public bool AdaptiveLearning { get; set; } = true;
}
