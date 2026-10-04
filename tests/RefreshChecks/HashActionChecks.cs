using System.Collections.ObjectModel;
using Avalonia.Controls;
using ZScape.Controls;
using ZScape.Services;
using ZScape.Views;

internal static class HashActionChecks
{
    private const string AbcMd5 = "900150983cd24fb0d6963f7d28e17f72";

    public static void Run()
    {
        using var fixture = new TestDirectory();
        var file = fixture.GetPath("mod.pk3");
        File.WriteAllText(file, "abc");
        var enabled = true;
        var cache = new WadHashCacheService(fixture.GetPath("cache.json"), () => enabled);
        Task.Run(() => CheckServiceAsync(cache, file, () => enabled = false, () => enabled = true))
            .GetAwaiter().GetResult();
        CheckBrowser(cache, file, new SettingsService(fixture.Path), () => enabled = false, () => enabled = true);
        CheckWorkerDispatch(fixture, file);
    }

    private static async Task CheckServiceAsync(
        WadHashCacheService cache, string file, Action disable, Action enable)
    {
        var empty = cache.GetActionsForFiles([]);
        RegressionAssert.That(!empty.CanCalculate && !empty.CanToggleExclusion,
            "Hash menus disable file actions without a selected local path");
        var initial = cache.GetActionsForFiles([file]);
        RegressionAssert.That(initial.CanCalculate && !initial.IsRecalculation
            && initial.CalculateHeader == "Calculate _Hash",
            "An uncached path has the shared calculate action");
        RegressionAssert.That(!cache.GetActionsForFiles([file, file]).CanCalculate,
            "A multi-selection never becomes a single calculate action after canonicalizing paths");
        var busy = cache.GetActionsForFiles([file], isBusy: true);
        RegressionAssert.That(!busy.CanCalculate && !busy.CanToggleExclusion,
            "Hash actions are disabled while their view is busy");
        var calculated = await cache.CalculateAndCacheHashAsync(file, CancellationToken.None);
        RegressionAssert.That(calculated.HashResult.Hash == AbcMd5 && calculated.CachedHash == AbcMd5
            && !calculated.WasRecalculation,
            "The shared manual operation calculates and returns a validated cache result");
        var cached = cache.GetActionsForFiles([file]);
        RegressionAssert.That(cached.CanCalculate && cached.IsRecalculation
            && cached.CalculateHeader == "_Recalculate Hash",
            "Recorded paths use the shared recalculate header and eligibility");
        var recalculated = await cache.CalculateAndCacheHashAsync(file, CancellationToken.None);
        RegressionAssert.That(recalculated.WasRecalculation && !recalculated.HashResult.FromCache
            && recalculated.CachedHash == AbcMd5,
            "Explicit recalculation reads the file rather than reusing its cache entry");

        var absent = file + ".missing";
        var absentActions = cache.GetActionsForFiles([absent]);
        RegressionAssert.That(absentActions.CanCalculate && !absentActions.IsRecalculation,
            "Menu hints do not inspect an uncached path's filesystem state");
        File.Delete(file);
        var deleted = cache.GetActionsForFiles([file]);
        RegressionAssert.That(deleted == cached,
            "Deleting a file does not cause menu opening to validate its recorded snapshot");
        var missingResult = await cache.CalculateAndCacheHashAsync(file, CancellationToken.None);
        RegressionAssert.That(!missingResult.HashResult.IsSuccess && missingResult.CachedHash == null,
            "The actual manual operation validates a missing file and rejects its stale cache");
        await File.WriteAllTextAsync(file, "abc");

        await cache.SetHashCachingExcludedAsync([file], true);
        var excluded = cache.GetActionsForFiles([file]);
        RegressionAssert.That(!excluded.CanCalculate && excluded.CanToggleExclusion
            && !excluded.ShouldExclude && excluded.ToggleExclusionHeader == "Allow Hash _Caching",
            "Excluded files cannot be manually cached but can be allowed again");
        var excludedAction = await cache.CalculateAndCacheHashAsync(file, CancellationToken.None);
        RegressionAssert.That(!excludedAction.HashResult.IsSuccess && !cache.HasCachedEntryForPath(file),
            "Manual hash actions enforce per-file exclusions in the service");
        var verification = await cache.GetHashAsync(file, null, CancellationToken.None);
        RegressionAssert.That(verification.Hash == AbcMd5 && !verification.FromCache
            && !cache.HasCachedEntryForPath(file),
            "A cache exclusion still permits normal full hash verification");
        var mixed = cache.GetActionsForFiles([file, absent]);
        RegressionAssert.That(!mixed.CanCalculate && mixed.CanToggleExclusion && mixed.ShouldExclude
            && mixed.ToggleExclusionHeader == "_Don't Cache Selected Hashes",
            "Mixed browser selections exclude all selected files on the next toggle");
        await cache.SetHashCachingExcludedAsync([absent], true);
        var allExcluded = cache.GetActionsForFiles([file, absent]);
        RegressionAssert.That(!allExcluded.ShouldExclude
            && allExcluded.ToggleExclusionHeader == "Allow Hash Caching for _Selected Files",
            "All-excluded browser selections allow all selected files on the next toggle");

        disable();
        var disabled = cache.GetActionsForFiles([file]);
        RegressionAssert.That(!disabled.CanCalculate && disabled.CanToggleExclusion,
            "Global cache disabling blocks calculation without blocking exclusion preferences");
        var disabledAction = await cache.CalculateAndCacheHashAsync(file, CancellationToken.None);
        RegressionAssert.That(!disabledAction.HashResult.IsSuccess,
            "Manual operations enforce the global cache setting in the service");
        RegressionAssert.That((await cache.GetHashAsync(file, null, CancellationToken.None)).Hash == AbcMd5,
            "Global cache disabling does not suppress verification");
        enable();
        await cache.SetHashCachingExcludedAsync([file, absent], false);
        await cache.CalculateAndCacheHashAsync(file, CancellationToken.None);
    }

    private static void CheckBrowser(
        WadHashCacheService cache, string file, SettingsService settings, Action disable, Action enable)
    {
        var browser = new WadBrowserDialog(settings, cache);
        var list = browser.FindControl<ResizableListView>("WadListView")!;
        // The row intentionally has no cached marker; menus use service-owned
        // metadata rather than a view model that may have been loaded earlier.
        var row = new WadFileEntry { Name = "mod", Extension = ".pk3", FullPath = file };
        list.ItemsSource = new ObservableCollection<WadFileEntry> { row };
        try
        {
            File.Delete(file);
            list.SelectItem(row);
            var items = list.ContextMenu!.Items.OfType<MenuItem>().ToList();
            var calculate = items.Single(item => Equals(item.Header, "_Recalculate Hash"));
            var toggle = items.Single(item => Equals(item.Header, "_Don't Cache Hash"));
            var actions = cache.GetActionsForFiles([file]);
            RegressionAssert.That(calculate.IsEnabled && !row.IsHashCached
                && Equals(ToolTip.GetTip(calculate), actions.CalculateToolTip),
                "Browser selection uses shared memory hints and tooltip without validating the deleted file");
            RegressionAssert.That(toggle.IsEnabled && Equals(ToolTip.GetTip(toggle), actions.ToggleExclusionToolTip),
                "Browser cache-exclusion presentation comes from the same shared action state");
            disable();
            list.ClearSelection();
            list.SelectItem(row);
            RegressionAssert.That(!calculate.IsEnabled && toggle.IsEnabled
                && Equals(ToolTip.GetTip(calculate), cache.GetActionsForFiles([file]).CalculateToolTip),
                "The real browser menu reflects globally disabled caching while retaining exclusion control");
            list.ClearSelection();
            RegressionAssert.That(!calculate.IsEnabled && !toggle.IsEnabled,
                "The real browser menu disables file actions after deselection");
        }
        finally
        {
            enable();
            File.WriteAllText(file, "abc");
            browser.Close();
        }
    }

    private static void CheckWorkerDispatch(TestDirectory fixture, string file)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var callingThread = Environment.CurrentManagedThreadId;
        var workerThread = callingThread;
        var cache = new WadHashCacheService(fixture.GetPath("worker-cache.json"), () =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Manual hashing was not released by its test.");
            return true;
        });
        var pending = cache.CalculateAndCacheHashAsync(file, CancellationToken.None);
        try
        {
            RegressionAssert.That(entered.Wait(TimeSpan.FromSeconds(10)) && workerThread != callingThread
                && !pending.IsCompleted,
                "Manual hashing returns immediately and begins all eligibility/inspection work on a worker");
        }
        finally { release.Set(); }
        var result = pending.GetAwaiter().GetResult();
        RegressionAssert.That(result.HashResult.Hash == AbcMd5 && result.CachedHash == AbcMd5,
            "The worker-dispatched manual action completes with its validated cache result");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellationObserved = false;
        try { cache.CalculateAndCacheHashAsync(file, cancelled.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        RegressionAssert.That(cancellationObserved, "Manual cache action cancellation remains distinguishable from failure");
    }
}
