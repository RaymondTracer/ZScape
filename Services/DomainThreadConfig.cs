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
    private readonly Dictionary<string, DomainSettings> _domainSettings;
    private readonly Func<AppSettings> _getGlobalSettings;
    private readonly Action _saveSettings;
    private readonly Action<string>? _verboseLog;
    private readonly Action<string>? _warningLog;

    private DomainThreadConfig() : this(
        SettingsService.Instance.DomainThreadSettings,
        () => SettingsService.Instance.Settings,
        SettingsService.Instance.SaveDomainSettings,
        LoggingService.Instance.Verbose,
        LoggingService.Instance.Warning)
    {
    }

    internal DomainThreadConfig(
        Dictionary<string, DomainSettings> domainSettings,
        Func<AppSettings> getGlobalSettings,
        Action saveSettings,
        Action<string>? verboseLog = null,
        Action<string>? warningLog = null)
    {
        _domainSettings = domainSettings;
        _getGlobalSettings = getGlobalSettings;
        _saveSettings = saveSettings;
        _verboseLog = verboseLog;
        _warningLog = warningLog;

        var normalizedSettings = CreateNormalizedSnapshot(domainSettings);
        _domainSettings.Clear();
        foreach (var pair in normalizedSettings)
            _domainSettings.Add(pair.Key, pair.Value);

        _verboseLog?.Invoke($"Domain thread config initialized: {_domainSettings.Count} domains");
    }

    /// <summary>
    /// Gets the configured thread count for a domain, or null if not configured.
    /// </summary>
    public int? GetThreadCount(string domain)
    {
        domain = NormalizeDomain(domain);
        lock (_lock)
        {
            if (_domainSettings.TryGetValue(domain, out var settings))
            {
                return settings.MaxThreads;
            }
            return null;
        }
    }

    /// <summary>
    /// Gets a detached settings snapshot for a domain, or null if not configured.
    /// </summary>
    public DomainSettings? GetSettings(string domain)
    {
        domain = NormalizeDomain(domain);
        lock (_lock)
        {
            return _domainSettings.TryGetValue(domain, out var settings) ? CopySettings(settings) : null;
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
        var globalSettings = _getGlobalSettings();
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
        var globalSettings = _getGlobalSettings();

        lock (_lock)
        {
            if (!_domainSettings.TryGetValue(domain, out var settings))
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
        if (domain.Length == 0)
            return;

        threadCount = NormalizeMaxThreads(threadCount);
        lock (_lock)
        {
            if (!_domainSettings.TryGetValue(domain, out var settings))
            {
                settings = new DomainSettings { MaxThreads = threadCount };
                _domainSettings[domain] = settings;
            }

            if (settings.AdaptiveLearning && threadCount > settings.MaxThreads)
            {
                _verboseLog?.Invoke($"Domain {domain}: Updated thread count from {settings.MaxThreads} to {threadCount}");
                settings.MaxThreads = threadCount;
            }

            _saveSettings();
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
        if (domain.Length == 0)
            return reducedCount;

        lock (_lock)
        {
            if (!_domainSettings.TryGetValue(domain, out var settings))
            {
                settings = new DomainSettings();
                _domainSettings[domain] = settings;
            }
            
            // Only reduce thread count if adaptive learning is enabled
            if (settings.AdaptiveLearning)
            {
                settings.MaxThreads = reducedCount;
                _warningLog?.Invoke($"Domain {domain}: Reduced threads from {currentThreads} to {reducedCount}");
            }
            else
            {
                // Keep using current max, just log the failure
                reducedCount = settings.MaxThreads;
                _verboseLog?.Invoke($"Domain {domain}: Connection issue (adaptive learning disabled, keeping {reducedCount} threads)");
            }
            
            _saveSettings();
        }

        return reducedCount;
    }

    /// <summary>
    /// Gets detached settings snapshots for all configured domains.
    /// </summary>
    public IReadOnlyDictionary<string, DomainSettings> GetAllSettings()
    {
        lock (_lock)
        {
            return CreateNormalizedSnapshot(_domainSettings);
        }
    }

    /// <summary>
    /// Applies fields changed since the editor's original snapshot. Adaptive updates
    /// to untouched fields and newly learned domains remain intact. Explicit edits
    /// and removals win over concurrent adaptive updates under the same lock.
    /// </summary>
    public void ApplyConfiguredChanges(
        IEnumerable<KeyValuePair<string, DomainSettings>> editedSettings,
        IReadOnlyDictionary<string, DomainSettings> originalSettings)
    {
        var edited = CreateNormalizedSnapshot(editedSettings);
        var original = CreateNormalizedSnapshot(originalSettings);
        lock (_lock)
        {
            foreach (var domain in original.Keys)
            {
                if (!edited.ContainsKey(domain))
                    _domainSettings.Remove(domain);
            }

            foreach (var pair in edited)
            {
                var changes = pair.Value;
                if (!original.TryGetValue(pair.Key, out var baseline))
                {
                    _domainSettings[pair.Key] = new DomainSettings
                    {
                        MaxThreads = NormalizeMaxThreads(changes.MaxThreads),
                        MinSegmentSizeKb = NormalizeMinSegmentSizeKb(changes.MinSegmentSizeKb),
                        AdaptiveLearning = changes.AdaptiveLearning
                    };
                    continue;
                }

                var threadsChanged = changes.MaxThreads != baseline.MaxThreads;
                var segmentSizeChanged = changes.MinSegmentSizeKb != baseline.MinSegmentSizeKb;
                var adaptiveChanged = changes.AdaptiveLearning != baseline.AdaptiveLearning;
                if (!threadsChanged && !segmentSizeChanged && !adaptiveChanged)
                    continue;

                if (!_domainSettings.TryGetValue(pair.Key, out var current))
                {
                    current = CopySettings(baseline);
                    _domainSettings.Add(pair.Key, current);
                }

                if (threadsChanged)
                    current.MaxThreads = NormalizeMaxThreads(changes.MaxThreads);
                if (segmentSizeChanged)
                    current.MinSegmentSizeKb = NormalizeMinSegmentSizeKb(changes.MinSegmentSizeKb);
                if (adaptiveChanged)
                    current.AdaptiveLearning = changes.AdaptiveLearning;
            }

            _saveSettings();
        }
    }

    internal static int NormalizeMaxThreads(int maxThreads) => Math.Max(0, maxThreads);

    internal static int NormalizeMinSegmentSizeKb(int minSegmentSizeKb) =>
        minSegmentSizeKb <= 0 ? 0 : Math.Clamp(minSegmentSizeKb, 64, 4096);

    private static Dictionary<string, DomainSettings> CreateNormalizedSnapshot(
        IEnumerable<KeyValuePair<string, DomainSettings>> settings)
    {
        var snapshot = new Dictionary<string, DomainSettings>(StringComparer.Ordinal);
        foreach (var pair in settings)
        {
            var domain = NormalizeDomain(pair.Key);
            if (domain.Length > 0)
                snapshot[domain] = CopySettings(pair.Value);
        }

        return snapshot;
    }

    private static DomainSettings CopySettings(DomainSettings settings) => new()
    {
        MaxThreads = settings.MaxThreads,
        MinSegmentSizeKb = settings.MinSegmentSizeKb,
        AdaptiveLearning = settings.AdaptiveLearning
    };

    private static string NormalizeDomain(string domain)
    {
        domain = domain.Trim();
        if (Uri.TryCreate(domain, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
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
