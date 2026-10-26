using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.Services;

/// <summary>
/// Handles launching Zandronum to connect to servers, including automatic
/// downloading of testing versions.
/// </summary>
public class GameLauncher : IDisposable
{
    private static readonly Lazy<GameLauncher> _instance = new(() => new GameLauncher());
    private static readonly Uri TestingArchiveBaseUri = new("https://zandronum.com/", UriKind.Absolute);
    public static GameLauncher Instance => _instance.Value;

    private readonly DomainThreadConfig _domainConfig = DomainThreadConfig.Instance;
    private readonly HttpClient _httpClient = new();
    private bool _disposed;

    public event EventHandler<string>? LaunchError;
    public event EventHandler<string>? LaunchSuccess;
    public event EventHandler<TestingBuildDownloadProgress>? DownloadProgress;

    private GameLauncher() 
    {
        _httpClient.Timeout = TimeSpan.FromMinutes(AppConstants.Timeouts.HttpLongOperationTimeoutMinutes);
    }

    /// <summary>
    /// Launches Zandronum to connect to the specified server.
    /// </summary>
    /// <param name="server">The server to connect to.</param>
    /// <param name="connectPassword">Optional connect password.</param>
    /// <param name="joinPassword">Optional join/in-game password.</param>
    /// <param name="excludedOptionalPwads">Optional PWAD names to exclude from the launch command line.</param>
    /// <returns>True if launch succeeded, false otherwise.</returns>
    public bool LaunchGame(
        ServerInfo server,
        string? connectPassword = null,
        string? joinPassword = null,
        ISet<string>? excludedOptionalPwads = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        // Select correct executable based on testing status
        var exePath = GetExecutablePath(server);
            
        if (string.IsNullOrEmpty(exePath))
        {
            var error = server.IsTestingServer 
                ? "Zandronum Testing executable path not configured. Please set it in Settings."
                : "Zandronum executable path not configured. Please set it in Settings.";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Warning(error);
            return false;
        }
        
        if (!File.Exists(exePath))
        {
            var error = $"Zandronum executable not found: {exePath}";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Warning(error);
            return false;
        }

        try
        {
            var args = BuildCommandLine(server, connectPassword, joinPassword, excludedOptionalPwads, cancellationToken, resolvedWads);
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            };
            
            LoggingService.Instance.Verbose($"Launching: {exePath} {args}");
            
            cancellationToken.ThrowIfCancellationRequested();
            var launchedProcess = Process.Start(startInfo);
            ZandronumConfigSyncService.Instance.TrackLaunchedProcess(launchedProcess, exePath);
            
            // Record connection in history
            SettingsService.Instance.RecordConnection(
                server.Address, 
                server.Port, 
                server.Name,
                server.IWAD,
                server.GameMode?.Name);
            
            var msg = $"Launched Zandronum to connect to {server.Name}";
            LaunchSuccess?.Invoke(this, msg);
            LoggingService.Instance.Info(msg);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = $"Failed to launch Zandronum: {ex.Message}";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Error(error);
            return false;
        }
    }

    /// <summary>
    /// Validates if all required WADs are available for the server.
    /// Uses the shared search-folder priority and retains paths for the join.
    /// Optional PWADs do not block joins and are skipped here.
    /// </summary>
    /// <param name="server">The server to check.</param>
    /// <returns>A tuple with (allFound, missingWads) where each missing WAD includes name and expected hash.</returns>
    public (bool AllFound, List<(string Name, string? Hash)> MissingWads) CheckRequiredWads(
        ServerInfo server,
        CancellationToken cancellationToken = default,
        IDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        return CheckPwads(server, pwad => !pwad.IsOptional, includeIwad: true, WadManager.Instance,
            cancellationToken, resolvedWads: resolvedWads);
    }

    /// <summary>
    /// Finds optional PWADs that are not currently available.
    /// These do not block joins, but can be offered for download.
    /// </summary>
    public (bool AllFound, List<(string Name, string? Hash)> MissingWads) CheckOptionalWads(
        ServerInfo server,
        CancellationToken cancellationToken = default,
        IDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        return CheckPwads(server, pwad => pwad.IsOptional, includeIwad: false, WadManager.Instance,
            cancellationToken, resolvedWads: resolvedWads);
    }

    private static (bool AllFound, List<(string Name, string? Hash)> MissingWads) CheckPwads(
        ServerInfo server,
        Func<PWadInfo, bool> predicate,
        bool includeIwad,
        WadManager wadManager,
        CancellationToken cancellationToken,
        IProgress<SaveStateProgress>? progress = null,
        IDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        var missing = new List<(string Name, string? Hash)>();
        cancellationToken.ThrowIfCancellationRequested();
        var pwads = server.PWADs.Where(predicate).ToList();
        var total = pwads.Count + (includeIwad && !string.IsNullOrEmpty(server.IWAD) ? 1 : 0);
        var current = 0;

        string? Find(string name, string? expectedHash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SaveStateProgress { Status = $"Checking {name}", Current = current++, Total = total });
            if (resolvedWads != null
                && resolvedWads.TryGetValue(name, out var previous)
                && CanReuseJoinResolution(wadManager, previous, expectedHash))
            {
                return previous.LocalPath;
            }

            var result = wadManager.ResolveWadAsync(name, expectedHash, WadHashLookupMode.FileNameOnly,
                cancellationToken).GetAwaiter().GetResult();
            // A presence check must not erase a completed hash verdict. The
            // downloaded replacement is resolved again when building the
            // command, while optional mismatches remain excluded by the join.
            if (resolvedWads != null
                && (!resolvedWads.TryGetValue(name, out previous)
                    || previous.LookupMode == WadHashLookupMode.FileNameOnly
                    || !string.Equals(previous.ExpectedHash, expectedHash, StringComparison.OrdinalIgnoreCase)))
                resolvedWads[name] = result;
            return result.LocalPath;
        }
        
        // Check IWAD (IWADs don't have server-provided hashes)
        if (includeIwad && !string.IsNullOrEmpty(server.IWAD))
        {
            var iwadPath = Find(server.IWAD, null);
            if (string.IsNullOrEmpty(iwadPath))
            {
                missing.Add((server.IWAD, null));
            }
        }
        
        foreach (var pwad in pwads)
        {
            var pwadPath = Find(pwad.Name, pwad.Hash);
            if (string.IsNullOrEmpty(pwadPath))
            {
                missing.Add((pwad.Name, pwad.Hash));
            }
        }
        
        cancellationToken.ThrowIfCancellationRequested();
        return (missing.Count == 0, missing);
    }

    private static bool CanReuseJoinResolution(
        WadManager wadManager, WadResolutionResult resolution, string? expectedHash) =>
        resolution.IsUsable
        && (resolution.LookupMode == WadHashLookupMode.FileNameOnly
            || resolution.Status == WadResolutionStatus.Matched)
        && string.Equals(resolution.ExpectedHash, expectedHash, StringComparison.OrdinalIgnoreCase)
        && wadManager.IsResolutionCurrent(resolution);

    public sealed record WadJoinPreparationResult(
        List<(string Name, string? Hash)> MissingWads,
        List<WadInfo> NeedsDownload);

    public Task<WadJoinPreparationResult> PrepareWadsForJoinAsync(
        ServerInfo server,
        bool optional,
        IProgress<SaveStateProgress>? progress,
        CancellationToken cancellationToken,
        IDictionary<string, WadResolutionResult>? resolvedWads = null) =>
        PrepareWadsForJoinAsync(server, WadManager.Instance, optional, progress, cancellationToken, resolvedWads);

    internal static Task<WadJoinPreparationResult> PrepareWadsForJoinAsync(
        ServerInfo server,
        WadManager wadManager,
        bool optional,
        IProgress<SaveStateProgress>? progress,
        CancellationToken cancellationToken,
        IDictionary<string, WadResolutionResult>? resolvedWads = null) => Task.Run(() =>
        {
            var (_, missing) = CheckPwads(server, pwad => pwad.IsOptional == optional,
                includeIwad: !optional, wadManager, cancellationToken, progress, resolvedWads);
            // Commercial IWADs must be supplied locally. Do not begin archive
            // work or change local files before the caller shows that gate.
            if (missing.Any(wad => WadManager.IsForbiddenWad(wad.Name)))
                return new WadJoinPreparationResult(missing, []);

            var (needsDownload, cacheChanged) = ResolveMissingWadsByHash(
                missing, wadManager, cancellationToken, progress);
            if (cacheChanged)
            {
                progress?.Report(new SaveStateProgress { Status = "Refreshing local WAD files..." });
                wadManager.RefreshCache(cancellationToken);
                if (resolvedWads != null)
                    CheckPwads(server, pwad => pwad.IsOptional == optional, includeIwad: !optional,
                        wadManager, cancellationToken, progress, resolvedWads);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new WadJoinPreparationResult(missing, needsDownload);
        }, cancellationToken);

    /// <summary>
    /// Resolves missing WADs by activating locally archived copies that match the server hash.
    /// Returns only the WADs that still need to be downloaded.
    /// </summary>
    public (List<WadInfo> NeedsDownload, bool CacheChanged) ResolveMissingWadsByHash(
        List<(string Name, string? Hash)> missingWads,
        CancellationToken cancellationToken = default,
        IProgress<SaveStateProgress>? progress = null) =>
        ResolveMissingWadsByHash(missingWads, WadManager.Instance, cancellationToken, progress);

    private static (List<WadInfo> NeedsDownload, bool CacheChanged) ResolveMissingWadsByHash(
        List<(string Name, string? Hash)> missingWads,
        WadManager wadManager,
        CancellationToken cancellationToken,
        IProgress<SaveStateProgress>? progress)
    {
        var needsDownload = new List<WadInfo>();
        var cacheChanged = false;
        var current = 0;

        foreach (var (name, hash) in missingWads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SaveStateProgress { Status = $"Looking for local versions of {name}", Current = current++, Total = missingWads.Count });
            if (string.IsNullOrEmpty(hash))
            {
                needsDownload.Add(new WadInfo(name, hash));
                continue;
            }

            var baseName = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            var matchingPath = wadManager.FindWadByHash(hash, baseName, extension, cancellationToken,
                file => progress?.Report(new SaveStateProgress
                {
                    Status = $"Checking local version of {Path.GetFileName(file)}",
                    Current = current - 1,
                    Total = missingWads.Count
                }),
                (file, bytesProcessed) => progress?.Report(new SaveStateProgress
                {
                    Status = $"Checking local version of {Path.GetFileName(file)} ({bytesProcessed / (1024d * 1024d):F1} MB)",
                    Current = current - 1,
                    Total = missingWads.Count
                }));
            if (string.IsNullOrEmpty(matchingPath))
            {
                needsDownload.Add(new WadInfo(name, hash));
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var activatedPath = wadManager.ActivateArchivedWad(matchingPath, name, cancellationToken);
            if (activatedPath != null)
            {
                cacheChanged = true;
                LoggingService.Instance.Info(
                    $"Activated local WAD for {name}: {Path.GetFileName(matchingPath)} -> {Path.GetFileName(activatedPath)}");
                continue;
            }

            LoggingService.Instance.Warning($"Found matching local WAD for {name} but failed to activate it: {matchingPath}");
            needsDownload.Add(new WadInfo(name, hash));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (needsDownload, cacheChanged);
    }

    /// <summary>
    /// Represents a WAD that has a hash mismatch.
    /// </summary>
    public class WadHashMismatch
    {
        public string WadName { get; init; } = string.Empty;
        public bool IsOptional { get; init; }
        public string LocalPath { get; init; } = string.Empty;
        public string LocalHash { get; init; } = string.Empty;
        public string ExpectedHash { get; init; } = string.Empty;
        public string? MatchingVersionPath { get; set; }
        public bool NeedsDownload => string.IsNullOrEmpty(MatchingVersionPath);
    }

    /// <summary>
    /// Describes whether every relevant PWAD already has a valid cached full
    /// MD5 that matches the server's advertised full MD5. A successful result
    /// means no file bytes need to be read and no verification UI is required.
    /// </summary>
    public readonly record struct CachedWadHashVerificationResult(
        bool AllHashesMatch,
        int VerifiedFileCount);

    /// <summary>
    /// Progress information for hash verification.
    /// </summary>
    public class HashVerificationProgress
    {
        public string CurrentFile { get; init; } = string.Empty;
        public int CurrentIndex { get; init; }
        public int TotalFiles { get; init; }
        public string Status { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public long BytesProcessed { get; init; }
        public int FilePercentComplete => (int)(new FileOperationProgress(BytesProcessed, FileSize).ReadFraction * 100);
        public double OverallPercentComplete => OverallProgress.PercentComplete;
        public OverallOperationProgress OverallProgress { get; init; } = OverallOperationProgress.Initial;
        
        /// <summary>
        /// Per-file progress info for concurrent verification.
        /// Key = stable requirement index, including queued and completed files.
        /// </summary>
        public IReadOnlyDictionary<string, FileOperationProgress>? FileProgress { get; init; }
    }

    /// <summary>
    /// Progress information for testing build downloads.
    /// </summary>
    public class TestingBuildDownloadProgress
    {
        public string Status { get; init; } = string.Empty;
        public int ProgressPercent { get; init; }
        public long DownloadedBytes { get; init; }
        public long TotalBytes { get; init; }
        public double BytesPerSecond { get; init; }
        public TimeSpan? EstimatedTimeRemaining { get; init; }
        public int ThreadCount { get; init; } = 1;
    }

    /// <summary>
    /// Verifies that all local PWAD files with advertised hashes match the server's expected hashes.
    /// </summary>
    /// <param name="server">The server to verify WADs for.</param>
    /// <param name="progress">Progress callback with per-file byte-level progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of WADs with hash mismatches.</returns>
    public async Task<List<WadHashMismatch>> VerifyWadHashesAsync(
        ServerInfo server, 
        IProgress<HashVerificationProgress>? progress,
        CancellationToken cancellationToken,
        IDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        return await Task.Run(() => VerifyWadHashesCoreAsync(server, WadManager.Instance,
            WadHashCacheService.Instance, progress, cancellationToken, _ => true, resolvedWads), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies local optional PWADs that advertise hashes.
    /// Unresolved mismatches can be excluded from launch or offered for download.
    /// </summary>
    public async Task<List<WadHashMismatch>> VerifyOptionalWadHashesAsync(
        ServerInfo server,
        IProgress<HashVerificationProgress>? progress,
        CancellationToken cancellationToken,
        IDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        return await Task.Run(() => VerifyWadHashesCoreAsync(server, WadManager.Instance,
            WadHashCacheService.Instance, progress, cancellationToken, pwad => pwad.IsOptional, resolvedWads), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks whether every server PWAD with an advertised hash can be trusted
    /// from the local WAD hash cache. This never calculates an MD5: it succeeds
    /// only when all required local files are present, their cache snapshots are
    /// still valid, and their full cached MD5 values match the server.
    /// </summary>
    public CachedWadHashVerificationResult CheckCachedWadHashes(
        ServerInfo server,
        IDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        return CheckCachedWadHashes(server, WadManager.Instance, WadHashCacheService.Instance, _ => true, resolvedWads);
    }

    /// <summary>
    /// Checks the cache-only fast path for server PWADs marked optional.
    /// </summary>
    public CachedWadHashVerificationResult CheckCachedOptionalWadHashes(
        ServerInfo server,
        IDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        return CheckCachedWadHashes(server, WadManager.Instance, WadHashCacheService.Instance,
            pwad => pwad.IsOptional, resolvedWads);
    }

    internal static CachedWadHashVerificationResult CheckCachedWadHashes(
        ServerInfo server,
        WadManager wadManager,
        WadHashCacheService hashCache,
        Func<PWadInfo, bool> predicate,
        IDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        var pwadsWithHashes = server.PWADs
            .Where(pwad => predicate(pwad) && !string.IsNullOrEmpty(pwad.Hash))
            .ToList();
        if (pwadsWithHashes.Count == 0)
            return new CachedWadHashVerificationResult(false, 0);

        if (!hashCache.IsEnabled)
            return new CachedWadHashVerificationResult(false, 0);

        var verifiedFileCount = 0;
        foreach (var pwad in pwadsWithHashes)
        {
            var previous = resolvedWads != null && resolvedWads.TryGetValue(pwad.Name, out var selected)
                ? selected : null;
            WadResolutionResult result;
            if (previous is { Status: WadResolutionStatus.Matched }
                && CanReuseJoinResolution(wadManager, previous, pwad.Hash))
            {
                var cachedHash = hashCache.TryGetCachedHashResult(previous.LocalPath);
                if (cachedHash == null || !string.Equals(cachedHash.Hash, pwad.Hash, StringComparison.OrdinalIgnoreCase))
                    return new CachedWadHashVerificationResult(false, verifiedFileCount);
                result = previous with { HashResult = cachedHash };
            }
            else
            {
                result = wadManager.ResolveWadAsync(pwad.Name, pwad.Hash, WadHashLookupMode.CachedOnly,
                    hashCache).GetAwaiter().GetResult();
            }
            if (resolvedWads != null)
                resolvedWads[pwad.Name] = result;
            if (result.Status != WadResolutionStatus.Matched)
                return new CachedWadHashVerificationResult(false, verifiedFileCount);

            verifiedFileCount++;
        }

        return new CachedWadHashVerificationResult(true, verifiedFileCount);
    }

    internal static async Task<List<WadHashMismatch>> VerifyWadHashesCoreAsync(
        ServerInfo server,
        WadManager wadManager,
        WadHashCacheService hashCache,
        IProgress<HashVerificationProgress>? progress,
        CancellationToken cancellationToken,
        Func<PWadInfo, bool> predicate,
        IDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        var mismatches = new System.Collections.Concurrent.ConcurrentBag<WadHashMismatch>();
        var settings = SettingsService.Instance.Settings;
        
        // Verify only the PWADs requested by the caller that advertise hashes.
        var pwadsWithHashes = server.PWADs
            .Where(p => predicate(p) && !string.IsNullOrEmpty(p.Hash))
            .ToList();
        var totalFiles = pwadsWithHashes.Count;
        
        if (totalFiles == 0)
        {
            progress?.Report(new HashVerificationProgress
            {
                CurrentFile = string.Empty,
                CurrentIndex = 0,
                TotalFiles = 0,
                Status = "No hashes to verify.",
                FileSize = 0,
                OverallProgress = new OverallOperationProgress(100, TimeSpan.Zero, true)
            });
            return [];
        }
        
        // Track per-file progress for concurrent display
        var fileProgress = new System.Collections.Concurrent.ConcurrentDictionary<string, FileOperationProgress>();
        var completedCount = 0;
        var overallEstimator = new OverallProgressEstimator();
        
        // Prepare verification tasks
        var verificationItems = new List<PWadInfo>();
        foreach (var pwad in pwadsWithHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            verificationItems.Add(pwad);
            fileProgress[(verificationItems.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                new FileOperationProgress(0, 0, FileName: pwad.Name);
        }

        // Report initial state
        progress?.Report(new HashVerificationProgress
        {
            CurrentFile = string.Empty,
            CurrentIndex = 0,
            TotalFiles = verificationItems.Count,
            Status = $"Verifying {verificationItems.Count} file(s)...",
            FileSize = 0,
            FileProgress = new Dictionary<string, FileOperationProgress>(fileProgress),
            OverallProgress = overallEstimator.Update(new Dictionary<string, FileOperationProgress>(fileProgress))
        });
        
        // Determine concurrency level
        var maxConcurrency = settings.HashVerificationConcurrency;
        if (maxConcurrency <= 0)
            maxConcurrency = verificationItems.Count; // Unlimited = all at once
        
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        
        var progressLock = new object();
        var latestCurrentFile = string.Empty;
        var latestStatus = $"Verifying {verificationItems.Count} file(s)...";
        
        void ReportProgress()
        {
            lock (progressLock)
            {
                var snapshot = new Dictionary<string, FileOperationProgress>(fileProgress);
                progress?.Report(new HashVerificationProgress
                {
                    CurrentFile = Volatile.Read(ref latestCurrentFile),
                    CurrentIndex = Volatile.Read(ref completedCount),
                    TotalFiles = verificationItems.Count,
                    Status = Volatile.Read(ref latestStatus),
                    FileSize = 0,
                    BytesProcessed = 0,
                    FileProgress = snapshot,
                    OverallProgress = overallEstimator.Update(snapshot)
                });
            }
        }
        
        // Create verification tasks - run entirely on thread pool
        var tasks = verificationItems.Select((item, index) => Task.Run(async () =>
        {
            var progressKey = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                void ReportCandidateProgress(WadCandidateHashProgress candidate)
                {
                    fileProgress[progressKey] =
                        new FileOperationProgress(candidate.BytesProcessed, candidate.TotalBytes, FileName: item.Name);
                    Volatile.Write(ref latestCurrentFile, item.Name);
                    Volatile.Write(ref latestStatus,
                        $"Hashing {item.Name} (location {candidate.CandidateIndex}/{candidate.CandidateCount})...");
                }

                WadResolutionResult? previous = null;
                if (resolvedWads != null)
                {
                    lock (resolvedWads)
                        resolvedWads.TryGetValue(item.Name, out previous);
                }
                var resolution = previous is { Status: WadResolutionStatus.Matched }
                    && CanReuseJoinResolution(wadManager, previous, item.Hash)
                    ? previous
                    : await wadManager.ResolveWadAsync(item.Name, item.Hash, WadHashLookupMode.Verify,
                        hashCache, cancellationToken, ReportCandidateProgress).ConfigureAwait(false);
                if (resolvedWads != null)
                {
                    lock (resolvedWads)
                        resolvedWads[item.Name] = resolution;
                }
                if (resolution.Status == WadResolutionStatus.Unavailable)
                {
                    throw new IOException($"Could not verify {item.Name}: {resolution.ErrorMessage ?? "the local file could not be read"}");
                }

                var fileSize = resolution.HashResult?.FileSize ?? 0;
                fileProgress[progressKey] =
                    new FileOperationProgress(fileSize, fileSize, IsComplete: true, FileName: item.Name);
                if (resolution.Status != WadResolutionStatus.Matched)
                {
                    mismatches.Add(new WadHashMismatch
                    {
                        WadName = item.Name,
                        IsOptional = item.IsOptional,
                        LocalPath = resolution.LocalPath ?? string.Empty,
                        LocalHash = resolution.LocalHash ?? string.Empty,
                        ExpectedHash = item.Hash!
                    });
                    LoggingService.Instance.Warning(
                        $"No matching local file for {item.Name} in the configured WAD folders.");
                }
                
                Interlocked.Increment(ref completedCount);
                Volatile.Write(ref latestCurrentFile, item.Name);
                Volatile.Write(ref latestStatus, resolution.Status == WadResolutionStatus.Matched
                    ? resolution.FromCache ? $"Using cached hash: {item.Name}" : $"Verified: {item.Name}"
                    : $"No matching version: {item.Name}");
            }
            finally
            {
                semaphore.Release();
            }
        }, cancellationToken)).ToList();
        
        // Sample the latest byte counts independently of the file-read callbacks.
        using var progressSamplerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progressSamplerTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(
                TimeSpan.FromMilliseconds(AppConstants.UiIntervals.ProgressDisplayRefreshMs));
            try
            {
                while (await timer.WaitForNextTickAsync(progressSamplerCancellation.Token).ConfigureAwait(false))
                    ReportProgress();
            }
            catch (OperationCanceledException) when (progressSamplerCancellation.IsCancellationRequested)
            {
                // The verification batch ended; stop sampling its final state.
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            progressSamplerCancellation.Cancel();
            await progressSamplerTask.ConfigureAwait(false);
        }
        
        var result = mismatches.ToList();
        
        // Final progress report
        Volatile.Write(ref latestCurrentFile, string.Empty);
        Volatile.Write(ref latestStatus, result.Count > 0
            ? $"Verification complete. {result.Count} mismatch(es) found."
            : "All WAD hashes verified successfully.");
        ReportProgress();
        
        return result;
    }

    /// <summary>
    /// Selects a matching local version without changing other search folders.
    /// The downloader replaces its destination only after a verified transfer.
    /// </summary>
    /// <param name="mismatches">List of WAD hash mismatches to resolve.</param>
    /// <returns>List of WADs that need to be downloaded (no local version with matching hash).</returns>
    public List<WadInfo> ResolveHashMismatches(
        List<WadHashMismatch> mismatches,
        CancellationToken cancellationToken = default,
        IProgress<SaveStateProgress>? progress = null,
        IDictionary<string, WadResolutionResult>? resolvedWads = null) =>
        ResolveHashMismatches(mismatches, WadManager.Instance, WadHashCacheService.Instance,
            cancellationToken, progress, resolvedWads);

    internal static List<WadInfo> ResolveHashMismatches(
        List<WadHashMismatch> mismatches,
        WadManager wadManager,
        WadHashCacheService hashCache,
        CancellationToken cancellationToken = default,
        IProgress<SaveStateProgress>? progress = null,
        IDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        var needsDownload = new List<WadInfo>();
        var current = 0;

        foreach (var mismatch in mismatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SaveStateProgress
            {
                Status = $"Resolving version of {mismatch.WadName}",
                Current = current,
                Total = mismatches.Count
            });
            var resolution = wadManager.ResolveWadAsync(mismatch.WadName, mismatch.ExpectedHash,
                WadHashLookupMode.Verify, hashCache, cancellationToken).GetAwaiter().GetResult();
            if (resolution.Status != WadResolutionStatus.Matched)
            {
                var matchingPath = wadManager.FindWadByHash(mismatch.ExpectedHash,
                    Path.GetFileNameWithoutExtension(mismatch.WadName), Path.GetExtension(mismatch.WadName),
                    hashCache,
                    cancellationToken, file => progress?.Report(new SaveStateProgress
                    {
                        Status = $"Checking local version of {Path.GetFileName(file)}",
                        Current = current,
                        Total = mismatches.Count
                    }), (file, bytesProcessed) => progress?.Report(new SaveStateProgress
                    {
                        Status = $"Checking local version of {Path.GetFileName(file)} ({bytesProcessed / (1024d * 1024d):F1} MB)",
                        Current = current,
                        Total = mismatches.Count
                    }));
                if (!string.IsNullOrEmpty(matchingPath))
                {
                    var hash = hashCache.GetHashAsync(matchingPath, null, cancellationToken)
                        .GetAwaiter().GetResult();
                    if (string.Equals(hash.Hash, mismatch.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        resolution = new WadResolutionResult(mismatch.WadName, mismatch.ExpectedHash,
                            WadResolutionStatus.Matched, matchingPath, hash)
                        {
                            IsArchived = true,
                            LookupMode = WadHashLookupMode.Verify
                        };
                    }
                }
            }

            if (resolvedWads != null)
                resolvedWads[mismatch.WadName] = resolution;
            if (resolution.Status != WadResolutionStatus.Matched)
                needsDownload.Add(new WadInfo(mismatch.WadName, mismatch.ExpectedHash));

            current++;
            progress?.Report(new SaveStateProgress
            {
                Status = $"Resolved version of {mismatch.WadName}",
                Current = current,
                Total = mismatches.Count
            });
        }
        
        cancellationToken.ThrowIfCancellationRequested();
        return needsDownload;
    }

    /// <summary>
    /// Checks if the appropriate Zandronum executable is configured for the server.
    /// For testing servers, checks if the version is installed.
    /// </summary>
    /// <param name="server">The server to check.</param>
    /// <returns>True if executable is configured and exists.</returns>
    public bool IsExecutableConfigured(ServerInfo server)
    {
        var settings = SettingsService.Instance.Settings;
        
        if (server.IsTestingServer)
        {
            // For testing, check if testing folder is available (explicit or default)
            var testingRoot = GetTestingRootPath();
            return !string.IsNullOrEmpty(testingRoot);
        }
        
        return !string.IsNullOrEmpty(settings.ZandronumPath) && File.Exists(settings.ZandronumPath);
    }

    /// <summary>
    /// Checks if a specific testing version is installed.
    /// </summary>
    public bool IsTestingVersionInstalled(ServerInfo server)
    {
        var exePath = GetTestingExePath(server);
        return !string.IsNullOrEmpty(exePath) && File.Exists(exePath);
    }

    /// <summary>
    /// Checks whether the exact stable version required by a non-testing server is installed.
    /// Falls back to the configured stable executable when the server does not report a stable version.
    /// </summary>
    public bool IsStableVersionInstalled(ServerInfo server)
    {
        if (server.IsTestingServer)
        {
            return false;
        }

        var exePath = ResolveStableExecutablePath(server);
        return !string.IsNullOrEmpty(exePath) && File.Exists(exePath);
    }

    /// <summary>
    /// Gets the normalized stable version required by a non-testing server, if one can be extracted.
    /// </summary>
    public string? GetRequiredStableVersion(ServerInfo server)
    {
        if (server.IsTestingServer)
        {
            return null;
        }

        return ZandronumStableReleaseService.Instance.TryExtractStableVersion(server.GameVersion, out var version)
            ? version
            : null;
    }

    /// <summary>
    /// Gets the folder path for a specific testing version.
    /// </summary>
    public string? GetTestingVersionFolder(ServerInfo server)
    {
        var testingRoot = GetTestingRootPath();
        if (string.IsNullOrEmpty(testingRoot) || string.IsNullOrEmpty(server.GameVersion))
        {
            return null;
        }
        
        // Sanitize version string for folder name
        var safeVersion = SanitizeFolderName(server.GameVersion);
        return Path.Combine(testingRoot, safeVersion);
    }

    /// <summary>
    /// Gets the root path for testing versions.
    /// Falls back to {ZandronumPath}/TestingVersions/ if not explicitly configured.
    /// </summary>
    public string? GetTestingRootPath() => PathResolver.GetTestingVersionsPath();

    /// <summary>
    /// Gets the exe path for a specific testing version.
    /// </summary>
    private string? GetTestingExePath(ServerInfo server)
    {
        var folder = GetTestingVersionFolder(server);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }
        return PathResolver.FindZandronumExecutable(folder);
    }

    /// <summary>
    /// Extracts the core version string from a full game version.
    /// Strips extraneous data after the version number (e.g., OS info, mod names).
    /// Example: "3.3-alpha-r260112-1855 (TSPGv32) on Linux 6.8.0-58-generic" -> "3.3-alpha-r260112-1855"
    /// </summary>
    public static string ExtractCoreVersion(string fullVersion)
    {
        if (string.IsNullOrEmpty(fullVersion))
        {
            return fullVersion;
        }
        
        // Strip everything from the first space character (matches Doomseeker behavior)
        var spaceIndex = fullVersion.IndexOf(' ');
        if (spaceIndex > 0)
        {
            return fullVersion.Substring(0, spaceIndex);
        }
        
        return fullVersion;
    }

    /// <summary>
    /// Sanitizes a string to be safe for use as a folder name.
    /// </summary>
    private static string SanitizeFolderName(string name)
    {
        // First extract the core version, then sanitize
        var coreVersion = ExtractCoreVersion(name);
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", coreVersion.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Gets the path to the Zandronum executable for the given server.
    /// For testing servers, returns the versioned path.
    /// </summary>
    public string GetExecutablePath(ServerInfo server)
    {
        var settings = SettingsService.Instance.Settings;
        
        if (server.IsTestingServer)
        {
            return GetTestingExePath(server) ?? string.Empty;
        }

        return ResolveStableExecutablePath(server) ?? string.Empty;
    }

    private string? ResolveStableExecutablePath(ServerInfo server)
    {
        var configuredStablePath = SettingsService.Instance.Settings.ZandronumPath;
        if (string.IsNullOrEmpty(configuredStablePath) || !File.Exists(configuredStablePath))
        {
            return null;
        }

        var stableReleaseService = ZandronumStableReleaseService.Instance;
        if (!stableReleaseService.TryExtractStableVersion(server.GameVersion, out var requiredVersion))
        {
            return configuredStablePath;
        }

        if (stableReleaseService.TryGetInstalledStableVersion(configuredStablePath, out var configuredVersion, out _) &&
            stableReleaseService.CompareStableVersions(configuredVersion, requiredVersion) == 0)
        {
            return configuredStablePath;
        }

        return stableReleaseService.GetInstalledArchivedExecutablePath(configuredStablePath, requiredVersion);
    }

    /// <summary>
    /// Downloads and installs a testing version for the specified server.
    /// </summary>
    /// <param name="server">The server requiring the testing version.</param>
    /// <returns>True if download and installation succeeded.</returns>
    public async Task<bool> DownloadTestingBuildAsync(ServerInfo server)
    {
        if (string.IsNullOrEmpty(server.TestingArchive))
        {
            LaunchError?.Invoke(this, "No testing archive URL available from server.");
            return false;
        }

        var archiveUri = ResolveTestingArchiveUri(server.TestingArchive);
        if (archiveUri == null)
        {
            LaunchError?.Invoke(this, $"Invalid testing archive URL: {server.TestingArchive}");
            return false;
        }

        var versionFolder = GetTestingVersionFolder(server);
        if (string.IsNullOrEmpty(versionFolder))
        {
            LaunchError?.Invoke(this, "Testing versions folder not configured.");
            return false;
        }

        LoggingService.Instance.Info($"Downloading testing version from: {archiveUri}");
        DownloadProgress?.Invoke(this, new TestingBuildDownloadProgress
        {
            Status = "Starting download...",
            ProgressPercent = 0
        });

        try
        {
            using var response = await _httpClient.GetAsync(archiveUri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var tempFile = Path.GetTempFileName();

            try
            {
                await DownloadTestingArchiveAsync(archiveUri, tempFile, totalBytes, response);

                DownloadProgress?.Invoke(this, new TestingBuildDownloadProgress
                {
                    Status = "Extracting...",
                    ProgressPercent = 90,
                    DownloadedBytes = totalBytes > 0 ? totalBytes : 0,
                    TotalBytes = totalBytes > 0 ? totalBytes : 0
                });

                Directory.CreateDirectory(versionFolder);
                await Task.Run(() => ExtractArchive(tempFile, versionFolder, archiveUri.AbsoluteUri, totalBytes));
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }

            // Copy configuration files from base Zandronum directory (matches Doomseeker behavior)
            DownloadProgress?.Invoke(this, new TestingBuildDownloadProgress
            {
                Status = "Copying configuration files...",
                ProgressPercent = 95,
                DownloadedBytes = totalBytes > 0 ? totalBytes : 0,
                TotalBytes = totalBytes > 0 ? totalBytes : 0
            });
            await Task.Run(() => CopyConfigFilesToTestingVersion(versionFolder));

            DownloadProgress?.Invoke(this, new TestingBuildDownloadProgress
            {
                Status = "Complete!",
                ProgressPercent = 100,
                DownloadedBytes = totalBytes > 0 ? totalBytes : 0,
                TotalBytes = totalBytes > 0 ? totalBytes : 0
            });
            LoggingService.Instance.Info($"Testing version {ExtractCoreVersion(server.GameVersion)} installed to: {versionFolder}");

            return IsTestingVersionInstalled(server);
        }
        catch (Exception ex)
        {
            var error = $"Failed to download testing build: {ex.Message}";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Error(error);
            return false;
        }
    }

    private async Task DownloadTestingArchiveAsync(Uri archiveUri, string outputPath, long totalBytes, HttpResponseMessage initialResponse)
    {
        var domain = archiveUri.Host;
        var (domainMaxThreads, minSegmentSizeKb, _, _) = _domainConfig.GetEffectiveThreadSettings(domain);
        var threadCount = CalculateOptimalThreads(totalBytes, domainMaxThreads, minSegmentSizeKb);
        var minSegmentSizeBytes = minSegmentSizeKb * 1024L;
        var supportsRange = totalBytes > 0 && threadCount > 1 && await TestRangeRequestAsync(archiveUri);

        if (supportsRange && totalBytes > minSegmentSizeBytes * 2)
        {
            LoggingService.Instance.Verbose($"Using {threadCount} threads for testing build download from {domain}.");
            initialResponse.Dispose();
            await MultiThreadedDownloadTestingArchiveAsync(archiveUri, outputPath, totalBytes, threadCount);
            _domainConfig.UpdateThreadCount(domain, threadCount);
            return;
        }

        await SingleThreadDownloadTestingArchiveAsync(archiveUri, outputPath, totalBytes, initialResponse);
    }

    private async Task SingleThreadDownloadTestingArchiveAsync(Uri archiveUri, string outputPath, long totalBytes, HttpResponseMessage? response)
    {
        using var ownedResponse = response is null
            ? await _httpClient.GetAsync(archiveUri, HttpCompletionOption.ResponseHeadersRead)
            : null;
        using var activeResponse = response ?? ownedResponse!;

        activeResponse.EnsureSuccessStatusCode();

        var resolvedTotalBytes = totalBytes > 0 ? totalBytes : activeResponse.Content.Headers.ContentLength ?? -1;
        await using var contentStream = await activeResponse.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, AppConstants.BufferSizes.FileStreamBuffer, true);

        var buffer = new byte[AppConstants.BufferSizes.NetworkBuffer];
        var totalRead = 0L;
        var stopwatch = Stopwatch.StartNew();
        var lastUpdate = Stopwatch.StartNew();
        var lastBytes = 0L;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalRead += bytesRead;

            if (lastUpdate.ElapsedMilliseconds >= AppConstants.UiIntervals.UiUpdateThrottleMs ||
                (resolvedTotalBytes > 0 && totalRead >= resolvedTotalBytes))
            {
                var elapsedSeconds = Math.Max(lastUpdate.Elapsed.TotalSeconds, 0.001);
                var speed = (totalRead - lastBytes) / elapsedSeconds;
                ReportTestingDownloadProgress(totalRead, resolvedTotalBytes, speed, threadCount: 1);
                lastBytes = totalRead;
                lastUpdate.Restart();
            }
        }

        var finalElapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        var finalSpeed = totalRead / finalElapsedSeconds;
        ReportTestingDownloadProgress(totalRead, resolvedTotalBytes, finalSpeed, threadCount: 1);
    }

    private async Task MultiThreadedDownloadTestingArchiveAsync(Uri archiveUri, string outputPath, long totalBytes, int threadCount)
    {
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

        var lastUpdate = Stopwatch.StartNew();
        var lastBytes = 0L;
        using var progressTimer = new System.Timers.Timer(AppConstants.UiIntervals.UiUpdateThrottleMs);
        progressTimer.Elapsed += (_, _) =>
        {
            var currentBytes = downloadedBytes.Sum();
            var elapsedSeconds = Math.Max(lastUpdate.Elapsed.TotalSeconds, 0.001);
            var speed = (currentBytes - lastBytes) / elapsedSeconds;
            ReportTestingDownloadProgress(currentBytes, totalBytes, speed, threadCount);
            lastBytes = currentBytes;
            lastUpdate.Restart();
        };

        progressTimer.Start();

        const int maxSegmentRetries = 3;

        try
        {
            var downloadTasks = segments.Select((segment, index) =>
                DownloadTestingArchiveSegmentAsync(archiveUri, segment.Start, segment.End, outputHandle, index, downloadedBytes, failedSegments));

            await Task.WhenAll(downloadTasks);

            for (var retry = 0; retry < maxSegmentRetries && !failedSegments.IsEmpty; retry++)
            {
                var retryCandidates = failedSegments.ToList();
                failedSegments = new ConcurrentBag<(int SegmentIndex, bool IsConnectionLimit)>();

                LoggingService.Instance.Verbose($"Retrying {retryCandidates.Count} testing build segment(s), attempt {retry + 1}/{maxSegmentRetries}.");
                var retryTasks = retryCandidates.Select(candidate =>
                {
                    var segment = segments[candidate.SegmentIndex];
                    return DownloadTestingArchiveSegmentAsync(archiveUri, segment.Start, segment.End, outputHandle, candidate.SegmentIndex, downloadedBytes, failedSegments);
                });

                await Task.WhenAll(retryTasks);
            }

            var connectionLimitFailures = failedSegments.Count(f => f.IsConnectionLimit);
            if (connectionLimitFailures > segments.Count * 0.3)
            {
                _domainConfig.ReduceThreadCount(archiveUri.Host, threadCount);
                throw new Exception($"Testing build download hit connection limits with {threadCount} threads.");
            }

            if (!failedSegments.IsEmpty)
            {
                throw new Exception($"{failedSegments.Count} download segments failed.");
            }
        }
        finally
        {
            progressTimer.Stop();
        }

        var finalBytes = downloadedBytes.Sum();
        ReportTestingDownloadProgress(finalBytes, totalBytes, 0, threadCount);
    }

    private async Task DownloadTestingArchiveSegmentAsync(
        Uri archiveUri,
        long start,
        long end,
        Microsoft.Win32.SafeHandles.SafeFileHandle outputHandle,
        int segmentIndex,
        long[] downloadedBytes,
        ConcurrentBag<(int SegmentIndex, bool IsConnectionLimit)> failedSegments)
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
                LoggingService.Instance.Verbose($"Resuming testing build segment bytes={start}-{end} from {requestStart}.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, archiveUri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(requestStart, end);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
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

            await using var stream = await response.Content.ReadAsStreamAsync();
            long position = requestStart;
            var buffer = new byte[AppConstants.BufferSizes.NetworkBuffer];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
            {
                if (position + bytesRead - 1 > end)
                {
                    throw new IOException($"Segment response exceeded requested range bytes={requestStart}-{end}");
                }

                await RandomAccess.WriteAsync(outputHandle, buffer.AsMemory(0, bytesRead), position);
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
            var isConnectionLimit = ex.StatusCode == HttpStatusCode.TooManyRequests ||
                                    ex.StatusCode == HttpStatusCode.ServiceUnavailable ||
                                    ex.InnerException is System.Net.Sockets.SocketException;
            failedSegments.Add((segmentIndex, isConnectionLimit));
        }
        catch
        {
            failedSegments.Add((segmentIndex, false));
        }
    }

    private async Task<bool> TestRangeRequestAsync(Uri archiveUri)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, archiveUri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode && response.StatusCode == HttpStatusCode.PartialContent;
        }
        catch
        {
            return false;
        }
    }

    private static int CalculateOptimalThreads(long totalBytes, int domainMaxThreads, int minSegmentSizeKb)
    {
        if (totalBytes <= 0)
        {
            return 1;
        }

        var minSegmentSize = minSegmentSizeKb * 1024L;
        var maxBySegmentSize = (int)Math.Max(1, totalBytes / minSegmentSize);
        var maxByFileSize = totalBytes switch
        {
            < 1_000_000 => 4,
            < 5_000_000 => 16,
            < 20_000_000 => 32,
            < 50_000_000 => 64,
            < 100_000_000 => 128,
            _ => domainMaxThreads
        };

        return Math.Max(1, Math.Min(Math.Min(maxByFileSize, domainMaxThreads), maxBySegmentSize));
    }

    private void ReportTestingDownloadProgress(long downloadedBytes, long totalBytes, double bytesPerSecond, int threadCount)
    {
        TimeSpan? estimatedTimeRemaining = null;
        if (totalBytes > 0 && bytesPerSecond > 0 && downloadedBytes < totalBytes)
        {
            estimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - downloadedBytes) / bytesPerSecond);
        }

        DownloadProgress?.Invoke(this, new TestingBuildDownloadProgress
        {
            Status = "Downloading...",
            ProgressPercent = totalBytes > 0 ? (int)((downloadedBytes * 100) / totalBytes) : 0,
            DownloadedBytes = downloadedBytes,
            TotalBytes = totalBytes > 0 ? totalBytes : 0,
            BytesPerSecond = bytesPerSecond,
            EstimatedTimeRemaining = estimatedTimeRemaining,
            ThreadCount = threadCount
        });
    }

    private static Uri? ResolveTestingArchiveUri(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return null;
        }

        if (Uri.TryCreate(archivePath, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        return Uri.TryCreate(TestingArchiveBaseUri, archivePath, out var relativeUri)
            ? relativeUri
            : null;
    }

    /// <summary>
    /// Extracts a testing archive through the shared installer destination policy.
    /// </summary>
    private void ExtractArchive(string archivePath, string destinationFolder, string originalUrl, long totalBytes)
    {
        var extension = Path.GetExtension(originalUrl).ToLowerInvariant();
        var format = extension is ".zip" or ".pk3"
            ? ArchiveExtractionFormat.Zip
            : ArchiveExtractionFormat.SharpCompress;
        ArchiveExtractionService.Extract(archivePath, destinationFolder, format, update =>
            DownloadProgress?.Invoke(this, new TestingBuildDownloadProgress
            {
                Status = $"Extracting {update.EntryName}...",
                ProgressPercent = 90 + update.Percent * 5 / 100,
                DownloadedBytes = Math.Max(totalBytes, 0),
                TotalBytes = Math.Max(totalBytes, 0)
            }));
    }

    /// <summary>
    /// Copies configuration files (.ini) from the base Zandronum directory to the testing version folder.
    /// This matches Doomseeker's behavior of copying config files when installing testing versions.
    /// </summary>
    private void CopyConfigFilesToTestingVersion(string testingVersionFolder)
    {
        var settings = SettingsService.Instance.Settings;
        
        // Get the base Zandronum directory (where the stable exe is located)
        if (string.IsNullOrEmpty(settings.ZandronumPath) || !File.Exists(settings.ZandronumPath))
        {
            LoggingService.Instance.Verbose("No base Zandronum path configured, skipping config file copy.");
            return;
        }

        var baseDir = Path.GetDirectoryName(settings.ZandronumPath);
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
        {
            LoggingService.Instance.Verbose("Base Zandronum directory not found, skipping config file copy.");
            return;
        }

        // Copy all .ini files from the base directory
        var iniFiles = Directory.GetFiles(baseDir, "*.ini", SearchOption.TopDirectoryOnly);
        var copiedCount = 0;

        foreach (var iniFile in iniFiles)
        {
            var fileName = Path.GetFileName(iniFile);
            var targetPath = Path.Combine(testingVersionFolder, fileName);
            
            try
            {
                // Only copy if target doesn't exist (don't overwrite user's testing config)
                if (!File.Exists(targetPath))
                {
                    File.Copy(iniFile, targetPath);
                    copiedCount++;
                    LoggingService.Instance.Verbose($"Copied config file: {fileName}");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Warning($"Failed to copy {fileName}: {ex.Message}");
            }
        }

        if (copiedCount > 0)
        {
            LoggingService.Instance.Info($"Copied {copiedCount} configuration file(s) to testing version folder.");
        }
    }

    /// <summary>
    /// Gets a list of installed testing versions.
    /// </summary>
    public List<string> GetInstalledTestingVersions()
    {
        return PathResolver.GetManagedZandronumVersions(GetTestingRootPath())
            .Select(version => version.Name).ToList();
    }

    private string BuildCommandLine(
        ServerInfo server,
        string? connectPassword,
        string? joinPassword,
        ISet<string>? excludedOptionalPwads = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, WadResolutionResult>? resolvedWads = null) =>
        BuildConnectArguments(server, WadManager.Instance, connectPassword, joinPassword,
            excludedOptionalPwads, cancellationToken, resolvedWads);

    internal static string BuildConnectArguments(
        ServerInfo server,
        WadManager wadManager,
        string? connectPassword,
        string? joinPassword,
        ISet<string>? excludedOptionalPwads = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, WadResolutionResult>? resolvedWads = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = new List<string>();

        string? ResolveLaunchPath(string name, string? expectedHash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolvedWads == null || !resolvedWads.TryGetValue(name, out var selected))
                return wadManager.FindWad(name);

            if (CanReuseJoinResolution(wadManager, selected, expectedHash))
            {
                return selected.LocalPath;
            }

            var mode = selected.LookupMode == WadHashLookupMode.FileNameOnly
                ? WadHashLookupMode.FileNameOnly
                : WadHashLookupMode.Verify;
            var replacement = wadManager.ResolveWadAsync(name, expectedHash, mode, cancellationToken)
                .GetAwaiter().GetResult();
            if (!replacement.IsUsable)
                throw new IOException($"No usable local file for {name}. {replacement.ErrorMessage ?? "The file changed or is no longer available; try joining again."}");
            return replacement.LocalPath;
        }
        
        // Connection
        args.Add($"-connect {server.Address}:{server.Port}");
        
        // IWAD
        if (!string.IsNullOrEmpty(server.IWAD))
        {
            var iwadPath = ResolveLaunchPath(server.IWAD, null);
            if (!string.IsNullOrEmpty(iwadPath))
            {
                args.Add($"-iwad \"{iwadPath}\"");
            }
        }
        
        // PWADs
        var pwadPaths = new List<string>();
        foreach (var pwad in server.PWADs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pwad.IsOptional && excludedOptionalPwads?.Contains(pwad.Name) == true)
            {
                continue;
            }

            var path = ResolveLaunchPath(pwad.Name, pwad.Hash);
            if (!string.IsNullOrEmpty(path))
            {
                pwadPaths.Add($"\"{path}\"");
            }
        }
        if (pwadPaths.Count > 0)
        {
            args.Add($"-file {string.Join(" ", pwadPaths)}");
        }
        
        // Passwords
        if (!string.IsNullOrEmpty(connectPassword))
        {
            args.Add($"+cl_password \"{connectPassword}\"");
        }
        if (!string.IsNullOrEmpty(joinPassword))
        {
            args.Add($"+cl_joinpassword \"{joinPassword}\"");
        }
        
        cancellationToken.ThrowIfCancellationRequested();
        return string.Join(" ", args);
    }
    
    /// <summary>
    /// Gets the full connect command for a server including the executable path.
    /// Uses full paths for all files.
    /// </summary>
    /// <param name="server">The server to connect to.</param>
    /// <returns>The full command string, or null if executable not found.</returns>
    public string? GetFullConnectCommand(ServerInfo server)
    {
        var exePath = GetExecutablePath(server);
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return null;
        
        var args = BuildCommandLine(server, null, null);
        return $"\"{exePath}\" {args}";
    }

    /// <summary>
    /// Launches Zandronum in offline (single-player) mode with the given IWAD and PWADs.
    /// </summary>
    /// <param name="exePath">Full path to the Zandronum executable, or null for the stable configured path.</param>
    public bool LaunchOffline(string? exePath, string iwadPath, IReadOnlyList<string> pwadPaths, int skill, string? map)
    {
        exePath ??= SettingsService.Instance.Settings.ZandronumPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            LaunchError?.Invoke(this, "Zandronum executable not configured. Please set it in Settings.");
            return false;
        }

        var args = new List<string>
        {
            $"-iwad \"{iwadPath}\""
        };

        if (pwadPaths.Count > 0)
        {
            args.Add($"-file {string.Join(" ", pwadPaths.Select(p => $"\"{p}\""))}");
        }

        if (skill >= 0 && skill <= 4)
        {
            args.Add($"-skill {skill}");
        }

        if (!string.IsNullOrWhiteSpace(map))
        {
            args.Add($"+map \"{map}\"");
        }

        var argString = string.Join(" ", args);
        LoggingService.Instance.Verbose($"Launching offline: {exePath} {argString}");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = argString,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            };
            var launchedProcess = Process.Start(startInfo);
            ZandronumConfigSyncService.Instance.TrackLaunchedProcess(launchedProcess, exePath);

            var msg = "Launched Zandronum in offline mode";
            LaunchSuccess?.Invoke(this, msg);
            LoggingService.Instance.Info(msg);
            return true;
        }
        catch (Exception ex)
        {
            var error = $"Failed to launch Zandronum offline: {ex.Message}";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Error(error);
            return false;
        }
    }

    /// <summary>
    /// Launches Zandronum as a host server (listen or dedicated).
    /// For dedicated servers, tries zandronum-server.exe first, falling back to zandronum.exe -host.
    /// </summary>
    /// <param name="exePath">Full path to the Zandronum executable, or null for the stable configured path.</param>
    public bool LaunchHost(
        string? exePath,
        string iwadPath,
        IReadOnlyList<string> pwadPaths,
        int skill,
        int maxPlayers,
        int maxClients,
        bool isDedicated,
        string? map,
        string? serverName,
        string? password,
        string? joinPassword,
        IReadOnlyList<string>? optionalPwadPaths = null)
    {
        exePath ??= SettingsService.Instance.Settings.ZandronumPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            LaunchError?.Invoke(this, "Zandronum executable not configured. Please set it in Settings.");
            return false;
        }

        string resolvedExePath;
        var args = new List<string>();

        if (isDedicated)
        {
            var exeDir = Path.GetDirectoryName(exePath) ?? "";
            var dedicatedExe = Path.Combine(exeDir, "zandronum-server.exe");
            if (File.Exists(dedicatedExe))
            {
                resolvedExePath = dedicatedExe;
            }
            else
            {
                resolvedExePath = exePath;
                args.Add("-host");
            }
        }
        else
        {
            resolvedExePath = exePath;
            args.Add("-host");
        }

        args.Add($"-iwad \"{iwadPath}\"");

        if (pwadPaths.Count > 0)
        {
            args.Add($"-file {string.Join(" ", pwadPaths.Select(p => $"\"{p}\""))}");
        }

        if (optionalPwadPaths is { Count: > 0 })
        {
            // Zandronum's -optfile marks these loaded files as optional for
            // clients connecting to a hosted server. They remain in the
            // server's load order, but clients may join without them.
            args.Add($"-optfile {string.Join(" ", optionalPwadPaths.Select(p => $"\"{p}\""))}");
        }

        if (skill >= 0 && skill <= 4)
        {
            args.Add($"-skill {skill}");
        }

        args.Add($"+sv_maxplayers {maxPlayers}");
        args.Add($"+sv_maxclients {maxClients}");

        if (!string.IsNullOrWhiteSpace(map))
        {
            args.Add($"+map \"{map}\"");
        }

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            args.Add($"+sv_hostname \"{serverName}\"");
        }

        if (!string.IsNullOrWhiteSpace(password))
        {
            args.Add($"+sv_password \"{password}\"");
        }

        if (!string.IsNullOrWhiteSpace(joinPassword))
        {
            args.Add($"+sv_joinpassword \"{joinPassword}\"");
        }

        var argString = string.Join(" ", args);
        LoggingService.Instance.Verbose($"Launching host: {resolvedExePath} {argString}");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = resolvedExePath,
                Arguments = argString,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(resolvedExePath)
            };
            var launchedProcess = Process.Start(startInfo);
            ZandronumConfigSyncService.Instance.TrackLaunchedProcess(launchedProcess, resolvedExePath);

            var mode = isDedicated ? "dedicated server" : "listen server";
            var msg = $"Launched Zandronum as {mode}";
            LaunchSuccess?.Invoke(this, msg);
            LoggingService.Instance.Info(msg);
            return true;
        }
        catch (Exception ex)
        {
            var error = $"Failed to launch Zandronum host: {ex.Message}";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Error(error);
            return false;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
