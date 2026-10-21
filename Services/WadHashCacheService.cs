using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ZScape.Utilities;

namespace ZScape.Services;

/// <summary>
/// Stores completed WAD MD5 calculations so an unchanged local file does not
/// need to be read again for every server join. Cache entries are never keyed
/// by filename or a shortened hash: every entry is tied to a canonical full
/// path and a file snapshot, then compared to the server's full MD5 by the
/// caller.
/// </summary>
public sealed class WadHashCacheService
{
    private const int CurrentSchemaVersion = 1;
    private const int FileBufferSize = 1024 * 1024;
    // Smaller hashing reads keep the latest byte count current; the UI samples
    // that state at a fixed interval while sequential file I/O stays buffered.
    private const int HashReadBufferSize = 64 * 1024;

    private static readonly Lazy<WadHashCacheService> InstanceFactory =
        new(() => new WadHashCacheService());

    private readonly object _entriesLock = new();
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly Dictionary<string, WadHashCacheEntry> _entries;
    // Per-file opt-outs are deliberately stored with the cache metadata rather
    // than in general settings. They are canonical-path rules and only affect
    // this performance cache; they never suppress a server hash check.
    private readonly HashSet<string> _excludedPaths;
    private readonly string _cachePath;
    private readonly Func<bool> _isEnabled;
    private readonly LoggingService _logger = LoggingService.Instance;
    private bool _isDirty;

    public static WadHashCacheService Instance => InstanceFactory.Value;

    /// <summary>The portable cache file written beside the running executable.</summary>
    public string CacheFilePath => _cachePath;

    /// <summary>
    /// Indicates whether cached MD5 values may be reused or newly recorded.
    /// Disabling this setting leaves the cache file untouched and forces normal
    /// file hashing during verification. Individually excluded files behave the
    /// same way without changing the global preference.
    /// </summary>
    public bool IsEnabled => _isEnabled();

    private WadHashCacheService() : this(
        Path.Combine(AppContext.BaseDirectory, "wad-hash-cache.json"),
        () => SettingsService.Instance.Settings.EnableWadHashCache) { }

    internal WadHashCacheService(string cachePath, Func<bool> isEnabled)
    {
        _cachePath = cachePath;
        _isEnabled = isEnabled;
        _entries = new Dictionary<string, WadHashCacheEntry>(PathResolver.DirectoryPathComparer);
        _excludedPaths = new HashSet<string>(PathResolver.DirectoryPathComparer);
        LoadCache();
    }

    /// <summary>
    /// Returns whether this exact local file has been explicitly excluded from
    /// hash caching. An exclusion is path-specific and does not skip normal
    /// hash verification when joining a server.
    /// </summary>
    public bool IsHashCachingExcluded(string? filePath)
    {
        var normalizedPath = NormalizePath(filePath);
        if (normalizedPath == null)
            return false;

        lock (_entriesLock)
            return _excludedPaths.Contains(normalizedPath);
    }

    /// <summary>
    /// Persists per-file cache exclusions. Excluding a file immediately removes
    /// its existing MD5 entry so it cannot be reused later. Allowing caching
    /// again does not fabricate a hash; the next verification or cache pass
    /// calculates a fresh one.
    /// </summary>
    public async Task SetHashCachingExcludedAsync(
        IEnumerable<string> filePaths,
        bool isExcluded)
    {
        var paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(PathResolver.DirectoryPathComparer)
            .ToList();
        if (paths.Count == 0)
            return;

        var changed = false;
        lock (_entriesLock)
        {
            foreach (var path in paths)
            {
                if (isExcluded)
                {
                    changed |= _excludedPaths.Add(path);
                    changed |= _entries.Remove(path);
                }
                else
                {
                    changed |= _excludedPaths.Remove(path);
                }
            }

            if (changed)
                _isDirty = true;
        }

        if (changed)
            await Task.Run(PersistIfDirtyAsync).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a cached MD5 only when the file still matches the exact snapshot
    /// captured at hash time. A filename on its own is never sufficient.
    /// </summary>
    public string? TryGetCachedHash(string? filePath)
        => TryGetCachedHashResult(filePath)?.Hash;

    internal WadHashResult? TryGetCachedHashResult(string? filePath)
    {
        if (!IsEnabled)
            return null;

        var snapshot = TryCreateSnapshot(filePath);
        var hash = snapshot == null ? null : TryGetCachedHash(snapshot);
        return hash == null ? null : new WadHashResult(hash, true, snapshot!.Length, null)
        {
            Snapshot = snapshot
        };
    }

    internal static bool IsHashResultCurrent(WadHashResult result) =>
        result.Snapshot is { } original
        && TryCreateSnapshot(original.Path) is { } current
        && original.Matches(current);

    // Historical full-path entries also let discovery recognize a disappeared
    // matching copy after restart. They are hints only, never validated hashes.
    internal IReadOnlyList<string> GetRecordedPathsForHash(string expectedHash)
    {
        if (!IsEnabled)
            return [];

        lock (_entriesLock)
            return _entries.Where(pair => !_excludedPaths.Contains(pair.Key)
                && string.Equals(pair.Value.Md5, expectedHash, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key).ToArray();
    }

    /// <summary>
    /// Returns whether the cache has ever recorded this canonical path. This
    /// is deliberately only a cheap hint for UI scans; callers must still use
    /// <see cref="TryGetCachedHash"/> before treating the entry as valid.
    /// </summary>
    public bool HasCachedEntryForPath(string? filePath)
    {
        if (!IsEnabled)
            return false;

        var normalizedPath = NormalizePath(filePath);
        if (normalizedPath == null)
            return false;

        lock (_entriesLock)
            return HasCachedEntryForNormalizedPath(normalizedPath);
    }

    /// <summary>
    /// Menu hints use only canonical paths and in-memory metadata. A recorded
    /// entry means an explicit action should recalculate it, not that its file
    /// snapshot is still valid. Validation belongs to the actual hash operation.
    /// </summary>
    public WadHashActions GetActionsForFiles(IEnumerable<string> filePaths, bool isBusy = false)
    {
        // Preserve the selection count: even two rows pointing at the same
        // canonical file are a multi-selection, not a single calculate action.
        var paths = filePaths.Select(NormalizePath).OfType<string>().ToList();
        var enabled = IsEnabled;
        lock (_entriesLock)
        {
            var allExcluded = paths.Count > 0 && paths.All(_excludedPaths.Contains);
            var hasRecordedHash = paths.Count == 1 && enabled
                && HasCachedEntryForNormalizedPath(paths[0]);
            return new WadHashActions(paths.Count, enabled, allExcluded, hasRecordedHash, isBusy);
        }
    }

    private bool HasCachedEntryForNormalizedPath(string path) =>
        !_excludedPaths.Contains(path) && _entries.ContainsKey(path);

    /// <summary>
    /// Runs a manual cache action, including its initial file inspection and
    /// post-calculation snapshot validation, away from the caller's UI thread.
    /// Ordinary verification still uses GetHashAsync and is never suppressed
    /// by cache preferences or the manual-action eligibility policy.
    /// </summary>
    public Task<WadHashActionResult> CalculateAndCacheHashAsync(
        string filePath, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var actions = GetActionsForFiles([filePath]);
            if (!actions.CanCalculate)
            {
                return new WadHashActionResult(
                    new WadHashResult(null, false, 0, actions.CalculateToolTip),
                    CachedHash: null, actions.IsRecalculation);
            }

            var result = actions.IsRecalculation
                ? await RefreshHashAsync(filePath, null, cancellationToken).ConfigureAwait(false)
                : await GetHashAsync(filePath, null, cancellationToken).ConfigureAwait(false);
            return new WadHashActionResult(result, TryGetCachedHash(filePath), actions.IsRecalculation);
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a full MD5 for a file, reusing an unchanged cached value when safe.
    /// New calculations are recorded automatically when the cache is enabled.
    /// </summary>
    public async Task<WadHashResult> GetHashAsync(
        string filePath,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        return await GetHashCoreAsync(
            filePath,
            progress,
            cancellationToken,
            forceRefresh: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Recalculates a file's full MD5 even when its current snapshot already
    /// has a valid cache entry. The freshly calculated value replaces that
    /// entry only when the file remains unchanged throughout the calculation.
    /// </summary>
    public async Task<WadHashResult> RefreshHashAsync(
        string filePath,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        return await GetHashCoreAsync(
            filePath,
            progress,
            cancellationToken,
            forceRefresh: true).ConfigureAwait(false);
    }

    // Download staging files are transient. Reuse the snapshot-checked hash
    // calculation without persisting their temporary paths in the cache.
    internal Task<WadHashResult> CalculateUncachedHashAsync(
        string filePath, Action<long>? progress, CancellationToken cancellationToken) =>
        GetHashCoreAsync(filePath, progress, cancellationToken, forceRefresh: true, cacheResult: false);

    private async Task<WadHashResult> GetHashCoreAsync(
        string filePath,
        Action<long>? progress,
        CancellationToken cancellationToken,
        bool forceRefresh,
        bool cacheResult = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var initialSnapshot = TryCreateSnapshot(filePath);
        if (initialSnapshot == null)
        {
            return new WadHashResult(
                Hash: null,
                FromCache: false,
                FileSize: 0,
                ErrorMessage: "The file no longer exists or could not be inspected.");
        }

        if (!forceRefresh
            && IsEnabled
            && !IsHashCachingExcluded(initialSnapshot.Path)
            && TryGetCachedHash(initialSnapshot) is { } cachedHash)
        {
            progress?.Invoke(initialSnapshot.Length);
            return new WadHashResult(cachedHash, FromCache: true, initialSnapshot.Length, ErrorMessage: null)
            {
                Snapshot = initialSnapshot
            };
        }

        var computedHash = await ComputeFileHashAsync(filePath, progress, cancellationToken,
            FileShare.ReadWrite | FileShare.Delete).ConfigureAwait(false);
        if (computedHash == null)
        {
            return new WadHashResult(
                Hash: null,
                FromCache: false,
                FileSize: initialSnapshot.Length,
                ErrorMessage: "The file could not be read while calculating its MD5.");
        }

        var finalSnapshot = TryCreateSnapshot(filePath);
        if (finalSnapshot == null || !initialSnapshot.Matches(finalSnapshot))
        {
            return new WadHashResult(null, FromCache: false, initialSnapshot.Length,
                ErrorMessage: "The file changed while its MD5 was being calculated. Try again.");
        }

        var canCacheResult = cacheResult && IsEnabled && !IsHashCachingExcluded(initialSnapshot.Path);
        if (canCacheResult)
        {
            StoreHash(finalSnapshot, computedHash);
            await PersistIfDirtyAsync().ConfigureAwait(false);
        }
        return new WadHashResult(computedHash, FromCache: false, initialSnapshot.Length, ErrorMessage: null)
        {
            Snapshot = finalSnapshot
        };
    }

    /// <summary>
    /// Records a verified full MD5 after a staged file is installed. The
    /// installed file must still have the verified identity and metadata;
    /// a prior hash must never be attached to a replacement file's snapshot.
    /// </summary>
    internal async Task RecordHashAsync(string filePath, WadHashResult verifiedResult)
    {
        if (!IsEnabled || verifiedResult.Hash is not { } hash || !IsValidMd5(hash)
            || verifiedResult.Snapshot is not { } original)
            return;

        var snapshot = TryCreateSnapshot(filePath);
        if (snapshot == null || !original.Matches(snapshot))
            return;

        StoreHash(snapshot, hash);
        await PersistIfDirtyAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Computes and records hashes for a set of local files sequentially. This
    /// intentionally favors a responsive desktop and predictable disk use over
    /// saturating every drive with concurrent reads.
    /// </summary>
    public async Task<WadHashCacheSummary> CacheFilesAsync(
        IEnumerable<string> filePaths,
        IProgress<WadHashCacheProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException(
                "WAD hash caching is disabled in Preferences. Enable it before caching files.");
        }

        var files = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(PathResolver.DirectoryPathComparer)
            .Where(File.Exists)
            .OrderBy(path => path, PathResolver.DirectoryPathComparer)
            .ToList();

        var cachedCount = 0;
        var alreadyCachedCount = 0;
        var failedCount = 0;
        var excludedCount = 0;
        var totalFiles = files.Count;

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = files[index];
            var fileName = Path.GetFileName(filePath);
            var fileSize = TryGetFileLength(filePath);

            // Exclusions are checked before any cache snapshot or MD5 work.
            // They remain visible in the progress dialog as an intentional
            // user choice rather than looking like a stalled queued item.
            if (IsHashCachingExcluded(filePath))
            {
                excludedCount++;
                progress?.Report(new WadHashCacheProgress(
                    filePath,
                    fileName,
                    index + 1,
                    totalFiles,
                    fileSize,
                    fileSize,
                    WadHashCacheProgressStage.Excluded,
                    Hash: null,
                    ErrorMessage: null));
                continue;
            }

            var lastProgressReport = DateTime.MinValue;

            void ReportHashProgress(long bytesProcessed)
            {
                var now = DateTime.UtcNow;
                if (bytesProcessed < fileSize
                    && (now - lastProgressReport).TotalMilliseconds < AppConstants.UiIntervals.ProgressReportThrottleMs)
                {
                    return;
                }

                lastProgressReport = now;
                progress?.Report(new WadHashCacheProgress(
                    filePath,
                    fileName,
                    index,
                    totalFiles,
                    bytesProcessed,
                    fileSize,
                    WadHashCacheProgressStage.Hashing,
                    Hash: null,
                    ErrorMessage: null));
            }

            progress?.Report(new WadHashCacheProgress(
                filePath,
                fileName,
                index,
                totalFiles,
                0,
                fileSize,
                WadHashCacheProgressStage.Hashing,
                Hash: null,
                ErrorMessage: null));

            WadHashResult result;
            try
            {
                result = await GetHashAsync(filePath, ReportHashProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new WadHashResult(null, false, fileSize, ex.Message);
            }

            var stage = result.IsSuccess
                ? result.FromCache
                    ? WadHashCacheProgressStage.AlreadyCached
                    : WadHashCacheProgressStage.Cached
                : WadHashCacheProgressStage.Failed;

            if (stage == WadHashCacheProgressStage.Cached)
                cachedCount++;
            else if (stage == WadHashCacheProgressStage.AlreadyCached)
                alreadyCachedCount++;
            else
                failedCount++;

            progress?.Report(new WadHashCacheProgress(
                filePath,
                fileName,
                index + 1,
                totalFiles,
                result.FileSize,
                result.FileSize,
                stage,
                result.Hash,
                result.ErrorMessage));
        }

        await PersistIfDirtyAsync().ConfigureAwait(false);
        return new WadHashCacheSummary(
            totalFiles,
            cachedCount,
            alreadyCachedCount,
            failedCount,
            excludedCount);
    }

    private string? TryGetCachedHash(WadHashFileSnapshot snapshot)
    {
        lock (_entriesLock)
        {
            return !_excludedPaths.Contains(snapshot.Path)
                && _entries.TryGetValue(snapshot.Path, out var entry)
                && entry.Matches(snapshot)
                ? entry.Md5
                : null;
        }
    }

    private void StoreHash(WadHashFileSnapshot snapshot, string hash)
    {
        if (!IsValidMd5(hash))
            return;

        lock (_entriesLock)
        {
            if (_excludedPaths.Contains(snapshot.Path))
                return;

            _entries[snapshot.Path] = WadHashCacheEntry.FromSnapshot(snapshot, hash);
            _isDirty = true;
        }
    }

    private async Task PersistIfDirtyAsync()
    {
        await _persistenceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            WadHashCacheFile? cacheFile;
            lock (_entriesLock)
            {
                if (!_isDirty)
                    return;

                cacheFile = new WadHashCacheFile
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Entries = _entries.Values
                        .OrderBy(entry => entry.FilePath, PathResolver.DirectoryPathComparer)
                        .ToList(),
                    ExcludedPaths = _excludedPaths
                        .OrderBy(path => path, PathResolver.DirectoryPathComparer)
                        .ToList()
                };
                _isDirty = false;
            }

            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temporaryPath = _cachePath + ".tmp";
            var json = JsonSerializer.Serialize(cacheFile, JsonUtils.DefaultOptions);
            await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            lock (_entriesLock)
                _isDirty = true;

            _logger.Warning($"Failed to save WAD hash cache: {ex.Message}");
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return;

            var json = File.ReadAllText(_cachePath);
            var cacheFile = JsonSerializer.Deserialize<WadHashCacheFile>(json, JsonUtils.DefaultOptions);
            if (cacheFile == null || cacheFile.SchemaVersion != CurrentSchemaVersion)
            {
                _logger.Warning("Ignoring an unsupported WAD hash-cache file.");
                return;
            }

            lock (_entriesLock)
            {
                foreach (var excludedPath in cacheFile.ExcludedPaths ?? [])
                {
                    var path = NormalizePath(excludedPath);
                    if (path != null)
                        _excludedPaths.Add(path);
                }

                foreach (var entry in cacheFile.Entries ?? [])
                {
                    var path = NormalizePath(entry.FilePath);
                    if (path == null || _excludedPaths.Contains(path) || !IsValidMd5(entry.Md5))
                        continue;

                    entry.FilePath = path;
                    _entries[path] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to load WAD hash cache: {ex.Message}");
        }
    }

    /// <summary>Fresh full-file hashing; never consults or changes cache policy.</summary>
    internal static async Task<string?> ComputeFileHashAsync(
        string filePath,
        Action<long>? progress,
        CancellationToken cancellationToken,
        FileShare fileShare = FileShare.Read)
    {
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                fileShare,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ComputeStreamHashAsync(stream, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Failed to compute hash for {filePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Hashes the supplied content without owning its lifetime. Archive callers
    /// supply the decompressed payload stream, not the compressed container.
    /// </summary>
    internal static async Task<string> ComputeStreamHashAsync(
        Stream stream, Action<long>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var md5 = MD5.Create();
        var buffer = new byte[HashReadBufferSize];
        long bytesProcessed = 0;
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            md5.TransformBlock(buffer, 0, bytesRead, null, 0);
            bytesProcessed += bytesRead;
            progress?.Invoke(bytesProcessed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        md5.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(md5.Hash!).ToLowerInvariant();
    }

    private static WadHashFileSnapshot? TryCreateSnapshot(string? filePath)
    {
        var normalizedPath = NormalizePath(filePath);
        if (normalizedPath == null)
            return null;

        try
        {
            var fileInfo = new FileInfo(normalizedPath);
            if (!fileInfo.Exists)
                return null;

            return new WadHashFileSnapshot(
                normalizedPath,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks,
                fileInfo.CreationTimeUtc.Ticks,
                TryGetWindowsFileIdentity(normalizedPath));
        }
        catch
        {
            return null;
        }
    }

    private static long TryGetFileLength(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValidMd5(string? hash) =>
        hash is { Length: 32 } && hash.All(Uri.IsHexDigit);

    private static string? TryGetWindowsFileIdentity(string filePath)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information))
                return null;

            return $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

}

/// <summary>Persistent cache payload for <see cref="WadHashCacheService"/>.</summary>
internal sealed class WadHashCacheFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<WadHashCacheEntry> Entries { get; set; } = [];
    public List<string> ExcludedPaths { get; set; } = [];
}

/// <summary>One full-path, full-MD5 WAD hash-cache record.</summary>
internal sealed class WadHashCacheEntry
{
    public string FilePath { get; set; } = string.Empty;
    public string Md5 { get; set; } = string.Empty;
    public long Length { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
    public long CreationTimeUtcTicks { get; set; }
    public string? FileIdentity { get; set; }

    internal static WadHashCacheEntry FromSnapshot(WadHashFileSnapshot snapshot, string hash)
    {
        return new WadHashCacheEntry
        {
            FilePath = snapshot.Path,
            Md5 = hash,
            Length = snapshot.Length,
            LastWriteTimeUtcTicks = snapshot.LastWriteTimeUtcTicks,
            CreationTimeUtcTicks = snapshot.CreationTimeUtcTicks,
            FileIdentity = snapshot.FileIdentity
        };
    }

    internal bool Matches(WadHashFileSnapshot snapshot)
    {
        return Length == snapshot.Length
            && LastWriteTimeUtcTicks == snapshot.LastWriteTimeUtcTicks
            && CreationTimeUtcTicks == snapshot.CreationTimeUtcTicks
            && string.Equals(FileIdentity, snapshot.FileIdentity, StringComparison.Ordinal);
    }
}

/// <summary>Identity and metadata captured for a local file at hash time.</summary>
internal sealed record WadHashFileSnapshot(
    string Path,
    long Length,
    long LastWriteTimeUtcTicks,
    long CreationTimeUtcTicks,
    string? FileIdentity)
{
    public bool Matches(WadHashFileSnapshot other) =>
        Length == other.Length
        && LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks
        && CreationTimeUtcTicks == other.CreationTimeUtcTicks
        && string.Equals(FileIdentity, other.FileIdentity, StringComparison.Ordinal);
}

/// <summary>Result of calculating or reusing a WAD MD5.</summary>
public sealed record WadHashResult(
    string? Hash,
    bool FromCache,
    long FileSize,
    string? ErrorMessage)
{
    public bool IsSuccess => !string.IsNullOrWhiteSpace(Hash);
    internal WadHashFileSnapshot? Snapshot { get; init; }
}

/// <summary>Filesystem-free presentation and eligibility for WAD hash actions.</summary>
public sealed record WadHashActions(
    int SelectedFileCount,
    bool CachingEnabled,
    bool AllFilesExcluded,
    bool IsRecalculation,
    bool IsBusy)
{
    public static WadHashActions NoSelection { get; } = new(0, false, false, false, false);
    public bool CanCalculate => !IsBusy && CachingEnabled && SelectedFileCount == 1 && !AllFilesExcluded;
    // Exclusions remain editable while global caching is disabled.
    public bool CanToggleExclusion => !IsBusy && SelectedFileCount > 0;
    public bool ShouldExclude => SelectedFileCount > 0 && !AllFilesExcluded;
    public string CalculateHeader => IsRecalculation ? "_Recalculate Hash" : "Calculate _Hash";
    public string CalculateToolTip => SelectedFileCount != 1
        ? "Select one local WAD file first."
        : !CachingEnabled
            ? "Enable WAD hash caching in Preferences before calculating a cached MD5."
            : AllFilesExcluded
                ? "Allow hash caching for this file before calculating a cached MD5."
                : IsRecalculation
                    ? "Calculate this file's MD5 again and replace its cached value."
                    : "Calculate this file's MD5 and save it in the local hash cache.";
    public string ToggleExclusionHeader => AllFilesExcluded
        ? SelectedFileCount == 1 ? "Allow Hash _Caching" : "Allow Hash Caching for _Selected Files"
        : SelectedFileCount <= 1 ? "_Don't Cache Hash" : "_Don't Cache Selected Hashes";
    public string ToggleExclusionToolTip => AllFilesExcluded
        ? "Allow these files to receive a fresh cached MD5 on a later verification or cache pass."
        : "Do not reuse or store cached MD5 values for these files. Server hash verification still runs normally.";
}

/// <summary>A completed manual operation plus its freshly validated cache state.</summary>
public sealed record WadHashActionResult(WadHashResult HashResult, string? CachedHash, bool WasRecalculation);

/// <summary>Progress stage for a background WAD hash-cache operation.</summary>
public enum WadHashCacheProgressStage
{
    Hashing,
    Cached,
    AlreadyCached,
    Excluded,
    Failed
}

/// <summary>One update from a background WAD hash-cache operation.</summary>
public sealed record WadHashCacheProgress(
    string FilePath,
    string FileName,
    int CompletedFiles,
    int TotalFiles,
    long BytesProcessed,
    long TotalBytes,
    WadHashCacheProgressStage Stage,
    string? Hash,
    string? ErrorMessage);

/// <summary>Aggregate result of caching a group of local WAD files.</summary>
public sealed record WadHashCacheSummary(
    int TotalFiles,
    int NewlyCachedCount,
    int AlreadyCachedCount,
    int FailedCount,
    int ExcludedCount);
