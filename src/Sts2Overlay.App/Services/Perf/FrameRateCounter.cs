namespace Sts2Overlay.App.Services.Perf;

/// <summary>
/// Counts the game frames Windows.Graphics.Capture hands us (<c>WgcCapturer.OnFrameArrived</c>) and the largest gap
/// between two of them. WGC delivers a frame whenever the window's content changes, and the game redraws every
/// frame, so this is the game's presented frame rate as DWM sees it (capped by the monitor refresh rate).
///
/// <see cref="Arrived"/> runs on a WGC thread up to the refresh rate (144 Hz and more), so it is a handful of
/// interlocked operations: no lock, no allocation, no logging.
///
/// The rate is counted over running time only: the sum of the gaps between consecutive frames. <see cref="Break"/>
/// (session stopped or suspended) forgets the last arrival, so the time with no session is in no gap.
/// </summary>
public sealed class FrameRateCounter
{
    private long _frames;
    private long _gaps;
    private long _gapTicks;
    private long _maxGapTicks;
    private long _last; // Stopwatch timestamp of the previous arrival; 0 = none since the session (re)started

    /// <summary>One frame arrived at <paramref name="timestamp"/> (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>).</summary>
    public void Arrived(long timestamp)
    {
        Interlocked.Increment(ref _frames);
        var prev = Interlocked.Exchange(ref _last, timestamp);
        if (prev == 0 || timestamp <= prev) return;
        var gap = timestamp - prev;
        Interlocked.Increment(ref _gaps);
        Interlocked.Add(ref _gapTicks, gap);
        // Max by compare-and-swap; nearly always a single read, because the gap is rarely a new maximum.
        var max = Volatile.Read(ref _maxGapTicks);
        while (gap > max)
        {
            var seen = Interlocked.CompareExchange(ref _maxGapTicks, gap, max);
            if (seen == max) break;
            max = seen;
        }
    }

    /// <summary>The capture session stopped (suspend, window change): the next frame starts a new run of gaps.</summary>
    public void Break() => Interlocked.Exchange(ref _last, 0);

    /// <summary>
    /// The figures since the last call, then zero. The fields are swapped one by one, so a frame arriving in between
    /// may count in one period and its gap in the next; at summary periods of minutes that is noise.
    /// </summary>
    public FrameRateStats SnapshotAndReset(double tickFrequency)
    {
        var frames = Interlocked.Exchange(ref _frames, 0);
        var gaps = Interlocked.Exchange(ref _gaps, 0);
        var gapTicks = Interlocked.Exchange(ref _gapTicks, 0);
        var maxGap = Interlocked.Exchange(ref _maxGapTicks, 0);
        return new FrameRateStats(frames, gaps, gapTicks / tickFrequency, maxGap * 1000.0 / tickFrequency);
    }
}

/// <summary>Game frames of one summary period.</summary>
/// <param name="Frames">Frames that arrived.</param>
/// <param name="Gaps">Gaps measured between two frames of the same session.</param>
/// <param name="RunningSeconds">Sum of those gaps: the time the rate is counted over.</param>
/// <param name="WorstGapMs">Largest single gap.</param>
public readonly record struct FrameRateStats(long Frames, long Gaps, double RunningSeconds, double WorstGapMs)
{
    /// <summary>Frames per second over the running time, or null when there is nothing to count.</summary>
    public double? Fps => Gaps > 0 && RunningSeconds > 0 ? Gaps / RunningSeconds : null;
}
