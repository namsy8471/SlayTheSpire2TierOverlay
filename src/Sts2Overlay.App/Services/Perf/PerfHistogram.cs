namespace Sts2Overlay.App.Services.Perf;

/// <summary>
/// Durations in milliseconds, kept as a fixed log-scale histogram plus exact count, sum and max.
///
/// Why buckets and not a list of samples: the capture worker records several durations per frame for hours, and a
/// summary only needs percentiles to about 15 %. A fixed array costs nothing to keep and never allocates on
/// <see cref="Record"/>. Bucket i holds [<see cref="MinMs"/>·G^i, <see cref="MinMs"/>·G^(i+1)) with G = 1.15; values
/// below <see cref="MinMs"/> land in the first bucket, values above <see cref="MaxMs"/> in the last one.
///
/// Thread-safe with a plain lock: writers come at a few to a few dozen per second, so contention is not a concern.
/// </summary>
public sealed class PerfHistogram
{
    public const double MinMs = 0.01;
    public const double MaxMs = 60_000;
    public const double Growth = 1.15;

    /// <summary>Enough buckets for <see cref="MinMs"/>·G^n ≥ <see cref="MaxMs"/> (112).</summary>
    public static readonly int BucketCount = (int)Math.Ceiling(Math.Log(MaxMs / MinMs) / Math.Log(Growth));

    private static readonly double LogGrowth = Math.Log(Growth);

    private readonly Lock _gate = new();
    private readonly long[] _counts = new long[BucketCount];
    private long _count;
    private double _sum;
    private double _max;

    public void Record(double ms)
    {
        if (double.IsNaN(ms)) return;
        if (ms < 0) ms = 0; // a clock step can't happen with Stopwatch, but a caller's subtraction might go wrong
        var i = BucketOf(ms);
        lock (_gate)
        {
            _counts[i]++;
            _count++;
            _sum += ms;
            if (ms > _max) _max = ms;
        }
    }

    /// <summary>The figures recorded since the last reset.</summary>
    public PerfStats Snapshot()
    {
        lock (_gate)
        {
            return Build();
        }
    }

    /// <summary>The figures since the last reset, then starts a new period (one summary = one period).</summary>
    public PerfStats SnapshotAndReset()
    {
        lock (_gate)
        {
            var stats = Build();
            ResetLocked();
            return stats;
        }
    }

    public void Reset()
    {
        lock (_gate) ResetLocked();
    }

    /// <summary>Bucket index of a duration (clamped to the range).</summary>
    internal static int BucketOf(double ms)
    {
        if (ms < MinMs) return 0;
        var i = (int)(Math.Log(ms / MinMs) / LogGrowth);
        return Math.Clamp(i, 0, BucketCount - 1);
    }

    /// <summary>Upper edge of bucket <paramref name="i"/>: the value a percentile in that bucket is reported as.</summary>
    internal static double UpperEdge(int i) => MinMs * Math.Pow(Growth, i + 1);

    private void ResetLocked()
    {
        Array.Clear(_counts);
        _count = 0;
        _sum = 0;
        _max = 0;
    }

    private PerfStats Build()
    {
        if (_count == 0) return PerfStats.Empty;
        return new PerfStats(_count, _sum / _count, Percentile(0.50), Percentile(0.90), Percentile(0.99), _max);
    }

    /// <summary>
    /// The upper edge of the bucket holding the p-th sample, so the true value is at most one bucket (15 %) lower.
    /// Never above the exact max: with few samples the top bucket's edge would otherwise overstate it.
    /// </summary>
    private double Percentile(double p)
    {
        var rank = Math.Max(1, (long)Math.Ceiling(p * _count));
        long seen = 0;
        for (var i = 0; i < _counts.Length; i++)
        {
            seen += _counts[i];
            if (seen >= rank) return Math.Min(UpperEdge(i), _max);
        }
        return _max;
    }
}

/// <summary>One period of a <see cref="PerfHistogram"/>, in milliseconds.</summary>
public readonly record struct PerfStats(long Count, double Mean, double P50, double P90, double P99, double Max)
{
    public static PerfStats Empty => default;

    public bool IsEmpty => Count == 0;
}
