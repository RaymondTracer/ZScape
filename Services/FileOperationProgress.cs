using System.Diagnostics;
using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.Services;

/// <summary>One requested file, whether queued, active, or finished.</summary>
public readonly record struct FileOperationProgress(
    long BytesProcessed, long TotalBytes, bool IsComplete = false, string? FileName = null)
{
    internal double ReadFraction => TotalBytes > 0
        ? Math.Clamp((double)BytesProcessed / TotalBytes, 0, 1)
        : 0;

    public double FractionComplete => IsComplete ? 1 : ReadFraction;

    public static FileOperationProgress FromDownload(WadDownloadTask task) => new(
        task.BytesDownloaded, task.TotalBytes, task.IsFinished,
        task.Wad.FileName);
}

public sealed record OverallOperationProgress(
    double PercentComplete, TimeSpan? EstimatedTimeRemaining, bool IsComplete, bool IsStopped = false)
{
    public static OverallOperationProgress Initial { get; } = new(0, null, false);
}

/// <summary>
/// Aggregates every requested file's fraction, including queued files. ETA
/// estimates remaining file-work from recent measured progress, not disk
/// preflight or instant cached completions. Callers serialize updates.
/// </summary>
internal sealed class OverallProgressEstimator
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(5);
    private readonly Func<long> _timestamp;
    private readonly long _frequency;
    private readonly RollingRate _rate;
    private Dictionary<string, FileOperationProgress> _previous = [];
    private long _observationStart;
    private double _measuredWork;
    private OverallOperationProgress? _terminalProgress;

    public OverallProgressEstimator(Func<long>? timestampProvider = null, long timestampFrequency = 0)
    {
        _timestamp = timestampProvider ?? Stopwatch.GetTimestamp;
        _frequency = timestampFrequency == 0 ? Stopwatch.Frequency : timestampFrequency;
        _rate = new RollingRate(RateWindow, 0, _timestamp, _frequency);
        _observationStart = _timestamp();
    }

    public OverallOperationProgress Update(
        IReadOnlyDictionary<string, FileOperationProgress> files, bool isStopped = false)
    {
        if (_terminalProgress != null)
        {
            if (isStopped && !_terminalProgress.IsStopped)
                _terminalProgress = _terminalProgress with { EstimatedTimeRemaining = null, IsStopped = true };
            return _terminalProgress;
        }

        var completedWork = files.Values.Sum(file => file.FractionComplete);
        var percent = files.Count == 0 ? 100 : completedWork / files.Count * 100;
        var complete = files.Values.All(file => file.IsComplete);
        if (isStopped || complete)
        {
            return _terminalProgress = new OverallOperationProgress(percent,
                isStopped ? null : TimeSpan.Zero, complete, isStopped);
        }

        var reset = _previous.Count != files.Count;
        foreach (var (key, file) in files)
        {
            if (!_previous.TryGetValue(key, out var previous))
                continue;
            if (!file.IsComplete && (previous.IsComplete
                || file.ReadFraction < previous.ReadFraction
                || (previous.TotalBytes > 0 && file.TotalBytes != previous.TotalBytes)))
                reset = true;
        }

        if (reset)
        {
            _rate.Reset(_measuredWork);
            _observationStart = _timestamp();
        }

        foreach (var (key, file) in files)
        {
            // Terminal jumps (cached, absent, failed, or already present) are
            // completed work for the bar, but are not measured read throughput.
            if (file.IsComplete)
                continue;
            var previousFraction = _previous.TryGetValue(key, out var previous)
                ? previous.ReadFraction : 0;
            if (!reset)
                _measuredWork += Math.Max(0, file.ReadFraction - previousFraction);
        }
        _previous = new Dictionary<string, FileOperationProgress>(files);

        var workPerSecond = _rate.AddSample(_measuredWork);
        var elapsedSeconds = (double)(_timestamp() - _observationStart) / _frequency;
        var remainingSeconds = workPerSecond > 0 ? (files.Count - completedWork) / workPerSecond : 0;
        var eta = elapsedSeconds >= 1 && workPerSecond > 0
            && double.IsFinite(remainingSeconds) && remainingSeconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(Math.Max(0, remainingSeconds))
            : (TimeSpan?)null;
        return new OverallOperationProgress(percent, eta, false);
    }
}
