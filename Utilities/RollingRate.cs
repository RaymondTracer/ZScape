using System.Diagnostics;

namespace ZScape.Utilities;

/// <summary>A trailing-window rate for cumulative bytes or completed work.</summary>
internal sealed class RollingRate
{
    private readonly Func<long> _timestamp;
    private readonly long _frequency;
    private readonly long _windowTicks;
    private readonly LinkedList<Sample> _samples = [];

    public RollingRate(TimeSpan window, double initialValue,
        Func<long>? timestampProvider = null, long timestampFrequency = 0)
    {
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));
        if (timestampFrequency < 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));

        _timestamp = timestampProvider ?? Stopwatch.GetTimestamp;
        _frequency = timestampFrequency == 0 ? Stopwatch.Frequency : timestampFrequency;
        var windowTicks = window.TotalSeconds * _frequency;
        if (windowTicks > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(window));
        _windowTicks = Math.Max(1, (long)Math.Round(windowTicks));
        Reset(initialValue);
    }

    public void Reset(double totalWork)
    {
        _samples.Clear();
        _samples.AddLast(new Sample(_timestamp(), Normalize(totalWork)));
    }

    public double AddSample(double totalWork)
    {
        var now = _timestamp();
        totalWork = Normalize(totalWork);
        if (totalWork < _samples.Last!.Value.TotalWork)
        {
            Reset(totalWork);
            return 0;
        }
        _samples.AddLast(new Sample(now, totalWork));

        var cutoff = now - _windowTicks;
        while (_samples.First?.Next is { } next && next.Value.Timestamp <= cutoff)
            _samples.RemoveFirst();

        var oldest = _samples.First!.Value;
        var baselineTimestamp = oldest.Timestamp;
        var baselineWork = oldest.TotalWork;
        if (oldest.Timestamp < cutoff && _samples.First.Next is { } afterCutoff)
        {
            var later = afterCutoff.Value;
            var sampleSpan = later.Timestamp - oldest.Timestamp;
            if (sampleSpan > 0)
            {
                var position = (double)(cutoff - oldest.Timestamp) / sampleSpan;
                baselineWork += (later.TotalWork - oldest.TotalWork) * position;
                baselineTimestamp = cutoff;
            }
        }

        var elapsedTicks = now - baselineTimestamp;
        var completedWork = totalWork - baselineWork;
        return elapsedTicks > 0 && completedWork > 0
            ? completedWork * _frequency / elapsedTicks
            : 0;
    }

    private static double Normalize(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    private readonly record struct Sample(long Timestamp, double TotalWork);
}
