using System.Collections.Concurrent;
using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.Services;

public partial class WadManager
{
    // A filename alone cannot identify the version required by a server.
    // This remembers locations only; hash validity remains owned by the cache.
    private readonly ConcurrentDictionary<(string Name, string Hash), string> _matchedWadPaths = new();

    public IReadOnlyList<string> FindWadCandidates(string wadName) =>
        EnumerateWadCandidates(wadName, CancellationToken.None).ToArray();

    private IEnumerable<string> EnumerateWadCandidates(string wadName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(wadName))
            yield break;
        var fileName = Path.GetFileName(wadName);
        if (string.IsNullOrWhiteSpace(fileName))
            yield break;

        var seen = new HashSet<string>(PathResolver.DirectoryPathComparer);
        foreach (var root in GetSearchRootsInPriorityOrder(requireExistingDirectories: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootKey = PathResolver.NormalizeDirectoryPath(root);
            var directPath = Path.GetFullPath(Path.Combine(root, fileName));
            var candidates = new List<string>();
            if (_rootFileNameIndex.TryGetValue(rootKey, out var index)
                && index.TryGetValue(fileName, out var indexedPath))
            {
                if (File.Exists(indexedPath))
                    candidates.Add(Path.GetFullPath(indexedPath));
                else
                    index.TryRemove(fileName, out _);
            }
            if (File.Exists(directPath))
                candidates.Add(directPath);

            // Windows provides case-insensitive direct probes. On other
            // platforms, enumerate only matching top-level filenames; there
            // can also be multiple differently-cased candidates in one root.
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    candidates.AddRange(Directory.EnumerateFiles(root, fileName, new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        MatchType = MatchType.Simple,
                        MatchCasing = MatchCasing.CaseInsensitive,
                        IgnoreInaccessible = false,
                        AttributesToSkip = 0
                    }).Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        .Select(Path.GetFullPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _logger.Verbose($"Could not inspect WAD filenames in {root}: {ex.Message}");
                }
            }

            foreach (var path in candidates.Distinct(PathResolver.DirectoryPathComparer)
                .OrderBy(path => !Path.GetFileName(path).Equals(fileName, StringComparison.Ordinal))
                .ThenBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rootIndex = _rootFileNameIndex.GetOrAdd(rootKey,
                    _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                rootIndex[fileName] = path;
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    public Task<WadResolutionResult> ResolveWadAsync(
        string wadName,
        string? expectedHash,
        WadHashLookupMode mode,
        CancellationToken cancellationToken = default,
        Action<WadCandidateHashProgress>? progress = null) => Task.Run(
            () => ResolveWadCoreAsync(wadName, expectedHash, mode, WadHashCacheService.Instance,
                cancellationToken, progress), cancellationToken);

    internal Task<WadResolutionResult> ResolveWadAsync(
        string wadName,
        string? expectedHash,
        WadHashLookupMode mode,
        WadHashCacheService hashCache,
        CancellationToken cancellationToken = default,
        Action<WadCandidateHashProgress>? progress = null) => Task.Run(
            () => ResolveWadCoreAsync(wadName, expectedHash, mode, hashCache, cancellationToken, progress),
            cancellationToken);

    private async Task<WadResolutionResult> ResolveWadCoreAsync(
        string wadName,
        string? expectedHash,
        WadHashLookupMode mode,
        WadHashCacheService hashCache,
        CancellationToken cancellationToken,
        Action<WadCandidateHashProgress>? progress)
    {
        var fileName = Path.GetFileName(wadName);
        WadResolutionResult Result(WadResolutionStatus status, string? path = null,
            WadHashResult? hash = null, string? error = null) =>
            new(fileName, expectedHash, status, path, hash, error)
            {
                LookupMode = string.IsNullOrWhiteSpace(expectedHash) ? WadHashLookupMode.FileNameOnly : mode
            };

        if (mode == WadHashLookupMode.FileNameOnly || string.IsNullOrWhiteSpace(expectedHash))
        {
            var path = EnumerateWadCandidates(wadName, cancellationToken).FirstOrDefault();
            cancellationToken.ThrowIfCancellationRequested();
            return path == null
                ? Result(WadResolutionStatus.Missing)
                : Result(WadResolutionStatus.Found, path,
                    hashCache.TryGetCachedHashResult(path));
        }

        var candidates = EnumerateWadCandidates(wadName, cancellationToken).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Length == 0)
            return Result(WadResolutionStatus.Missing);

        var key = (fileName.ToLowerInvariant(), expectedHash.ToLowerInvariant());
        if (!_matchedWadPaths.TryGetValue(key, out var previousPath))
        {
            var recordedPaths = hashCache.GetRecordedPathsForHash(expectedHash)
                .Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            previousPath = GetSearchRootsInPriorityOrder(requireExistingDirectories: false)
                .SelectMany(root => recordedPaths.Where(path =>
                    PathResolver.DirectoryPathsEqual(Path.GetDirectoryName(path), root)))
                .FirstOrDefault();
        }
        var moved = previousPath != null && !File.Exists(previousPath);
        WadResolutionResult? firstUnknown = null;
        WadResolutionResult? firstMismatch = null;
        WadResolutionResult? firstUnavailable = null;
        var mismatchedPaths = new List<string>();

        for (var index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = candidates[index];
            var mustReverifyLocation = moved && !PathResolver.DirectoryPathsEqual(previousPath, path);
            var hashResult = mustReverifyLocation ? null : hashCache.TryGetCachedHashResult(path);
            if (mode == WadHashLookupMode.CachedOnly && hashResult == null)
            {
                firstUnknown ??= Result(WadResolutionStatus.Found, path);
                continue;
            }

            if (hashResult == null)
            {
                var totalBytes = 0L;
                try { totalBytes = new FileInfo(path).Length; } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                void Report(long bytes) => progress?.Invoke(new(fileName, path, index + 1, candidates.Length,
                    bytes, totalBytes));
                Report(0);
                hashResult = mustReverifyLocation
                    ? await hashCache.RefreshHashAsync(path, Report, cancellationToken).ConfigureAwait(false)
                    : await hashCache.GetHashAsync(path, Report, cancellationToken).ConfigureAwait(false);
            }

            if (!hashResult.IsSuccess)
            {
                firstUnavailable ??= Result(WadResolutionStatus.Unavailable, path,
                    hashResult, hashResult.ErrorMessage);
                continue;
            }

            if (string.Equals(hashResult.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                _matchedWadPaths[key] = path;
                return Result(WadResolutionStatus.Matched, path, hashResult);
            }

            mismatchedPaths.Add(path);
            firstMismatch ??= Result(WadResolutionStatus.Mismatch, path, hashResult);
        }

        if (firstUnknown != null)
            return firstUnknown;
        if (firstUnavailable != null)
            return firstUnavailable;
        return firstMismatch! with { MismatchedPaths = mismatchedPaths };
    }

    /// <summary>Checks the chosen file immediately before its path is used.</summary>
    public bool IsResolutionCurrent(WadResolutionResult result)
    {
        if (!result.IsUsable || string.IsNullOrEmpty(result.LocalPath))
            return false;

        var path = Path.GetFullPath(result.LocalPath);
        var stillConfigured = GetSearchRootsInPriorityOrder(requireExistingDirectories: false).Any(root =>
        {
            if (!result.IsArchived)
                return PathResolver.DirectoryPathsEqual(Path.GetDirectoryName(path), root);

            var relative = Path.GetRelativePath(root, path);
            return !Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        });
        if (!stillConfigured)
            return false;

        return result.Status == WadResolutionStatus.Matched
            ? result.HashResult is { } hash && WadHashCacheService.IsHashResultCurrent(hash)
            : File.Exists(path);
    }
}
