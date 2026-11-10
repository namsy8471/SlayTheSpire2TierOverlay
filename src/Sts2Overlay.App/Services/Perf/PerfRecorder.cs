using System.Diagnostics;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;

namespace Sts2Overlay.App.Services.Perf;

/// <summary>What <see cref="PerfRecorder"/> times. The names appear as they are in the <c>perf:</c> lines.</summary>
public enum PerfStage
{
    /// <summary>Whole capture call on the worker (acquire + copy + backend overhead).</summary>
    Capture,
    /// <summary>Getting pixels the CPU can read (WGC: GPU copy + Map wait; GDI: BitBlt).</summary>
    CaptureAcquire,
    /// <summary>Copying them into the managed frame buffer.</summary>
    CaptureCopy,
    /// <summary>The event-screen probe (<c>CapturePipeline.FrameProbe</c>).</summary>
    EventProbe,
    /// <summary><c>MapScreenWatcher.Analyze</c>.</summary>
    MapAnalyze,
    /// <summary><c>ICardRecognizer.Recognize</c> (only frames that were not skipped as unchanged).</summary>
    Recognize,
    /// <summary>One worker step that captured for recognition, start to end.</summary>
    StepTotal,
    /// <summary>The worker raised <c>Recognized</c> → the UI thread started applying it.</summary>
    UiQueue,
    /// <summary><c>AppController.ApplyRecognition</c> of an accepted result (filter, score, lay out badges).</summary>
    UiApply,
    /// <summary><c>AppController.OnMapSighted</c> on the UI thread.</summary>
    MapUi,
    /// <summary><c>AppController.ApplyEventScreen</c> on the UI thread.</summary>
    EventUi,
    /// <summary>One 50 ms hover-yield poll on the UI thread.</summary>
    HoverPoll,
    /// <summary>Frame that first showed a reward / shop screen → its badges laid out and rendered.</summary>
    FrameToPaint,
}

/// <summary>
/// Always-on performance log: per-stage time distributions, worker counters, the game's frame rate through WGC and
/// the overlay's CPU / memory use, summarised as <c>perf:</c> lines in app.log every <see cref="SummaryEvery"/> (when
/// anything happened) and at exit.
///
/// Cheap by design, so it never needs a setting: recording is a timestamp subtraction and a locked array increment
/// (frames come at 1.5 to 6 Hz, 20 Hz for the few seconds of a hunt), the only extra thread is a <see cref="Timer"/> tick every <see cref="SampleEvery"/>,
/// and nothing allocates per frame.
///
/// One instance per app (created in <c>App.OnStartup</c>, handed to <see cref="CapturePipeline"/>, which exposes it to
/// <c>AppController</c> as <see cref="CapturePipeline.Perf"/>).
/// </summary>
public sealed class PerfRecorder : IDisposable
{
    public static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SummaryEvery = TimeSpan.FromMinutes(5);

    private static readonly PerfStage[] AllStages = Enum.GetValues<PerfStage>();

    private readonly PerfHistogram[] _stages = new PerfHistogram[AllStages.Length];
    private readonly Action<string> _log;
    private readonly ResourceSampler? _sampler;
    private readonly Timer? _timer;
    private readonly Lock _summaryGate = new();
    private ResourceAccumulator _resources = new();
    private long _periodStart = Stopwatch.GetTimestamp();
    private long _steps;
    private long _recognised;
    private long _unchanged;
    private long _reused;
    private readonly Lock _huntGate = new();
    private long _huntSince; // Stopwatch timestamp the running hunt stretch started; 0 = not hunting
    private long _huntTicks; // Stopwatch ticks hunted in this period before _huntSince
    private long _huntSteps;
    private int _huntFound;
    private int _huntHintLeft;
    private int _huntRanOut;
    private bool _disposed;
    private bool _tickFailed;

    /// <param name="gameProcessId">The game's process id, read on the sampler thread; null or ≤ 0 = no game.</param>
    /// <param name="sampleResources">False: no timer and no CPU / memory sampling (tests, a pipeline built without one).</param>
    /// <param name="log">Where the lines go; <see cref="AppLog.Info"/> by default.</param>
    public PerfRecorder(Func<int?>? gameProcessId = null, bool sampleResources = true, Action<string>? log = null)
    {
        for (var i = 0; i < _stages.Length; i++) _stages[i] = new PerfHistogram();
        _log = log ?? AppLog.Info;
        if (!sampleResources) return;
        _sampler = new ResourceSampler(gameProcessId ?? (() => null));
        _sampler.Sample(); // baselines: CPU figures are deltas from one sample to the next
        _timer = new Timer(_ => OnTick(), null, SampleEvery, SampleEvery);
    }

    /// <summary>Game frames seen by WGC; the capturer reports into it (<see cref="WgcCapturer"/>).</summary>
    public FrameRateCounter GameFrames { get; } = new();

    public void Record(PerfStage stage, double ms) => _stages[(int)stage].Record(ms);

    /// <summary>Records the time from <paramref name="start"/> (<see cref="Stopwatch.GetTimestamp"/>) to now.</summary>
    public void RecordSince(PerfStage stage, long start) => Record(stage, Stopwatch.GetElapsedTime(start).TotalMilliseconds);

    /// <summary>Records the time between two <see cref="Stopwatch.GetTimestamp"/> values.</summary>
    public void RecordBetween(PerfStage stage, long start, long end) => Record(stage, Stopwatch.GetElapsedTime(start, end).TotalMilliseconds);

    /// <summary>A worker step captured a frame for recognition; <paramref name="reused"/> = WGC had no new frame.</summary>
    public void CountStep(bool reused)
    {
        Interlocked.Increment(ref _steps);
        if (reused) Interlocked.Increment(ref _reused);
        if (Interlocked.Read(ref _huntSince) != 0) Interlocked.Increment(ref _huntSteps);
    }

    /// <summary>
    /// The capture runs at the hunt rate now, or no longer (<c>AppController.UpdateCadence</c>, UI thread): steps counted
    /// meanwhile are hunt steps, and the time is summed. Repeating the same value changes nothing.
    /// </summary>
    public void SetHunting(bool hunting)
    {
        lock (_huntGate)
        {
            var now = Stopwatch.GetTimestamp();
            if (hunting && _huntSince == 0)
            {
                Interlocked.Exchange(ref _huntSince, now);
            }
            else if (!hunting && _huntSince != 0)
            {
                _huntTicks += now - _huntSince;
                Interlocked.Exchange(ref _huntSince, 0);
            }
        }
    }

    /// <summary>A hunt ended (<see cref="OfferHunt.LastEnd"/>); <see cref="HuntEnd.Off"/> and <see cref="HuntEnd.None"/> are not counted.</summary>
    public void CountHuntEnd(HuntEnd end)
    {
        lock (_huntGate)
        {
            switch (end)
            {
                case HuntEnd.Found: _huntFound++; break;
                case HuntEnd.HintLeft: _huntHintLeft++; break;
                case HuntEnd.RanOut: _huntRanOut++; break;
            }
        }
    }

    public void CountUnchanged() => Interlocked.Increment(ref _unchanged);

    public void CountRecognised() => Interlocked.Increment(ref _recognised);

    /// <summary>
    /// Writes the summary of the period so far (and starts a new one) and returns the lines written. Nothing is
    /// written when nothing happened and no resource sample was taken.
    /// </summary>
    public IReadOnlyList<string> WriteSummary(string? reason = null)
    {
        lock (_summaryGate) return WriteLocked(reason, requireActivity: false);
    }

    private void OnTick()
    {
        lock (_summaryGate)
        {
            if (_disposed) return;
            try
            {
                SampleLocked();
                if (Stopwatch.GetElapsedTime(_periodStart) >= SummaryEvery) WriteLocked(null, requireActivity: true);
            }
            catch (Exception ex)
            {
                // A timer callback that throws would take the process down; perf logging is never worth that.
                if (!_tickFailed) AppLog.Warn($"perf: sampling failed ({ex.GetType().Name}: {ex.Message}); continuing");
                _tickFailed = true;
            }
        }
    }

    private void SampleLocked()
    {
        if (_sampler?.Sample() is { } sample) _resources.Add(sample);
    }

    /// <param name="requireActivity">Periodic summary: only when the worker or the UI recorded something (an idle
    /// overlay with the game closed would otherwise write the same resource line every 5 minutes).</param>
    private IReadOnlyList<string> WriteLocked(string? reason, bool requireActivity)
    {
        var summary = TakeSummary(reason);
        var active = !summary.Counts.IsEmpty || summary.Game.Frames > 0 || summary.Stages.Any(s => !s.Stats.IsEmpty);
        if (!active && (requireActivity || summary.Resources is null)) return [];
        var lines = PerfFormat.SummaryLines(summary);
        foreach (var line in lines) _log(line);
        return lines;
    }

    /// <summary>Snapshots and resets everything: the next summary covers only the time after this one.</summary>
    private PerfSummary TakeSummary(string? reason)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(_periodStart, now).TotalSeconds;
        _periodStart = now;
        var counts = new PerfCounts(
            Interlocked.Exchange(ref _steps, 0),
            Interlocked.Exchange(ref _recognised, 0),
            Interlocked.Exchange(ref _unchanged, 0),
            Interlocked.Exchange(ref _reused, 0));
        var stages = AllStages.Select(s => (s, _stages[(int)s].SnapshotAndReset())).ToArray();
        var game = GameFrames.SnapshotAndReset(Stopwatch.Frequency);
        var resources = _resources.Build(Environment.ProcessorCount);
        _resources = new ResourceAccumulator();
        return new PerfSummary(seconds, reason, counts, game, stages, resources, TakeHunt(now));
    }

    /// <summary>The period's hunt figures, reset; a hunt still running counts up to <paramref name="now"/> and goes on in the next period.</summary>
    private HuntCounts TakeHunt(long now)
    {
        lock (_huntGate)
        {
            var ticks = _huntTicks;
            if (_huntSince != 0)
            {
                ticks += now - _huntSince;
                Interlocked.Exchange(ref _huntSince, now);
            }
            var hunt = new HuntCounts(Interlocked.Exchange(ref _huntSteps, 0), (double)ticks / Stopwatch.Frequency, _huntFound, _huntHintLeft, _huntRanOut);
            _huntTicks = 0;
            _huntFound = _huntHintLeft = _huntRanOut = 0;
            return hunt;
        }
    }

    /// <summary>Stops the sampler and writes the final summary (app exit).</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        lock (_summaryGate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                SampleLocked();
                WriteLocked("exit", requireActivity: false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"perf: final summary failed ({ex.GetType().Name}: {ex.Message})");
            }
            _sampler?.Dispose();
        }
    }
}

/// <summary>One resource sample: CPU over the time since the previous sample, memory now. Null CPU = not measured.</summary>
internal readonly record struct ResourceSample(double? ProcessCore, double? MachineLoad, double? GameCore, double WorkingSetMb, double PrivateMb, double ManagedMb);

/// <summary>Averages and maxima of the samples of one summary period, plus the GC collections in it.</summary>
internal sealed class ResourceAccumulator
{
    private readonly int[] _gcAtStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private int _samples;
    private Acc _process;
    private Acc _machine;
    private Acc _game;
    private Acc _ws;
    private Acc _private;
    private Acc _managed;

    public void Add(ResourceSample s)
    {
        _samples++;
        _process.Add(s.ProcessCore);
        _machine.Add(s.MachineLoad);
        _game.Add(s.GameCore);
        _ws.Add(s.WorkingSetMb);
        _private.Add(s.PrivateMb);
        _managed.Add(s.ManagedMb);
    }

    /// <summary>The period's figures, or null when no sample measured the process CPU yet.</summary>
    public ResourceStats? Build(int cores)
    {
        if (_process.N == 0) return null;
        return new ResourceStats(
            _samples,
            _process.Avg ?? 0, _process.Max,
            cores,
            _machine.Avg, _machine.N > 0 ? _machine.Max : null,
            _game.Avg, _game.N > 0 ? _game.Max : null,
            _ws.Avg ?? 0, _ws.Max,
            _private.Avg ?? 0,
            _managed.Avg ?? 0,
            GC.CollectionCount(0) - _gcAtStart[0],
            GC.CollectionCount(1) - _gcAtStart[1],
            GC.CollectionCount(2) - _gcAtStart[2]);
    }

    private struct Acc
    {
        public int N;
        public double Sum;
        public double Max;

        public void Add(double? v)
        {
            if (v is not { } x) return;
            N++;
            Sum += x;
            if (N == 1 || x > Max) Max = x;
        }

        public readonly double? Avg => N > 0 ? Sum / N : null;
    }
}

/// <summary>
/// Reads the overlay's CPU and memory, the machine's CPU load and (when its id is known) the game's CPU. Called on the
/// recorder's timer thread only, every few seconds, so the <see cref="Process"/> refreshes it does are no concern.
/// </summary>
internal sealed class ResourceSampler : IDisposable
{
    private const double Mb = 1024.0 * 1024.0;

    private readonly Process _self = Process.GetCurrentProcess();
    private readonly Func<int?> _gamePid;
    private long _lastAt;
    private TimeSpan _lastCpu;
    private (long Idle, long Busy)? _lastSystem;
    private Process? _game;
    private TimeSpan _lastGameCpu;
    private long _lastGameAt;
    private int _deniedPid; // a game process we may not read: don't retry (and fail) every 5 s

    public ResourceSampler(Func<int?> gamePid) => _gamePid = gamePid;

    /// <summary>A sample; its CPU figures are null on the first call (they need a previous sample).</summary>
    public ResourceSample Sample()
    {
        var now = Stopwatch.GetTimestamp();
        _self.Refresh(); // working set / private bytes are cached by Process until refreshed
        var cpu = _self.TotalProcessorTime;
        double? processCore = null;
        if (_lastAt != 0) processCore = CpuMath.PercentOfCore(cpu - _lastCpu, Stopwatch.GetElapsedTime(_lastAt, now));
        _lastAt = now;
        _lastCpu = cpu;

        double? machine = null;
        if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var sys = (Idle: idle, Busy: kernel + user);
            if (_lastSystem is { } prev) machine = CpuMath.MachineLoad(sys.Idle - prev.Idle, sys.Busy - prev.Busy);
            _lastSystem = sys;
        }

        return new ResourceSample(processCore, machine, SampleGame(now),
            _self.WorkingSet64 / Mb, _self.PrivateMemorySize64 / Mb, GC.GetTotalMemory(false) / Mb);
    }

    private double? SampleGame(long now)
    {
        var pid = _gamePid() ?? 0;
        if (pid <= 0 || pid == Environment.ProcessId || pid == _deniedPid)
        {
            DropGame();
            return null;
        }
        try
        {
            if (_game is null || _game.Id != pid)
            {
                DropGame();
                _game = Process.GetProcessById(pid);
                _lastGameCpu = _game.TotalProcessorTime;
                _lastGameAt = now;
                return null; // first look: no delta yet
            }
            var cpu = _game.TotalProcessorTime;
            var result = CpuMath.PercentOfCore(cpu - _lastGameCpu, Stopwatch.GetElapsedTime(_lastGameAt, now));
            _lastGameCpu = cpu;
            _lastGameAt = now;
            return result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            _deniedPid = pid; // access denied (e.g. the game runs elevated): leave it out quietly
            DropGame();
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            DropGame(); // exited between the tracker's look and ours
            return null;
        }
    }

    private void DropGame()
    {
        _game?.Dispose();
        _game = null;
    }

    public void Dispose()
    {
        DropGame();
        _self.Dispose();
    }
}

/// <summary>CPU percentages from time deltas. Pure, for the tests.</summary>
internal static class CpuMath
{
    /// <summary>CPU time used over a wall-clock span, as % of one core (two busy cores = 200).</summary>
    public static double? PercentOfCore(TimeSpan cpu, TimeSpan wall)
        => wall > TimeSpan.Zero ? Math.Max(0, cpu.TotalMilliseconds / wall.TotalMilliseconds * 100) : null;

    /// <summary>Machine load % from GetSystemTimes deltas (kernel time includes idle, so busy = kernel + user − idle).</summary>
    public static double? MachineLoad(long idleDelta, long kernelPlusUserDelta)
        => kernelPlusUserDelta > 0 ? Math.Clamp((1 - (double)idleDelta / kernelPlusUserDelta) * 100, 0, 100) : null;
}
