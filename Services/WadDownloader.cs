using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using SharpCompress.Archives;
using SharpCompress.Common;
using ZScape.Models;
using ZScape.Utilities;
using static ZScape.Utilities.FormatUtils;

namespace ZScape.Services;

/// <summary>
/// Downloads WAD files from various sources using multi-threaded downloading.
/// Supports parallel URL searching with streaming downloads that start immediately.
/// </summary>
public partial class WadDownloader : IDisposable
{
    /// <summary>
    /// /idgames Archive fullsort.gz index URL (used for file lookups since API is blocked).
    /// </summary>
    private const string IdgamesIndexUrl = "https://www.quaddicted.com/files/idgames/fullsort.gz";
    
    /// <summary>
    /// /idgames Archive mirror base URLs for constructing download links.
    /// </summary>
    private static readonly string[] IdgamesMirrors =
    [
        "https://youfailit.net/pub/idgames/",
        "https://www.quaddicted.com/files/idgames/",
        "https://ftpmirror1.infania.net/pub/idgames/",
        "https://www.gamers.org/pub/idgames/",
    ];
    
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
    
    /// <summary>
    /// Cached idgames index (filename -> (path, size) mapping).
    /// </summary>
    private static Dictionary<string, (string Path, long Size)>? _idgamesIndex;
    private static readonly SemaphoreSlim _idgamesIndexLock = new(1, 1);
    private static DateTime _idgamesIndexExpiry = DateTime.MinValue;
    
    /// <summary>
    /// Browser-like User-Agent for services that block bot requests (idgames, DuckDuckGo).
    /// </summary>
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    
    /// <summary>
    /// Supported WAD file extensions to search for (uses centralized WadExtensions).
    /// </summary>
    private static string[] SupportedExtensions => Utilities.WadExtensions.AllSupportedExtensions;
    
    // Static HttpClient instances - reused across all WadDownloader instances for connection pooling
    // HttpClient is thread-safe and designed to be reused
    private static readonly Lazy<HttpClient> _sharedHttpClient = new(CreateHttpClient);
    private static readonly Lazy<HttpClient> _sharedWebClient = new(CreateWebClient);
    
    private readonly DomainThreadConfig _domainConfig = DomainThreadConfig.Instance;
    private readonly List<string> _downloadSites;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _domainSemaphores = new();
    private bool _disposed;
    
    // Property accessors for the shared HttpClient instances
    private static HttpClient HttpClient => _sharedHttpClient.Value;
    private static HttpClient WebClient => _sharedWebClient.Value;
    
    /// <summary>
    /// Whether to search the /idgames Archive. Default is true.
    /// </summary>
    public bool IdgamesEnabled { get; set; } = true;
    
    /// <summary>
    /// Whether to use web search (DuckDuckGo) as a last resort fallback. Default is true.
    /// </summary>
    public bool WebSearchEnabled { get; set; } = true;
    
    /// <summary>
    /// Default WAD hosting sites.
    /// </summary>
    public static readonly List<string> DefaultSites =
    [
        "https://action.fapnow.xyz/zandronum/download.php?file=%WadName%",
        "https://allfearthesentinel.com/zandronum/download.php?file=%WadName%",
        "https://euroboros.net/zandronum/download.php?file=%WadName%",
        "https://audrealms.org/zandronum/download.php?file=%WadName%",
        "https://pizza-doom.it/wads/download.php?file=%WadName%",
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
    /// Gets the list of download sites.
    /// </summary>
    public List<string> DownloadSites => _downloadSites;
    
    private void Log(LogLevel level, string message) => LogMessage?.Invoke(this, (level, message));
    private void LogVerbose(string message) => Log(LogLevel.Verbose, message);
    private void LogInfo(string message) => Log(LogLevel.Info, message);
    private void LogWarning(string message) => Log(LogLevel.Warning, message);
    private void LogError(string message) => Log(LogLevel.Error, message);
    private void LogSuccess(string message) => Log(LogLevel.Success, message);
    
    /// <summary>
    /// Downloads multiple WADs with streaming - starts downloads immediately as URLs are found.
    /// </summary>
    public async Task DownloadWadsAsync(
        IEnumerable<WadDownloadTask> tasks, 
        string downloadPath,
        CancellationToken cancellationToken = default)
    {
        var taskList = tasks.ToList();
        if (taskList.Count == 0) return;
        
        LogInfo($"Starting download of {taskList.Count} WAD(s)");
        
        // Track tasks by filename (lowercase)
        var tasksByName = new ConcurrentDictionary<string, WadDownloadTask>(
            taskList.ToDictionary(t => t.Wad.FileName.ToLowerInvariant(), t => t));
        
        // Track which tasks have been queued (first URL found and sent to channel)
        var queuedTasks = new ConcurrentDictionary<string, bool>();
        
        // Lock object for thread-safe alternate URL addition
        var urlLock = new object();
        
        // Channel for streaming ready-to-download tasks to domain workers
        var downloadChannel = Channel.CreateUnbounded<(WadDownloadTask Task, string Url, long Size, string Domain)>();
        
        // Calculate total sources to search per task
        var siteCount = _downloadSites.Count;
        
        // Set all tasks to searching
        foreach (var task in taskList)
        {
            task.Status = WadDownloadStatus.Searching;
            task.SitesSearched = 0;
            // Each task searches: download sites + 1 if it has a server URL
            task.TotalSitesToSearch = siteCount + (string.IsNullOrEmpty(task.Wad.ServerUrl) ? 0 : 1);
            task.StatusMessage = $"Searching (0/{task.TotalSitesToSearch})...";
            task.AlternateUrls.Clear();
            task.RetryCount = 0;
            ProgressUpdated?.Invoke(this, task);
        }
        
        // Track active domain workers
        var domainWorkers = new ConcurrentDictionary<string, Task>();
        var domainQueues = new ConcurrentDictionary<string, ConcurrentQueue<(WadDownloadTask Task, string Url, long Size)>>();
        var searchComplete = false;
        
        // Start domain worker manager (processes items from channel)
        var workerManagerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var (task, url, size, domain) in downloadChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    // Get or create queue for this domain
                    var queue = domainQueues.GetOrAdd(domain, _ => new ConcurrentQueue<(WadDownloadTask, string, long)>());
                    queue.Enqueue((task, url, size));
                    
                    // Start domain worker if not already running
                    _ = domainWorkers.GetOrAdd(domain, d =>
                    {
                        LogVerbose($"Started worker for domain: {d}");
                        return Task.Run(async () =>
                        {
                            await DomainWorkerAsync(d, domainQueues[d], downloadPath, () => searchComplete, cancellationToken);
                            domainWorkers.TryRemove(d, out _);
                        }, cancellationToken);
                    });
                }
            }
            catch (OperationCanceledException) { }
        }, cancellationToken);
        
        // Group by server URL for efficient batch parsing
        var serverGroups = taskList
            .Where(t => !string.IsNullOrEmpty(t.Wad.ServerUrl))
            .GroupBy(t => t.Wad.ServerUrl!)
            .ToList();
        
        // Start URL discovery in ordered sequence:
        // 1. Server URLs (in parallel per server, but before download sites)
        // 2. Download sites (sequentially, top to bottom)
        // 3. idgames Archive
        // 4. Web search (DuckDuckGo) fallback
        
        _ = Task.Run(async () =>
        {
            try
            {
                // Phase 1: Search server URLs (in parallel per server)
                if (serverGroups.Count > 0)
                {
                    var serverTasks = serverGroups.Select(serverGroup =>
                        Task.Run(async () =>
                        {
                            var serverUrl = serverGroup.Key;
                            var serverTasks = serverGroup.ToList();
                            await SearchServerUrlAsync(serverUrl, serverTasks, tasksByName, queuedTasks, urlLock, downloadChannel.Writer, cancellationToken);
                        }, cancellationToken));
                    
                    await Task.WhenAll(serverTasks);
                }
                
                // Phase 2: Search download sites (sequentially, in order)
                foreach (var site in _downloadSites)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    await SearchSiteAsync(site, taskList, tasksByName, queuedTasks, urlLock, downloadChannel.Writer, cancellationToken);
                }
                
                // Phase 3: Search /idgames Archive
                if (IdgamesEnabled && !cancellationToken.IsCancellationRequested)
                {
                    await SearchIdgamesAsync(taskList, tasksByName, queuedTasks, urlLock, downloadChannel.Writer, cancellationToken);
                }
                
                // Phase 4: Web search fallback (DuckDuckGo)
                if (WebSearchEnabled && !cancellationToken.IsCancellationRequested)
                {
                    await SearchWebAsync(taskList, tasksByName, queuedTasks, urlLock, downloadChannel.Writer, cancellationToken);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                // Mark search as complete and close channel
                searchComplete = true;
                downloadChannel.Writer.Complete();
                
                // Mark any unfound WADs as failed
                foreach (var (fileName, task) in tasksByName)
                {
                    if (!queuedTasks.ContainsKey(fileName))
                    {
                        task.Status = WadDownloadStatus.Failed;
                        task.StatusMessage = "Not found";
                        task.ErrorMessage = "WAD not found on any download source";
                        LogWarning($"Not found: {task.Wad.FileName}");
                        DownloadCompleted?.Invoke(this, task);
                    }
                }
            }
        }, cancellationToken);
        
        // Wait for worker manager to finish processing channel
        try
        {
            await workerManagerTask;
        }
        catch (OperationCanceledException) { }
        
        // Wait for all domain workers to complete
        if (domainWorkers.Count > 0)
        {
            await Task.WhenAll(domainWorkers.Values);
        }
        
        // Summary
        var completed = taskList.Count(t => t.Status == WadDownloadStatus.Completed);
        var failed = taskList.Count(t => t.Status == WadDownloadStatus.Failed);
        LogInfo($"Download complete: {completed} succeeded, {failed} failed");
    }
    
    /// <summary>
    /// Searches a server URL for all needed WADs and streams found URLs to channel.
    /// First URL queues the task, subsequent URLs are stored as alternates for retry.
    /// </summary>
    private async Task SearchServerUrlAsync(
        string serverUrl,
        List<WadDownloadTask> tasks,
        ConcurrentDictionary<string, WadDownloadTask> tasksByName,
        ConcurrentDictionary<string, bool> queuedTasks,
        object urlLock,
        ChannelWriter<(WadDownloadTask, string, long, string)> channel,
        CancellationToken ct)
    {
        try
        {
            LogInfo($"Checking server: {new Uri(serverUrl).Host}");
            
            // Parse server page once for all links
            var allLinks = await ParseAllWadLinksFromPage(serverUrl, ct);
            var neededWads = tasks.ToDictionary(t => t.Wad.FileName.ToLowerInvariant(), t => t);
            
            foreach (var (fileName, url) in allLinks)
            {
                var lowerName = fileName.ToLowerInvariant();
                if (!neededWads.TryGetValue(lowerName, out var task)) continue;
                
                var size = await GetFileSizeAsync(url, ct);
                if (size <= 0) continue;
                
                var domain = new Uri(url).Host;
                
                // First URL found: queue the download
                if (queuedTasks.TryAdd(lowerName, true))
                {
                    task.SourceUrl = url;
                    task.TotalBytes = size;
                    task.Status = WadDownloadStatus.Queued;
                    task.StatusMessage = $"Queued ({domain})";
                    ProgressUpdated?.Invoke(this, task);
                    
                    LogSuccess($"Found {fileName} at {domain} ({FormatBytes(size)})");
                    await channel.WriteAsync((task, url, size, domain), ct);
                }
                else
                {
                    // Add as alternate source for retry
                    lock (urlLock)
                    {
                        if (!task.AlternateUrls.Any(a => a.Url == url))
                        {
                            task.AlternateUrls.Add((url, size));
                            LogVerbose($"Added alternate source for {fileName}: {domain}");
                        }
                    }
                }
            }
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
    /// Searches a download site for all needed WADs and streams found URLs to channel.
    /// First URL queues the task, subsequent URLs are stored as alternates for retry.
    /// </summary>
    private async Task SearchSiteAsync(
        string site,
        List<WadDownloadTask> tasks,
        ConcurrentDictionary<string, WadDownloadTask> tasksByName,
        ConcurrentDictionary<string, bool> queuedTasks,
        object urlLock,
        ChannelWriter<(WadDownloadTask, string, long, string)> channel,
        CancellationToken ct)
    {
        try
        {
            var siteUri = new Uri(site.Contains("%WadName%") ? site.Replace("%WadName%", "test") : site);
            var siteHost = siteUri.Host;
            
            // Check for %WadName% template URLs - direct download links
            if (site.Contains("%WadName%", StringComparison.OrdinalIgnoreCase))
            {
                // Check each WAD with HEAD request - try multiple extensions if needed
                // Filter to tasks still searching or failed (can benefit from new sources)
                var tasksToCheck = tasks.Where(t => 
                    t.Status == WadDownloadStatus.Searching || 
                    t.Status == WadDownloadStatus.Failed ||
                    !queuedTasks.ContainsKey(t.Wad.FileName.ToLowerInvariant())).ToList();
                
                var checkTasks = tasksToCheck.Select(async task =>
                {
                    var lowerName = task.Wad.FileName.ToLowerInvariant();
                    var baseName = Path.GetFileNameWithoutExtension(lowerName);
                    
                    // Build list of filenames to try
                    var filenamesToTry = GetFilenameVariants(task.Wad.FileName);
                    
                    foreach (var filename in filenamesToTry)
                    {
                        if (queuedTasks.ContainsKey(lowerName)) break; // Already found
                        
                        var url = site.Replace("%WadName%", filename.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
                        var size = await GetFileSizeAsync(url, ct);
                        
                        if (size <= 0) continue;
                        
                        // First URL found: queue the download
                        if (queuedTasks.TryAdd(lowerName, true))
                        {
                            task.SourceUrl = url;
                            task.TotalBytes = size;
                            task.Status = WadDownloadStatus.Queued;
                            task.StatusMessage = $"Queued ({siteHost})";
                            task.DownloadedFileName = filename; // Track actual downloaded filename
                            ProgressUpdated?.Invoke(this, task);
                            
                            var foundAs = filename != task.Wad.FileName ? $" (as {filename})" : "";
                            LogSuccess($"Found {task.Wad.FileName}{foundAs} at {siteHost} ({FormatBytes(size)})");
                            await channel.WriteAsync((task, url, size, siteHost), ct);
                            break;
                        }
                        else
                        {
                            // Add as alternate source for retry
                            lock (urlLock)
                            {
                                if (!task.AlternateUrls.Any(a => a.Url == url))
                                {
                                    task.AlternateUrls.Add((url, size));
                                    LogVerbose($"Added alternate source for {task.Wad.FileName}: {siteHost}");
                                }
                            }
                            break;
                        }
                    }
                });
                
                await Task.WhenAll(checkTasks);
            }
            else
            {
                // Parse page for all WAD links
                LogVerbose($"Parsing {siteHost}...");
                var allLinks = await ParseAllWadLinksFromPage(site, ct);
                
                // Build lookup maps for tasks still searching or failed (can benefit from new sources)
                var neededWads = new Dictionary<string, WadDownloadTask>(StringComparer.OrdinalIgnoreCase);
                foreach (var task in tasks)
                {
                    // Only include tasks that are still searching, failed, or not yet queued
                    if (task.Status != WadDownloadStatus.Searching && 
                        task.Status != WadDownloadStatus.Failed &&
                        queuedTasks.ContainsKey(task.Wad.FileName.ToLowerInvariant()))
                        continue;
                    
                    var lowerName = task.Wad.FileName.ToLowerInvariant();
                    neededWads[lowerName] = task;
                    
                    // Also add base name for matching archives
                    var baseName = Path.GetFileNameWithoutExtension(lowerName);
                    if (!neededWads.ContainsKey(baseName + ".zip"))
                        neededWads[baseName + ".zip"] = task;
                }
                
                foreach (var (fileName, url) in allLinks)
                {
                    var lowerName = fileName.ToLowerInvariant();
                    if (!neededWads.TryGetValue(lowerName, out var task)) continue;
                    
                    var taskKey = task.Wad.FileName.ToLowerInvariant();
                    
                    var size = await GetFileSizeAsync(url, ct);
                    if (size <= 0) continue;
                    
                    // First URL found: queue the download (use task's original name as key)
                    if (queuedTasks.TryAdd(taskKey, true))
                    {
                        task.SourceUrl = url;
                        task.TotalBytes = size;
                        task.Status = WadDownloadStatus.Queued;
                        task.StatusMessage = $"Queued ({siteHost})";
                        task.DownloadedFileName = fileName; // Track actual filename for extraction
                        ProgressUpdated?.Invoke(this, task);
                        
                        var foundAs = !lowerName.Equals(taskKey, StringComparison.OrdinalIgnoreCase) ? $" (as {fileName})" : "";
                        LogSuccess($"Found {task.Wad.FileName}{foundAs} at {siteHost} ({FormatBytes(size)})");
                        await channel.WriteAsync((task, url, size, siteHost), ct);
                    }
                    else
                    {
                        // Add as alternate source for retry
                        lock (urlLock)
                        {
                            if (!task.AlternateUrls.Any(a => a.Url == url))
                            {
                                task.AlternateUrls.Add((url, size));
                                LogVerbose($"Added alternate source for {fileName}: {siteHost}");
                            }
                        }
                    }
                }
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
        ConcurrentDictionary<string, WadDownloadTask> tasksByName,
        ConcurrentDictionary<string, bool> queuedTasks,
        object urlLock,
        ChannelWriter<(WadDownloadTask, string, long, string)> channel,
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
                
                var lowerName = task.Wad.FileName.ToLowerInvariant();
                
                // Skip if already found
                if (queuedTasks.ContainsKey(lowerName)) continue;
                
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
                        
                    // First URL found: queue the download
                    if (queuedTasks.TryAdd(lowerName, true))
                    {
                        task.SourceUrl = workingUrl;
                        task.TotalBytes = actualSize;
                        task.Status = WadDownloadStatus.Queued;
                        task.StatusMessage = $"Queued (/idgames - {domain})";
                        task.DownloadedFileName = Path.GetFileName(filePath); // Actual filename (usually .zip)
                        ProgressUpdated?.Invoke(this, task);
                        
                        LogSuccess($"Found {task.Wad.FileName} on /idgames ({domain}, {FormatBytes(actualSize)})");
                        await channel.WriteAsync((task, workingUrl, actualSize, domain), ct);
                    }
                    else
                    {
                        // Add as alternate source
                        lock (urlLock)
                        {
                            if (!task.AlternateUrls.Any(a => a.Url == workingUrl))
                            {
                                task.AlternateUrls.Add((workingUrl, actualSize));
                                LogVerbose($"Added /idgames alternate for {task.Wad.FileName}: {domain}");
                            }
                        }
                    }
                    
                    // Add remaining mirrors as alternates
                    foreach (var altUrl in downloadUrls.Where(u => u != workingUrl))
                    {
                        lock (urlLock)
                        {
                            if (!task.AlternateUrls.Any(a => a.Url == altUrl))
                            {
                                task.AlternateUrls.Add((altUrl, actualSize)); // Assume same size
                            }
                        }
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
            
            LogVerbose("/idgames: downloading index...");
            
            using var request = new HttpRequestMessage(HttpMethod.Get, IdgamesIndexUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(AppConstants.Timeouts.WebRequestTimeoutSeconds));
            
            var response = await WebClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogVerbose($"/idgames index download failed: {response.StatusCode}");
                return null;
            }
            
            // Download and decompress
            await using var compressedStream = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream);
            
            var index = new Dictionary<string, (string Path, long Size)>(StringComparer.OrdinalIgnoreCase);
            string? line;
            
            // fullsort.gz format: "YYYY/MM/DD  SIZE  path/to/file.zip"
            // Example: "2025/07/13  652517  music/avmidi.zip"
            var linePattern = IdgamesIndexLineRegex();
            
            while ((line = await reader.ReadLineAsync(cts.Token)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                var match = linePattern.Match(line);
                if (!match.Success) continue;
                
                var size = long.TryParse(match.Groups[1].Value, out var s) ? s : 0;
                var filePath = match.Groups[2].Value.Trim();
                
                // Extract filename from path
                var fileName = Path.GetFileName(filePath);
                if (string.IsNullOrEmpty(fileName)) continue;
                
                // Only index .zip files (WADs are stored as .zip in idgames)
                if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                
                // Store: filename -> (full path, size) - may have duplicates, last wins
                index[fileName] = (filePath, size);
            }
            
            _idgamesIndex = index;
            _idgamesIndexExpiry = DateTime.UtcNow.AddHours(24);
            
            LogVerbose($"/idgames: indexed {index.Count} files");
            return index;
        }
        catch (Exception ex)
        {
            LogVerbose($"/idgames index error: {ex.Message}");
            return null;
        }
        finally
        {
            _idgamesIndexLock.Release();
        }
    }
    
    [GeneratedRegex(@"^\d{4}/\d{2}/\d{2}\s+(\d+)\s+(.+)$")]
    private static partial Regex IdgamesIndexLineRegex();
    
    /// <summary>
    /// Searches the web using DuckDuckGo as a last resort fallback.
    /// Crawls result pages to find download links.
    /// </summary>
    private async Task SearchWebAsync(
        List<WadDownloadTask> tasks,
        ConcurrentDictionary<string, WadDownloadTask> tasksByName,
        ConcurrentDictionary<string, bool> queuedTasks,
        object urlLock,
        ChannelWriter<(WadDownloadTask, string, long, string)> channel,
        CancellationToken ct)
    {
        try
        {
            // Only search for WADs not already found
            var notFoundTasks = tasks.Where(t => !queuedTasks.ContainsKey(t.Wad.FileName.ToLowerInvariant())).ToList();
            if (notFoundTasks.Count == 0)
            {
                LogVerbose("Web search: all WADs already found, skipping");
                return;
            }
            
            LogInfo($"Web search: looking for {notFoundTasks.Count} unfound WAD(s)...");
            
            foreach (var task in notFoundTasks)
            {
                if (ct.IsCancellationRequested) break;
                
                var lowerName = task.Wad.FileName.ToLowerInvariant();
                
                // Skip if found by another source while we were waiting
                if (queuedTasks.ContainsKey(lowerName)) continue;
                
                try
                {
                    var baseName = Path.GetFileNameWithoutExtension(task.Wad.FileName);
                    var searchQuery = $"\"{baseName}\" download doom";
                    var encodedQuery = Uri.EscapeDataString(searchQuery);
                    
                    // Use DuckDuckGo HTML search
                    var searchUrl = $"https://html.duckduckgo.com/html?q={encodedQuery}";
                    LogVerbose($"Web search: {searchQuery}");
                    
                    using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                    request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                    request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                    request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.5");
                    
                    var response = await WebClient.SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        LogVerbose($"Web search failed: {response.StatusCode}");
                        continue;
                    }
                    
                    var html = await response.Content.ReadAsStringAsync(ct);
                    
                    // Extract result page URLs from search results
                    var resultPageUrls = ExtractSearchResultPageUrls(html);
                    
                    if (resultPageUrls.Count == 0)
                    {
                        LogVerbose($"Web search: no result pages found for {task.Wad.FileName}");
                        continue;
                    }
                    
                    LogVerbose($"Web search: crawling {resultPageUrls.Count} result pages for {task.Wad.FileName}");
                    
                    // Crawl each result page to find download links
                    foreach (var pageUrl in resultPageUrls.Take(5)) // Limit to first 5 pages
                    {
                        if (ct.IsCancellationRequested) break;
                        if (queuedTasks.ContainsKey(lowerName)) break; // Already found
                        
                        try
                        {
                            LogVerbose($"Web search: crawling {new Uri(pageUrl).Host}...");
                            var downloadUrls = await CrawlPageForWadDownloads(pageUrl, task.Wad.FileName, ct);
                            
                            foreach (var url in downloadUrls)
                            {
                                if (ct.IsCancellationRequested) break;
                                if (queuedTasks.ContainsKey(lowerName)) break;
                                
                                try
                                {
                                    var size = await GetFileSizeAsync(url, ct);
                                    if (size <= 0) continue;
                                    
                                    var domain = new Uri(url).Host;
                                    
                                    // First URL found: queue the download
                                    if (queuedTasks.TryAdd(lowerName, true))
                                    {
                                        task.SourceUrl = url;
                                        task.TotalBytes = size;
                                        task.Status = WadDownloadStatus.Queued;
                                        task.StatusMessage = $"Queued (web search - {domain})";
                                        ProgressUpdated?.Invoke(this, task);
                                        
                                        LogSuccess($"Found {task.Wad.FileName} via web search ({domain}, {FormatBytes(size)})");
                                        await channel.WriteAsync((task, url, size, domain), ct);
                                        break;
                                    }
                                    else
                                    {
                                        lock (urlLock)
                                        {
                                            if (!task.AlternateUrls.Any(a => a.Url == url))
                                            {
                                                task.AlternateUrls.Add((url, size));
                                            }
                                        }
                                    }
                                }
                                catch { }
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
    /// Extracts result page URLs from DuckDuckGo HTML search results.
    /// </summary>
    private List<string> ExtractSearchResultPageUrls(string html)
    {
        var urls = new List<string>();
        
        try
        {
            // DuckDuckGo HTML results encode actual URLs in the uddg parameter
            // Format: /l/?uddg=https%3A%2F%2Fexample.com%2Fpage&...
            var uddgPattern = UddgUrlRegex();
            var uddgMatches = uddgPattern.Matches(html);
            
            foreach (Match match in uddgMatches)
            {
                var encodedUrl = match.Groups[1].Value;
                var decodedUrl = Uri.UnescapeDataString(encodedUrl);
                
                // Validate it's a proper URL
                if (Uri.TryCreate(decodedUrl, UriKind.Absolute, out var uri))
                {
                    // Skip excluded hosts (search engines, social media, etc.)
                    var host = uri.Host.ToLowerInvariant();
                    if (ExcludedSearchHosts.Any(excluded => host.Contains(excluded))) continue;
                    
                    if (!urls.Contains(decodedUrl))
                    {
                        urls.Add(decodedUrl);
                    }
                }
            }
            
            // Also try extracting from result__url class (backup method)
            var resultUrlPattern = ResultUrlRegex();
            var resultMatches = resultUrlPattern.Matches(html);
            
            foreach (Match match in resultMatches)
            {
                var href = WebUtility.HtmlDecode(match.Groups[1].Value);
                if (href.StartsWith("//"))
                    href = "https:" + href;
                    
                if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
                {
                    var host = uri.Host.ToLowerInvariant();
                    if (ExcludedSearchHosts.Any(excluded => host.Contains(excluded))) continue;
                    
                    if (!urls.Contains(href))
                    {
                        urls.Add(href);
                    }
                }
            }
        }
        catch { }
        
        return urls;
    }
    
    /// <summary>
    /// Crawls a page to find WAD download links.
    /// </summary>
    private async Task<List<string>> CrawlPageForWadDownloads(string pageUrl, string wadFileName, CancellationToken ct)
    {
        var downloadUrls = new List<string>();
        var baseName = Path.GetFileNameWithoutExtension(wadFileName).ToLowerInvariant();
        var targetExtensions = Utilities.WadExtensions.AllSupportedExtensions;
        
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(AppConstants.Timeouts.PageCrawlTimeoutSeconds));
            
            var response = await WebClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode) return downloadUrls;
            
            // Check content type - only parse HTML
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase) && 
                !contentType.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                // Might be a direct download link
                if (targetExtensions.Any(ext => pageUrl.ToLowerInvariant().EndsWith(ext)))
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
                var hrefLower = href.ToLowerInvariant();
                
                // Check if it ends with a supported extension
                if (!targetExtensions.Any(ext => hrefLower.EndsWith(ext))) continue;
                
                // Check if filename matches what we're looking for
                string fileName;
                try
                {
                    fileName = Path.GetFileNameWithoutExtension(href).ToLowerInvariant();
                }
                catch
                {
                    continue;
                }
                
                // Match if filename contains base name or vice versa
                if (!fileName.Contains(baseName) && !baseName.Contains(fileName)) continue;
                
                // Build absolute URL
                string absoluteUrl;
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
                    
                    if (!downloadUrls.Contains(absoluteUrl) && Uri.TryCreate(absoluteUrl, UriKind.Absolute, out _))
                    {
                        downloadUrls.Add(absoluteUrl);
                    }
                }
                catch { }
            }
        }
        catch { }
        
        return downloadUrls;
    }
    
    [GeneratedRegex(@"uddg=([^&""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex UddgUrlRegex();
    
    [GeneratedRegex(@"class=[""']result__url[""'][^>]*>([^<]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResultUrlRegex();
    
    /// <summary>
    /// Parses a page for all WAD download links and returns filename -> URL mapping.
    /// </summary>
    private async Task<List<(string FileName, string Url)>> ParseAllWadLinksFromPage(string pageUrl, CancellationToken ct)
    {
        var results = new List<(string FileName, string Url)>();
        
        try
        {
            var response = await HttpClient.GetAsync(pageUrl, ct);
            if (!response.IsSuccessStatusCode)
                return results;
            
            var html = await response.Content.ReadAsStringAsync(ct);
            var baseUri = new Uri(pageUrl);
            
            var hrefPattern = HrefRegex();
            var matches = hrefPattern.Matches(html);
            
            foreach (Match match in matches)
            {
                var href = match.Groups[1].Value;
                href = WebUtility.HtmlDecode(href);
                
                var hrefLower = href.ToLowerInvariant();
                var ext = Path.GetExtension(hrefLower);
                if (Utilities.WadExtensions.IsSupportedExtension(ext))
                {
                    string absoluteUrl;
                    if (href.StartsWith("http://") || href.StartsWith("https://"))
                    {
                        absoluteUrl = href;
                    }
                    else if (href.StartsWith("/"))
                    {
                        absoluteUrl = $"{baseUri.Scheme}://{baseUri.Host}{href}";
                    }
                    else
                    {
                        absoluteUrl = new Uri(baseUri, href).ToString();
                    }
                    
                    // Extract filename from URL
                    var fileName = Path.GetFileName(new Uri(absoluteUrl).LocalPath);
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        results.Add((fileName, absoluteUrl));
                    }
                }
            }
            
            LogVerbose($"Parsed {results.Count} WAD links from {baseUri.Host}");
        }
        catch (Exception ex)
        {
            LogVerbose($"Parse error ({pageUrl}): {ex.Message}");
        }
        
        return results;
    }
    
    /// <summary>
    /// Domain worker - processes one WAD at a time from its queue with max threads.
    /// Handles retry logic for failed downloads.
    /// </summary>
    private async Task DomainWorkerAsync(
        string domain,
        ConcurrentQueue<(WadDownloadTask Task, string Url, long Size)> queue,
        string downloadPath,
        Func<bool> isSearchComplete,
        CancellationToken ct)
    {
        LogVerbose($"Domain worker started: {domain}");
        
        while (!ct.IsCancellationRequested)
        {
            if (queue.TryDequeue(out var item))
            {
                var (task, url, size) = item;
                
                // Skip if already completed (might have succeeded from alternate source)
                if (task.Status == WadDownloadStatus.Completed) continue;
                
                task.SourceUrl = url;
                task.TotalBytes = size;
                
                // Get effective thread settings from domain thread manager
                var (maxThreads, initialThreads, minSegmentSizeKb, shouldProbe, adaptiveLearning) = 
                    _domainConfig.GetEffectiveThreadSettings(domain);
                
                var domainThreads = maxThreads;
                
                // Probe domain if recommended by thread manager
                if (shouldProbe)
                {
                    LogVerbose($"Probing thread capacity for {domain}...");
                    domainThreads = await ProbeDomainThreadCapacityAsync(domain, url, initialThreads, ct);
                    LogInfo($"Domain {domain} supports {domainThreads} threads");
                }
                
                // Calculate threads for this file using domain manager's settings
                task.ThreadCount = CalculateOptimalThreads(size, domainThreads, minSegmentSizeKb);
                
                var success = await ExecuteDownloadAsync(task, downloadPath, ct);
                
                // Handle retry logic if download failed
                if (!success && task.Status != WadDownloadStatus.Cancelled)
                {
                    task.RetryCount++;
                    
                    // Try same source again if under retry limit
                    if (task.RetryCount <= WadDownloadTask.MaxRetriesPerSource)
                    {
                        LogWarning($"Retrying {task.Wad.FileName} (attempt {task.RetryCount}/{WadDownloadTask.MaxRetriesPerSource})");
                        task.BytesDownloaded = 0;
                        task.Status = WadDownloadStatus.Queued;
                        task.StatusMessage = $"Retry {task.RetryCount} ({domain})";
                        ProgressUpdated?.Invoke(this, task);
                        
                        // Re-queue for retry
                        queue.Enqueue((task, url, size));
                    }
                    else if (task.AlternateUrls.Count > 0)
                    {
                        // Try next alternate source
                        var (altUrl, altSize) = task.AlternateUrls[0];
                        task.AlternateUrls.RemoveAt(0);
                        task.RetryCount = 0;
                        
                        var altDomain = new Uri(altUrl).Host;
                        LogWarning($"Trying alternate source for {task.Wad.FileName}: {altDomain}");
                        
                        task.BytesDownloaded = 0;
                        task.SourceUrl = altUrl;
                        task.TotalBytes = altSize;
                        task.Status = WadDownloadStatus.Queued;
                        task.StatusMessage = $"Queued ({altDomain})";
                        ProgressUpdated?.Invoke(this, task);
                        
                        // Re-queue for alternate (will be processed by appropriate domain worker)
                        queue.Enqueue((task, altUrl, altSize));
                    }
                    else
                    {
                        // No more retries or alternates - mark as failed
                        LogError($"All sources exhausted for {task.Wad.FileName}");
                        task.Status = WadDownloadStatus.Failed;
                        task.StatusMessage = "All sources failed";
                        DownloadCompleted?.Invoke(this, task);
                    }
                }
            }
            else if (isSearchComplete())
            {
                // No more items and search is done
                break;
            }
            else
            {
                // Wait a bit for more items
                await Task.Delay(100, ct);
            }
        }
        
        LogVerbose($"Domain worker completed: {domain}");
    }
    
    /// <summary>
    /// Calculates optimal thread count based on file size and domain settings.
    /// Global thread cap is already applied by DomainThreadConfig.GetEffectiveThreadSettings().
    /// </summary>
    private int CalculateOptimalThreads(long fileSize, int domainMaxThreads, int minSegmentSizeKb)
    {
        int minSegmentSize = minSegmentSizeKb * 1024;
        int maxBySegmentSize = (int)Math.Max(1, fileSize / minSegmentSize);
        
        // Note: domainMaxThreads already has global MaxThreadsPerFile cap applied by DomainThreadConfig
        
        // Scale threads with file size
        int maxByFileSize = fileSize switch
        {
            < 1_000_000 => 4,        // < 1 MB: 4 threads
            < 5_000_000 => 16,       // < 5 MB: 16 threads
            < 20_000_000 => 32,      // < 20 MB: 32 threads
            < 50_000_000 => 64,      // < 50 MB: 64 threads
            < 100_000_000 => 128,    // < 100 MB: 128 threads
            _ => domainMaxThreads    // >= 100 MB: use domain max
        };
        
        int threads = Math.Min(Math.Min(maxByFileSize, domainMaxThreads), maxBySegmentSize);
        
        LogVerbose($"Using {threads} threads for {FormatBytes(fileSize)} (domain max: {domainMaxThreads}, segment: {minSegmentSizeKb}KB)");
        return threads;
    }
    
    /// <summary>
    /// Executes the actual download for a task.
    /// Returns true if download succeeded, false if it failed and may be retried.
    /// </summary>
    private async Task<bool> ExecuteDownloadAsync(WadDownloadTask task, string downloadPath, CancellationToken ct)
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
        
        try
        {
            task.Status = WadDownloadStatus.Downloading;
            task.StatusMessage = $"Downloading ({task.ThreadCount} threads)...";
            ProgressUpdated?.Invoke(this, task);
            
            LogInfo($"Downloading {task.Wad.FileName} ({FormatBytes(task.TotalBytes)}) from {domain} [{task.ThreadCount} threads]");
            
            // Use the actual filename from the download (may differ from requested if found as different extension)
            var downloadFileName = task.DownloadedFileName ?? task.Wad.FileName;
            var outputPath = Path.Combine(downloadPath, downloadFileName);
            
            // Ensure directory exists
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            // Check for range support
            bool supportsRange = await TestRangeRequestAsync(uri, ct);
            
            // Get min segment size from domain thread manager
            var (_, _, minSegmentSizeKb, _, _) = _domainConfig.GetEffectiveThreadSettings(domain);
            int minSegmentSize = minSegmentSizeKb * 1024;
            
            if (supportsRange && task.TotalBytes > minSegmentSize * 2 && task.ThreadCount > 1)
            {
                await MultiThreadedDownloadAsync(task, outputPath, ct);
            }
            else
            {
                await SingleThreadDownloadAsync(task, outputPath, ct);
            }
            
            // Verify hash if expected hash is provided
            if (!string.IsNullOrEmpty(task.Wad.ExpectedHash))
            {
                task.StatusMessage = "Verifying hash...";
                ProgressUpdated?.Invoke(this, task);
                
                var actualHash = await ComputeFileHashAsync(outputPath, ct);
                var expectedHash = task.Wad.ExpectedHash;
                if (actualHash == null || !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    var actualTrunc = actualHash?[..Math.Min(12, actualHash.Length)] ?? "null";
                    var expectedTrunc = expectedHash[..Math.Min(12, expectedHash.Length)];
                    LogWarning($"Hash mismatch for {task.Wad.FileName}: expected={expectedTrunc}..., got={actualTrunc}...");
                    
                    // Delete the bad file
                    try { File.Delete(outputPath); } catch { }
                    
                    task.Status = WadDownloadStatus.Failed;
                    task.StatusMessage = "Hash mismatch";
                    task.ErrorMessage = "Downloaded file hash does not match server expectation";
                    return false; // Will trigger retry with alternate source
                }
                
                LogVerbose($"Hash verified for {task.Wad.FileName}");
            }
            
            // Extract archive if needed and find the WAD file inside
            var finalPath = ExtractArchiveIfNeeded(task, downloadPath, outputPath);
            if (finalPath == null)
            {
                task.Status = WadDownloadStatus.Failed;
                task.StatusMessage = "Extraction failed";
                task.ErrorMessage = "Could not find WAD in downloaded archive";
                return false;
            }
            
            sw.Stop();
            var speed = task.TotalBytes / Math.Max(1, sw.Elapsed.TotalSeconds);
            
            task.Status = WadDownloadStatus.Completed;
            task.StatusMessage = "Complete";
            task.BytesDownloaded = task.TotalBytes;
            task.BytesPerSecond = speed;
            
            // Notify UI of final speed update
            ProgressUpdated?.Invoke(this, task);
            
            _domainConfig.UpdateThreadCount(domain, task.ThreadCount, wasSuccessful: true);
            LogSuccess($"Downloaded {task.Wad.FileName} ({FormatBytes(task.TotalBytes)}) in {sw.Elapsed.TotalSeconds:F1}s ({FormatBytes((long)speed)}/s)");
            
            DownloadCompleted?.Invoke(this, task);
            return true;
        }
        catch (TooManyConnectionsException)
        {
            _domainConfig.ReduceThreadCount(domain, task.ThreadCount);
            task.Status = WadDownloadStatus.Failed;
            task.StatusMessage = "Too many connections";
            task.ErrorMessage = "Server rejected connections - threads reduced";
            LogWarning($"Failed {task.Wad.FileName}: Too many connections, reducing threads");
            return false;
        }
        catch (OperationCanceledException)
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
            LogError($"Failed {task.Wad.FileName}: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Probes domain to find maximum supported thread count.
    /// </summary>
    private async Task<int> ProbeDomainThreadCapacityAsync(string domain, string testUrl, int initialThreads, CancellationToken ct)
    {
        int currentThreads = initialThreads;
        int lastSuccessful = currentThreads;
        
        // Get max probe limit from domain thread manager
        var (maxThreads, _, _, _, _) = _domainConfig.GetEffectiveThreadSettings(domain);
        int maxProbeThreads = Math.Max(maxThreads * 2, 512); // Probe up to 2x current max or 512
        
        while (currentThreads <= maxProbeThreads)
        {
            try
            {
                // Try making multiple concurrent HEAD requests
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(AppConstants.Timeouts.ConnectionTestTimeoutMs);
                
                var testTasks = Enumerable.Range(0, currentThreads)
                    .Select(async _ =>
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Head, testUrl);
                        using var response = await HttpClient.SendAsync(request, cts.Token);
                        return response.IsSuccessStatusCode;
                    });
                
                var results = await Task.WhenAll(testTasks);
                
                if (results.All(r => r))
                {
                    lastSuccessful = currentThreads;
                    currentThreads *= 2;
                }
                else
                {
                    break;
                }
            }
            catch
            {
                break;
            }
        }
        
        _domainConfig.UpdateThreadCount(domain, lastSuccessful, wasSuccessful: true);
        return lastSuccessful;
    }
    
    private async Task<bool> TestRangeRequestAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await HttpClient.SendAsync(request, ct);
            return response.Headers.AcceptRanges.Contains("bytes");
        }
        catch
        {
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
        
        var buffer = new byte[totalBytes];
        var downloadedBytes = new long[segments.Count];
        var failedSegments = new ConcurrentBag<(int SegmentIndex, bool IsConnectionLimit)>();
        
        long lastBytes = 0;
        var lastUpdate = DateTime.Now;
        var progressTimer = new System.Timers.Timer(AppConstants.UiIntervals.UiUpdateThrottleMs);
        progressTimer.Elapsed += (_, _) =>
        {
            var now = DateTime.Now;
            var currentBytes = downloadedBytes.Sum();
            var elapsed = (now - lastUpdate).TotalSeconds;
            
            if (elapsed > 0)
            {
                task.BytesPerSecond = (currentBytes - lastBytes) / elapsed;
            }
            
            task.BytesDownloaded = currentBytes;
            lastBytes = currentBytes;
            lastUpdate = now;
            ProgressUpdated?.Invoke(this, task);
        };
        progressTimer.Start();
        
        try
        {
            var downloadTasks = segments.Select((segment, index) =>
                DownloadSegmentAsync(uri, segment.Start, segment.End, buffer, index, downloadedBytes, failedSegments, ct));
            
            await Task.WhenAll(downloadTasks);
            
            // Check for connection limit failures
            var connectionLimitFailures = failedSegments.Count(f => f.IsConnectionLimit);
            if (connectionLimitFailures > segments.Count * 0.3)
            {
                throw new TooManyConnectionsException($"{connectionLimitFailures} of {segments.Count} segments failed due to connection limits");
            }
            
            if (failedSegments.Count > 0)
            {
                throw new Exception($"{failedSegments.Count} segments failed to download");
            }
            
            await File.WriteAllBytesAsync(outputPath, buffer, ct);
        }
        finally
        {
            progressTimer.Stop();
            progressTimer.Dispose();
        }
    }
    
    private async Task DownloadSegmentAsync(
        Uri uri, long start, long end, byte[] buffer, int segmentIndex,
        long[] downloadedBytes, ConcurrentBag<(int, bool)> failedSegments, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
            
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            
            long position = start;
            var tempBuffer = new byte[AppConstants.BufferSizes.NetworkBuffer];
            int bytesRead;
            
            while ((bytesRead = await stream.ReadAsync(tempBuffer, ct)) > 0)
            {
                Array.Copy(tempBuffer, 0, buffer, position, bytesRead);
                position += bytesRead;
                downloadedBytes[segmentIndex] = position - start;
            }
        }
        catch (HttpRequestException ex)
        {
            bool isConnectionLimit = ex.StatusCode == HttpStatusCode.TooManyRequests ||
                                    ex.StatusCode == HttpStatusCode.ServiceUnavailable ||
                                    ex.InnerException is System.Net.Sockets.SocketException;
            failedSegments.Add((segmentIndex, isConnectionLimit));
        }
        catch
        {
            failedSegments.Add((segmentIndex, false));
        }
    }
    
    private async Task SingleThreadDownloadAsync(WadDownloadTask task, string outputPath, CancellationToken ct)
    {
        using var response = await HttpClient.GetAsync(task.SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, AppConstants.BufferSizes.FileStreamBuffer, useAsync: true);
        
        var buffer = new byte[AppConstants.BufferSizes.NetworkBuffer];
        int bytesRead;
        var sw = Stopwatch.StartNew();
        long lastBytes = 0;
        var lastUpdate = DateTime.Now;
        
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            task.BytesDownloaded += bytesRead;
            
            var now = DateTime.Now;
            if ((now - lastUpdate).TotalMilliseconds >= AppConstants.UiIntervals.UiUpdateThrottleMs)
            {
                var elapsed = sw.Elapsed.TotalSeconds;
                var speed = elapsed > 0 ? (task.BytesDownloaded - lastBytes) / (now - lastUpdate).TotalSeconds : 0;
                task.BytesPerSecond = speed;
                
                lastBytes = task.BytesDownloaded;
                lastUpdate = now;
                ProgressUpdated?.Invoke(this, task);
            }
        }
    }
    
    private async Task<long> GetFileSizeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await HttpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength.HasValue)
            {
                return response.Content.Headers.ContentLength.Value;
            }
        }
        catch { }
        return 0;
    }
    
    /// <summary>
    /// Gets filename variants to try when searching (original + alternative extensions).
    /// When a file has no extension, tries all supported extensions.
    /// </summary>
    private static List<string> GetFilenameVariants(string filename)
    {
        var variants = new List<string>();
        var baseName = Path.GetFileNameWithoutExtension(filename);
        var originalExt = Path.GetExtension(filename).ToLowerInvariant();
        
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
        else
        {
            // Always try the original filename first
            variants.Add(filename);
            
            // If the file already has a supported extension, also try as .zip
            if (SupportedExtensions.Contains(originalExt) && originalExt != ".zip")
            {
                variants.Add(baseName + ".zip");
            }
            // If unsupported extension, try all supported extensions
            else if (!SupportedExtensions.Contains(originalExt))
            {
                foreach (var ext in SupportedExtensions)
                {
                    var variant = baseName + ext;
                    if (!variants.Contains(variant, StringComparer.OrdinalIgnoreCase))
                        variants.Add(variant);
                }
            }
        }
        
        return variants;
    }
    
    /// <summary>
    /// Extracts archive if downloaded file is a zip/7z/rar and looks for the requested WAD inside.
    /// Returns the path to the extracted WAD file, or null if extraction failed or file not found.
    /// </summary>
    private string? ExtractArchiveIfNeeded(WadDownloadTask task, string downloadPath, string archivePath)
    {
        var archiveExt = Path.GetExtension(archivePath).ToLowerInvariant();
        
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
            
            var baseName = Path.GetFileNameWithoutExtension(task.Wad.FileName).ToLowerInvariant();
            
            using var archive = ArchiveFactory.Open(archivePath);
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            
            // Look for exact match first, then base name match with any supported extension
            IArchiveEntry? matchingEntry = null;
            
            // First try exact match
            matchingEntry = entries.FirstOrDefault(e => 
                Path.GetFileName(e.Key ?? "").Equals(task.Wad.FileName, StringComparison.OrdinalIgnoreCase));
            
            // Then try matching by base name with WAD extensions
            if (matchingEntry == null)
            {
                foreach (var entry in entries)
                {
                    var entryName = Path.GetFileName(entry.Key ?? "");
                    var entryBaseName = Path.GetFileNameWithoutExtension(entryName).ToLowerInvariant();
                    var entryExt = Path.GetExtension(entryName).ToLowerInvariant();
                    
                    // Match if base name matches and extension is a WAD-like format
                    if (entryBaseName == baseName && WadExtensions.IsWadExtension(entryExt))
                    {
                        matchingEntry = entry;
                        break;
                    }
                }
            }
            
            // If still no match, try to find any WAD file
            if (matchingEntry == null)
            {
                matchingEntry = entries.FirstOrDefault(e =>
                {
                    var entryExt = Path.GetExtension(e.Key ?? "").ToLowerInvariant();
                    return WadExtensions.IsWadExtension(entryExt);
                });
            }
            
            if (matchingEntry != null)
            {
                var outputFileName = Path.GetFileName(matchingEntry.Key ?? "unknown.wad");
                var outputPath = Path.Combine(downloadPath, outputFileName);
                
                // Extract the file
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                
                matchingEntry.WriteToFile(outputPath, new ExtractionOptions { Overwrite = true });
                
                LogSuccess($"Extracted {outputFileName} from archive");
                
                // Clean up the archive
                try { File.Delete(archivePath); } catch { }
                
                return outputPath;
            }
            else
            {
                // No WAD found in archive - list contents for debugging
                var contents = string.Join(", ", entries.Take(5).Select(e => Path.GetFileName(e.Key ?? "")));
                LogWarning($"No matching WAD found in archive. Contents: {contents}...");
                return null;
            }
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to extract archive: {ex.Message}");
            return null;
        }
    }
    
    private static string FormatBytes(long bytes) => FormatUtils.FormatBytes(bytes);
    
    private static async Task<string?> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return null;
        
        try
        {
            await using var stream = File.OpenRead(filePath);
            using var md5 = MD5.Create();
            var hashBytes = await md5.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
    
    [GeneratedRegex(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();
    
    public void Dispose()
    {
        if (!_disposed)
        {
            // Note: HttpClient instances are static and shared - do not dispose them
            // Only dispose the per-instance domain semaphores
            foreach (var semaphore in _domainSemaphores.Values)
            {
                semaphore.Dispose();
            }
            _domainSemaphores.Clear();
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
