using System.Diagnostics;

namespace SimTrial;

/// <summary>
/// Every 10 s (and once at the end): "progress done/total (pct) elapsed .. eta HH:mm:ss (.. left)" on
/// stderr and in <c>&lt;output&gt;.progress</c> (overwritten; the last line says "finished"), so a long
/// run can be checked from another shell at any time. run.sh sums its shards' files the same way.
/// The ETA is a straight-line estimate from the fights done so far. <see cref="Tick"/> is thread-safe.
/// </summary>
public sealed class Progress : IDisposable
{
    private readonly string _path;
    private readonly int _total;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _closed;
    private readonly Func<int>? _doneSource;
    private int _done;

    /// <param name="doneSource">Counts done work itself instead of <see cref="Tick"/>; SimFuzz
    /// --claim uses it to report the fights finished by all processes sharing the sample.</param>
    public Progress(string outputPath, int total, Func<int>? doneSource = null)
    {
        (_path, _total, _doneSource) = (outputPath + ".progress", total, doneSource);
        _timer = new Timer(_ => Report(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    public void Tick() => Interlocked.Increment(ref _done);

    private void Report(bool final = false)
    {
        lock (_gate)
        {
            if (_closed) return; // a timer tick that lands after the final line must not overwrite it
            _closed = final;
            string line = Line(_doneSource?.Invoke() ?? Volatile.Read(ref _done), _total, _sw.Elapsed);
            Console.Error.WriteLine(line);
            try { File.WriteAllText(_path, line + Environment.NewLine); } catch (IOException) { }
        }
    }

    public static string Line(int done, int total, TimeSpan elapsed)
    {
        string eta = "eta ?";
        if (done > 0 && done < total)
        {
            var left = TimeSpan.FromTicks(elapsed.Ticks / done * (total - done));
            eta = $"eta {DateTime.Now + left:HH:mm:ss} ({Fmt(left)} left)";
        }
        else if (done >= total)
        {
            eta = "finished";
        }
        return $"progress {done}/{total} ({100.0 * done / Math.Max(1, total):F1}%) elapsed {Fmt(elapsed)} {eta}";
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:D2}m" : $"{t.Minutes}m{t.Seconds:D2}s";

    public void Dispose()
    {
        _timer.Dispose();
        Report(final: true);
    }
}
