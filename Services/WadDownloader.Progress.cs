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
        private readonly RollingRate _transferRate;
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
            _transferRate = new RollingRate(
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

}
