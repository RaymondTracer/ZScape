using System.Net;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Threading;
using ZScape.Models;
using ZScape.Services;
using ZScape.Views;

internal static class JoinPreparationChecks
{
    public static void Run()
    {
        CheckRequiredAndOptionalPreparation();
        CheckArchiveTraversalAndCancellation();
        CheckBackgroundPreparationAndCommandAssembly();
        CheckDuplicateResolutionAndLaunchPaths();
        CheckNestedResolutionMembership();
        CheckMismatchResolutionPreservesFiles();
        CheckDownloadedMismatchAndOptionalSkip();
        CheckDownloadedPreparationKeepsArchivedRequirements();
        CheckUnreadableVerification();
        CheckProgressDialogCancellation();
    }

    private static void CheckRequiredAndOptionalPreparation()
    {
        using var directory = new TestDirectory();
        var manager = new WadManager { DownloadPath = directory.Path };
        var server = new ServerInfo
        {
            IWAD = "freedoom2.wad",
            PWADs =
            [
                new PWadInfo { Name = "required.pk3", Hash = new string('a', 32) },
                new PWadInfo { Name = "optional.pk3", Hash = new string('b', 32), IsOptional = true }
            ]
        };
        var required = GameLauncher.PrepareWadsForJoinAsync(server, manager, false, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        var optional = GameLauncher.PrepareWadsForJoinAsync(server, manager, true, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        RegressionAssert.That(required.NeedsDownload.Select(wad => wad.FileName).SequenceEqual(new[] { "freedoom2.wad", "required.pk3" })
            && required.NeedsDownload[1].ExpectedHash == new string('a', 32),
            "Required preparation retains IWADs, PWAD order and full advertised hashes; Freedoom remains downloadable");
        RegressionAssert.That(optional.NeedsDownload.Select(wad => wad.FileName).SequenceEqual(new[] { "optional.pk3" }),
            "Optional preparation excludes the required IWAD and required PWADs");

        File.WriteAllText(directory.GetPath("required_archive.pk3"), "local archived content");
        server.IWAD = "doom2.wad";
        server.PWADs[0].Hash = Hash(directory.GetPath("required_archive.pk3"));
        var blocked = GameLauncher.PrepareWadsForJoinAsync(server, manager, false, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        RegressionAssert.That(blocked.MissingWads.Any(wad => wad.Name == "doom2.wad")
            && blocked.NeedsDownload.Count == 0
            && File.Exists(directory.GetPath("required_archive.pk3"))
            && !File.Exists(directory.GetPath("required.pk3")),
            "A missing commercial IWAD stops preparation before activating or downloading other files");
    }

    private static void CheckArchiveTraversalAndCancellation()
    {
        using var directory = new TestDirectory();
        var nested = directory.GetPath("nested");
        Directory.CreateDirectory(nested);
        var archived = directory.GetPath("nested", "MOD_archive.PK3");
        File.WriteAllText(archived, "expected archive");
        var manager = new WadManager { DownloadPath = directory.Path };
        manager.RefreshCache();
        RegressionAssert.That(manager.FindWad("MOD_archive.PK3") == null
            && manager.FindWadByHash(Hash(archived), "mod", ".pk3") == archived,
            "Normal filename lookup stays root-only while archive matching shares safe recursive, case-insensitive traversal");
        var deh = directory.GetPath("nested", "patch_archive.DEH");
        File.WriteAllText(deh, "advertised patch content");
        RegressionAssert.That(manager.FindWadByHash(Hash(deh), "patch", ".deh") == deh,
            "Archive search preserves server-advertised extensions rather than adopting the browser's extension filter");

        var server = new ServerInfo { PWADs = [new PWadInfo { Name = "mod.pk3", Hash = Hash(archived) }] };
        var result = GameLauncher.PrepareWadsForJoinAsync(server, manager, false, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        RegressionAssert.That(result.NeedsDownload.Count == 0
            && manager.FindWad("mod.pk3") == directory.GetPath("mod.pk3") && !File.Exists(archived),
            "A matching nested archived copy is activated at its search root and stays discoverable without a download");

        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        try
        {
            manager.FindWadByHash(new string('c', 32), "mod", ".pk3", cancellation.Token,
                _ => cancellation.Cancel());
        }
        catch (OperationCanceledException) { cancelled = true; }
        RegressionAssert.That(cancelled && File.Exists(directory.GetPath("mod.pk3")),
            "Cancelling archive lookup propagates cancellation without moving or deleting its candidate");

        var first = WadFileScanner.Scan([directory.Path], null, null, CancellationToken.None,
            stopAfterFile: _ => true);
        RegressionAssert.That(first.Files.Count == 1,
            "Shared traversal can stop at the first matched file rather than scanning the remaining tree");
    }

    private static void CheckBackgroundPreparationAndCommandAssembly()
    {
        using var directory = new TestDirectory();
        var manager = new WadManager { DownloadPath = directory.Path };
        var server = new ServerInfo
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 10666),
            IWAD = "freedoom2.wad",
            PWADs =
            [
                new PWadInfo { Name = "first.pk3" },
                new PWadInfo { Name = "skip.pk3", IsOptional = true },
                new PWadInfo { Name = "second.pk3" }
            ]
        };
        foreach (var name in new[] { "freedoom2.wad", "first.pk3", "skip.pk3", "second.pk3" })
            File.WriteAllText(directory.GetPath(name), name);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var callerThread = Environment.CurrentManagedThreadId;
        var preparationThread = callerThread;
        var progress = new CallbackProgress(state =>
        {
            preparationThread = Environment.CurrentManagedThreadId;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        var task = GameLauncher.PrepareWadsForJoinAsync(server, manager, false, progress, cancellation.Token);
        RegressionAssert.That(entered.Wait(TimeSpan.FromSeconds(10)) && !task.IsCompleted
            && preparationThread != callerThread,
            "Join preparation returns while slow file work runs on a worker thread");
        cancellation.Cancel();
        release.Set();
        var cancelled = false;
        try { task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelled = true; }
        RegressionAssert.That(cancelled, "Cancelling an active preparation discards its result instead of allowing the join to continue");

        var args = GameLauncher.BuildConnectArguments(server, manager, "connect", "join",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skip.pk3" });
        RegressionAssert.That(args.Contains("-connect 127.0.0.1:10666")
            && args.Contains($"-iwad \"{directory.GetPath("freedoom2.wad")}\"")
            && args.Contains($"-file \"{directory.GetPath("first.pk3")}\" \"{directory.GetPath("second.pk3")}\"")
            && !args.Contains("skip.pk3") && args.Contains("+cl_password \"connect\"")
            && args.Contains("+cl_joinpassword \"join\""),
            "Command preparation keeps full paths, advertised PWAD order, optional exclusions and passwords");
        cancelled = false;
        try { GameLauncher.BuildConnectArguments(server, manager, null, null, cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        RegressionAssert.That(cancelled, "Cancelled command preparation cannot produce launch arguments");
    }

    private static void CheckProgressDialogCancellation()
    {
        using var entered = new ManualResetEventSlim();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = false;
        var dialog = new UpdateProgressDialog(async (_, token) =>
        {
            entered.Set();
            await release.Task;
            observedCancellation = token.IsCancellationRequested;
            token.ThrowIfCancellationRequested();
            return true;
        }, "Preparing Server Connection", "Checking local WADs...", "preparing server connection");
        dialog.Show();
        PumpUntil(() => entered.IsSet);
        dialog.Close();
        RegressionAssert.That(dialog.IsVisible && dialog.FindControl<Button>("CancelButton") is { IsEnabled: false },
            "Closing an active progress window requests cancellation and keeps it open until its worker finishes");
        release.SetResult(true);
        PumpUntil(() => !dialog.IsVisible);
        RegressionAssert.That(observedCancellation && dialog.WasCancelled && !dialog.SaveSucceeded,
            "The worker retains a usable cancellation token until cleanup and closing never reports successful preparation");
    }

    private static void CheckDuplicateResolutionAndLaunchPaths()
    {
        using var directory = new TestDirectory();
        var preferred = directory.GetPath("preferred");
        var second = directory.GetPath("second");
        Directory.CreateDirectory(preferred);
        Directory.CreateDirectory(second);
        var wrongPath = Path.Combine(preferred, "duplicate.pk3");
        var rightPath = Path.Combine(second, "duplicate.pk3");
        File.WriteAllText(wrongPath, "first folder has another version");
        File.WriteAllText(rightPath, "server's requested version");
        var manager = new WadManager { DownloadPath = preferred };
        manager.AddSearchPath(second);
        var hashCache = new WadHashCacheService(directory.GetPath("hashes.json"), () => true);
        var server = new ServerInfo
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 10666),
            PWADs = [new PWadInfo { Name = "duplicate.pk3", Hash = Hash(rightPath) }]
        };
        var selected = new Dictionary<string, WadResolutionResult>(StringComparer.OrdinalIgnoreCase);
        var mismatches = GameLauncher.VerifyWadHashesCoreAsync(server, manager, hashCache,
            null, CancellationToken.None, _ => true, selected).GetAwaiter().GetResult();
        var args = GameLauncher.BuildConnectArguments(server, manager, null, null, resolvedWads: selected);
        RegressionAssert.That(mismatches.Count == 0 && selected["duplicate.pk3"].LocalPath == rightPath
            && args.Contains($"-file \"{rightPath}\"") && !args.Contains(wrongPath)
            && File.ReadAllText(wrongPath) == "first folder has another version",
            "Verification selects the first matching full hash across folders and launch keeps that exact path without changing the earlier duplicate");

        selected.Clear();
        var cached = GameLauncher.CheckCachedWadHashes(server, manager, hashCache, _ => true, selected);
        RegressionAssert.That(cached.AllHashesMatch && cached.VerifiedFileCount == 1
            && selected["duplicate.pk3"].LocalPath == rightPath,
            "The cache-only join fast path searches all same-name paths and records the later matching file for launch");

        var skippedSelection = new Dictionary<string, WadResolutionResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["duplicate.pk3"] = manager.ResolveWadAsync("duplicate.pk3", server.PWADs[0].Hash,
                WadHashLookupMode.FileNameOnly).GetAwaiter().GetResult()
        };
        var skippedArgs = GameLauncher.BuildConnectArguments(server, manager, null, null,
            resolvedWads: skippedSelection);
        RegressionAssert.That(skippedArgs.Contains($"-file \"{wrongPath}\""),
            "Skipping hash verification preserves normal filename priority instead of silently enforcing a server hash at launch");

        var relocated = Path.Combine(preferred, "duplicate.pk3");
        File.Delete(rightPath);
        File.WriteAllText(relocated, "server's requested version");
        args = GameLauncher.BuildConnectArguments(server, manager, null, null, resolvedWads: selected);
        RegressionAssert.That(args.Contains($"-file \"{relocated}\"") && !args.Contains(rightPath),
            "If the selected file disappears, launch rechecks search paths and verifies the relocated copy before using it");

        var otherServer = new ServerInfo
        {
            EndPoint = server.EndPoint,
            PWADs = [new PWadInfo { Name = "duplicate.pk3", Hash = new string('0', 32) }]
        };
        var rejected = false;
        try { GameLauncher.BuildConnectArguments(otherServer, manager, null, null, resolvedWads: selected); }
        catch (IOException) { rejected = true; }
        RegressionAssert.That(rejected,
            "A resolved filename from another server cannot bypass that server's different expected full hash");
    }

    private static void CheckMismatchResolutionPreservesFiles()
    {
        using var directory = new TestDirectory();
        var preferred = directory.GetPath("preferred");
        var second = directory.GetPath("second");
        Directory.CreateDirectory(preferred);
        Directory.CreateDirectory(second);
        var firstPath = Path.Combine(preferred, "mod.pk3");
        var secondPath = Path.Combine(second, "mod.pk3");
        File.WriteAllText(firstPath, "keep preferred version");
        File.WriteAllText(secondPath, "keep other folder's version");
        var manager = new WadManager { DownloadPath = preferred };
        manager.AddSearchPath(second);
        var hashCache = new WadHashCacheService(directory.GetPath("hashes.json"), () => true);
        var expected = new string('0', 32);
        var mismatch = new GameLauncher.WadHashMismatch
        {
            WadName = "mod.pk3", LocalPath = firstPath, ExpectedHash = expected, LocalHash = Hash(firstPath)
        };
        var selected = new Dictionary<string, WadResolutionResult>(StringComparer.OrdinalIgnoreCase);
        var download = GameLauncher.ResolveHashMismatches([mismatch], manager, hashCache,
            resolvedWads: selected);
        RegressionAssert.That(download.Count == 1 && download[0].ExpectedHash == expected
            && File.ReadAllText(firstPath) == "keep preferred version"
            && File.ReadAllText(secondPath) == "keep other folder's version"
            && Directory.GetFiles(preferred).Length == 1 && Directory.GetFiles(second).Length == 1,
            "An all-folder mismatch schedules a download while retaining every existing copy until verified replacement");

        var preparationServer = new ServerInfo
        {
            PWADs = [new PWadInfo { Name = "mod.pk3", Hash = expected, IsOptional = true }]
        };
        GameLauncher.PrepareWadsForJoinAsync(preparationServer, manager, true, null,
            CancellationToken.None, selected).GetAwaiter().GetResult();
        RegressionAssert.That(selected["mod.pk3"].Status == WadResolutionStatus.Mismatch,
            "Optional filename preparation retains the previous hash mismatch instead of replacing it with a found verdict");

        var archivedDirectory = Path.Combine(second, "nested");
        Directory.CreateDirectory(archivedDirectory);
        var archived = Path.Combine(archivedDirectory, "mod_archive.pk3");
        File.WriteAllText(archived, "matching archived version");
        mismatch = new GameLauncher.WadHashMismatch
        {
            WadName = "mod.pk3", LocalPath = firstPath, ExpectedHash = Hash(archived), LocalHash = Hash(firstPath)
        };
        download = GameLauncher.ResolveHashMismatches([mismatch], manager, hashCache,
            resolvedWads: selected);
        var server = new ServerInfo
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 10666),
            PWADs = [new PWadInfo { Name = "mod.pk3", Hash = mismatch.ExpectedHash }]
        };
        var args = GameLauncher.BuildConnectArguments(server, manager, null, null, resolvedWads: selected);
        RegressionAssert.That(download.Count == 0 && args.Contains($"-file \"{archived}\"")
            && selected["mod.pk3"].IsArchived && manager.IsResolutionCurrent(selected["mod.pk3"])
            && File.Exists(archived) && File.ReadAllText(firstPath) == "keep preferred version"
            && File.ReadAllText(secondPath) == "keep other folder's version",
            "A verified archived copy is carried into launch without swapping or renaming copies in any search folder");

        server.PWADs[0].IsOptional = true;
        var cached = GameLauncher.CheckCachedWadHashes(server, manager, hashCache, pwad => pwad.IsOptional, selected);
        var remaining = GameLauncher.VerifyWadHashesCoreAsync(server, manager, hashCache, null,
            CancellationToken.None, pwad => pwad.IsOptional, selected).GetAwaiter().GetResult();
        RegressionAssert.That(cached.AllHashesMatch && remaining.Count == 0
            && selected["mod.pk3"].IsArchived && selected["mod.pk3"].LocalPath == archived,
            "Optional cache and verification passes retain the already verified archived path instead of rediscovering the wrong same-name copy");

        hashCache.SetHashCachingExcludedAsync([archived], true).GetAwaiter().GetResult();
        cached = GameLauncher.CheckCachedWadHashes(server, manager, hashCache, pwad => pwad.IsOptional, selected);
        RegressionAssert.That(!cached.AllHashesMatch && selected["mod.pk3"].LocalPath == archived,
            "A noncached archived file cannot use the cache fast path, but a cache miss preserves its verified selection for the active join");
    }

    private static void CheckNestedResolutionMembership()
    {
        using var directory = new TestDirectory();
        var nested = directory.GetPath("configured-child");
        Directory.CreateDirectory(nested);
        var path = Path.Combine(nested, "nested.pk3");
        File.WriteAllText(path, "nested configured file");
        var manager = new WadManager { DownloadPath = directory.Path };
        manager.AddSearchPath(nested);
        var hashCache = new WadHashCacheService(directory.GetPath("hashes.json"), () => true);
        var result = manager.ResolveWadAsync("nested.pk3", Hash(path), WadHashLookupMode.Verify,
            hashCache).GetAwaiter().GetResult();
        RegressionAssert.That(manager.IsResolutionCurrent(result),
            "A same-name result remains valid while its containing folder is configured");
        manager.SetSearchPaths([]);
        RegressionAssert.That(!manager.IsResolutionCurrent(result),
            "Removing a nested search folder invalidates an ordinary resolved file even if its parent remains configured");
    }

    private static void CheckDownloadedMismatchAndOptionalSkip()
    {
        using var directory = new TestDirectory();
        var preferred = directory.GetPath("preferred");
        var second = directory.GetPath("second");
        Directory.CreateDirectory(preferred);
        Directory.CreateDirectory(second);
        var firstPath = Path.Combine(preferred, "required.pk3");
        var secondPath = Path.Combine(second, "required.pk3");
        var optionalPath = Path.Combine(preferred, "optional.pk3");
        File.WriteAllText(firstPath, "wrong earlier copy");
        File.WriteAllText(secondPath, "wrong later copy");
        File.WriteAllText(optionalPath, "optional content");
        var expected = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes("downloaded matching version")))
            .ToLowerInvariant();
        var manager = new WadManager { DownloadPath = preferred };
        manager.AddSearchPath(second);
        var hashCache = new WadHashCacheService(directory.GetPath("hashes.json"), () => true);
        var server = new ServerInfo
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 10666),
            PWADs =
            [
                new PWadInfo { Name = "required.pk3", Hash = expected },
                new PWadInfo { Name = "optional.pk3", Hash = Hash(optionalPath), IsOptional = true }
            ]
        };
        var selected = new Dictionary<string, WadResolutionResult>(StringComparer.OrdinalIgnoreCase);
        var mismatches = GameLauncher.VerifyWadHashesCoreAsync(server, manager, hashCache, null,
            CancellationToken.None, _ => true, selected).GetAwaiter().GetResult();
        RegressionAssert.That(mismatches.Count == 1 && selected["required.pk3"].Status == WadResolutionStatus.Mismatch,
            "The active join retains the failed full-hash verdict before a downloaded replacement arrives");
        File.WriteAllText(secondPath, "downloaded matching version");
        var args = GameLauncher.BuildConnectArguments(server, manager, null, null, resolvedWads: selected);
        RegressionAssert.That(args.Contains($"\"{secondPath}\"") && !args.Contains(firstPath),
            "Post-download command preparation rechecks a retained mismatch and selects the newly matching later-folder copy");

        GameLauncher.VerifyWadHashesCoreAsync(server, manager, hashCache, null,
            CancellationToken.None, _ => true, selected).GetAwaiter().GetResult();
        foreach (var optional in server.PWADs.Where(pwad => pwad.IsOptional))
            selected.Remove(optional.Name);
        args = GameLauncher.BuildConnectArguments(server, manager, null, null, resolvedWads: selected);
        RegressionAssert.That(args.Contains($"\"{secondPath}\"") && !args.Contains(firstPath)
            && args.Contains($"\"{optionalPath}\""),
            "Skipping only optional verification leaves the verified required path intact and uses ordinary priority for optional files");
    }

    private static void CheckDownloadedPreparationKeepsArchivedRequirements()
    {
        using var directory = new TestDirectory();
        var nested = directory.GetPath("archives");
        Directory.CreateDirectory(nested);
        var archived = Path.Combine(nested, "archived_version.pk3");
        File.WriteAllText(archived, "required archived contents");
        var downloadedDirectory = directory.GetPath("downloaded-folder");
        Directory.CreateDirectory(downloadedDirectory);
        var earlierPath = directory.GetPath("downloaded.pk3");
        var downloadedPath = Path.Combine(downloadedDirectory, "downloaded.pk3");
        var downloadedHash = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes("downloaded required contents")))
            .ToLowerInvariant();
        var manager = new WadManager { DownloadPath = directory.Path };
        manager.AddSearchPath(downloadedDirectory);
        var hashCache = new WadHashCacheService(directory.GetPath("hashes.json"), () => true);
        var selected = new Dictionary<string, WadResolutionResult>(StringComparer.OrdinalIgnoreCase);
        GameLauncher.ResolveHashMismatches(
            [new GameLauncher.WadHashMismatch { WadName = "archived.pk3", ExpectedHash = Hash(archived) }],
            manager, hashCache, resolvedWads: selected);
        var server = new ServerInfo
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 10666),
            PWADs =
            [
                new PWadInfo { Name = "archived.pk3", Hash = Hash(archived) },
                new PWadInfo { Name = "downloaded.pk3", Hash = downloadedHash }
            ]
        };
        GameLauncher.VerifyWadHashesCoreAsync(server, manager, hashCache, null,
            CancellationToken.None, _ => true, selected).GetAwaiter().GetResult();
        File.WriteAllText(earlierPath, "wrong copy appearing after the initial missing check");
        File.WriteAllText(downloadedPath, "downloaded required contents");
        var prepared = GameLauncher.PrepareWadsForJoinAsync(server, manager, false, null,
            CancellationToken.None, selected).GetAwaiter().GetResult();
        var args = GameLauncher.BuildConnectArguments(server, manager, null, null, resolvedWads: selected);
        RegressionAssert.That(prepared.MissingWads.Count == 0 && prepared.NeedsDownload.Count == 0
            && selected["archived.pk3"].IsArchived && selected["archived.pk3"].LocalPath == archived
            && args.Contains($"\"{archived}\"") && args.Contains($"\"{downloadedPath}\"")
            && !args.Contains($"\"{earlierPath}\""),
            "Post-download preparation retains a nested archived required file and a verified missing requirement still chooses its correct later-folder copy");
        File.WriteAllText(downloadedPath, "wrong replacement after the presence check");
        var rejected = false;
        try { GameLauncher.BuildConnectArguments(server, manager, null, null, resolvedWads: selected); }
        catch (IOException) { rejected = true; }
        RegressionAssert.That(rejected,
            "A presence check after a verified missing result cannot weaken the expected hash check before launch");
    }

    private static void CheckUnreadableVerification()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var directory = new TestDirectory();
        var path = directory.GetPath("locked.pk3");
        File.WriteAllText(path, "exclusive file");
        var manager = new WadManager { DownloadPath = directory.Path };
        var hashCache = new WadHashCacheService(directory.GetPath("hashes.json"), () => true);
        var server = new ServerInfo { PWADs = [new PWadInfo { Name = "locked.pk3", Hash = Hash(path) }] };
        using var fileLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var rejected = false;
        try
        {
            GameLauncher.VerifyWadHashesCoreAsync(server, manager, hashCache, null,
                CancellationToken.None, _ => true).GetAwaiter().GetResult();
        }
        catch (IOException) { rejected = true; }
        RegressionAssert.That(rejected,
            "An unreadable local file fails verification instead of being silently counted as a successful match");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        RegressionAssert.That(condition(), "Background operation completed within the regression timeout");
    }

    private static string Hash(string path) => Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class CallbackProgress(Action<SaveStateProgress> callback) : IProgress<SaveStateProgress>
    {
        public void Report(SaveStateProgress value) => callback(value);
    }
}
