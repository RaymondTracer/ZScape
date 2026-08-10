using System.Diagnostics;
using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.Services;

public partial class WadDownloader
{
    private DownloadProgressSampler StartProgressSampling(
        WadDownloadTask task,
        Func<long> readDownloadedBytes)
    {
        return new DownloadProgressSampler(
            task,
            readDownloadedBytes,
            updatedTask => ProgressUpdated?.Invoke(this, updatedTask));
    }

    private sealed class DownloadProgressSampler : IDisposable
    {
        private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(1);

        private readonly object _sync = new();
        private readonly WadDownloadTask _task;
        private readonly Func<long> _readDownloadedBytes;
        private readonly Action<WadDownloadTask> _reportProgress;
        private readonly RollingTransferRate _transferRate;
        private readonly System.Timers.Timer _timer;
        private bool _disposed;

        public DownloadProgressSampler(
            WadDownloadTask task,
            Func<long> readDownloadedBytes,
            Action<WadDownloadTask> reportProgress)
        {
            _task = task;
            _readDownloadedBytes = readDownloadedBytes;
            _reportProgress = reportProgress;
            _transferRate = new RollingTransferRate(
                SpeedWindow,
                Math.Max(0, readDownloadedBytes()));

            _timer = new System.Timers.Timer(
                AppConstants.UiIntervals.UiUpdateThrottleMs)
            {
                AutoReset = true,
            };
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();
        }

        public void ReportNow()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                var downloadedBytes = Math.Max(0, _readDownloadedBytes());
                _task.BytesDownloaded = downloadedBytes;
                _task.BytesPerSecond = _transferRate.AddSample(downloadedBytes);
                _reportProgress(_task);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _timer.Stop();
                _timer.Elapsed -= OnTimerElapsed;
            }

            _timer.Dispose();
        }

        private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            ReportNow();
        }
    }

    /// <summary>
    /// Calculates a transfer rate over a trailing time window while allowing
    /// progress to be rendered more frequently than that window.
    /// </summary>
    private sealed class RollingTransferRate
    {
        private readonly long _windowTicks;
        private readonly LinkedList<Sample> _samples = [];

        public RollingTransferRate(TimeSpan window, long initialBytes)
        {
            if (window <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(window));
            }

            _windowTicks = Math.Max(
                1,
                (long)Math.Round(window.TotalSeconds * Stopwatch.Frequency));
            _samples.AddLast(new Sample(
                Stopwatch.GetTimestamp(),
                Math.Max(0, initialBytes)));
        }

        public double AddSample(long totalBytes)
        {
            var now = Stopwatch.GetTimestamp();
            totalBytes = Math.Max(0, totalBytes);
            _samples.AddLast(new Sample(now, totalBytes));

            var cutoff = now - _windowTicks;
            while (_samples.First?.Next is { } next
                   && next.Value.Timestamp <= cutoff)
            {
                _samples.RemoveFirst();
            }

            var oldest = _samples.First!.Value;
            var baselineTimestamp = oldest.Timestamp;
            double baselineBytes = oldest.TotalBytes;

            if (oldest.Timestamp < cutoff
                && _samples.First.Next is { } sampleAfterCutoff)
            {
                var later = sampleAfterCutoff.Value;
                var sampleSpan = later.Timestamp - oldest.Timestamp;
                if (sampleSpan > 0)
                {
                    var position = (double)(cutoff - oldest.Timestamp)
                        / sampleSpan;
                    baselineBytes = oldest.TotalBytes
                        + ((later.TotalBytes - oldest.TotalBytes) * position);
                    baselineTimestamp = cutoff;
                }
            }

            var elapsedTicks = now - baselineTimestamp;
            var transferredBytes = totalBytes - baselineBytes;
            if (elapsedTicks <= 0 || transferredBytes <= 0)
            {
                return 0;
            }

            return transferredBytes * Stopwatch.Frequency / elapsedTicks;
        }

        private readonly record struct Sample(long Timestamp, long TotalBytes);
    }
}
