using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using ZScape.Models;
using ZScape.Utilities;
using static ZScape.Utilities.FormatUtils;

namespace ZScape.Services;

/// <summary>
/// Downloads WAD files from multiple sources using range requests and a
/// source-aware scheduler with global, per-domain, and per-file limits.
/// </summary>
public partial class WadDownloader : IDisposable
{
    /// <summary>
    /// /idgames Archive fullsort.gz mirrors, in preferred order.
    /// The formerly used Quaddicted URL now returns 404.
    /// </summary>
    private static readonly string[] IdgamesIndexUrls =
    [
        "https://ftp.fu-berlin.de/pc/games/idgames/fullsort.gz",
        "https://youfailit.net/pub/idgames/fullsort.gz",
        "https://lethe.chinstrap.org/idgames/fullsort.gz",
        "https://mirrors.lug.mtu.edu/idgames/fullsort.gz",
        "https://ftpmirror.infania.net/pub/idgames/fullsort.gz",
        "https://mirror.braindrainlan.nu/pub/idgames/fullsort.gz",
        "https://files.xvertigox.com/idgames/fullsort.gz",
        "https://www.gamers.org/pub/idgames/fullsort.gz",
    ];
    
    /// <summary>
    /// /idgames Archive mirror base URLs for constructing download links.
    /// </summary>
    private static readonly string[] IdgamesMirrors =
    [
        "https://ftp.fu-berlin.de/pc/games/idgames/",
        "https://youfailit.net/pub/idgames/",
        "https://lethe.chinstrap.org/idgames/",
        "https://mirrors.lug.mtu.edu/idgames/",
        "https://planetzdoom.com/idgames/",
        "https://ftpmirror.infania.net/pub/idgames/",
        "https://mirror.braindrainlan.nu/pub/idgames/",
        "https://files.xvertigox.com/idgames/",
    ];

    /// <summary>
    /// Permanent official Freedoom release assets. The phase archive contains
    /// freedoom1.wad and freedoom2.wad; FreeDM has its own archive. The
    /// downloader extracts only the WAD requested by the current task.
    /// </summary>
    private static readonly Dictionary<string, (string Url, long Size, string ArchiveFileName)>
        OfficialFreedoomArchives = new(StringComparer.OrdinalIgnoreCase)
    {
        ["freedoom1.wad"] =
            ("https://github.com/freedoom/freedoom/releases/download/v0.13.0/freedoom-0.13.0.zip",
             24_143_781,
             "freedoom-0.13.0.zip"),
        ["freedoom2.wad"] =
            ("https://github.com/freedoom/freedoom/releases/download/v0.13.0/freedoom-0.13.0.zip",
             24_143_781,
             "freedoom-0.13.0.zip"),
        ["freedm.wad"] =
            ("https://github.com/freedoom/freedoom/releases/download/v0.13.0/freedm-0.13.0.zip",
             11_331_651,
             "freedm-0.13.0.zip")
    };
    
    /// <summary>
    /// Sites to exclude from web search results (unlikely to have WAD downloads).
    /// </summary>
    private static readonly string[] ExcludedSearchHosts =
    [
        "youtube.com", "youtu.be",
        "twitter.com", "x.com",
        "facebook.com", "instagram.com",
        "reddit.com",
        "wikipedia.org",
        "duckduckgo.com", "bing.com", "google.com",
        "pinterest.com", "tiktok.com",
        "linkedin.com", "discord.com",
        "twitch.tv", "steam.com",
    ];

    // DuckDuckGo Lite currently returns HTTP 403 for direct application
    // requests. Use the HTML form endpoint instead of repeatedly querying two
    // surfaces backed by the same provider.
    private static readonly (string Name, string Url)[] WebSearchEndpoints =
    [
        ("DuckDuckGo HTML", "https://html.duckduckgo.com/html/"),
    ];

    private const int TargetWebSourceDomains = 2;
    private const int MaxWebResultPagesPerWad = 8;
    private const int WebSearchRequestIntervalMs = 750;
    
    /// <summary>
    /// Cached idgames index (filename -> (path, size) mapping).
    /// </summary>
    private static Dictionary<string, (string Path, long Size)>? _idgamesIndex;
    private static readonly SemaphoreSlim _idgamesIndexLock = new(1, 1);
    private static DateTime _idgamesIndexExpiry = DateTime.MinValue;
    
    /// <summary>
    /// Supported WAD file extensions to search for (uses centralized WadExtensions).
    /// </summary>
    private static string[] SupportedExtensions => Utilities.WadExtensions.AllSupportedExtensions;
    
    // Static HttpClient instances - reused across all WadDownloader instances for connection pooling
    // HttpClient is thread-safe and designed to be reused
    private static readonly Lazy<HttpClient> _sharedHttpClient = new(CreateHttpClient);
    private static readonly Lazy<HttpClient> _sharedWebClient = new(CreateWebClient);
    private static readonly Lazy<HttpClient> _fallbackWebClient =
        new(CreateIpv4FirstWebClient);
    private static readonly SemaphoreSlim _webSearchRequestLock = new(1, 1);
    private static long _lastWebSearchRequestTimestamp;
    
    private readonly DomainThreadConfig _domainConfig = DomainThreadConfig.Instance;
    private readonly List<string> _downloadSites;
    private bool _disposed;
    
    // Property accessors for the shared HttpClient instances
    private static HttpClient HttpClient => _sharedHttpClient.Value;
    private static HttpClient WebClient => _sharedWebClient.Value;
    private static HttpClient FallbackWebClient => _fallbackWebClient.Value;
    
    /// <summary>
    /// Whether to search the /idgames Archive. Default is true.
    /// </summary>
    public bool IdgamesEnabled { get; set; } = true;
    
    /// <summary>
    /// Whether to use web search as a last-resort fallback. Default is true.
    /// </summary>
    public bool WebSearchEnabled { get; set; } = true;
    
    /// <summary>
    /// Default WAD hosting sites.
    /// </summary>
    public static readonly List<string> DefaultSites =
    [
        "https://static.action.fapnow.xyz/wads/%WadName%",
        "https://static.allfearthesentinel.com/wads/%WadName%",
        "https://euroboros.net/zandronum/wads/%WadName%",
        "https://wads.pizza-doom.it/%WadName%",
        "https://static.audrealms.org/wads/%WadName%",
        "https://wads.firestick.games/%WadName%",
        "https://doomshack.org/wads/%WadName%",
    ];
    
    /// <summary>
    /// Event fired when download progress updates.
    /// </summary>
    public event EventHandler<WadDownloadTask>? ProgressUpdated;
    
    /// <summary>
    /// Event fired when a download completes (success or failure).
    /// </summary>
    public event EventHandler<WadDownloadTask>? DownloadCompleted;
    
    /// <summary>
    /// Event fired for log messages (displayed in download dialog only).
    /// </summary>
    public event EventHandler<(LogLevel Level, string Message)>? LogMessage;
    
    /// <summary>
    /// Log levels for WAD downloader messages.
    /// </summary>
    public enum LogLevel { Verbose, Info, Warning, Error, Success }
    
    public WadDownloader(List<string>? customSites = null)
    {
        _downloadSites = customSites ?? new List<string>(DefaultSites);
        // HttpClient instances are now static and shared - no need to create them here
    }
    
    /// <summary>
    /// Creates the shared HttpClient for WAD downloads.
    /// </summary>
    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1024, // High limit - actual concurrency controlled by settings
            PooledConnectionLifetime = TimeSpan.FromMinutes(AppConstants.HttpPooling.DownloadPooledConnectionLifetimeMinutes),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(AppConstants.HttpPooling.PooledConnectionIdleTimeoutMinutes),
            ConnectTimeout = TimeSpan.FromSeconds(AppConstants.Timeouts.HttpConnectTimeoutSeconds),
            EnableMultipleHttp2Connections = true
        };
        
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(AppConstants.Timeouts.HttpLongOperationTimeoutMinutes)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppConstants.AppInfo.WadDownloaderUserAgent);
        return client;
    }
    
    /// <summary>
    /// Creates the shared HttpClient for web requests (idgames, DuckDuckGo).
    /// </summary>
    private static HttpClient CreateWebClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 10,
            PooledConnectionLifetime = TimeSpan.FromMinutes(AppConstants.HttpPooling.WebPooledConnectionLifetimeMinutes),
            ConnectTimeout = TimeSpan.FromSeconds(AppConstants.Timeouts.HttpConnectTimeoutSeconds)
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(AppConstants.Timeouts.WebRequestTimeoutSeconds)
        };
    }

    /// <summary>
    /// Some fallback web hosts advertise IPv6 even on networks where IPv6
    /// routing is unavailable. Prefer IPv4 so a dead route does not consume the
    /// entire request timeout, while retaining IPv6 fallback.
    /// </summary>
    private static HttpClient CreateIpv4FirstWebClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(
                AppConstants.HttpPooling.WebPooledConnectionLifetimeMinutes),
            ConnectTimeout = TimeSpan.FromSeconds(
                AppConstants.Timeouts.HttpConnectTimeoutSeconds),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(
                    context.DnsEndPoint.Host,
                    ct);
                Exception? lastError = null;
                foreach (var address in addresses.OrderBy(address =>
                             address.AddressFamily
                             == System.Net.Sockets.AddressFamily.InterNetwork
                                 ? 0
                                 : 1))
                {
                    var socket = new System.Net.Sockets.Socket(
                        address.AddressFamily,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(
                            new IPEndPoint(
                                address,
                                context.DnsEndPoint.Port),
                            ct);
                        return new System.Net.Sockets.NetworkStream(
                            socket,
                            ownsSocket: true);
                    }
                    catch (OperationCanceledException)
                        when (ct.IsCancellationRequested)
                    {
                        socket.Dispose();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        socket.Dispose();
                    }
                }

                throw lastError
                      ?? new HttpRequestException(
                          $"No address resolved for "
                          + context.DnsEndPoint.Host);
            }
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(
                AppConstants.Timeouts.WebRequestTimeoutSeconds)
        };
    }
    
    /// <summary>
    /// Gets the list of download sites.
    /// </summary>
    public List<string> DownloadSites => _downloadSites;
    
    private void Log(LogLevel level, string message) => LogMessage?.Invoke(this, (level, message));
    private void LogVerbose(string message) => Log(LogLevel.Verbose, message);
    private void LogInfo(string message) => Log(LogLevel.Info, message);
    private void LogWarning(string message) => Log(LogLevel.Warning, message);
    private void LogError(string message) => Log(LogLevel.Error, message);
    private void LogSuccess(string message) => Log(LogLevel.Success, message);

    // Failed is intentionally not terminal here. ExecuteDownloadAsync uses it
    // transiently while the scheduler decides whether to retry or change
    // source; discovery must remain able to add an alternate during that gap.
    // Once a source is actively downloading, stop probing that file. The
    // scheduler already owns the transfer and any later source would only be
    // useful after this attempt fails and the task returns to a retry/search
    // state.
    private static bool IsEligibleForSourceDiscovery(WadDownloadTask task) =>
        task.Status is not (
            WadDownloadStatus.Completed
            or WadDownloadStatus.AlreadyExists
            or WadDownloadStatus.Downloading
            or WadDownloadStatus.Cancelled);

    private static int GetTotalSourcePhases(
        bool idgamesEnabled,
        bool webSearchEnabled,
        bool hasServerUrl,
        bool hasOfficialFreedoomSource,
        int siteCount)
    {
        return siteCount
            + (idgamesEnabled ? 1 : 0)
            + (webSearchEnabled ? 1 : 0)
            + (hasServerUrl ? 1 : 0)
            + (hasOfficialFreedoomSource ? 1 : 0);
    }

    private void ResetTaskForSourceDiscovery(WadDownloadTask task)
    {
        task.Status = WadDownloadStatus.Searching;
        task.StatusMessage = $"Searching (0/{task.TotalSitesToSearch})...";
        task.SourceUrl = null;
        task.DownloadedFileName = null;
        task.TotalBytes = 0;
        task.BytesDownloaded = 0;
        task.BytesPerSecond = 0;
        task.ErrorMessage = null;
        task.RetryCount = 0;
        task.SkipSourceRetry = false;
        task.SitesSearched = 0;
        ProgressUpdated?.Invoke(this, task);
    }

    private void MarkTaskSourceDiscoveryFailed(WadDownloadTask task, string message, string errorMessage)
    {
        task.Status = WadDownloadStatus.Failed;
        task.StatusMessage = message;
        task.ErrorMessage = errorMessage;
        LogWarning($"Not found: {task.Wad.FileName}");
        DownloadCompleted?.Invoke(this, task);
    }

    private void AddAlternateSource(
        WadDownloadTask task,
        string url,
        long size,
        string? downloadedFileName,
        string logMessage,
        SourceDiscoveryState discoveryState)
    {
        var sourceAdded = false;
        lock (discoveryState.SyncRoot)
        {
            if (task.ExhaustedUrls.Contains(url) || string.Equals(task.SourceUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (task.AlternateUrls.Any(alternate => alternate.Url == url))
            {
                return;
            }

            task.AlternateUrls.Add((url, size, downloadedFileName));
            sourceAdded = true;
        }

        if (sourceAdded)
        {
            discoveryState.NotifySourceChanged();
        }
        LogVerbose(logMessage);
    }

    private static int GetDiscoveredDomainCount(
        WadDownloadTask task,
        SourceDiscoveryState discoveryState)
    {
        lock (discoveryState.SyncRoot)
        {
            return task.AlternateUrls
                .Select(alternate => alternate.Url)
                .Prepend(task.SourceUrl)
                .Where(url => Uri.TryCreate(
                    url,
                    UriKind.Absolute,
                    out _))
                .Select(url => new Uri(url!).Host)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }
    }

    private static bool NeedsWebSourceDiscovery(
        WadDownloadTask task,
        SourceDiscoveryState discoveryState) =>
        IsEligibleForSourceDiscovery(task)
        && GetDiscoveredDomainCount(task, discoveryState) < TargetWebSourceDomains;

    private void RecordDiscoveredSource(
        WadDownloadTask task,
        string url,
        long size,
        string queuedStatus,
        string successMessage,
        string alternateMessage,
        string? downloadedFileName,
        SourceDiscoveryState discoveryState)
    {
        if (!IsEligibleForSourceDiscovery(task))
        {
            return;
        }

        var isFirstSource = false;
        lock (discoveryState.SyncRoot)
        {
            if (task.ExhaustedUrls.Contains(url)
                || string.Equals(
                    task.SourceUrl,
                    url,
                    StringComparison.OrdinalIgnoreCase)
                || task.AlternateUrls.Any(alternate =>
                    alternate.Url.Equals(
                        url,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (task.SourceUrl == null)
            {
                task.SourceUrl = url;
                task.TotalBytes = size;
                task.DownloadedFileName = downloadedFileName;
                isFirstSource = true;
            }
            else
            {
                task.AlternateUrls.Add((url, size, downloadedFileName));
            }
        }

        discoveryState.NotifySourceChanged();

        if (isFirstSource)
        {
            task.Status = WadDownloadStatus.Queued;
            task.StatusMessage = $"Queued ({queuedStatus})";
            task.SkipSourceRetry = false;
            ProgressUpdated?.Invoke(this, task);
            LogSuccess(successMessage);
        }
        else
        {
            LogVerbose(alternateMessage);
        }
    }

    private static string? ExtractDownloadFileName(string urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath))
        {
            return null;
        }

        static string? NormalizeCandidate(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            candidate = Path.GetFileName(candidate.Trim());
            var extension = WadExtensions.GetLowerExtension(candidate);
            return WadExtensions.IsSupportedExtension(extension) ? candidate : null;
        }

        var withoutFragment = urlOrPath.Split('#', 2)[0];
        var queryIndex = withoutFragment.IndexOf('?');
        var pathPart = queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment;

        var pathCandidate = NormalizeCandidate(Uri.UnescapeDataString(pathPart));
        if (!string.IsNullOrWhiteSpace(pathCandidate))
        {
            return pathCandidate;
        }

        if (queryIndex < 0 || queryIndex == withoutFragment.Length - 1)
        {
            return null;
        }

        var query = withoutFragment[(queryIndex + 1)..];
        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            var value = separatorIndex >= 0 ? segment[(separatorIndex + 1)..] : segment;
            var candidate = NormalizeCandidate(Uri.UnescapeDataString(value));
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool TryGetDirectDownloadFileName(string url, out string fileName)
    {
        fileName = ExtractDownloadFileName(url) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(fileName);
    }

    private static string GetLowercaseFileName(string filename)
    {
        var fileName = Path.GetFileName(filename);
        return string.IsNullOrWhiteSpace(fileName)
            ? filename.ToLowerInvariant()
            : fileName.ToLowerInvariant();
    }

    private static bool TryBuildAbsoluteUrl(Uri baseUri, string href, out string absoluteUrl)
    {
        absoluteUrl = string.Empty;
        try
        {
            if (href.StartsWith("http://") || href.StartsWith("https://"))
            {
                absoluteUrl = href;
            }
            else if (href.StartsWith("//"))
            {
                absoluteUrl = "https:" + href;
            }
            else if (href.StartsWith("/"))
            {
                absoluteUrl = $"{baseUri.Scheme}://{baseUri.Host}{href}";
            }
            else
            {
                absoluteUrl = new Uri(baseUri, href).ToString();
            }

            if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out _))
            {
                return false;
            }

            absoluteUrl = NormalizeKnownDownloadUrl(absoluteUrl);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, WadDownloadTask> BuildPageSourceLookup(
        IEnumerable<WadDownloadTask> tasks)
    {
        var lookup = new Dictionary<string, WadDownloadTask>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks.Where(IsEligibleForSourceDiscovery))
        {
            foreach (var variant in GetFilenameVariants(task.Wad.FileName))
            {
                lookup.TryAdd(variant, task);
            }
        }

        return lookup;
    }

    private async Task SearchPageForWadsAsync(
        string pageUrl,
        IEnumerable<WadDownloadTask> tasks,
        SourceDiscoveryState discoveryState,
        CancellationToken ct)
    {
        var neededWads = BuildPageSourceLookup(tasks);
        if (neededWads.Count == 0)
        {
            return;
        }

        List<(string FileName, string Url)> allLinks;
        if (TryGetDirectDownloadFileName(pageUrl, out var directFileName))
        {
            allLinks = new List<(string FileName, string Url)> { (directFileName, pageUrl) };
        }
        else
        {
            allLinks = await ParseAllWadLinksFromPage(pageUrl, ct);
        }

        foreach (var (fileName, url) in allLinks)
        {
            if (!neededWads.TryGetValue(fileName, out var match)) continue;
            if (!IsEligibleForSourceDiscovery(match)) continue;

            var size = await GetFileSizeAsync(url, ct);
            if (size < 0) continue;

            var foundAs = fileName.Equals(match.Wad.FileName, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $" (as {fileName})";
            RecordDiscoveredSource(
                match,
                url,
                size,
                new Uri(url).Host,
                $"Found {match.Wad.FileName}{foundAs} at {url} ({FormatSizeOrUnknown(size)})",
                $"Added alternate source for {match.Wad.FileName}: {url}",
                fileName,
                discoveryState);
        }
    }

    private void LogUrlAttempt(string operation, HttpMethod method, string url)
    {
        LogVerbose($"{operation}: {method} {url}");
    }

    private void LogUrlFailure(string operation, HttpMethod method, string url, HttpStatusCode statusCode)
    {
        var message = $"{operation} failed: {method} {url} -> HTTP {(int)statusCode} {statusCode}";
        if ((int)statusCode >= 500)
        {
            LogError(message);
            return;
        }

        LogWarning(message);
    }

    private void LogUrlFailure(string operation, HttpMethod method, string url, Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            return;
        }

        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            LogUrlFailure(operation, method, url, httpEx.StatusCode.Value);
            return;
        }

        var message = $"{operation} failed: {method} {url} -> {DescribeRequestFailure(ex, ct)}";
        if (ex is OperationCanceledException)
        {
            LogWarning(message);
            return;
        }

        LogError(message);
    }

    private static string DescribeRequestFailure(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return "request timed out";
        }

        if (ex.InnerException is System.Net.Sockets.SocketException socketEx)
        {
            return $"socket error {socketEx.SocketErrorCode}: {socketEx.Message}";
        }

        return ex.Message;
    }
    
    /// <summary>
    /// Discovers sources and downloads concurrently. Each first usable source
    /// wakes the scheduler immediately; discovery continues in the background
    /// so later files and retry attempts can still use alternate domains.
    /// </summary>
    public async Task DownloadWadsAsync(
        IEnumerable<WadDownloadTask> tasks, 
        string downloadPath,
        CancellationToken cancellationToken = default)
    {
        var taskList = tasks.ToList();
        if (taskList.Count == 0) return;

        LogInfo($"Starting source discovery for {taskList.Count} WAD(s)");

        var discoveryState = new SourceDiscoveryState();
        var siteCount = _downloadSites.Count;

        foreach (var task in taskList)
        {
            task.TotalSitesToSearch = GetTotalSourcePhases(
                IdgamesEnabled,
                WebSearchEnabled,
                !string.IsNullOrEmpty(task.Wad.ServerUrl),
                WadManager.IsFreedoomIwad(task.Wad.FileName),
                siteCount);
            task.AlternateUrls.Clear();
            task.ExhaustedUrls.Clear();
            ResetTaskForSourceDiscovery(task);
        }

        using var discoveryCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var discoveryTask = DiscoverSourcesAsync(
            taskList,
            discoveryState,
            discoveryCts.Token);

        try
        {
            await RunSourceAwareDownloadsAsync(
                taskList,
                downloadPath,
                discoveryState,
                cancellationToken);
        }
        finally
        {
            // Once every file is resolved, further alternate-source searching
            // has no value. Stop it and observe the task before returning.
            discoveryCts.Cancel();
            await discoveryTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            LogWarning("WAD download cancelled");
            return;
        }

        var completed = taskList.Count(task =>
            task.Status is WadDownloadStatus.Completed or WadDownloadStatus.AlreadyExists);
        var failed = taskList.Count(task => task.Status == WadDownloadStatus.Failed);
        LogInfo($"Download complete: {completed} succeeded, {failed} failed");
    }

    private async Task DiscoverSourcesAsync(
        List<WadDownloadTask> tasks,
        SourceDiscoveryState discoveryState,
        CancellationToken ct)
    {
        try
        {
            await SearchOfficialFreedoomAsync(tasks, discoveryState, ct);

            var serverGroups = tasks
                .Where(task => !string.IsNullOrEmpty(task.Wad.ServerUrl))
                .GroupBy(task => task.Wad.ServerUrl!)
                .ToList();

            if (serverGroups.Count > 0)
            {
                await Task.WhenAll(serverGroups.Select(group =>
                    SearchServerUrlAsync(
                        group.Key,
                        group.ToList(),
                        discoveryState,
                        ct)));
            }

            foreach (var site in _downloadSites)
            {
                ct.ThrowIfCancellationRequested();
                await SearchSiteAsync(
                    site,
                    tasks,
                    discoveryState,
                    ct);
            }

            if (IdgamesEnabled)
            {
                ct.ThrowIfCancellationRequested();
                await SearchIdgamesAsync(
                    tasks,
                    discoveryState,
                    ct);
            }

            if (WebSearchEnabled)
            {
                ct.ThrowIfCancellationRequested();
                await SearchWebAsync(
                    tasks,
                    discoveryState,
                    ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when the user cancels or all downloads finish before
            // every optional alternate-source phase has completed.
        }
        catch (Exception ex)
        {
            LogError($"Source discovery stopped unexpectedly: {ex.Message}");
        }
        finally
        {
            discoveryState.MarkComplete();
        }
    }

    /// <summary>
    /// Adds the official Freedoom archive as an immediate source for each
    /// requested Freedoom IWAD. Archive extraction selects only the requested
    /// file, so joining a Freedoom Phase 2 server downloads freedoom2.wad
    /// rather than the entire suite.
    /// </summary>
    private Task SearchOfficialFreedoomAsync(
        List<WadDownloadTask> tasks,
        SourceDiscoveryState discoveryState,
        CancellationToken ct)
    {
        var freedoomTasks = tasks
            .Where(task => WadManager.IsFreedoomIwad(task.Wad.FileName))
            .ToList();
        if (freedoomTasks.Count == 0)
        {
            return Task.CompletedTask;
        }

        LogInfo(
            $"Freedoom: adding official release sources for "
            + $"{freedoomTasks.Count} IWAD(s)...");

        try
        {
            foreach (var task in freedoomTasks)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsEligibleForSourceDiscovery(task))
                {
                    continue;
                }

                var requestedFileName = Path.GetFileName(task.Wad.FileName);
                if (!OfficialFreedoomArchives.TryGetValue(
                        requestedFileName,
                        out var archive))
                {
                    continue;
                }

                RecordDiscoveredSource(
                    task,
                    archive.Url,
                    archive.Size,
                    "official Freedoom release",
                    $"Found {task.Wad.FileName} in the official Freedoom release "
                    + $"at {archive.Url} ({FormatBytes(archive.Size)})",
                    $"Added official Freedoom alternate for {task.Wad.FileName}: "
                    + archive.Url,
                    archive.ArchiveFileName,
                    discoveryState);
            }
        }
        finally
        {
            foreach (var task in freedoomTasks)
            {
                task.IncrementSitesSearched();
                if (task.Status == WadDownloadStatus.Searching)
                {
                    task.StatusMessage =
                        $"Searching ({task.SitesSearched}/{task.TotalSitesToSearch})...";
                    ProgressUpdated?.Invoke(this, task);
                }
            }
        }

        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Searches a server URL for all needed WADs and records every matching
    /// source in each task's candidate pool.
    /// </summary>
    private async Task SearchServerUrlAsync(
        string serverUrl,
        List<WadDownloadTask> tasks,
        SourceDiscoveryState discoveryState,
        CancellationToken ct)
    {
        try
        {
            LogInfo($"Checking server URL: {serverUrl}");

            await SearchPageForWadsAsync(
                serverUrl,
                tasks,
                discoveryState,
                ct);
        }
        catch (Exception ex)
        {
            LogWarning($"Server search failed: {ex.Message}");
        }
        finally
        {
            // Update search progress for all tasks in this server group
            foreach (var task in tasks)
            {
                task.IncrementSitesSearched();
                if (task.Status == WadDownloadStatus.Searching)
                {
                    task.StatusMessage = $"Searching ({task.SitesSearched}/{task.TotalSitesToSearch})...";
                    ProgressUpdated?.Invoke(this, task);
                }
            }
        }
    }
    
    /// <summary>
    /// Searches a download site for all needed WADs and records every matching
    /// source in each task's candidate pool.
    /// </summary>
    private async Task SearchSiteAsync(
        string site,
        List<WadDownloadTask> tasks,
        SourceDiscoveryState discoveryState,
        CancellationToken ct)
    {
        try
        {
            var siteUri = new Uri(site.Contains("%WadName%") ? site.Replace("%WadName%", "test") : site);
            var siteHost = siteUri.Host;
            LogVerbose($"Searching site: {site}");
            
            // Check for %WadName% template URLs - direct download links
            if (site.Contains("%WadName%", StringComparison.OrdinalIgnoreCase))
            {
                // Check each WAD with HEAD request and GET fallback - try multiple extensions if needed.
                var tasksToCheck = tasks.Where(IsEligibleForSourceDiscovery).ToList();
                
                var checkTasks = tasksToCheck.Select(async task =>
                {
                    // Build list of filenames to try
                    var filenamesToTry = GetFilenameVariants(task.Wad.FileName);
                    
                    foreach (var filename in filenamesToTry)
                    {
                        // A source may have been found while this site's
                        // filename variants were being prepared. Avoid issuing
                        // more HEAD/GET probes once the scheduler starts it.
                        if (!IsEligibleForSourceDiscovery(task))
                        {
                            break;
                        }

                        var url = site.Replace("%WadName%", Uri.EscapeDataString(filename), StringComparison.OrdinalIgnoreCase);
                        var size = await GetFileSizeAsync(url, ct);
                        
                        if (size < 0) continue;

                        var foundAs = filename != task.Wad.FileName ? $" (as {filename})" : string.Empty;
                        RecordDiscoveredSource(
                            task,
                            url,
                            size,
                            siteHost,
                            $"Found {task.Wad.FileName}{foundAs} at {url} ({FormatSizeOrUnknown(size)})",
                            $"Added alternate source for {task.Wad.FileName}: {url}",
                            filename,
                            discoveryState);
                        break;
                    }
                });
                
                await Task.WhenAll(checkTasks);
            }
            else
            {
                // Parse page for all WAD links
                LogVerbose($"Parsing site page: {site}");
                await SearchPageForWadsAsync(
                    site,
                    tasks,
                    discoveryState,
                    ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogVerbose($"Site search error ({site}): {ex.Message}");
        }
        finally
        {
            // Update search progress for all tasks
            foreach (var task in tasks)
            {
                task.IncrementSitesSearched();
                if (task.Status == WadDownloadStatus.Searching)
                {
                    task.StatusMessage = $"Searching ({task.SitesSearched}/{task.TotalSitesToSearch})...";
                    ProgressUpdated?.Invoke(this, task);
                }
            }
        }
    }
    
    /// <summary>
    /// Searches the /idgames Archive for WAD files using the fullsort.gz index.
    /// Downloads and caches the index, then constructs download URLs from mirrors.
    /// </summary>
    private async Task SearchIdgamesAsync(
        List<WadDownloadTask> tasks,
        SourceDiscoveryState discoveryState,
        CancellationToken ct)
    {
        LogInfo("Searching /idgames Archive...");
        
        try
        {
            // Load the idgames index (cached for 24 hours)
            var index = await LoadIdgamesIndexAsync(ct);
            if (index == null || index.Count == 0)
            {
                LogVerbose("/idgames: failed to load index");
                return;
            }
            
            LogVerbose($"/idgames: index loaded with {index.Count} files");
            
            foreach (var task in tasks)
            {
                if (ct.IsCancellationRequested) break;
                if (!IsEligibleForSourceDiscovery(task)) continue;
                
                try
                {
                    // Search for file in index (files are stored as .zip in idgames)
                    var baseName = Path.GetFileNameWithoutExtension(task.Wad.FileName).ToLowerInvariant();
                    var zipName = $"{baseName}.zip";
                    
                    LogVerbose($"/idgames search: {zipName}");
                    
                    // Find matching file in index (returns path and size)
                    string? filePath = null;
                    long fileSize = 0;
                    
                    if (index.TryGetValue(zipName, out var fileInfo))
                    {
                        filePath = fileInfo.Path;
                        fileSize = fileInfo.Size;
                    }
                    else
                    {
                        // Try partial match (file might have different case or be in different folder)
                        var match = index.FirstOrDefault(kv => 
                            kv.Key.Equals(zipName, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(match.Value.Path))
                        {
                            filePath = match.Value.Path;
                            fileSize = match.Value.Size;
                        }
                    }
                    
                    if (string.IsNullOrEmpty(filePath))
                    {
                        LogVerbose($"/idgames: no match for {zipName}");
                        continue;
                    }
                    
                    LogVerbose($"/idgames found: {filePath} ({FormatBytes(fileSize)})");
                    
                    // Construct download URLs from all mirrors
                    var downloadUrls = IdgamesMirrors.Select(m => m + filePath).ToList();
                    
                    // Use the size from the index directly - skip HEAD checks since:
                    // 1. We already have the size from fullsort.gz
                    // 2. Some mirrors may block HEAD requests or our User-Agent
                    var actualSize = fileSize > 0 ? fileSize : 1; // Use 1 as fallback if size unknown
                    var workingUrl = downloadUrls.FirstOrDefault();
                    
                    if (workingUrl == null) continue;
                    
                    var domain = new Uri(workingUrl).Host;

                    var downloadedFileName = Path.GetFileName(filePath);
                    RecordDiscoveredSource(
                        task,
                        workingUrl,
                        actualSize,
                        $"/idgames - {domain}",
                        $"Found {task.Wad.FileName} on /idgames at {workingUrl} ({FormatBytes(actualSize)})",
                        $"Added /idgames alternate for {task.Wad.FileName}: {workingUrl}",
                        downloadedFileName,
                        discoveryState);
                    
                    // Add remaining mirrors as alternates
                    foreach (var altUrl in downloadUrls.Where(u => u != workingUrl))
                    {
                        AddAlternateSource(task, altUrl, actualSize, downloadedFileName, $"Added /idgames alternate for {task.Wad.FileName}: {altUrl}", discoveryState);
                    }
                }
                catch (Exception ex)
                {
                    LogVerbose($"/idgames error for {task.Wad.FileName}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogWarning($"/idgames search error: {ex.Message}");
        }
        finally
        {
            // Update search progress
            foreach (var task in tasks)
            {
                task.IncrementSitesSearched();
                if (task.Status == WadDownloadStatus.Searching)
                {
                    task.StatusMessage = $"Searching ({task.SitesSearched}/{task.TotalSitesToSearch})...";
                    ProgressUpdated?.Invoke(this, task);
                }
            }
        }
    }
    
    /// <summary>
    /// Downloads and parses the idgames fullsort.gz index file.
    /// Caches the result for 24 hours.
    /// Format: "YYYY/MM/DD  SIZE  path/to/file.zip"
    /// </summary>
    private async Task<Dictionary<string, (string Path, long Size)>?> LoadIdgamesIndexAsync(CancellationToken ct)
    {
        await _idgamesIndexLock.WaitAsync(ct);
        try
        {
            // Return cached index if not expired
            if (_idgamesIndex != null && DateTime.UtcNow < _idgamesIndexExpiry)
            {
                return _idgamesIndex;
            }
            
            LogVerbose("/idgames: downloading index from official mirrors...");

            foreach (var indexUrl in IdgamesIndexUrls)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, indexUrl);
                    request.Headers.TryAddWithoutValidation(
                        "User-Agent",
                        AppConstants.AppInfo.WadDownloaderUserAgent);
                    LogUrlAttempt(
                        "/idgames index download",
                        request.Method,
                        indexUrl);

                    using var requestCts =
                        CancellationTokenSource.CreateLinkedTokenSource(ct);
                    requestCts.CancelAfter(TimeSpan.FromSeconds(
                        AppConstants.Timeouts.WebRequestTimeoutSeconds));

                    using var response = await WebClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        requestCts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        LogUrlFailure(
                            "/idgames index download",
                            request.Method,
                            indexUrl,
                            response.StatusCode);
                        continue;
                    }

                    await using var compressedStream =
                        await response.Content.ReadAsStreamAsync(requestCts.Token);
                    await using var gzipStream =
                        new GZipStream(compressedStream, CompressionMode.Decompress);
                    using var reader = new StreamReader(gzipStream);

                    var index =
                        new Dictionary<string, (string Path, long Size)>(
                            StringComparer.OrdinalIgnoreCase);
                    var linePattern = IdgamesIndexLineRegex();
                    while (await reader.ReadLineAsync(requestCts.Token) is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        var match = linePattern.Match(line);
                        if (!match.Success)
                        {
                            continue;
                        }

                        var size = long.TryParse(
                            match.Groups[1].Value,
                            out var parsedSize)
                            ? parsedSize
                            : 0;
                        var filePath = match.Groups[2].Value.Trim();
                        var fileName = Path.GetFileName(filePath);
                        if (string.IsNullOrEmpty(fileName)
                            || !fileName.EndsWith(
                                ".zip",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // fullsort is newest-first and often repeats the same
                        // upload under newstuff/incoming and its permanent
                        // archive path. Keep the newest basename match, but
                        // prefer the permanent path for an identical payload.
                        if (!index.TryGetValue(fileName, out var existing))
                        {
                            index[fileName] = (filePath, size);
                        }
                        else if (existing.Size == size
                                 && IsTransientIdgamesPath(existing.Path)
                                 && !IsTransientIdgamesPath(filePath))
                        {
                            index[fileName] = (filePath, size);
                        }
                    }

                    if (index.Count == 0)
                    {
                        LogWarning(
                            $"/idgames index at {indexUrl} contained no usable entries");
                        continue;
                    }

                    _idgamesIndex = index;
                    _idgamesIndexExpiry = DateTime.UtcNow.AddHours(24);
                    LogSuccess(
                        $"/idgames: indexed {index.Count} files from "
                        + new Uri(indexUrl).Host);
                    return index;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogUrlFailure(
                        "/idgames index download",
                        HttpMethod.Get,
                        indexUrl,
                        ex,
                        ct);
                }
            }

            LogWarning("/idgames: every index mirror failed");
            return null;
        }
        finally
        {
            _idgamesIndexLock.Release();
        }
    }
    
    [GeneratedRegex(@"^\d{4}/\d{2}/\d{2}\s+(\d+)\s+(.+)$")]
    private static partial Regex IdgamesIndexLineRegex();

    private static bool IsTransientIdgamesPath(string path) =>
        path.StartsWith("incoming/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("newstuff/", StringComparison.OrdinalIgnoreCase);
    
    /// <summary>
    /// Searches the web as a last-resort fallback. Uses both maintained
    /// DuckDuckGo HTML surfaces with several filename-aware query forms, then
    /// crawls a bounded, de-duplicated set of result pages.
    /// </summary>
    private async Task SearchWebAsync(
        List<WadDownloadTask> tasks,
        SourceDiscoveryState discoveryState,
        CancellationToken ct)
    {
        try
        {
            var sourceDiscoveryTasks = tasks
                .Where(task => NeedsWebSourceDiscovery(task, discoveryState))
                .ToList();
            if (sourceDiscoveryTasks.Count == 0)
            {
                LogVerbose(
                    "Web search: every WAD already has sources on at least "
                    + $"{TargetWebSourceDomains} domains, skipping");
                return;
            }
            
            LogInfo(
                $"Web search: looking for extra sources for "
                + $"{sourceDiscoveryTasks.Count} WAD(s)...");

            var unavailableProviders = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            
            foreach (var task in sourceDiscoveryTasks)
            {
                if (ct.IsCancellationRequested) break;
                if (!NeedsWebSourceDiscovery(task, discoveryState)) continue;
                
                try
                {
                    await SearchWadArchiveAsync(task, discoveryState, ct);
                    if (!NeedsWebSourceDiscovery(task, discoveryState))
                    {
                        continue;
                    }

                    var resultPageUrls = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var searchQuery in BuildWebSearchQueries(
                                 task.Wad.FileName))
                    {
                        if (ct.IsCancellationRequested
                            || unavailableProviders.Count
                                == WebSearchEndpoints.Length
                            || resultPageUrls.Count >= MaxWebResultPagesPerWad)
                        {
                            break;
                        }

                        LogVerbose($"Web search: {searchQuery}");
                        foreach (var endpoint in WebSearchEndpoints)
                        {
                            if (unavailableProviders.Contains(endpoint.Name))
                            {
                                continue;
                            }

                            var result = await FetchSearchResultPagesAsync(
                                endpoint.Name,
                                endpoint.Url,
                                searchQuery,
                                ct);
                            if (result.ProviderUnavailable)
                            {
                                unavailableProviders.Add(endpoint.Name);
                                LogWarning(
                                    $"{endpoint.Name} is unavailable; skipping "
                                    + "it for the rest of this download");
                                continue;
                            }

                            foreach (var page in result.Pages)
                            {
                                resultPageUrls.Add(page);
                                if (resultPageUrls.Count
                                    >= MaxWebResultPagesPerWad)
                                {
                                    break;
                                }
                            }

                            if (result.Pages.Count > 0)
                            {
                                break;
                            }
                        }
                    }

                    if (resultPageUrls.Count == 0)
                    {
                        LogVerbose($"Web search: no result pages found for {task.Wad.FileName}");
                        continue;
                    }
                    
                    LogVerbose(
                        $"Web search: crawling {resultPageUrls.Count} result pages "
                        + $"for {task.Wad.FileName}");
                    
                    foreach (var pageUrl in resultPageUrls)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!NeedsWebSourceDiscovery(task, discoveryState)) break;
                        
                        try
                        {
                            LogVerbose($"Web search crawl target: {pageUrl}");
                            var downloadUrls =
                                await CrawlPageForWadDownloads(
                                    pageUrl,
                                    task.Wad.FileName,
                                    ct);
                            
                            foreach (var url in downloadUrls)
                            {
                                if (ct.IsCancellationRequested) break;
                                if (!NeedsWebSourceDiscovery(task, discoveryState)) break;
                                
                                try
                                {
                                    var size = await GetFileSizeAsync(url, ct);
                                    if (size < 0) continue;

                                    RecordDiscoveredSource(
                                        task,
                                        url,
                                        size,
                                        $"web search - {new Uri(url).Host}",
                                        $"Found {task.Wad.FileName} via web search at {url} ({FormatSizeOrUnknown(size)})",
                                        $"Added web search alternate for {task.Wad.FileName}: {url}",
                                        Path.GetFileName(new Uri(url).LocalPath),
                                        discoveryState);
                                }
                                catch (Exception ex)
                                {
                                    LogVerbose(
                                        $"Web search candidate rejected ({url}): "
                                        + ex.Message);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogVerbose($"Web search: failed to crawl {pageUrl}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogVerbose($"Web search error for {task.Wad.FileName}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogWarning($"Web search error: {ex.Message}");
        }
        finally
        {
            // Update search progress
            foreach (var task in tasks)
            {
                task.IncrementSitesSearched();
                if (task.Status == WadDownloadStatus.Searching)
                {
                    task.StatusMessage = $"Searching ({task.SitesSearched}/{task.TotalSitesToSearch})...";
                    ProgressUpdated?.Invoke(this, task);
                }
            }
        }
    }

    /// <summary>
    /// Searches the server-rendered WAD Archive index, then resolves its result
    /// page to the actual compressed file hosted by Internet Archive.
    /// </summary>
    private async Task SearchWadArchiveAsync(
        WadDownloadTask task,
        SourceDiscoveryState discoveryState,
        CancellationToken ct)
    {
        var searchUrl = "https://www.wad-archive.com/search?q="
                        + Uri.EscapeDataString(task.Wad.FileName);
        var searchHtml = await FetchTextPageAsync(
            "WAD Archive search",
            searchUrl,
            ct);
        if (searchHtml == null)
        {
            return;
        }

        var resultPages = WadArchiveResultRegex()
            .Matches(searchHtml)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(relative =>
                new Uri(new Uri("https://www.wad-archive.com"), relative)
                    .ToString())
            .ToList();
        LogVerbose(
            $"WAD Archive: {resultPages.Count} candidate page(s) for "
            + task.Wad.FileName);

        foreach (var resultPageUrl in resultPages)
        {
            if (ct.IsCancellationRequested
                || !NeedsWebSourceDiscovery(task, discoveryState))
            {
                break;
            }

            var resultHtml = await FetchTextPageAsync(
                "WAD Archive result",
                resultPageUrl,
                ct);
            if (resultHtml == null
                || !resultHtml.Contains(
                    task.Wad.FileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resultBaseUri = new Uri(resultPageUrl);
            var downloadPageUrls = WadArchiveDownloadPageRegex()
                .Matches(resultHtml)
                .Select(match =>
                    WebUtility.HtmlDecode(match.Groups[1].Value))
                .Where(url =>
                    string.Equals(
                        ExtractDownloadFileName(url),
                        task.Wad.FileName,
                        StringComparison.OrdinalIgnoreCase))
                .Where(url =>
                    TryBuildAbsoluteUrl(resultBaseUri, url, out _))
                .Select(url =>
                {
                    TryBuildAbsoluteUrl(resultBaseUri, url, out var absolute);
                    return absolute;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3);

            foreach (var downloadPageUrl in downloadPageUrls)
            {
                var downloadPageHtml = await FetchTextPageAsync(
                    "WAD Archive download resolution",
                    downloadPageUrl,
                    ct);
                if (downloadPageHtml == null)
                {
                    continue;
                }

                foreach (Match archiveMatch in
                         WadArchiveFileUrlRegex().Matches(downloadPageHtml))
                {
                    var archiveUrl = WebUtility.HtmlDecode(
                        archiveMatch.Value);
                    var size = await GetFileSizeAsync(archiveUrl, ct);
                    if (size < 0)
                    {
                        continue;
                    }

                    RecordDiscoveredSource(
                        task,
                        archiveUrl,
                        size,
                        "WAD Archive - archive.org",
                        $"Found {task.Wad.FileName} in WAD Archive at "
                        + $"{archiveUrl} ({FormatSizeOrUnknown(size)})",
                        $"Added WAD Archive alternate for "
                        + $"{task.Wad.FileName}: {archiveUrl}",
                        task.Wad.FileName + ".gz",
                        discoveryState);
                    return;
                }
            }
        }
    }

    private async Task<string?> FetchTextPageAsync(
        string operation,
        string url,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                AppConstants.AppInfo.WadDownloaderUserAgent);
            request.Headers.TryAddWithoutValidation(
                "Accept",
                "text/html,application/xhtml+xml");
            LogUrlAttempt(operation, request.Method, url);

            using var response = await FallbackWebClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                LogUrlFailure(
                    operation,
                    request.Method,
                    url,
                    response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogUrlFailure(operation, HttpMethod.Get, url, ex, ct);
            return null;
        }
    }

    private async Task<SearchPageFetchResult> FetchSearchResultPagesAsync(
        string providerName,
        string searchEndpoint,
        string searchQuery,
        CancellationToken ct)
    {
        await _webSearchRequestLock.WaitAsync(ct);
        try
        {
            var previousRequestTimestamp = Volatile.Read(
                ref _lastWebSearchRequestTimestamp);
            if (previousRequestTimestamp > 0)
            {
                var elapsed = Stopwatch.GetElapsedTime(
                    previousRequestTimestamp);
                var minimumInterval = TimeSpan.FromMilliseconds(
                    WebSearchRequestIntervalMs);
                if (elapsed < minimumInterval)
                {
                    await Task.Delay(minimumInterval - elapsed, ct);
                }
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                searchEndpoint)
            {
                Content = new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["q"] = searchQuery,
                    })
            };
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                AppConstants.AppInfo.WadDownloaderUserAgent);
            request.Headers.TryAddWithoutValidation(
                "Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation(
                "Accept-Language",
                "en-US,en;q=0.5");
            LogUrlAttempt(
                $"{providerName} search request",
                request.Method,
                searchEndpoint);

            using var response = await FallbackWebClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                LogUrlFailure(
                    $"{providerName} search request",
                    request.Method,
                    searchEndpoint,
                    response.StatusCode);
                return new SearchPageFetchResult([], true);
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            if (IsSearchProviderBlockPage(html))
            {
                LogWarning(
                    $"{providerName} returned an automated-request challenge");
                return new SearchPageFetchResult([], true);
            }

            var pages = ExtractSearchResultPageUrls(html);
            LogVerbose(
                $"{providerName}: extracted {pages.Count} result page(s)");
            return new SearchPageFetchResult(pages, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogUrlFailure(
                $"{providerName} search request",
                HttpMethod.Post,
                searchEndpoint,
                ex,
                ct);
            return new SearchPageFetchResult([], true);
        }
        finally
        {
            Volatile.Write(
                ref _lastWebSearchRequestTimestamp,
                Stopwatch.GetTimestamp());
            _webSearchRequestLock.Release();
        }
    }

    private static bool IsSearchProviderBlockPage(string html) =>
        html.Contains(
            "Unfortunately, bots use DuckDuckGo too",
            StringComparison.OrdinalIgnoreCase)
        || html.Contains(
            "anomaly-modal",
            StringComparison.OrdinalIgnoreCase)
        || html.Contains(
            "challenge-form",
            StringComparison.OrdinalIgnoreCase);

    private readonly record struct SearchPageFetchResult(
        List<string> Pages,
        bool ProviderUnavailable);

    private static IReadOnlyList<string> BuildWebSearchQueries(string wadFileName)
    {
        var fileName = Path.GetFileName(wadFileName).ToLowerInvariant();
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var words = Regex.Replace(baseName, @"[_\-.]+", " ")
            .Trim();

        var queries = new List<string>
        {
            $"\"{fileName}\"",
            $"\"{baseName}\" doom wad download"
        };
        if (!words.Equals(baseName, StringComparison.OrdinalIgnoreCase))
        {
            queries.Add($"\"{words}\" doom wad download");
        }

        return queries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Extracts result page URLs from DuckDuckGo HTML results.
    /// </summary>
    private List<string> ExtractSearchResultPageUrls(string html)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (Match match in UddgUrlRegex().Matches(html))
            {
                TryAddSearchResultUrl(
                    urls,
                    WebUtility.HtmlDecode(match.Groups[1].Value),
                    isEncoded: true);
            }

            // This also covers direct result links if DuckDuckGo changes away
            // from its current uddg redirect format.
            foreach (Match match in HrefRegex().Matches(html))
            {
                var href = WebUtility.HtmlDecode(match.Groups[1].Value);
                var redirectMatch = UddgUrlRegex().Match(href);
                if (redirectMatch.Success)
                {
                    TryAddSearchResultUrl(
                        urls,
                        redirectMatch.Groups[1].Value,
                        isEncoded: true);
                    continue;
                }

                TryAddSearchResultUrl(urls, href, isEncoded: false);
            }
        }
        catch (Exception ex)
        {
            LogVerbose($"Web search result parsing failed: {ex.Message}");
        }

        return urls.ToList();
    }

    private static void TryAddSearchResultUrl(
        ISet<string> urls,
        string candidate,
        bool isEncoded)
    {
        try
        {
            candidate = WebUtility.HtmlDecode(candidate);
            if (isEncoded)
            {
                candidate = Uri.UnescapeDataString(candidate);
            }
            if (candidate.StartsWith("//", StringComparison.Ordinal))
            {
                candidate = "https:" + candidate;
            }

            candidate = NormalizeKnownDownloadUrl(candidate);
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp
                    && uri.Scheme != Uri.UriSchemeHttps)
                || IsExcludedSearchHost(uri.Host))
            {
                return;
            }

            urls.Add(candidate);
        }
        catch
        {
            // Ignore malformed third-party result URLs.
        }
    }

    private static bool IsExcludedSearchHost(string host) =>
        ExcludedSearchHosts.Any(excluded =>
            host.Equals(excluded, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(
                "." + excluded,
                StringComparison.OrdinalIgnoreCase));

    private static string NormalizeKnownDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && segments.Length >= 5
            && segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase))
        {
            var rawPath = string.Join('/', segments.Skip(4));
            return $"https://raw.githubusercontent.com/"
                   + $"{segments[0]}/{segments[1]}/{segments[3]}/{rawPath}";
        }

        if (uri.Host.EndsWith("gitlab.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains(
                "/-/blob/",
                StringComparison.OrdinalIgnoreCase))
        {
            return url.Replace(
                "/-/blob/",
                "/-/raw/",
                StringComparison.OrdinalIgnoreCase);
        }

        if (uri.Host.EndsWith("dropbox.com", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri);
            var queryParts = builder.Query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !part.StartsWith(
                    "dl=",
                    StringComparison.OrdinalIgnoreCase))
                .Append("dl=1");
            builder.Query = string.Join('&', queryParts);
            return builder.Uri.ToString();
        }

        return url;
    }
    
    /// <summary>
    /// Crawls a page to find WAD download links.
    /// </summary>
    private async Task<List<string>> CrawlPageForWadDownloads(string pageUrl, string wadFileName, CancellationToken ct)
    {
        var downloadUrls = new List<string>();
        var baseName = Path.GetFileNameWithoutExtension(wadFileName).ToLowerInvariant();
        pageUrl = NormalizeKnownDownloadUrl(pageUrl);
        
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", AppConstants.AppInfo.WadDownloaderUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            LogUrlAttempt("Page crawl", request.Method, pageUrl);
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(AppConstants.Timeouts.PageCrawlTimeoutSeconds));
            
            using var response = await WebClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogUrlFailure("Page crawl", request.Method, pageUrl, response.StatusCode);
                return downloadUrls;
            }
            
            // Check content type - only parse HTML
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase) && 
                !contentType.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                // Might be a direct download link
                if (TryGetDirectDownloadFileName(
                        pageUrl,
                        out var directFileName)
                    && IsWebCandidateNameMatch(directFileName, baseName))
                {
                    downloadUrls.Add(pageUrl);
                }
                return downloadUrls;
            }
            
            var html = await response.Content.ReadAsStringAsync(cts.Token);
            var baseUri = new Uri(pageUrl);
            
            // Find all links
            var hrefPattern = HrefRegex();
            var matches = hrefPattern.Matches(html);
            
            foreach (Match match in matches)
            {
                var href = WebUtility.HtmlDecode(match.Groups[1].Value);
                if (!TryBuildAbsoluteUrl(baseUri, href, out var absoluteUrl)) continue;

                var matchedFileName = ExtractDownloadFileName(absoluteUrl);
                if (string.IsNullOrWhiteSpace(matchedFileName)) continue;

                if (!IsWebCandidateNameMatch(matchedFileName, baseName))
                {
                    continue;
                }

                if (!downloadUrls.Contains(absoluteUrl))
                {
                    downloadUrls.Add(absoluteUrl);
                }
            }
        }
        catch (Exception ex)
        {
            LogUrlFailure("Page crawl", HttpMethod.Get, pageUrl, ex, ct);
        }
        
        return downloadUrls;
    }

    private static bool IsWebCandidateNameMatch(
        string candidateFileName,
        string targetBaseName)
    {
        var candidateBaseName = Path
            .GetFileNameWithoutExtension(candidateFileName)
            .ToLowerInvariant();
        return candidateBaseName.Length > 0
               && targetBaseName.Length > 0
               && candidateBaseName.Equals(
                   targetBaseName,
                   StringComparison.OrdinalIgnoreCase);
    }
    
    [GeneratedRegex(@"uddg=([^&""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex UddgUrlRegex();

    [GeneratedRegex(
        @"href=[""'](/wad/[a-f0-9]{40})[""']",
        RegexOptions.IgnoreCase)]
    private static partial Regex WadArchiveResultRegex();

    [GeneratedRegex(
        @"href=[""'](/wad/[a-f0-9]{40}/download/[^""']+)[""']",
        RegexOptions.IgnoreCase)]
    private static partial Regex WadArchiveDownloadPageRegex();

    [GeneratedRegex(
        @"https://archive\.org/download/wadarchive/[^""'<>\s]+?\.(?:wad|pk3|pk7|ipk3|ipk7|pke)\.gz",
        RegexOptions.IgnoreCase)]
    private static partial Regex WadArchiveFileUrlRegex();
    
    /// <summary>
    /// Parses a page for all WAD download links and returns filename -> URL mapping.
    /// </summary>
    private async Task<List<(string FileName, string Url)>> ParseAllWadLinksFromPage(string pageUrl, CancellationToken ct)
    {
        var results = new List<(string FileName, string Url)>();
        
        try
        {
            LogUrlAttempt("Page parse request", HttpMethod.Get, pageUrl);
            using var response = await HttpClient.GetAsync(pageUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogUrlFailure("Page parse request", HttpMethod.Get, pageUrl, response.StatusCode);
                return results;
            }
            
            var html = await response.Content.ReadAsStringAsync(ct);
            var baseUri = new Uri(pageUrl);
            
            var hrefPattern = HrefRegex();
            var matches = hrefPattern.Matches(html);
            
            foreach (Match match in matches)
            {
                var href = match.Groups[1].Value;
                href = WebUtility.HtmlDecode(href);

                if (!TryBuildAbsoluteUrl(baseUri, href, out var absoluteUrl))
                {
                    continue;
                }

                var fileName = ExtractDownloadFileName(absoluteUrl);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    results.Add((fileName, absoluteUrl));
                }
            }
            
            LogVerbose($"Parsed {results.Count} WAD links from {baseUri.Host}");
        }
        catch (Exception ex)
        {
            LogUrlFailure("Page parse request", HttpMethod.Get, pageUrl, ex, ct);
        }
        
        return results;
    }
    
    /// <summary>
    /// Executes the actual download for a task.
    /// Returns true if download succeeded, false if it failed and may be retried.
    /// </summary>
    private async Task<bool> ExecuteDownloadAsync(
        WadDownloadTask task,
        string downloadPath,
        bool supportsRange,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(task.SourceUrl))
        {
            task.Status = WadDownloadStatus.Failed;
            task.StatusMessage = "No URL";
            return false;
        }
        
        var sw = Stopwatch.StartNew();
        var uri = new Uri(task.SourceUrl);
        var domain = uri.Host;
        var archivedFilesToRestore = new List<(string OriginalPath, string ArchivedPath)>();
        var downloadSucceeded = false;
        var downloadOutputOwned = false;
        string? outputPath = null;
        
        try
        {
            task.Status = WadDownloadStatus.Downloading;
            task.StatusMessage = $"Downloading ({task.ThreadCount} threads)...";
            ProgressUpdated?.Invoke(this, task);
            
            LogInfo($"Downloading {task.Wad.FileName} from {task.SourceUrl} ({FormatSizeOrUnknown(task.TotalBytes)}, {task.ThreadCount} threads)");
            
            // Use the actual filename from the download (may differ from requested if found as different extension)
            var downloadFileName = GetLowercaseFileName(task.DownloadedFileName ?? task.Wad.FileName);
            outputPath = Path.Combine(downloadPath, downloadFileName);
            
            // Ensure directory exists
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!string.IsNullOrEmpty(task.Wad.ExpectedHash) && File.Exists(outputPath))
            {
                var existingHash = await ComputeDownloadedContentHashAsync(task, outputPath, ct);
                if (string.Equals(existingHash, task.Wad.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    var existingPath = ExtractArchiveIfNeeded(task, downloadPath, outputPath, archivedFilesToRestore);
                    if (existingPath == null)
                    {
                        LogWarning($"Failed to prepare existing archive for {task.Wad.FileName}, downloading again");
                    }
                    else
                    {
                        var existingSize = 0L;
                        try { existingSize = new FileInfo(existingPath).Length; } catch { }

                        task.Status = WadDownloadStatus.AlreadyExists;
                        task.StatusMessage = "Already exists";
                        task.TotalBytes = existingSize;
                        task.BytesDownloaded = existingSize;
                        task.BytesPerSecond = 0;
                        ProgressUpdated?.Invoke(this, task);

                        LogSuccess($"Using existing file for {task.Wad.FileName}: {existingPath}");
                        downloadSucceeded = true;
                        DownloadCompleted?.Invoke(this, task);
                        return true;
                    }
                }
            }

            if (!ArchiveExistingFile(outputPath, "download", out var archivedOutputPath))
            {
                task.Status = WadDownloadStatus.Failed;
                task.StatusMessage = "Archive failed";
                task.ErrorMessage = "Could not rename existing file before download";
                task.SkipSourceRetry = true;
                return false;
            }

            if (!string.IsNullOrEmpty(archivedOutputPath))
            {
                archivedFilesToRestore.Add((outputPath, archivedOutputPath));
            }
            downloadOutputOwned = true;
            
            if (supportsRange && task.TotalBytes > 0 && task.ThreadCount > 1)
            {
                await MultiThreadedDownloadAsync(task, outputPath, ct);
            }
            else
            {
                await SingleThreadDownloadAsync(task, outputPath, ct);
            }

            var downloadedLength = new FileInfo(outputPath).Length;
            if (task.TotalBytes > 0
                && downloadedLength != task.TotalBytes)
            {
                task.Status = WadDownloadStatus.Failed;
                task.StatusMessage = "Incomplete download";
                task.ErrorMessage =
                    $"Expected {task.TotalBytes} bytes but received "
                    + $"{downloadedLength}";
                LogWarning(
                    $"Incomplete download for {task.Wad.FileName} from "
                    + $"{task.SourceUrl}: expected {task.TotalBytes} bytes, "
                    + $"received {downloadedLength}");
                return false;
            }
            
            // Extract archive if needed and find the WAD file inside
            var finalPath = ExtractArchiveIfNeeded(task, downloadPath, outputPath, archivedFilesToRestore);
            if (finalPath == null)
            {
                task.Status = WadDownloadStatus.Failed;
                task.StatusMessage = "Extraction failed";
                task.ErrorMessage = "Could not find WAD in downloaded archive";
                task.SkipSourceRetry = true;
                return false;
            }
            if (!await HasExpectedFileSignatureAsync(finalPath, ct))
            {
                TryDeleteFile(finalPath);
                task.Status = WadDownloadStatus.Failed;
                task.StatusMessage = "Invalid download";
                task.ErrorMessage =
                    "The downloaded content is not a valid WAD/package file";
                task.SkipSourceRetry = true;
                LogWarning(
                    $"Rejected invalid content for {task.Wad.FileName} "
                    + $"from {task.SourceUrl}");
                return false;
            }

            // Verify hash against the final usable file (after extraction when needed).
            if (!string.IsNullOrEmpty(task.Wad.ExpectedHash))
            {
                task.StatusMessage = "Verifying hash...";
                ProgressUpdated?.Invoke(this, task);

                var actualHash = await ComputeFileHashAsync(finalPath, ct);
                var expectedHash = task.Wad.ExpectedHash;
                if (actualHash == null || !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    var actualTrunc = actualHash?[..Math.Min(12, actualHash.Length)] ?? "null";
                    var expectedTrunc = expectedHash[..Math.Min(12, expectedHash.Length)];
                    LogWarning($"Hash mismatch for {task.Wad.FileName} from {task.SourceUrl}: expected={expectedTrunc}..., got={actualTrunc}...");

                    try
                    {
                        if (File.Exists(finalPath))
                        {
                            File.Delete(finalPath);
                        }
                    }
                    catch { }

                    task.Status = WadDownloadStatus.Failed;
                    task.StatusMessage = "Hash mismatch";
                    task.ErrorMessage = "Downloaded file hash does not match server expectation";
                    task.SkipSourceRetry = true;
                    return false; // Will trigger retry with alternate source
                }

                LogVerbose($"Hash verified for {task.Wad.FileName}: {finalPath}");
            }

            var completedBytes = task.BytesDownloaded;
            if (completedBytes <= 0)
            {
                try { completedBytes = new FileInfo(finalPath).Length; } catch { }
            }

            if (task.TotalBytes <= 0 && completedBytes > 0)
            {
                task.TotalBytes = completedBytes;
            }
            
            sw.Stop();
            var speedBytes = completedBytes > 0 ? completedBytes : task.TotalBytes;
            var speed = speedBytes / Math.Max(1, sw.Elapsed.TotalSeconds);
            
            task.Status = WadDownloadStatus.Completed;
            task.StatusMessage = "Complete";
            task.BytesDownloaded = completedBytes > 0 ? completedBytes : task.TotalBytes;
            task.BytesPerSecond = speed;
            
            // Notify UI of final speed update
            ProgressUpdated?.Invoke(this, task);
            
            LogSuccess($"Downloaded {task.Wad.FileName} from {task.SourceUrl} in {sw.Elapsed.TotalSeconds:F1}s ({FormatBytes((long)speed)}/s)");
            
            downloadSucceeded = true;
            DownloadCompleted?.Invoke(this, task);
            return true;
        }
        catch (TooManyConnectionsException)
        {
            var (currentDomainBudget, _, _, _) =
                _domainConfig.GetEffectiveDomainBudgetSettings(domain);
            _domainConfig.ReduceThreadCount(domain, currentDomainBudget);
            task.Status = WadDownloadStatus.Failed;
            task.StatusMessage = "Too many connections";
            task.ErrorMessage = "Server rejected connections - threads reduced";
            LogWarning($"Failed {task.Wad.FileName} from {task.SourceUrl}: Too many connections, reducing threads");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            task.Status = WadDownloadStatus.Cancelled;
            task.StatusMessage = "Cancelled";
            DownloadCompleted?.Invoke(this, task);
            return false;
        }
        catch (Exception ex)
        {
            task.Status = WadDownloadStatus.Failed;
            task.StatusMessage = "Failed";
            task.ErrorMessage = ex.Message;
            LogError($"Failed {task.Wad.FileName} from {task.SourceUrl}: {ex.Message}");
            return false;
        }
        finally
        {
            if (!downloadSucceeded)
            {
                if (downloadOutputOwned
                    && !string.IsNullOrEmpty(outputPath))
                {
                    TryDeleteFile(outputPath);
                }

                if (archivedFilesToRestore.Count > 0)
                {
                    RestoreArchivedFiles(archivedFilesToRestore);
                }
            }
        }
    }

    private static async Task<bool> HasExpectedFileSignatureAsync(
        string filePath,
        CancellationToken ct)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension is not (
            ".wad"
            or ".pk3"
            or ".ipk3"
            or ".pke"
            or ".pk7"
            or ".ipk7"))
        {
            return true;
        }

        var header = new byte[8];
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            header.Length,
            useAsync: true);
        var bytesRead = await stream.ReadAsync(header, ct);

        if (extension == ".wad")
        {
            return bytesRead >= 4
                   && ((header[0] == (byte)'I'
                        && header[1] == (byte)'W'
                        && header[2] == (byte)'A'
                        && header[3] == (byte)'D')
                       || (header[0] == (byte)'P'
                           && header[1] == (byte)'W'
                           && header[2] == (byte)'A'
                           && header[3] == (byte)'D'));
        }

        if (extension is ".pk7" or ".ipk7")
        {
            return bytesRead >= 6
                   && header[0] == 0x37
                   && header[1] == 0x7A
                   && header[2] == 0xBC
                   && header[3] == 0xAF
                   && header[4] == 0x27
                   && header[5] == 0x1C;
        }

        return bytesRead >= 4
               && header[0] == 0x50
               && header[1] == 0x4B
               && ((header[2] == 0x03 && header[3] == 0x04)
                   || (header[2] == 0x05 && header[3] == 0x06)
                   || (header[2] == 0x07 && header[3] == 0x08));
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }
    
    private async Task<bool> TestRangeRequestAsync(Uri uri, CancellationToken ct)
    {
        var url = uri.ToString();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            LogUrlAttempt("Range support probe", request.Method, url);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogUrlFailure("Range support probe", request.Method, url, response.StatusCode);
                return false;
            }

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                return true;
            }

            LogVerbose($"Range support probe returned {response.StatusCode} instead of PartialContent: {url}");
            return false;
        }
        catch (Exception ex)
        {
            LogUrlFailure("Range support probe", HttpMethod.Get, url, ex, ct);
            return false;
        }
    }
    
    private async Task MultiThreadedDownloadAsync(WadDownloadTask task, string outputPath, CancellationToken ct)
    {
        var uri = new Uri(task.SourceUrl!);
        var totalBytes = task.TotalBytes;
        var threadCount = task.ThreadCount;
        
        var segmentSize = (long)Math.Ceiling((double)totalBytes / threadCount);
        var segments = new List<(long Start, long End)>();
        
        for (long start = 0; start < totalBytes; start += segmentSize)
        {
            var end = Math.Min(start + segmentSize - 1, totalBytes - 1);
            segments.Add((start, end));
        }
        
        var downloadedBytes = new long[segments.Count];
        var failedSegments = new ConcurrentBag<(int SegmentIndex, bool IsConnectionLimit)>();
        using var outputHandle = File.OpenHandle(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        RandomAccess.SetLength(outputHandle, totalBytes);
        
        long ReadDownloadedBytes()
        {
            long total = 0;
            for (var i = 0; i < downloadedBytes.Length; i++)
            {
                total += Volatile.Read(ref downloadedBytes[i]);
            }

            return total;
        }

        using var progressSampler = StartProgressSampling(
            task,
            ReadDownloadedBytes);
        
        const int maxSegmentRetries = 3;

        try
        {
            var downloadTasks = segments.Select((segment, index) =>
                DownloadSegmentAsync(uri, segment.Start, segment.End, outputHandle, index, downloadedBytes, failedSegments, ct));
            
            await Task.WhenAll(downloadTasks);

            // Retry failed segments concurrently
            for (int retry = 0; retry < maxSegmentRetries && failedSegments.Count > 0; retry++)
            {
                var retryCandidates = failedSegments.ToList();
                // ConcurrentBag has no Clear(); replace with a fresh instance so new failures are tracked separately
                failedSegments = new ConcurrentBag<(int, bool)>();

                if (ct.IsCancellationRequested) break;

                LogVerbose($"Retrying {retryCandidates.Count} segment(s), attempt {retry + 1}/{maxSegmentRetries}");
                var retryTasks = retryCandidates.Select(candidate =>
                {
                    var segment = segments[candidate.SegmentIndex];
                    return DownloadSegmentAsync(uri, segment.Start, segment.End, outputHandle, candidate.SegmentIndex, downloadedBytes, failedSegments, ct);
                });

                await Task.WhenAll(retryTasks);
            }
            
            // Check for connection limit failures
            var connectionLimitFailures = failedSegments.Count(f => f.IsConnectionLimit);
            if (connectionLimitFailures > segments.Count * 0.3)
            {
                throw new TooManyConnectionsException($"{connectionLimitFailures} of {segments.Count} segments failed due to connection limits");
            }
            
            if (failedSegments.Count > 0)
            {
                throw new Exception($"{failedSegments.Count} segments failed to download after {maxSegmentRetries} retries");
            }

        }
        finally
        {
            progressSampler.ReportNow();
        }
    }
    
    private async Task DownloadSegmentAsync(
        Uri uri,
        long start,
        long end,
        Microsoft.Win32.SafeHandles.SafeFileHandle outputHandle,
        int segmentIndex,
        long[] downloadedBytes, ConcurrentBag<(int, bool)> failedSegments, CancellationToken ct)
    {
        var requestStart = start;

        try
        {
            var segmentLength = end - start + 1;
            var existingBytes = Math.Clamp(Volatile.Read(ref downloadedBytes[segmentIndex]), 0, segmentLength);
            requestStart = start + existingBytes;
            if (requestStart > end)
            {
                return;
            }

            if (requestStart > start)
            {
                LogVerbose($"Resuming segment download bytes={start}-{end} from {requestStart}");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(requestStart, end);
            
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                response.EnsureSuccessStatusCode();
            }

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new HttpRequestException(
                    $"Server did not honor range request bytes={requestStart}-{end}",
                    null,
                    response.StatusCode);
            }
            
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            
            long position = requestStart;
            var tempBuffer = new byte[AppConstants.BufferSizes.NetworkBuffer];
            int bytesRead;
            
            while ((bytesRead = await stream.ReadAsync(tempBuffer, ct)) > 0)
            {
                if (position + bytesRead - 1 > end)
                {
                    throw new IOException($"Segment response exceeded requested range bytes={requestStart}-{end}");
                }

                await RandomAccess.WriteAsync(outputHandle, tempBuffer.AsMemory(0, bytesRead), position, ct);
                position += bytesRead;
                Volatile.Write(ref downloadedBytes[segmentIndex], position - start);
            }

            if (position <= end)
            {
                var receivedBytes = position - requestStart;
                var expectedBytes = end - requestStart + 1;
                throw new IOException($"Segment ended early after {receivedBytes} of {expectedBytes} bytes");
            }
        }
        catch (HttpRequestException ex)
        {
            bool isConnectionLimit = ex.StatusCode == HttpStatusCode.TooManyRequests ||
                                    ex.StatusCode == HttpStatusCode.ServiceUnavailable ||
                                    ex.InnerException is System.Net.Sockets.SocketException;
            var range = $"{requestStart}-{end}";
            if (ex.StatusCode.HasValue)
            {
                LogUrlFailure($"Segment download bytes={range}", HttpMethod.Get, uri.ToString(), ex.StatusCode.Value);
            }
            else
            {
                LogUrlFailure($"Segment download bytes={range}", HttpMethod.Get, uri.ToString(), ex, ct);
            }
            failedSegments.Add((segmentIndex, isConnectionLimit));
        }
        catch (Exception ex)
        {
            LogUrlFailure($"Segment download bytes={requestStart}-{end}", HttpMethod.Get, uri.ToString(), ex, ct);
            failedSegments.Add((segmentIndex, false));
        }
    }
    
    private async Task SingleThreadDownloadAsync(WadDownloadTask task, string outputPath, CancellationToken ct)
    {
        LogUrlAttempt("Download request", HttpMethod.Get, task.SourceUrl!);
        using var response = await HttpClient.GetAsync(task.SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, AppConstants.BufferSizes.FileStreamBuffer, useAsync: true);
        
        var buffer = new byte[AppConstants.BufferSizes.NetworkBuffer];
        int bytesRead;
        long downloadedBytes = 0;
        using var progressSampler = StartProgressSampling(
            task,
            () => Interlocked.Read(ref downloadedBytes));
        
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            Interlocked.Add(ref downloadedBytes, bytesRead);
        }

        progressSampler.ReportNow();
    }
    
    private async Task<long> GetFileSizeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            LogUrlAttempt("HEAD request", request.Method, url);
            using var response = await HttpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                if (IsHtmlResponse(response))
                {
                    LogVerbose(
                        $"HEAD request returned HTML instead of a file: {url}");
                    return -1;
                }

                if (response.Content.Headers.ContentLength.HasValue)
                {
                    return response.Content.Headers.ContentLength.Value;
                }

                LogVerbose($"HEAD request missing Content-Length, retrying with GET: {url}");
            }
            else
            {
                LogUrlFailure("HEAD request", request.Method, url, response.StatusCode);
                LogVerbose($"HEAD request falling back to GET: {url}");
            }
        }
        catch (Exception ex)
        {
            LogUrlFailure("HEAD request", HttpMethod.Head, url, ex, ct);
            LogVerbose($"HEAD request falling back to GET after failure: {url}");
        }

        try
        {
            using var fallbackRequest = new HttpRequestMessage(HttpMethod.Get, url);
            fallbackRequest.Headers.Range =
                new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            LogUrlAttempt("File probe fallback", fallbackRequest.Method, url);
            using var fallbackResponse = await HttpClient.SendAsync(fallbackRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (fallbackResponse.IsSuccessStatusCode
                && IsHtmlResponse(fallbackResponse))
            {
                LogVerbose(
                    $"File probe returned HTML instead of a file: {url}");
                return -1;
            }

            if (fallbackResponse.IsSuccessStatusCode
                && fallbackResponse.Content.Headers.ContentRange?.Length
                    is { } rangeLength)
            {
                return rangeLength;
            }

            if (fallbackResponse.IsSuccessStatusCode
                && fallbackResponse.Content.Headers.ContentLength.HasValue)
            {
                return fallbackResponse.Content.Headers.ContentLength.Value;
            }

            if (!fallbackResponse.IsSuccessStatusCode)
            {
                LogUrlFailure(
                    "File probe fallback",
                    fallbackRequest.Method,
                    url,
                    fallbackResponse.StatusCode);
            }
            else
            {
                LogWarning(
                    $"File probe succeeded without a total size: GET {url}");
                return 0;
            }
        }
        catch (Exception ex)
        {
            LogUrlFailure(
                "File probe fallback",
                HttpMethod.Get,
                url,
                ex,
                ct);
        }
        return -1;
    }

    private static bool IsHtmlResponse(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return mediaType != null
               && (mediaType.Equals(
                       "text/html",
                       StringComparison.OrdinalIgnoreCase)
                   || mediaType.Equals(
                       "application/xhtml+xml",
                       StringComparison.OrdinalIgnoreCase));
    }
    
    /// <summary>
    /// Gets filename variants to try when searching.
    /// When the file has a supported extension, only that exact filename is used.
    /// When a file has no extension or an unsupported one, tries all supported extensions.
    /// </summary>
    private static List<string> GetFilenameVariants(string filename)
    {
        var variants = new List<string>();
        var normalizedFileName = GetLowercaseFileName(filename);
        var baseName = Path.GetFileNameWithoutExtension(normalizedFileName);
        var originalExt = Path.GetExtension(normalizedFileName);
        
        // If no extension, try all supported extensions (prioritize common WAD formats)
        if (string.IsNullOrEmpty(originalExt))
        {
            // Priority order: .wad, .pk3, .pk7, .zip, then others
            var priorityOrder = new[] { ".wad", ".pk3", ".pk7", ".zip", ".7z", ".rar" };
            foreach (var ext in priorityOrder)
            {
                if (SupportedExtensions.Contains(ext))
                    variants.Add(baseName + ext);
            }
            // Add any remaining supported extensions
            foreach (var ext in SupportedExtensions)
            {
                var variant = baseName + ext;
                if (!variants.Contains(variant, StringComparer.OrdinalIgnoreCase))
                    variants.Add(variant);
            }
        }
        // If the file has a supported extension, only use that exact filename
        else if (SupportedExtensions.Contains(originalExt))
        {
            variants.Add(normalizedFileName);
        }
        // If unsupported extension, try all supported extensions
        else
        {
            foreach (var ext in SupportedExtensions)
            {
                var variant = baseName + ext;
                if (!variants.Contains(variant, StringComparer.OrdinalIgnoreCase))
                    variants.Add(variant);
            }
        }
        
        return variants;
    }
    
    /// <summary>
    /// Extracts archive if downloaded file is a zip/7z/rar and looks for the requested WAD inside.
    /// Returns the path to the extracted WAD file, or null if extraction failed or file not found.
    /// </summary>
    private string? ExtractArchiveIfNeeded(
        WadDownloadTask task,
        string downloadPath,
        string archivePath,
        List<(string OriginalPath, string ArchivedPath)> archivedFilesToRestore)
    {
        var archiveExt = Path.GetExtension(archivePath).ToLowerInvariant();

        if (archiveExt == ".gz")
        {
            return ExtractGzipDownload(
                task,
                downloadPath,
                archivePath,
                archivedFilesToRestore);
        }
        
        // Only process archive files (pk3/pk7/wad are already usable directly)
        if (!WadExtensions.IsArchiveExtension(archiveExt))
            return archivePath;
        
        // Check if the requested file is the zip itself (some servers serve pk3 as zip)
        var requestedExt = Path.GetExtension(task.Wad.FileName).ToLowerInvariant();
        if (requestedExt == archiveExt)
            return archivePath;

        try
        {
            task.StatusMessage = "Extracting archive...";
            ProgressUpdated?.Invoke(this, task);
            
            using var archive = ArchiveFactory.Open(archivePath);
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            var matchingEntry = FindMatchingArchiveEntry(entries, task);
            
            if (matchingEntry != null)
            {
                var outputFileName = Path.GetFileName(matchingEntry.Key ?? "unknown.wad");
                var outputPath = Path.Combine(downloadPath, outputFileName);
                
                // Extract the file
                if (!ArchiveExistingFile(outputPath, "extraction", out var archivedOutputPath))
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(archivedOutputPath))
                {
                    archivedFilesToRestore.Add((outputPath, archivedOutputPath));
                }
                
                matchingEntry.WriteToFile(outputPath, new ExtractionOptions { Overwrite = true });
                
                LogSuccess($"Extracted {outputFileName} from archive {archivePath}");
                
                // Clean up the archive
                try { File.Delete(archivePath); } catch { }
                
                return outputPath;
            }
            else
            {
                // No WAD found in archive - list contents for debugging
                var contents = string.Join(", ", entries.Take(5).Select(e => Path.GetFileName(e.Key ?? "")));
                LogWarning($"No matching WAD found in archive {archivePath}. Contents: {contents}...");
                return null;
            }
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to extract archive {archivePath}: {ex.Message}");
            return null;
        }
    }

    private string? ExtractGzipDownload(
        WadDownloadTask task,
        string downloadPath,
        string archivePath,
        List<(string OriginalPath, string ArchivedPath)> archivedFilesToRestore)
    {
        var outputFileName = GetLowercaseFileName(task.Wad.FileName);
        var outputPath = Path.Combine(downloadPath, outputFileName);

        try
        {
            task.StatusMessage = "Decompressing archive...";
            ProgressUpdated?.Invoke(this, task);

            if (!ArchiveExistingFile(
                    outputPath,
                    "gzip extraction",
                    out var archivedOutputPath))
            {
                return null;
            }
            if (!string.IsNullOrEmpty(archivedOutputPath))
            {
                archivedFilesToRestore.Add(
                    (outputPath, archivedOutputPath));
            }

            using var input = File.OpenRead(archivePath);
            using var gzip =
                new GZipStream(input, CompressionMode.Decompress);
            using var output = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            gzip.CopyTo(output);

            try
            {
                File.Delete(archivePath);
            }
            catch
            {
            }

            LogSuccess(
                $"Decompressed {task.Wad.FileName} from WAD Archive");
            return outputPath;
        }
        catch (Exception ex)
        {
            LogWarning(
                $"Failed to decompress {archivePath}: {ex.Message}");
            return null;
        }
    }
    
    private static string FormatBytes(long bytes) => FormatUtils.FormatBytes(bytes);
    private static string FormatSizeOrUnknown(long bytes) => bytes > 0 ? FormatBytes(bytes) : "size unknown";

    private static IArchiveEntry? FindMatchingArchiveEntry(IEnumerable<IArchiveEntry> entries, WadDownloadTask task)
    {
        var baseName = Path.GetFileNameWithoutExtension(task.Wad.FileName).ToLowerInvariant();

        var exactMatch = entries.FirstOrDefault(entry =>
            Path.GetFileName(entry.Key ?? string.Empty).Equals(task.Wad.FileName, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
        {
            return exactMatch;
        }

        foreach (var entry in entries)
        {
            var entryName = Path.GetFileName(entry.Key ?? string.Empty);
            var entryBaseName = Path.GetFileNameWithoutExtension(entryName).ToLowerInvariant();
            var entryExt = Path.GetExtension(entryName).ToLowerInvariant();
            if (entryBaseName == baseName && WadExtensions.IsWadExtension(entryExt))
            {
                return entry;
            }
        }

        return entries.FirstOrDefault(entry =>
        {
            var entryExt = Path.GetExtension(entry.Key ?? string.Empty).ToLowerInvariant();
            return WadExtensions.IsWadExtension(entryExt);
        });
    }

    private static async Task<string?> ComputeStreamHashAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var md5 = MD5.Create();
            var hashBytes = await md5.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ComputeDownloadedContentHashAsync(WadDownloadTask task, string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        var archiveExt = Path.GetExtension(filePath).ToLowerInvariant();
        var requestedExt = Path.GetExtension(task.Wad.FileName).ToLowerInvariant();

        if (archiveExt == ".gz")
        {
            try
            {
                await using var compressedStream = File.OpenRead(filePath);
                await using var gzipStream =
                    new GZipStream(compressedStream, CompressionMode.Decompress);
                return await ComputeStreamHashAsync(gzipStream, ct);
            }
            catch
            {
                return null;
            }
        }

        if (!WadExtensions.IsArchiveExtension(archiveExt) || requestedExt == archiveExt)
        {
            return await ComputeFileHashAsync(filePath, ct);
        }

        try
        {
            using var archive = ArchiveFactory.Open(filePath);
            var matchingEntry = FindMatchingArchiveEntry(archive.Entries.Where(entry => !entry.IsDirectory), task);
            if (matchingEntry == null)
            {
                return null;
            }

            await using var stream = matchingEntry.OpenEntryStream();
            return await ComputeStreamHashAsync(stream, ct);
        }
        catch
        {
            return null;
        }
    }
    
    private static async Task<string?> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return null;
        
        try
        {
            await using var stream = File.OpenRead(filePath);
            return await ComputeStreamHashAsync(stream, ct);
        }
        catch
        {
            return null;
        }
    }

    private bool ArchiveExistingFile(string filePath, string operation, out string? archivedPath)
    {
        archivedPath = null;
        if (!File.Exists(filePath))
        {
            return true;
        }

        archivedPath = WadManager.Instance.ArchiveWadWithHash(filePath);
        if (archivedPath == null)
        {
            LogError($"Failed to archive existing file before {operation}: {filePath}");
            return false;
        }

        LogWarning($"Archived existing file before {operation}: {filePath} -> {archivedPath}");
        return true;
    }

    private void RestoreArchivedFiles(List<(string OriginalPath, string ArchivedPath)> archivedFiles)
    {
        foreach (var (originalPath, archivedPath) in archivedFiles.AsEnumerable().Reverse())
        {
            try
            {
                if (!File.Exists(archivedPath))
                {
                    continue;
                }

                if (File.Exists(originalPath))
                {
                    File.Delete(originalPath);
                }

                File.Move(archivedPath, originalPath);
                LogWarning($"Restored archived file after failed update: {archivedPath} -> {originalPath}");
            }
            catch (Exception ex)
            {
                LogError($"Failed to restore archived file {archivedPath} -> {originalPath}: {ex.Message}");
            }
        }
    }
    
    [GeneratedRegex(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();
    
    public void Dispose()
    {
        if (!_disposed)
        {
            // Note: HttpClient instances are static and shared - do not dispose them
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Exception thrown when too many connections are attempted.
/// </summary>
public class TooManyConnectionsException : Exception
{
    public TooManyConnectionsException(string message) : base(message) { }
}
