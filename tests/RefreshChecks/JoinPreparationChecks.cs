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
