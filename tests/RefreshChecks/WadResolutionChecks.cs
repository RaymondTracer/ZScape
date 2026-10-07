using System.Security.Cryptography;
using ZScape.Models;
using ZScape.Services;

internal static class WadResolutionChecks
{
    private const string AbcMd5 = "900150983cd24fb0d6963f7d28e17f72";

    public static void Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await CheckDuplicateSelectionAsync();
        await CheckEffectiveRootPriorityAsync();
        await CheckRequirementScopedSelectionAsync();
        await CheckCachedOnlyAsync();
        await CheckRelocatedWinnerAsync();
        await CheckRelocationAfterRestartAsync();
        await CheckChangedFileAsync();
        await CheckSearchRootsAsync();
        await CheckNestedRootRemovalAsync();
        await CheckSearchRootChangeClearsRelocationAsync();
        await CheckHashPreferencesAsync();
        await CheckUnreadableCandidatesAsync();
        await CheckCancellationAsync();
    }

    private static async Task CheckEffectiveRootPriorityAsync()
    {
        using var fixture = new TestDirectory();
        var executableRoot = Root(fixture, "executable");
        var downloadRoot = Root(fixture, "download");
        var configuredRoot = Root(fixture, "configured");
        var executableCopy = Write(executableRoot, "mod.pk3", "wrong content");
        var downloadCopy = Write(downloadRoot, "mod.pk3", "abc");
        Write(configuredRoot, "mod.pk3", "abc");
        var manager = Manager(configuredRoot);
        manager.AddExecutableFolder(executableRoot);
        manager.DownloadPath = downloadRoot;
        var cache = Cache(fixture);
        var ordinary = await manager.ResolveWadAsync("mod.pk3", null, WadHashLookupMode.FileNameOnly, cache);
        var verified = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        RegressionAssert.That(ordinary.LocalPath == executableCopy && verified.LocalPath == downloadCopy
            && File.ReadAllText(executableCopy) == "wrong content",
            "Effective executable, download and configured roots share duplicate verification while retaining their existing priority");
    }

    private static async Task CheckDuplicateSelectionAsync()
    {
        using var fixture = new TestDirectory();
        var first = Root(fixture, "first");
        var second = Root(fixture, "second");
        var third = Root(fixture, "third");
        var wrong = Write(first, "MixedCase.PK3", "wrong content");
        var correct = Write(second, "MixedCase.PK3", "abc");
        var laterCorrect = Write(third, "MixedCase.PK3", "abc");
        var manager = Manager(first, second, third);
        var cache = Cache(fixture);
        var visited = new HashSet<string>();

        var resolution = await manager.ResolveWadAsync("mixedcase.pk3", AbcMd5.ToUpperInvariant(),
            WadHashLookupMode.Verify, cache, progress: value => visited.Add(value.CandidatePath));

        RegressionAssert.That(resolution.Status == WadResolutionStatus.Matched
            && resolution.LocalPath == correct && resolution.LocalHash == AbcMd5
            && !visited.Contains(laterCorrect),
            "Duplicate filenames select the first full-hash match in root order and stop reading later copies");
        RegressionAssert.That(File.ReadAllText(wrong) == "wrong content"
            && File.ReadAllText(correct) == "abc" && File.ReadAllText(laterCorrect) == "abc"
            && Directory.GetFiles(first).Length == 1 && Directory.GetFiles(second).Length == 1,
            "Resolving a good duplicate neither renames nor overwrites the mismatched copy");

        manager.SetSearchPaths([third, first, second]);
        var reordered = await manager.ResolveWadAsync("mixedcase.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        RegressionAssert.That(reordered.LocalPath == laterCorrect,
            "Reordering roots changes the preferred matching duplicate without retaining an earlier winning path");
    }

    private static async Task CheckRequirementScopedSelectionAsync()
    {
        using var fixture = new TestDirectory();
        var first = Root(fixture, "first");
        var second = Root(fixture, "second");
        var abc = Write(first, "mod.pk3", "abc");
        var abd = Write(second, "mod.pk3", "abd");
        var manager = Manager(first, second);
        var cache = Cache(fixture);
        var abdHash = Hash(abd);

        var firstServer = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        var secondServer = await manager.ResolveWadAsync("mod.pk3", abdHash, WadHashLookupMode.Verify, cache);
        var revisit = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.CachedOnly, cache);
        RegressionAssert.That(firstServer.LocalPath == abc && secondServer.LocalPath == abd
            && secondServer.LocalHash == abdHash && revisit.LocalPath == abc
            && revisit.Status == WadResolutionStatus.Matched && revisit.FromCache,
            "Servers requiring different full hashes for the same filename retain separate matching resolutions");
    }

    private static async Task CheckCachedOnlyAsync()
    {
        using var fixture = new TestDirectory();
        var first = Root(fixture, "first");
        var second = Root(fixture, "second");
        var unknown = Write(first, "mod.pk3", "unknown content");
        var matching = Write(second, "mod.pk3", "abc");
        var manager = Manager(first, second);
        var cache = Cache(fixture);
        await cache.GetHashAsync(matching, null, CancellationToken.None);
        var progressCalls = 0;

        var match = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.CachedOnly,
            cache, progress: _ => progressCalls++);
        RegressionAssert.That(match.Status == WadResolutionStatus.Matched && match.LocalPath == matching
            && match.FromCache && !cache.HasCachedEntryForPath(unknown) && progressCalls == 0,
            "Cached-only lookup selects a later cached match without hashing earlier unknown files");

        var unknownResult = await manager.ResolveWadAsync("mod.pk3", new string('f', 32),
            WadHashLookupMode.CachedOnly, cache);
        RegressionAssert.That(unknownResult.Status == WadResolutionStatus.Found
            && unknownResult.LocalPath == unknown && unknownResult.LocalHash is null
            && !cache.HasCachedEntryForPath(unknown),
            "Unknown duplicate hashes remain unverified rather than becoming a false mismatch");

        await cache.GetHashAsync(unknown, null, CancellationToken.None);
        var mismatch = await manager.ResolveWadAsync("mod.pk3", new string('f', 32),
            WadHashLookupMode.CachedOnly, cache);
        RegressionAssert.That(mismatch.Status == WadResolutionStatus.Mismatch && !mismatch.IsUsable
            && mismatch.MismatchedPaths?.SequenceEqual(new[] { unknown, matching }) == true,
            "Cached-only lookup reports a mismatch only when every available duplicate has a known wrong hash");
    }

    private static async Task CheckRelocatedWinnerAsync()
    {
        using var fixture = new TestDirectory();
        var first = Root(fixture, "first");
        var second = Root(fixture, "second");
        var original = Write(first, "mod.pk3", "abc");
        var alternate = Write(second, "mod.pk3", "abc");
        var manager = Manager(first, second);
        var cache = Cache(fixture);
        await cache.GetHashAsync(alternate, null, CancellationToken.None);
        var winning = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        File.Delete(original);

        RegressionAssert.That(!manager.IsResolutionCurrent(winning),
            "A previously verified file disappearing invalidates the launch resolution");
        var cachedOnly = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.CachedOnly, cache);
        RegressionAssert.That(cachedOnly.Status == WadResolutionStatus.Found && cachedOnly.LocalPath == alternate
            && cachedOnly.LocalHash is null,
            "A replacement location stays unverified after its previous winning path disappears, even with an existing cache entry");
        var readBytes = 0L;
        var rescanned = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache,
            progress: value => readBytes = value.BytesProcessed);
        RegressionAssert.That(rescanned.Status == WadResolutionStatus.Matched && rescanned.LocalPath == alternate
            && !rescanned.FromCache && readBytes == 3 && manager.IsResolutionCurrent(rescanned),
            "Lookup rescans every eligible root and freshly hashes the replacement location before it is launchable");
    }

    private static async Task CheckRelocationAfterRestartAsync()
    {
        using var fixture = new TestDirectory();
        var first = Root(fixture, "first");
        var second = Root(fixture, "second");
        var original = Write(first, "mod.pk3", "abc");
        var replacement = Write(second, "mod.pk3", "abc");
        var manager = Manager(first, second);
        var cache = Cache(fixture);
        await cache.GetHashAsync(replacement, null, CancellationToken.None);
        await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        File.Delete(original);

        var restartedManager = Manager(first, second);
        var restartedCache = Cache(fixture);
        var pending = await restartedManager.ResolveWadAsync("mod.pk3", AbcMd5,
            WadHashLookupMode.CachedOnly, restartedCache);
        RegressionAssert.That(pending.Status == WadResolutionStatus.Found && pending.LocalPath == replacement
            && pending.LocalHash is null,
            "Persisted path/hash records preserve the missing-winner revalidation requirement after restart");
        var bytesRead = 0L;
        var verified = await restartedManager.ResolveWadAsync("mod.pk3", AbcMd5,
            WadHashLookupMode.Verify, restartedCache, progress: value => bytesRead = value.BytesProcessed);
        RegressionAssert.That(verified.Status == WadResolutionStatus.Matched && verified.LocalPath == replacement
            && !verified.FromCache && bytesRead == 3 && restartedManager.IsResolutionCurrent(verified),
            "After restart a missing previous matching path causes its cached replacement location to be freshly hashed");
    }

    private static async Task CheckChangedFileAsync()
    {
        using var fixture = new TestDirectory();
        var root = Root(fixture, "root");
        var file = Write(root, "mod.pk3", "abc");
        var manager = Manager(root);
        var cache = Cache(fixture);
        var winning = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        File.WriteAllText(file, "abd");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        RegressionAssert.That(!manager.IsResolutionCurrent(winning) && cache.TryGetCachedHash(file) is null,
            "Same-length content changes invalidate both the exact launch resolution and its cached metadata snapshot");
        var pending = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.CachedOnly, cache);
        var verified = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        RegressionAssert.That(pending.Status == WadResolutionStatus.Found && pending.LocalHash is null
            && verified.Status == WadResolutionStatus.Mismatch && !verified.IsUsable,
            "An altered cached file must be rehashed and a wrong replacement cannot reuse its old match");
    }

    private static async Task CheckSearchRootsAsync()
    {
        using var fixture = new TestDirectory();
        var first = Root(fixture, "first");
        var second = Root(fixture, "second");
        var offline = fixture.GetPath("offline");
        var firstFile = Write(first, "mod.pk3", "abc");
        var secondFile = Write(second, "mod.pk3", "abc");
        var nested = Root(fixture, "first", "nested");
        Write(nested, "nested.pk3", "abc");
        var manager = Manager(offline, first, second);
        var cache = Cache(fixture);
        var winning = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        RegressionAssert.That(manager.FindWadCandidates("mod.pk3").SequenceEqual(new[] { firstFile, secondFile })
            && (await manager.ResolveWadAsync("nested.pk3", AbcMd5, WadHashLookupMode.Verify, cache)).Status
                == WadResolutionStatus.Missing,
            "Duplicate discovery remains top-level and probes unavailable configured roots without recursive traversal");

        manager.RemoveSearchPath(first);
        var afterRemoval = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        RegressionAssert.That(!manager.IsResolutionCurrent(winning) && afterRemoval.LocalPath == secondFile,
            "Removed search roots cannot remain eligible merely because their file and hash are still cached");
        Directory.CreateDirectory(offline);
        var reconnected = Write(offline, "reconnected.pk3", "abc");
        var restored = await manager.ResolveWadAsync("reconnected.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        RegressionAssert.That(restored.LocalPath == reconnected && restored.Status == WadResolutionStatus.Matched,
            "An unavailable configured root is searched again when it becomes available");
    }

    private static async Task CheckNestedRootRemovalAsync()
    {
        using var fixture = new TestDirectory();
        var parent = Root(fixture, "parent");
        var child = Root(fixture, "parent", "child");
        var file = Write(child, "mod.pk3", "abc");
        var manager = Manager(parent, child);
        var cache = Cache(fixture);
        var resolution = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        RegressionAssert.That(resolution.LocalPath == file && manager.IsResolutionCurrent(resolution),
            "An explicitly configured nested root can supply a regular top-level WAD candidate");

        manager.RemoveSearchPath(child);
        var remaining = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        RegressionAssert.That(!manager.IsResolutionCurrent(resolution)
            && remaining.Status == WadResolutionStatus.Missing && File.Exists(file),
            "Removing a nested search root invalidates its regular match even while its parent root remains configured");
    }

    private static async Task CheckSearchRootChangeClearsRelocationAsync()
    {
        using var fixture = new TestDirectory();
        var first = Root(fixture, "first");
        var second = Root(fixture, "second");
        var original = Write(first, "mod.pk3", "abc");
        var replacement = Write(second, "mod.pk3", "abc");
        var manager = Manager(first, second);
        var cache = Cache(fixture);
        await cache.GetHashAsync(replacement, null, CancellationToken.None);
        var winning = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        File.Delete(original);
        manager.SetSearchPaths([second]);

        var configured = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.CachedOnly, cache);
        RegressionAssert.That(!manager.IsResolutionCurrent(winning)
            && configured.Status == WadResolutionStatus.Matched && configured.LocalPath == replacement
            && configured.FromCache && manager.IsResolutionCurrent(configured),
            "An actual root configuration change clears obsolete relocation hints and permits its matching cached candidate");
    }

    private static async Task CheckHashPreferencesAsync()
    {
        using var fixture = new TestDirectory();
        var first = Root(fixture, "first");
        var second = Root(fixture, "second");
        var wrong = Write(first, "mod.pk3", "wrong content");
        var correct = Write(second, "mod.pk3", "abc");
        var manager = Manager(first, second);
        var enabled = true;
        var cache = new WadHashCacheService(fixture.GetPath("cache.json"), () => enabled);

        var noHash = await manager.ResolveWadAsync("mod.pk3", null, WadHashLookupMode.Verify, cache);
        var disabledVerification = await manager.ResolveWadAsync("mod.pk3", AbcMd5,
            WadHashLookupMode.FileNameOnly, cache);
        RegressionAssert.That(noHash.Status == WadResolutionStatus.Found && noHash.LocalPath == wrong
            && disabledVerification.Status == WadResolutionStatus.Found && disabledVerification.LocalPath == wrong
            && !cache.HasCachedEntryForPath(wrong) && !cache.HasCachedEntryForPath(correct),
            "Missing advertised hashes and disabled verification preserve normal filename priority without calculating hashes");

        await cache.GetHashAsync(correct, null, CancellationToken.None);
        var persisted = await File.ReadAllTextAsync(cache.CacheFilePath);
        enabled = false;
        var verified = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        RegressionAssert.That(verified.Status == WadResolutionStatus.Matched && verified.LocalPath == correct
            && !verified.FromCache && manager.IsResolutionCurrent(verified)
            && await File.ReadAllTextAsync(cache.CacheFilePath) == persisted,
            "Disabling hash caching still verifies duplicate candidates and retains a launch snapshot without rewriting the cache");
        File.SetLastWriteTimeUtc(correct, DateTime.UtcNow.AddMinutes(1));
        RegressionAssert.That(!manager.IsResolutionCurrent(verified),
            "Launch snapshot validation also notices changes when persistent hash caching is disabled");
    }

    private static async Task CheckUnreadableCandidatesAsync()
    {
        using var fixture = new TestDirectory();
        var first = Root(fixture, "first");
        var second = Root(fixture, "second");
        var blocked = Write(first, "mod.pk3", "abc");
        var manager = Manager(first, second);
        var cache = Cache(fixture);
        using var exclusive = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var failed = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        RegressionAssert.That(failed.Status == WadResolutionStatus.Unavailable && !failed.IsUsable
            && failed.LocalHash is null && !string.IsNullOrEmpty(failed.ErrorMessage),
            "An unreadable local file is neither a hash mismatch nor a successfully verified file");

        var later = Write(second, "mod.pk3", "abc");
        var usable = await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache);
        RegressionAssert.That(usable.Status == WadResolutionStatus.Matched && usable.LocalPath == later,
            "An unreadable earlier duplicate does not prevent using a later verified matching file");
    }

    private static async Task CheckCancellationAsync()
    {
        using var fixture = new TestDirectory();
        var root = Root(fixture, "root");
        var file = Write(root, "mod.pk3", "abc");
        var manager = Manager(root);
        var cache = Cache(fixture);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var observed = false;
        try { await manager.ResolveWadAsync("mod.pk3", AbcMd5, WadHashLookupMode.Verify, cache, cancellation.Token); }
        catch (OperationCanceledException) { observed = true; }
        RegressionAssert.That(observed && File.ReadAllText(file) == "abc" && !cache.HasCachedEntryForPath(file),
            "Cancelled resolution propagates cancellation without treating files as missing or changing their content");
    }

    private static WadManager Manager(params string[] roots)
    {
        var manager = new WadManager();
        manager.SetSearchPaths(roots);
        manager.RefreshCache();
        return manager;
    }

    private static WadHashCacheService Cache(TestDirectory fixture) =>
        new(fixture.GetPath("cache.json"), () => true);

    private static string Root(TestDirectory fixture, params string[] parts)
    {
        var path = fixture.GetPath(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Write(string root, string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Hash(string path) =>
        Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
