using System.Globalization;
using Sts2Overlay.App.Integration;

namespace Sts2Overlay.App.Services.Perf;

/// <summary>One summary period of <see cref="PerfRecorder"/>, ready to be formatted.</summary>
/// <param name="Reason">Shown after the length, e.g. "exit"; null for the periodic summary.</param>
/// <param name="Stages">Every stage, empty ones included (the formatter leaves those out).</param>
/// <param name="Resources">Null when no resource sample was taken in the period.</param>
public sealed record PerfSummary(
    double Seconds,
    string? Reason,
    PerfCounts Counts,
    FrameRateStats Game,
    IReadOnlyList<(PerfStage Stage, PerfStats Stats)> Stages,
    ResourceStats? Resources);

/// <summary>Worker counters of one period.</summary>
/// <param name="Steps">Worker steps that captured a frame for recognition (periodic or manual).</param>
/// <param name="Recognised">Steps that ran the recogniser.</param>
/// <param name="Unchanged">Steps that skipped it because the picture had not changed.</param>
/// <param name="Reused">Steps where WGC had no new game frame and the previous pixels were used again.</param>
public readonly record struct PerfCounts(long Steps, long Recognised, long Unchanged, long Reused)
{
    public bool IsEmpty => Steps == 0 && Recognised == 0 && Unchanged == 0 && Reused == 0;
}

/// <summary>Resource use over one period: averages and maxima of the 5 s samples.</summary>
/// <param name="ProcessCoreAvg">Overlay process CPU, % of ONE core (100 = one core busy).</param>
/// <param name="Cores">Logical processors, to turn a %-of-a-core into a %-of-the-machine.</param>
/// <param name="MachineLoadAvg">Whole machine CPU load %, null when GetSystemTimes failed.</param>
/// <param name="GameCoreAvg">The game process's CPU, % of one core; null when unknown (no game, access denied).</param>
public sealed record ResourceStats(
    int Samples,
    double ProcessCoreAvg,
    double ProcessCoreMax,
    int Cores,
    double? MachineLoadAvg,
    double? MachineLoadMax,
    double? GameCoreAvg,
    double? GameCoreMax,
    double WorkingSetMbAvg,
    double WorkingSetMbMax,
    double PrivateMbAvg,
    double ManagedMbAvg,
    int Gc0,
    int Gc1,
    int Gc2);

/// <summary>
/// The <c>perf:</c> lines of app.log. Pure (snapshots in, strings out) so the format is unit-tested; invariant culture
/// so a log from a comma-decimal Windows reads the same.
/// </summary>
public static class PerfFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>One header line, one line per stage that recorded something, then the resource line.</summary>
    public static IReadOnlyList<string> SummaryLines(PerfSummary s)
    {
        var lines = new List<string>();
        var c = s.Counts;
        var header = string.Create(Inv,
            $"perf: summary {s.Seconds:0} s{(s.Reason is null ? "" : $" ({s.Reason})")} | steps {c.Steps}, recognised {c.Recognised}, unchanged {c.Unchanged}, reused {c.Reused}");
        if (s.Game.Fps is { } fps) header += string.Create(Inv, $" | game {fps:0.0} fps (worst gap {s.Game.WorstGapMs:0} ms)");
        lines.Add(header);
        foreach (var (stage, st) in s.Stages)
        {
            if (st.IsEmpty) continue;
            lines.Add(string.Create(Inv,
                $"perf: {stage} n={st.Count} mean {Ms(st.Mean)} p50 {Ms(st.P50)} p90 {Ms(st.P90)} p99 {Ms(st.P99)} max {Ms(st.Max)} ms"));
        }
        if (s.Resources is { } r) lines.Add(ResourceLine(r));
        return lines;
    }

    internal static string ResourceLine(ResourceStats r)
    {
        var machineShare = r.Cores > 0 ? r.ProcessCoreAvg / r.Cores : 0;
        var line = string.Create(Inv,
            $"perf: process cpu avg {r.ProcessCoreAvg:0.0}% of a core (max {r.ProcessCoreMax:0.0}%) = {machineShare:0.00}% of {r.Cores} cores");
        if (r.MachineLoadAvg is { } load) line += string.Create(Inv, $", machine load avg {load:0}% (max {r.MachineLoadMax ?? load:0}%)");
        if (r.GameCoreAvg is { } game) line += string.Create(Inv, $", game cpu avg {game:0}% of a core (max {r.GameCoreMax ?? game:0}%)");
        line += string.Create(Inv,
            $", ws {r.WorkingSetMbAvg:0} MB (max {r.WorkingSetMbMax:0}), private {r.PrivateMbAvg:0} MB, managed {r.ManagedMbAvg:0} MB, gc {r.Gc0}/{r.Gc1}/{r.Gc2} ({r.Samples} samples)");
        return line;
    }

    /// <summary>Milliseconds with the precision that matters at that size: 0.05, 7.4, 312.</summary>
    internal static string Ms(double ms) => ms.ToString(ms < 1 ? "0.00" : ms < 100 ? "0.0" : "0", Inv);
}

/// <summary>
/// Where the time went between the frame that first showed a reward / shop screen and its badges on screen.
/// The six parts add up to <see cref="TotalMs"/> exactly (consecutive timestamps).
///
/// "Painted" means the UI thread finished layout and render for the change (a <c>DispatcherPriority.Loaded</c> callback
/// runs after the Render-priority work); DWM composition adds up to one more display frame, which is not measured.
/// </summary>
public readonly record struct OfferLatency(
    double CaptureMs,
    double AnalyseMs,
    double RecogniseMs,
    double QueueMs,
    double ApplyMs,
    double RenderMs,
    double? LookGapMs,
    double? HintLeadMs)
{
    /// <summary>Hints older than this are about some earlier screen, not this one.</summary>
    public static readonly TimeSpan HintWindow = TimeSpan.FromSeconds(30);

    public double TotalMs => CaptureMs + AnalyseMs + RecogniseMs + QueueMs + ApplyMs + RenderMs;

    /// <param name="frame">The worker's stamps for the frame (<see cref="RecognitionResult.Timing"/>).</param>
    /// <param name="uiStart">The UI thread started applying the result.</param>
    /// <param name="uiDone">It finished (badges laid out in WPF terms, not yet rendered).</param>
    /// <param name="painted">The Loaded-priority callback ran.</param>
    /// <param name="hint">The tracker's screen hint when the result was applied.</param>
    /// <param name="hintChangedAt">When the hint last changed (0 = never).</param>
    public static OfferLatency From(FrameTiming frame, long uiStart, long uiDone, long painted, ScreenHint hint, long hintChangedAt, double tickFrequency)
    {
        double Ms(long from, long to) => (to - from) * 1000.0 / tickFrequency;
        double? look = frame.PreviousLook != 0 ? Ms(frame.PreviousLook, frame.CaptureStart) : null;
        double? lead = null;
        if (hint is ScreenHint.RewardLikely or ScreenHint.ShopLikely && hintChangedAt != 0)
        {
            var ms = Ms(hintChangedAt, frame.CaptureStart);
            if (ms > 0 && ms < HintWindow.TotalMilliseconds) lead = ms;
        }
        return new OfferLatency(
            Ms(frame.CaptureStart, frame.CaptureDone),
            Ms(frame.CaptureDone, frame.RecognizeStart),
            Ms(frame.RecognizeStart, frame.RecognizeDone),
            Ms(frame.RecognizeDone, uiStart),
            Ms(uiStart, uiDone),
            Ms(uiDone, painted),
            look,
            lead);
    }

    /// <summary>The one app.log line for a newly shown offer screen.</summary>
    /// <param name="kind">The screen (Reward, Shop, Choice, Grid).</param>
    /// <param name="hint">Named in the hint clause, which is only there when <see cref="HintLeadMs"/> is.</param>
    public string ToLogLine(ScreenKind kind, ScreenHint hint)
    {
        var inv = CultureInfo.InvariantCulture;
        var line = string.Create(inv,
            $"perf: {kind} shown {TotalMs:0} ms after its frame (capture {CaptureMs:0}, probe+map {AnalyseMs:0}, recognise {RecogniseMs:0}, ui queue {QueueMs:0}, apply {ApplyMs:0}, render {RenderMs:0})");
        if (LookGapMs is { } look) line += string.Create(inv, $"; last look before that frame was {look:0} ms earlier");
        if (HintLeadMs is { } lead) line += string.Create(inv, $"; hint {hint} came {lead:0} ms before the frame");
        return line;
    }
}
