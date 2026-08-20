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
    private const int BufferSize = 1024 * 1024;

    private static readonly Lazy<WadHashCacheService> InstanceFactory =
        new(() => new WadHashCacheService());

    private readonly object _entriesLock = new();
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly Dictionary<string, WadHashCacheEntry> _entries;
    private readonly string _cachePath;
    private readonly LoggingService _logger = LoggingService.Instance;
    private bool _isDirty;

    public static WadHashCacheService Instance => InstanceFactory.Value;

    /// <summary>The portable cache file written beside the running executable.</summary>
    public string CacheFilePath => _cachePath;

    /// <summary>
    /// Indicates whether cached MD5 values may be reused or newly recorded.
    /// Disabling this setting leaves the cache file untouched and forces normal
    /// file hashing during verification.
    /// </summary>
    public bool IsEnabled => SettingsService.Instance.Settings.EnableWadHashCache;

    private WadHashCacheService()
    {
        _cachePath = Path.Combine(AppContext.BaseDirectory, "wad-hash-cache.json");
        _entries = new Dictionary<string, WadHashCacheEntry>(GetPathComparer());
        LoadCache();
    }

    /// <summary>
    /// Returns a cached MD5 only when the file still matches the exact snapshot
    /// captured at hash time. A filename on its own is never sufficient.
    /// </summary>
    public string? TryGetCachedHash(string? filePath)
    {
        if (!IsEnabled)
            return null;

        var snapshot = TryCreateSnapshot(filePath);
        return snapshot == null ? null : TryGetCachedHash(snapshot);
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
        var initialSnapshot = TryCreateSnapshot(filePath);
        if (initialSnapshot == null)
        {
            return new WadHashResult(
                Hash: null,
                FromCache: false,
                FileSize: 0,
                ErrorMessage: "The file no longer exists or could not be inspected.");
        }

        if (IsEnabled && TryGetCachedHash(initialSnapshot) is { } cachedHash)
        {
            progress?.Invoke(initialSnapshot.Length);
            return new WadHashResult(cachedHash, FromCache: true, initialSnapshot.Length, ErrorMessage: null);
        }

        var computedHash = await ComputeFileHashAsync(filePath, progress, cancellationToken).ConfigureAwait(false);
        if (computedHash == null)
        {
            return new WadHashResult(
                Hash: null,
                FromCache: false,
                FileSize: initialSnapshot.Length,
                ErrorMessage: "The file could not be read while calculating its MD5.");
        }

        var finalSnapshot = TryCreateSnapshot(filePath);
        if (IsEnabled && finalSnapshot != null && initialSnapshot.Matches(finalSnapshot))
        {
            StoreHash(finalSnapshot, computedHash);
            await PersistIfDirtyAsync().ConfigureAwait(false);
        }
        else if (IsEnabled)
        {
            _logger.Warning(
                $"Skipped hash-cache entry for {Path.GetFileName(filePath)} because the file changed while it was hashed.");
        }

        return new WadHashResult(computedHash, FromCache: false, initialSnapshot.Length, ErrorMessage: null);
    }

    /// <summary>
    /// Records a full MD5 that another trusted ZScape operation has just
    /// calculated, such as a downloaded WAD whose server hash was verified.
    /// The same unchanged-file snapshot rule applies before it is saved.
    /// </summary>
    public async Task RecordHashAsync(string filePath, string hash)
    {
        if (!IsEnabled || !IsValidMd5(hash))
            return;

        var snapshot = TryCreateSnapshot(filePath);
        if (snapshot == null)
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
            .Distinct(GetPathComparer())
            .Where(File.Exists)
            .OrderBy(path => path, GetPathComparer())
            .ToList();

        var cachedCount = 0;
        var alreadyCachedCount = 0;
        var failedCount = 0;
        var totalFiles = files.Count;

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = files[index];
            var fileName = Path.GetFileName(filePath);
            var fileSize = TryGetFileLength(filePath);
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
        return new WadHashCacheSummary(totalFiles, cachedCount, alreadyCachedCount, failedCount);
    }

    private string? TryGetCachedHash(WadHashFileSnapshot snapshot)
    {
        lock (_entriesLock)
        {
            return _entries.TryGetValue(snapshot.Path, out var entry) && entry.Matches(snapshot)
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
                        .OrderBy(entry => entry.FilePath, GetPathComparer())
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
                foreach (var entry in cacheFile.Entries)
                {
                    var path = NormalizePath(entry.FilePath);
                    if (path == null || !IsValidMd5(entry.Md5))
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

    private static async Task<string?> ComputeFileHashAsync(
        string filePath,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var md5 = MD5.Create();

            var buffer = new byte[BufferSize];
            long bytesProcessed = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                md5.TransformBlock(buffer, 0, bytesRead, null, 0);
                bytesProcessed += bytesRead;
                progress?.Invoke(bytesProcessed);
            }

            md5.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(md5.Hash!).ToLowerInvariant();
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

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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
}

/// <summary>Progress stage for a background WAD hash-cache operation.</summary>
public enum WadHashCacheProgressStage
{
    Hashing,
    Cached,
    AlreadyCached,
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
    int FailedCount);
