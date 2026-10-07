using System.IO.Compression;
using System.Text;
using ZScape.Services;

internal static class WadHashChecks
{
    private const string AbcMd5 = "900150983cd24fb0d6963f7d28e17f72";

    public static void Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task CheckFractionalHashProgressAsync(TestDirectory fixture)
    {
        const int bytesPerMiB = 1024 * 1024;
        var content = new byte[3 * bytesPerMiB];
        for (var index = 0; index < content.Length; index++)
            content[index] = (byte)(index % 251);
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(content)).ToLowerInvariant();

        var streamProgress = new List<long>();
        using (var stream = new MemoryStream(content, writable: false))
        {
            var streamHash = await WadHashCacheService.ComputeStreamHashAsync(stream, streamProgress.Add,
                CancellationToken.None);
            RegressionAssert.That(streamHash == expectedHash,
                "Fine-grained stream progress preserves the complete MD5");
        }
        AssertFractionalHashProgress(streamProgress, content.Length, "Shared stream hashing");

        var file = fixture.GetPath("fractional-progress.pk3");
        await File.WriteAllBytesAsync(file, content);
        var cache = new WadHashCacheService(fixture.GetPath("fractional-progress-cache.json"), () => true);
        var fileProgress = new List<long>();
        var fileHash = await cache.GetHashAsync(file, fileProgress.Add, CancellationToken.None);
        RegressionAssert.That(fileHash.Hash == expectedHash && !fileHash.FromCache,
            "Fine-grained file progress preserves the complete verified MD5");
        AssertFractionalHashProgress(fileProgress, content.Length, "File verification");

        var cancelledFile = fixture.GetPath("cancelled-progress.pk3");
        await File.WriteAllBytesAsync(cancelledFile, content);
        using var cancellation = new CancellationTokenSource();
        long bytesBeforeCancellation = 0;
        var cancellationObserved = false;
        try
        {
            await cache.GetHashAsync(cancelledFile, bytes =>
            {
                bytesBeforeCancellation = bytes;
                cancellation.Cancel();
            }, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        RegressionAssert.That(cancellationObserved && bytesBeforeCancellation > 0
            && bytesBeforeCancellation < bytesPerMiB,
            "Cancellation during fractional-MiB hashing propagates before the first whole MiB");
        RegressionAssert.That(cache.TryGetCachedHash(cancelledFile) is null
            && !cache.HasCachedEntryForPath(cancelledFile),
            "Cancelled file verification never caches a partial hash");
    }

    private static void AssertFractionalHashProgress(IReadOnlyList<long> progress, long length, string owner)
    {
        const int bytesPerMiB = 1024 * 1024;
        RegressionAssert.That(progress.Count > 0 && progress[0] > 0 && progress[0] < bytesPerMiB,
            $"{owner} reports byte progress before the first whole MiB");
        RegressionAssert.That(progress.Any(bytes => bytes > 0 && bytes < length && bytes % bytesPerMiB != 0),
            $"{owner} reports fractional-MiB progress while hashing an exact whole-MiB file");
        RegressionAssert.That(progress.Count > 0 && progress[^1] == length
            && progress.Select((bytes, index) => index == 0 || bytes > progress[index - 1]).All(increasing => increasing),
            $"{owner} byte progress increases strictly to the exact file length");
    }

    private static async Task RunAsync()
    {
        using var fixture = new TestDirectory();
        var file = fixture.GetPath("mod.pk3");
        await File.WriteAllTextAsync(file, "abc");
        var enabled = true;
        var cache = new WadHashCacheService(fixture.GetPath("cache.json"), () => enabled);
        long processed = 0;
        var initial = await cache.GetHashAsync(file, value => processed = value, CancellationToken.None);
        RegressionAssert.That(initial.Hash == AbcMd5 && !initial.FromCache && processed == 3,
            "Shared hashing returns the full known MD5 and reports bytes processed");
        await CheckFractionalHashProgressAsync(fixture);
        var reused = await cache.GetHashAsync(file, null, CancellationToken.None);
        RegressionAssert.That(reused.Hash == AbcMd5 && reused.FromCache,
            "An unchanged file reuses its validated hash snapshot");
        var refreshed = await cache.RefreshHashAsync(file, null, CancellationToken.None);
        RegressionAssert.That(refreshed.Hash == AbcMd5 && !refreshed.FromCache,
            "Manual hash refresh calculates bytes even when a cache entry matches");

        await File.WriteAllTextAsync(file, "abd");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        RegressionAssert.That(cache.TryGetCachedHash(file) is null,
            "A changed same-length file invalidates its cached hash");
        var changed = await cache.GetHashAsync(file, null, CancellationToken.None);
        RegressionAssert.That(changed.Hash is { Length: 32 } && changed.Hash != AbcMd5 && !changed.FromCache,
            "Modified files calculate a new full MD5");

        await cache.SetHashCachingExcludedAsync([file], isExcluded: true);
        var excluded = await cache.GetHashAsync(file, null, CancellationToken.None);
        RegressionAssert.That(excluded.Hash == changed.Hash && !excluded.FromCache
            && !cache.HasCachedEntryForPath(file),
            "Cache exclusion still hashes for verification without recording an entry");
        await cache.SetHashCachingExcludedAsync([file], isExcluded: false);
        await cache.GetHashAsync(file, null, CancellationToken.None);
        var savedCache = await File.ReadAllTextAsync(cache.CacheFilePath);
        enabled = false;
        var disabled = await cache.GetHashAsync(file, null, CancellationToken.None);
        RegressionAssert.That(disabled.Hash == changed.Hash && !disabled.FromCache
            && await File.ReadAllTextAsync(cache.CacheFilePath) == savedCache,
            "Disabling caching forces fresh verification without rewriting the cache");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledObserved = false;
        try { await cache.GetHashAsync(file, null, cancelled.Token); }
        catch (OperationCanceledException) { cancelledObserved = true; }
        RegressionAssert.That(cancelledObserved, "Hash cancellation is not converted into a missing or mismatched file");
        RegressionAssert.That(await WadHashCacheService.ComputeFileHashAsync(fixture.GetPath("missing.pk3"), null,
            CancellationToken.None) is null, "Missing files report no fresh hash");

        await File.WriteAllTextAsync(file, "abc");
        RegressionAssert.That(WadManager.ComputeFileHash(file) == AbcMd5,
            "Archive collision checks use the same fresh full-file hash implementation");
        using var compressed = new MemoryStream();
        await using (var writer = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
            await writer.WriteAsync(Encoding.ASCII.GetBytes("abc"));
        compressed.Position = 0;
        await using var payload = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        RegressionAssert.That(await WadHashCacheService.ComputeStreamHashAsync(payload, null, CancellationToken.None) == AbcMd5,
            "Archive verification hashes decompressed payload content, not its container");

        enabled = true;
        var mutationObserved = false;
        var unstable = await cache.RefreshHashAsync(file, _ =>
        {
            if (mutationObserved) return;
            mutationObserved = true;
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(1));
        }, CancellationToken.None);
        RegressionAssert.That(unstable.Hash is null && unstable.ErrorMessage?.Contains("changed") == true,
            "A file changed during calculation is neither verified nor cached as a stable hash");

        var stagedFile = fixture.GetPath("staged.pk3");
        await File.WriteAllTextAsync(stagedFile, "abc");
        var stagedHash = await cache.CalculateUncachedHashAsync(stagedFile, null, CancellationToken.None);
        RegressionAssert.That(stagedHash.Hash == AbcMd5 && !cache.HasCachedEntryForPath(stagedFile),
            "Staged download verification uses shared snapshot checks without caching disposable paths");
        var installedFile = fixture.GetPath("installed.pk3");
        File.Move(stagedFile, installedFile);
        await cache.RecordHashAsync(installedFile, stagedHash);
        RegressionAssert.That(cache.TryGetCachedHash(installedFile) == AbcMd5,
            "A verified staged hash follows the unchanged file into its installed path");

        await File.WriteAllTextAsync(stagedFile, "abc");
        var beforeReplacement = await cache.CalculateUncachedHashAsync(stagedFile, null, CancellationToken.None);
        await File.WriteAllTextAsync(stagedFile, "abd");
        File.SetLastWriteTimeUtc(stagedFile, DateTime.UtcNow.AddHours(2));
        await cache.RecordHashAsync(stagedFile, beforeReplacement);
        RegressionAssert.That(cache.TryGetCachedHash(stagedFile) is null && !cache.HasCachedEntryForPath(stagedFile),
            "An earlier hash cannot be recorded against a file changed after verification");
    }
}
