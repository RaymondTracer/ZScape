using ZScape.Models;

namespace ZScape.Services;

public partial class WadDownloader
{
    /// <summary>
    /// Coordinates source discovery with the live download scheduler. The
    /// versioned signal prevents a source discovered between a scheduler scan
    /// and its wait from being missed.
    /// </summary>
    private sealed class SourceDiscoveryState
    {
        private readonly object _syncRoot = new();
        private long _version;
        private bool _isComplete;
        private TaskCompletionSource<bool> _changed = CreateSignal();

        public object SyncRoot => _syncRoot;

        public bool IsComplete
        {
            get
            {
                lock (_syncRoot)
                {
                    return _isComplete;
                }
            }
        }

        public long CaptureVersion()
        {
            lock (_syncRoot)
            {
                return _version;
            }
        }

        public Task WaitForChangeAsync(
            long observedVersion,
            CancellationToken ct)
        {
            Task changeTask;
            lock (_syncRoot)
            {
                if (_version != observedVersion)
                {
                    return Task.CompletedTask;
                }

                changeTask = _changed.Task;
            }

            return changeTask.WaitAsync(ct);
        }

        public void NotifySourceChanged()
        {
            TaskCompletionSource<bool> changed;
            lock (_syncRoot)
            {
                _version++;
                changed = _changed;
                _changed = CreateSignal();
            }

            changed.TrySetResult(true);
        }

        public void MarkComplete()
        {
            TaskCompletionSource<bool> changed;
            lock (_syncRoot)
            {
                if (_isComplete)
                {
                    return;
                }

                _isComplete = true;
                _version++;
                changed = _changed;
                _changed = CreateSignal();
            }

            changed.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record DownloadSource(
        string Url,
        long Size,
        string? DownloadedFileName,
        int Priority);

    private sealed record SourceAssignment(
        WadDownloadTask Task,
        DownloadSource Source,
        string Domain,
        int ThreadCount,
        bool DomainAlreadyActive,
        bool SupportsRange = false);

    private sealed record ActiveDownload(
        DownloadSource Source,
        string Domain,
        int ReservedThreads,
        Task<bool> Operation);

    /// <summary>
    /// Runs downloads with three independent limits:
    /// total active files, distinct active domains, and the aggregate connection
    /// budget for each domain. A small file can therefore leave enough of a
    /// domain's budget for other files from that same domain.
    /// </summary>
    private async Task RunSourceAwareDownloadsAsync(
        IReadOnlyList<WadDownloadTask> tasks,
        string downloadPath,
        SourceDiscoveryState discoveryState,
        CancellationToken ct)
    {
        var settings = SettingsService.Instance.Settings;
        var maxActiveFiles = settings.MaxConcurrentDownloads > 0
            ? Math.Min(settings.MaxConcurrentDownloads, tasks.Count)
            : tasks.Count;
        var maxActiveDomains = settings.MaxConcurrentDomains;

        var taskOrder = tasks
            .Select((task, index) => (task, index))
            .ToDictionary(item => item.task, item => item.index);
        var remaining = new HashSet<WadDownloadTask>(tasks);
        var active = new Dictionary<WadDownloadTask, ActiveDownload>();
        var retrySources = new Dictionary<WadDownloadTask, DownloadSource>();
        var domainUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var activeUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rangeSupportByUrl =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        LogInfo(
            $"Scheduler limits: {maxActiveFiles} file(s), "
            + $"{(maxActiveDomains > 0 ? maxActiveDomains : "unlimited")} domain(s); "
            + "per-domain thread budgets are shared across files");

        try
        {
            while (remaining.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var observedSourceVersion = discoveryState.CaptureVersion();

                while (active.Count < maxActiveFiles)
                {
                    var assignment = SelectNextSource(
                        remaining,
                        active.Keys,
                        retrySources,
                        discoveryState,
                        taskOrder,
                        domainUsage,
                        activeUrls,
                        maxActiveDomains,
                        settings.MaxThreadsPerFile);
                    if (assignment == null)
                    {
                        break;
                    }

                    var supportsRange = false;
                    if (assignment.ThreadCount > 1
                        && assignment.Source.Size > 0)
                    {
                        if (!rangeSupportByUrl.TryGetValue(
                                assignment.Source.Url,
                                out supportsRange))
                        {
                            supportsRange = await TestRangeRequestAsync(
                                new Uri(assignment.Source.Url),
                                ct);
                            rangeSupportByUrl[assignment.Source.Url] = supportsRange;
                        }
                    }

                    ct.ThrowIfCancellationRequested();

                    if (!supportsRange)
                    {
                        assignment = assignment with { ThreadCount = 1 };
                    }
                    assignment = assignment with { SupportsRange = supportsRange };

                    var task = assignment.Task;
                    var source = assignment.Source;
                    AssignTaskSource(task, source, discoveryState);
                    task.ThreadCount = assignment.ThreadCount;
                    task.BytesDownloaded = 0;
                    task.BytesPerSecond = 0;
                    task.ErrorMessage = null;
                    task.SkipSourceRetry = false;
                    task.Status = WadDownloadStatus.Queued;
                    task.StatusMessage = retrySources.ContainsKey(task)
                        ? $"Retry {task.RetryCount} ({assignment.Domain})"
                        : $"Queued ({assignment.Domain})";
                    ProgressUpdated?.Invoke(this, task);

                    retrySources.Remove(task);
                    domainUsage[assignment.Domain] =
                        domainUsage.GetValueOrDefault(assignment.Domain)
                        + assignment.ThreadCount;
                    activeUrls.Add(source.Url);

                    var operation = ExecuteDownloadAsync(
                        task,
                        downloadPath,
                        assignment.SupportsRange,
                        ct);
                    active[task] = new ActiveDownload(
                        source,
                        assignment.Domain,
                        assignment.ThreadCount,
                        operation);

                    var reuse = assignment.DomainAlreadyActive
                        ? " using spare domain capacity"
                        : " on a distinct domain";
                    LogVerbose(
                        $"Scheduled {task.Wad.FileName} from {assignment.Domain} "
                        + $"with {assignment.ThreadCount} thread(s){reuse}");
                }

                ct.ThrowIfCancellationRequested();

                if (active.Count == 0)
                {
                    if (!discoveryState.IsComplete)
                    {
                        await discoveryState.WaitForChangeAsync(
                            observedSourceVersion,
                            ct);
                        continue;
                    }

                    // Discovery is finished and nothing can release capacity.
                    // Tasks without a source were not found; tasks with only
                    // exhausted sources have genuinely failed every option.
                    foreach (var task in remaining.ToArray())
                    {
                        if (BuildSourcePool(task, discoveryState).Count == 0)
                        {
                            MarkTaskSourceDiscoveryFailed(
                                task,
                                "Not found",
                                "WAD not found on any download source");
                        }
                        else
                        {
                            MarkTaskAllSourcesFailed(task);
                        }
                        remaining.Remove(task);
                    }
                    break;
                }

                await WaitForSchedulerActivityAsync(
                    active.Values,
                    discoveryState,
                    observedSourceVersion,
                    ct);

                foreach (var (task, download) in active
                             .Where(pair => pair.Value.Operation.IsCompleted)
                             .ToArray())
                {
                    active.Remove(task);
                    activeUrls.Remove(download.Source.Url);
                    ReleaseDomainThreads(
                        domainUsage,
                        download.Domain,
                        download.ReservedThreads);

                    var succeeded = false;
                    try
                    {
                        succeeded = await download.Operation;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // ExecuteDownloadAsync normally converts cancellation into a
                        // task state, but keep the scheduler safe if that ever changes.
                    }

                    if (succeeded)
                    {
                        remaining.Remove(task);
                        continue;
                    }

                    if (task.Status == WadDownloadStatus.Cancelled)
                    {
                        remaining.Remove(task);
                        continue;
                    }

                    if (!task.SkipSourceRetry
                        && task.RetryCount < WadDownloadTask.MaxRetriesPerSource)
                    {
                        task.RetryCount++;
                        retrySources[task] = download.Source;
                        task.Status = WadDownloadStatus.Queued;
                        task.StatusMessage =
                            $"Retry {task.RetryCount} ({download.Domain})";
                        ProgressUpdated?.Invoke(this, task);
                        LogWarning(
                            $"Retrying {task.Wad.FileName} from {download.Source.Url} "
                            + $"(attempt {task.RetryCount}/{WadDownloadTask.MaxRetriesPerSource})");
                        continue;
                    }

                    task.ExhaustedUrls.Add(download.Source.Url);
                    task.RetryCount = 0;
                    task.SkipSourceRetry = false;

                    var alternativesLeft = BuildSourcePool(task, discoveryState)
                        .Count(source =>
                            !task.ExhaustedUrls.Contains(source.Url));
                    if (alternativesLeft > 0)
                    {
                        task.Status = WadDownloadStatus.Queued;
                        task.StatusMessage =
                            $"Trying another source ({alternativesLeft} left)";
                        ProgressUpdated?.Invoke(this, task);
                        LogWarning(
                            $"Source exhausted for {task.Wad.FileName}: "
                            + $"{download.Source.Url}; cycling to another source");
                    }
                    else if (!discoveryState.IsComplete)
                    {
                        task.Status = WadDownloadStatus.Searching;
                        task.StatusMessage =
                            $"Searching for another source "
                            + $"({task.SitesSearched}/{task.TotalSitesToSearch})...";
                        ProgressUpdated?.Invoke(this, task);
                        LogWarning(
                            $"Source exhausted for {task.Wad.FileName}: "
                            + $"{download.Source.Url}; source discovery is still running");
                    }
                    else
                    {
                        MarkTaskAllSourcesFailed(task);
                        remaining.Remove(task);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await AwaitActiveDownloadsAfterCancellationAsync(active.Values);
            MarkPendingTasksCancelled(remaining);
        }
    }

    private SourceAssignment? SelectNextSource(
        IEnumerable<WadDownloadTask> remaining,
        ICollection<WadDownloadTask> activeTasks,
        IReadOnlyDictionary<WadDownloadTask, DownloadSource> retrySources,
        SourceDiscoveryState discoveryState,
        IReadOnlyDictionary<WadDownloadTask, int> taskOrder,
        IReadOnlyDictionary<string, int> domainUsage,
        IReadOnlySet<string> activeUrls,
        int maxActiveDomains,
        int maxThreadsPerFile)
    {
        var activeDomainCount = domainUsage.Count(pair => pair.Value > 0);
        SourceAssignment? best = null;
        (int ReusesDomain, int UsedThreads, int SourcePriority, int TaskOrder) bestScore =
            (int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);

        foreach (var task in remaining)
        {
            if (activeTasks.Contains(task))
            {
                continue;
            }

            IEnumerable<DownloadSource> candidates = retrySources.TryGetValue(
                task,
                out var retrySource)
                ? [retrySource]
                : BuildSourcePool(task, discoveryState);

            foreach (var source in candidates)
            {
                if (task.ExhaustedUrls.Contains(source.Url)
                    || activeUrls.Contains(source.Url)
                    || !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp
                        && uri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                var domain = uri.Host;
                var usedThreads = domainUsage.GetValueOrDefault(domain);
                var domainAlreadyActive = usedThreads > 0;
                if (!domainAlreadyActive
                    && maxActiveDomains > 0
                    && activeDomainCount >= maxActiveDomains)
                {
                    continue;
                }

                var (domainBudget, minSegmentSizeKb, _, _) =
                    _domainConfig.GetEffectiveDomainBudgetSettings(domain);
                domainBudget = Math.Max(1, domainBudget);
                var remainingThreads = domainBudget - usedThreads;
                if (remainingThreads <= 0)
                {
                    continue;
                }

                var desiredThreads = GetOptimalThreadDemand(
                    source.Size,
                    domainBudget,
                    minSegmentSizeKb,
                    maxThreadsPerFile);
                var allocatedThreads = Math.Min(desiredThreads, remainingThreads);
                if (allocatedThreads <= 0)
                {
                    continue;
                }

                // Prefer a source on a domain not currently downloading another
                // file. If all alternatives are active, use whichever domain has
                // the most capacity left, then preserve discovery priority.
                var score = (
                    domainAlreadyActive ? 1 : 0,
                    usedThreads,
                    source.Priority,
                    taskOrder[task]);
                if (score.CompareTo(bestScore) >= 0)
                {
                    continue;
                }

                bestScore = score;
                best = new SourceAssignment(
                    task,
                    source,
                    domain,
                    allocatedThreads,
                    domainAlreadyActive);
            }
        }

        return best;
    }

    private static List<DownloadSource> BuildSourcePool(
        WadDownloadTask task,
        SourceDiscoveryState discoveryState)
    {
        var sources = new List<DownloadSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(
            string? url,
            long size,
            string? downloadedFileName)
        {
            if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
            {
                return;
            }

            sources.Add(new DownloadSource(
                url,
                size,
                downloadedFileName,
                sources.Count));
        }

        lock (discoveryState.SyncRoot)
        {
            Add(task.SourceUrl, task.TotalBytes, task.DownloadedFileName);
            foreach (var alternate in task.AlternateUrls)
            {
                Add(
                    alternate.Url,
                    alternate.Size,
                    alternate.DownloadedFileName);
            }
        }

        return sources;
    }

    private static void AssignTaskSource(
        WadDownloadTask task,
        DownloadSource source,
        SourceDiscoveryState discoveryState)
    {
        lock (discoveryState.SyncRoot)
        {
            var previousUrl = task.SourceUrl;
            var previousSize = task.TotalBytes;
            var previousFileName = task.DownloadedFileName;

            // The selected alternate becomes the current source. Preserve the
            // old primary in the candidate pool so a source chosen early for
            // domain balancing is still available if this one later fails.
            task.AlternateUrls.RemoveAll(alternate =>
                alternate.Url.Equals(
                    source.Url,
                    StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(previousUrl)
                && !previousUrl.Equals(
                    source.Url,
                    StringComparison.OrdinalIgnoreCase)
                && !task.AlternateUrls.Any(alternate =>
                    alternate.Url.Equals(
                        previousUrl,
                        StringComparison.OrdinalIgnoreCase)))
            {
                task.AlternateUrls.Insert(
                    0,
                    (previousUrl, previousSize, previousFileName));
            }

            task.SourceUrl = source.Url;
            task.TotalBytes = source.Size;
            task.DownloadedFileName = source.DownloadedFileName;
        }
    }

    private static async Task WaitForSchedulerActivityAsync(
        IEnumerable<ActiveDownload> downloads,
        SourceDiscoveryState discoveryState,
        long observedSourceVersion,
        CancellationToken ct)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sourceChanged = discoveryState.WaitForChangeAsync(
            observedSourceVersion,
            waitCts.Token);
        var waitTasks = downloads
            .Select(download => (Task)download.Operation)
            .Append(sourceChanged);

        await Task.WhenAny(waitTasks);
        waitCts.Cancel();
    }

    private static int GetOptimalThreadDemand(
        long fileSize,
        int domainMaxThreads,
        int minSegmentSizeKb,
        int maxThreadsPerFile)
    {
        if (fileSize <= 0)
        {
            return 1;
        }

        var minSegmentSize = Math.Max(1, minSegmentSizeKb) * 1024L;
        var maxBySegmentSize = (int)Math.Clamp(
            fileSize / minSegmentSize,
            1,
            int.MaxValue);
        var maxByFileSize = fileSize switch
        {
            < 1_000_000 => 4,
            < 5_000_000 => 16,
            < 20_000_000 => 32,
            < 50_000_000 => 64,
            < 100_000_000 => 128,
            _ => domainMaxThreads
        };

        var threadDemand = Math.Max(
            1,
            Math.Min(
                Math.Min(maxByFileSize, domainMaxThreads),
                maxBySegmentSize));
        return maxThreadsPerFile > 0
            ? Math.Min(threadDemand, maxThreadsPerFile)
            : threadDemand;
    }

    private static void ReleaseDomainThreads(
        IDictionary<string, int> domainUsage,
        string domain,
        int reservedThreads)
    {
        domainUsage.TryGetValue(domain, out var usedThreads);
        var remainingThreads = usedThreads - reservedThreads;
        if (remainingThreads > 0)
        {
            domainUsage[domain] = remainingThreads;
        }
        else
        {
            domainUsage.Remove(domain);
        }
    }

    private static async Task AwaitActiveDownloadsAfterCancellationAsync(
        IEnumerable<ActiveDownload> downloads)
    {
        try
        {
            await Task.WhenAll(downloads.Select(download => download.Operation));
        }
        catch
        {
            // Individual operations have already updated their task state.
        }
    }

    private void MarkTaskAllSourcesFailed(WadDownloadTask task)
    {
        task.Status = WadDownloadStatus.Failed;
        task.StatusMessage = "All sources failed";
        task.ErrorMessage ??= "Every discovered download source failed";
        task.BytesPerSecond = 0;
        ProgressUpdated?.Invoke(this, task);
        LogError(
            $"All {task.ExhaustedUrls.Count} source(s) failed for "
            + task.Wad.FileName);
        DownloadCompleted?.Invoke(this, task);
    }

    private void MarkPendingTasksCancelled(IEnumerable<WadDownloadTask> tasks)
    {
        foreach (var task in tasks)
        {
            if (task.Status is WadDownloadStatus.Completed
                or WadDownloadStatus.AlreadyExists
                or WadDownloadStatus.Failed
                or WadDownloadStatus.Cancelled)
            {
                continue;
            }

            task.Status = WadDownloadStatus.Cancelled;
            task.StatusMessage = "Cancelled";
            task.BytesPerSecond = 0;
            ProgressUpdated?.Invoke(this, task);
            DownloadCompleted?.Invoke(this, task);
        }
    }
}
