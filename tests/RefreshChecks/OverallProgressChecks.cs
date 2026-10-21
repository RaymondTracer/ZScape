using Avalonia.Controls;
using ZScape.Controls;
using ZScape.Models;
using ZScape.Services;
using ZScape.Utilities;

internal static class OverallProgressChecks
{
    public static void Run()
    {
        CheckEveryRequestedFileContributes();
        CheckEtaUsesMeasuredWork();
        CheckRetriesAndStops();
        CheckDownloadStates();
        CheckSharedRollingRate();
        CheckSharedPresentation();
        Task.Run(CheckVerificationProducerAsync).GetAwaiter().GetResult();
    }

    private static void CheckEveryRequestedFileContributes()
    {
        var clock = new ProgressClock();
        var estimator = clock.CreateEstimator();
        var files = new Dictionary<string, FileOperationProgress>
        {
            ["first"] = new(0, 100),
            ["queued"] = new(0, 0)
        };
        var initial = estimator.Update(files);
        RegressionAssert.That(initial.PercentComplete == 0 && initial.EstimatedTimeRemaining is null
            && !initial.IsComplete,
            "Overall progress includes queued files before their sizes are known");

        files["first"] = new(50, 100);
        var halfFirst = estimator.Update(files);
        RegressionAssert.That(Near(halfFirst.PercentComplete, 25),
            "Sequential verification halfway through the first of two files is 25 percent overall");
        files["first"] = new(100, 100, IsComplete: true);
        RegressionAssert.That(Near(estimator.Update(files).PercentComplete, 50),
            "Completing the first sequential file leaves queued work in the overall denominator");

        var snapshot = Enumerable.Range(0, 37).ToDictionary(index => $"complete-{index}",
            _ => new FileOperationProgress(0, 0, IsComplete: true));
        snapshot["active-first"] = new(73, 100);
        snapshot["active-second"] = new(43, 100);
        RegressionAssert.That(Near(clock.CreateEstimator().Update(snapshot).PercentComplete,
                (37 + 0.73 + 0.43) / 39 * 100),
            "Overall progress combines completed and partially verified files rather than counting only completions");

        var limited = new Dictionary<string, FileOperationProgress>
        {
            ["active"] = new(50, 100),
            ["hidden-active"] = new(75, 100),
            ["queued-first"] = new(0, 100),
            ["queued-second"] = new(0, 0)
        };
        RegressionAssert.That(Near(clock.CreateEstimator().Update(limited).PercentComplete, 31.25),
            "Hidden active rows and limited-concurrency queued rows all contribute to overall progress");

        var zeroSizes = new Dictionary<string, FileOperationProgress>
        {
            ["cached"] = new(0, 0, IsComplete: true),
            ["missing"] = new(0, 0, IsComplete: true),
            ["empty"] = new(0, 0, IsComplete: true),
            ["queued"] = new(0, 0)
        };
        RegressionAssert.That(Near(clock.CreateEstimator().Update(zeroSizes).PercentComplete, 75),
            "Cached, missing, and zero-byte completed files count without fabricated byte totals");

        RegressionAssert.That(new FileOperationProgress(-1, 100).FractionComplete == 0
            && new FileOperationProgress(200, 100).FractionComplete == 1
            && new FileOperationProgress(100, 0).FractionComplete == 0
            && new FileOperationProgress(0, 0, IsComplete: true).FractionComplete == 1,
            "Individual file fractions clamp invalid bytes and honor explicit completion");
        RegressionAssert.That(Near(new FileOperationProgress(long.MaxValue / 2, long.MaxValue)
                .FractionComplete, 0.5),
            "Large byte totals preserve fractional progress without integer overflow");
        var manyFiles = Enumerable.Range(0, 10_000).ToDictionary(index => index.ToString(),
            _ => new FileOperationProgress(1, 2));
        RegressionAssert.That(Near(clock.CreateEstimator().Update(manyFiles).PercentComplete, 50),
            "Large file queues retain the same fractional overall progress");
    }

    private static void CheckEtaUsesMeasuredWork()
    {
        var clock = new ProgressClock();
        var estimator = clock.CreateEstimator();
        var files = new Dictionary<string, FileOperationProgress>
        {
            ["active"] = new(0, 100),
            ["queued"] = new(0, 0)
        };
        estimator.Update(files);
        clock.Advance(0.5);
        files["active"] = new(10, 100);
        RegressionAssert.That(estimator.Update(files).EstimatedTimeRemaining is null,
            "Overall ETA waits for a minimum observation period instead of estimating from its first sample");
        clock.Advance(0.5);
        files["active"] = new(25, 100);
        var active = estimator.Update(files);
        RegressionAssert.That(active.EstimatedTimeRemaining is { } eta && Near(eta.TotalSeconds, 7),
            "Overall ETA includes queued file-work using recent measured progress");

        clock.Advance(6);
        RegressionAssert.That(estimator.Update(files).EstimatedTimeRemaining is null,
            "Overall ETA becomes unavailable when progress stalls beyond the rolling window");

        var cacheClock = new ProgressClock();
        var cachedEstimator = cacheClock.CreateEstimator();
        var cachedFiles = new Dictionary<string, FileOperationProgress>
        {
            ["cached"] = new(0, 100),
            ["active"] = new(0, 100)
        };
        cachedEstimator.Update(cachedFiles);
        cachedFiles["cached"] = new(100, 100, IsComplete: true);
        cachedEstimator.Update(cachedFiles);
        cacheClock.Advance(1);
        cachedFiles["active"] = new(25, 100);
        var cachedProgress = cachedEstimator.Update(cachedFiles);
        RegressionAssert.That(Near(cachedProgress.PercentComplete, 62.5)
            && cachedProgress.EstimatedTimeRemaining is { } cachedEta && Near(cachedEta.TotalSeconds, 3),
            "Instant cached completion advances the bar without inflating measured throughput or shortening ETA");

        cachedFiles["active"] = new(100, 100, IsComplete: true);
        var finished = cachedEstimator.Update(cachedFiles);
        RegressionAssert.That(finished.PercentComplete == 100 && finished.IsComplete
            && finished.EstimatedTimeRemaining == TimeSpan.Zero,
            "Successful completion reports 100 percent overall and zero remaining time");
        cachedFiles["active"] = new(25, 100);
        RegressionAssert.That(cachedEstimator.Update(cachedFiles) == finished,
            "A stale progress snapshot cannot regress an already completed operation");
        var stoppedCompletion = cachedEstimator.Update(cachedFiles, isStopped: true);
        RegressionAssert.That(stoppedCompletion.IsComplete && stoppedCompletion.IsStopped
            && stoppedCompletion.PercentComplete == 100 && stoppedCompletion.EstimatedTimeRemaining is null,
            "A terminal failure notification clears ETA even if the last file snapshot already completed the batch");
        RegressionAssert.That(cachedEstimator.Update(cachedFiles) == stoppedCompletion,
            "Stale successful callbacks cannot undo a stopped terminal batch state");
        var empty = cacheClock.CreateEstimator().Update(new Dictionary<string, FileOperationProgress>());
        RegressionAssert.That(empty.IsComplete && empty.PercentComplete == 100
            && empty.EstimatedTimeRemaining == TimeSpan.Zero,
            "An operation with no requested files finishes without an undefined percentage");
    }

    private static void CheckRetriesAndStops()
    {
        var clock = new ProgressClock();
        var estimator = clock.CreateEstimator();
        var files = new Dictionary<string, FileOperationProgress> { ["file"] = new(0, 100) };
        estimator.Update(files);
        clock.Advance(1);
        files["file"] = new(50, 100);
        RegressionAssert.That(estimator.Update(files).EstimatedTimeRemaining is not null,
            "Measured file progress produces an overall ETA after the observation period");
        files["file"] = new(10, 100);
        RegressionAssert.That(estimator.Update(files).EstimatedTimeRemaining is null,
            "Retry progress regression clears the previous attempt's overall ETA");
        clock.Advance(1);
        files["file"] = new(20, 100);
        var retry = estimator.Update(files);
        RegressionAssert.That(retry.EstimatedTimeRemaining is { } retryEta
            && Near(retryEta.TotalSeconds, 8),
            "A retry rebuilds ETA using only the new attempt's measured progress");

        files["file"] = new(20, 200);
        RegressionAssert.That(estimator.Update(files).EstimatedTimeRemaining is null,
            "Switching to a differently sized candidate clears the old throughput estimate");
        clock.Advance(1);
        files["file"] = new(40, 200);
        estimator.Update(files);
        var stopped = estimator.Update(files, isStopped: true);
        RegressionAssert.That(stopped.IsStopped && !stopped.IsComplete
            && Near(stopped.PercentComplete, 20) && stopped.EstimatedTimeRemaining is null,
            "Stopping an operation preserves its actual percentage and clears remaining-time estimates");
        files["file"] = new(200, 200, IsComplete: true);
        RegressionAssert.That(estimator.Update(files) == stopped,
            "Late callbacks cannot replace a stopped operation with a successful completion");
    }

    private static void CheckDownloadStates()
    {
        foreach (var status in new[] { WadDownloadStatus.Pending, WadDownloadStatus.Searching,
                     WadDownloadStatus.Queued, WadDownloadStatus.Downloading })
        {
            var file = FileOperationProgress.FromDownload(new WadDownloadTask
            {
                Wad = new WadInfo("mod.pk3"), Status = status, TotalBytes = 100
            });
            RegressionAssert.That(file.FractionComplete == 0 && !file.IsComplete && file.FileName == "mod.pk3",
                $"A {status} download remains unfinished in the shared aggregate snapshot");
        }

        foreach (var status in new[] { WadDownloadStatus.Completed, WadDownloadStatus.AlreadyExists,
                     WadDownloadStatus.Failed, WadDownloadStatus.Cancelled })
        {
            var file = FileOperationProgress.FromDownload(new WadDownloadTask
            {
                Wad = new WadInfo("mod.pk3"), Status = status, IsFinished = true
            });
            RegressionAssert.That(file.IsComplete && file.FractionComplete == 1,
                $"A {status} download counts as finished batch work even with unknown byte totals");
        }

        var active = FileOperationProgress.FromDownload(new WadDownloadTask
        {
            Status = WadDownloadStatus.Downloading, BytesDownloaded = 1, TotalBytes = 4
        });
        RegressionAssert.That(Near(active.FractionComplete, 0.25),
            "Downloading files retain their exact byte fraction in the shared aggregate model");

        var clock = new ProgressClock();
        var estimator = clock.CreateEstimator();
        var retryingTask = new WadDownloadTask
        {
            Status = WadDownloadStatus.Failed, BytesDownloaded = 100, TotalBytes = 100
        };
        var files = new Dictionary<string, FileOperationProgress>
        {
            ["retrying"] = FileOperationProgress.FromDownload(retryingTask)
        };
        var transientFailure = estimator.Update(files);
        RegressionAssert.That(!files["retrying"].IsComplete && !transientFailure.IsComplete
            && transientFailure.PercentComplete == 100,
            "A transient failed source does not finish the batch even if that attempt transferred all bytes");
        retryingTask.Status = WadDownloadStatus.Downloading;
        retryingTask.BytesDownloaded = 0;
        files["retrying"] = FileOperationProgress.FromDownload(retryingTask);
        var resetAttempt = estimator.Update(files);
        RegressionAssert.That(!resetAttempt.IsComplete && resetAttempt.PercentComplete == 0
            && resetAttempt.EstimatedTimeRemaining is null,
            "A retry after a failed source resets actual progress without a latched completed batch");
        clock.Advance(1);
        retryingTask.BytesDownloaded = 25;
        files["retrying"] = FileOperationProgress.FromDownload(retryingTask);
        var resumedAttempt = estimator.Update(files);
        RegressionAssert.That(resumedAttempt.EstimatedTimeRemaining is { } retryEta
            && Near(retryEta.TotalSeconds, 3),
            "Download retry ETA is rebuilt from the new attempt rather than the failed source's transferred bytes");
    }

    private static void CheckSharedRollingRate()
    {
        var clock = new ProgressClock();
        var rate = new RollingRate(TimeSpan.FromSeconds(1), 0, () => clock.Timestamp,
            TimeSpan.TicksPerSecond);
        clock.Advance(1);
        RegressionAssert.That(Near(rate.AddSample(1024), 1024),
            "The shared rolling-rate owner preserves existing one-second download speed calculations");
        clock.Advance(1.1);
        RegressionAssert.That(rate.AddSample(1024) == 0,
            "The shared rolling-rate owner reports zero after a full window without transferred bytes");
        rate.Reset(1024);
        clock.Advance(1);
        RegressionAssert.That(Near(rate.AddSample(2048), 1024),
            "Resetting the shared rolling rate discards old attempt history");
    }

    private static void CheckSharedPresentation()
    {
        var control = new OverallProgressControl();
        var layout = (Grid)control.Content!;
        var bar = layout.Children.OfType<ProgressBar>().Single();
        var details = layout.Children.OfType<Grid>().Single();
        var percentage = details.Children.OfType<TextBlock>().Single(label => Grid.GetColumn(label) == 0);
        var eta = details.Children.OfType<TextBlock>().Single(label => Grid.GetColumn(label) == 1);
        RegressionAssert.That(bar.Minimum == 0 && bar.Maximum == 100 && bar.Value == 0
            && percentage.Text == "0.0%" && eta.Text == "ETA: Estimating...",
            "The shared overall-progress control starts with a visible percentage and honest pending ETA");
        control.UpdateProgress(new OverallOperationProgress(25, TimeSpan.FromSeconds(7), false));
        RegressionAssert.That(bar.Value == 25 && percentage.Text == "25.0%" && eta.Text == "ETA: 7s",
            "Verification and download progress use the same percentage and ETA presentation");
        control.UpdateProgress(new OverallOperationProgress(25, null, false, IsStopped: true));
        RegressionAssert.That(bar.Value == 25 && eta.Text == "ETA: —",
            "The shared progress control stops estimating after cancellation without filling the bar");
        control.UpdateProgress(new OverallOperationProgress(100, TimeSpan.Zero, true));
        RegressionAssert.That(bar.Value == 100 && percentage.Text == "100.0%" && eta.Text == "ETA: 0s",
            "The shared progress control renders completion without a stale ETA");
        RegressionAssert.That(FormatUtils.FormatEta(TimeSpan.Zero) == "0s"
            && FormatUtils.FormatEta(TimeSpan.FromSeconds(-1)) == "0s",
            "Shared ETA formatting shows zero for completed or negative remaining time");
        RegressionAssert.That(FormatUtils.FormatEta(TimeSpan.FromSeconds(0.1)) == "1s"
            && FormatUtils.FormatEta(TimeSpan.FromSeconds(1)) == "1s",
            "Shared ETA formatting rounds positive sub-second estimates up without confusing them with completion");
    }

    private static async Task CheckVerificationProducerAsync()
    {
        using var fixture = new TestDirectory();
        var contents = new byte[512 * 1024];
        for (var index = 0; index < contents.Length; index++)
            contents[index] = (byte)(index % 251);
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(contents))
            .ToLowerInvariant();
        await File.WriteAllBytesAsync(fixture.GetPath("first.pk3"), contents);
        await File.WriteAllBytesAsync(fixture.GetPath("second.pk3"), contents);
        var manager = new WadManager { DownloadPath = fixture.Path };
        var cache = new WadHashCacheService(fixture.GetPath("producer-cache.json"), () => true);
        var server = new ServerInfo
        {
            PWADs =
            [
                new PWadInfo { Name = "first.pk3", Hash = expectedHash },
                new PWadInfo { Name = "second.pk3", Hash = expectedHash }
            ]
        };
        var snapshots = new List<GameLauncher.HashVerificationProgress>();
        var settings = SettingsService.Instance.Settings;
        var originalConcurrency = settings.HashVerificationConcurrency;
        try
        {
            settings.HashVerificationConcurrency = 1;
            var mismatches = await GameLauncher.VerifyWadHashesCoreAsync(server, manager, cache,
                new VerificationObserver(snapshots.Add), CancellationToken.None, _ => true);
            var initial = snapshots[0];
            RegressionAssert.That(initial.TotalFiles == 2 && initial.FileProgress is { Count: 2 }
                && initial.FileProgress.Values.All(file => !file.IsComplete && file.FractionComplete == 0)
                && initial.OverallProgress.PercentComplete == 0,
                "The real verification producer initially publishes every queued file before hashing begins");
            var final = snapshots[^1];
            RegressionAssert.That(mismatches.Count == 0 && final.FileProgress is { Count: 2 }
                && final.FileProgress.Values.All(file => file.IsComplete)
                && final.OverallProgress.IsComplete && final.OverallProgress.PercentComplete == 100,
                "The real verification producer finishes with all-file snapshots and complete overall progress");

            snapshots.Clear();
            server.PWADs =
            [
                new PWadInfo { Name = "first.pk3", Hash = expectedHash },
                new PWadInfo { Name = "first.pk3", Hash = expectedHash },
                new PWadInfo { Name = "missing.pk3", Hash = expectedHash }
            ];
            var cachedAndMissing = await GameLauncher.VerifyWadHashesCoreAsync(server, manager, cache,
                new VerificationObserver(snapshots.Add), CancellationToken.None, _ => true);
            RegressionAssert.That(snapshots.All(progress => progress.FileProgress is { Count: 3 })
                && snapshots[0].FileProgress!.Values.Count(file => file.FileName == "first.pk3") == 2,
                "Duplicate filename requirements keep distinct stable identities throughout real verification");
            final = snapshots[^1];
            var missing = final.FileProgress!.Values.Single(file => file.FileName == "missing.pk3");
            RegressionAssert.That(cachedAndMissing.Count == 1 && missing.IsComplete && missing.TotalBytes == 0
                && final.FileProgress.Values.All(file => file.IsComplete)
                && final.OverallProgress.PercentComplete == 100,
                "Real cached and missing-file verification completes the batch without dropping zero-byte entries");
            RegressionAssert.That(final.OverallProgress.IsComplete
                && final.OverallProgress.PercentComplete == 100
                && final.OverallProgress.EstimatedTimeRemaining == TimeSpan.Zero,
                "Real cached completions finish aggregate verification and clear its ETA");
        }
        finally
        {
            settings.HashVerificationConcurrency = originalConcurrency;
        }
    }

    private sealed class VerificationObserver(Action<GameLauncher.HashVerificationProgress> callback)
        : IProgress<GameLauncher.HashVerificationProgress>
    {
        public void Report(GameLauncher.HashVerificationProgress value) => callback(value);
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 0.000001;

    private sealed class ProgressClock
    {
        public long Timestamp { get; private set; }
        public void Advance(double seconds) => Timestamp += (long)(seconds * TimeSpan.TicksPerSecond);
        public OverallProgressEstimator CreateEstimator() => new(() => Timestamp, TimeSpan.TicksPerSecond);
    }
}
