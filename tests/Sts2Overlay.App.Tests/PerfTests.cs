using System.Globalization;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services.Perf;
using Xunit.Abstractions;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The always-on perf log (Services/Perf): the histogram behind every "p50 / p90 / p99", the summary lines, the
/// frame-to-paint line of a newly shown offer, and the game frame counter fed by WGC.
/// </summary>
public class PerfTests(ITestOutputHelper output)
{
    // ---- PerfHistogram ----

    [Fact]
    public void Histogram_count_mean_and_max_are_exact()
    {
        var h = new PerfHistogram();
        foreach (var ms in new[] { 1.0, 2.0, 3.0, 10.0 }) h.Record(ms);
        var s = h.Snapshot();
        Assert.Equal(4, s.Count);
        Assert.Equal(4.0, s.Mean, 9);
        Assert.Equal(10.0, s.Max);
    }

    [Fact]
    public void Percentiles_are_within_one_bucket_above_the_true_value()
    {
        var h = new PerfHistogram();
        for (var i = 1; i <= 1000; i++) h.Record(i); // 1..1000 ms: p50 = 500, p90 = 900, p99 = 990
        var s = h.Snapshot();
        AssertWithinBucket(500, s.P50);
        AssertWithinBucket(900, s.P90);
        AssertWithinBucket(990, s.P99);
        Assert.True(s.P99 <= s.Max);

        static void AssertWithinBucket(double truth, double reported)
        {
            Assert.InRange(reported, truth, truth * PerfHistogram.Growth);
        }
    }

    [Fact]
    public void A_percentile_is_never_reported_above_the_max()
    {
        var h = new PerfHistogram();
        h.Record(7.01); // the bucket's upper edge is above 7.01
        var s = h.Snapshot();
        Assert.Equal(7.01, s.P50);
        Assert.Equal(7.01, s.P99);
    }

    [Fact]
    public void An_empty_histogram_gives_an_empty_snapshot()
    {
        var s = new PerfHistogram().Snapshot();
        Assert.True(s.IsEmpty);
        Assert.Equal(PerfStats.Empty, s);
    }

    [Fact]
    public void Values_outside_the_range_land_in_the_end_buckets_but_keep_the_exact_max()
    {
        Assert.Equal(0, PerfHistogram.BucketOf(0.0001));
        Assert.Equal(0, PerfHistogram.BucketOf(0));
        Assert.Equal(PerfHistogram.BucketCount - 1, PerfHistogram.BucketOf(1e9));
        Assert.True(PerfHistogram.UpperEdge(PerfHistogram.BucketCount - 1) >= PerfHistogram.MaxMs);

        var h = new PerfHistogram();
        h.Record(0.0001);
        h.Record(-5); // treated as 0, never a negative bucket
        h.Record(1e9);
        var s = h.Snapshot();
        Assert.Equal(3, s.Count);
        Assert.Equal(1e9, s.Max);
        Assert.True(s.P50 <= PerfHistogram.UpperEdge(0));
        Assert.Equal(PerfHistogram.UpperEdge(PerfHistogram.BucketCount - 1), s.P99, 6);
    }

    [Fact]
    public void Snapshot_and_reset_starts_a_new_period()
    {
        var h = new PerfHistogram();
        h.Record(5);
        Assert.Equal(1, h.SnapshotAndReset().Count);
        Assert.True(h.Snapshot().IsEmpty);
        h.Record(9);
        h.Reset();
        Assert.True(h.Snapshot().IsEmpty);
    }

    // ---- summary lines ----

    private static PerfSummary Summary(FrameRateStats game, ResourceStats? resources = null) => new(
        300.2, null, new PerfCounts(912, 310, 590, 12), game,
        [
            (PerfStage.Capture, new PerfStats(912, 5.43, 5.1, 7.9, 14.2, 31.5)),
            (PerfStage.EventProbe, PerfStats.Empty),
            (PerfStage.Recognize, new PerfStats(310, 8.1, 7.4, 12.0, 31.0, 44.2)),
            (PerfStage.HoverPoll, new PerfStats(6000, 0.041, 0.036, 0.07, 0.2, 1.3)),
        ],
        resources);

    [Fact]
    public void Summary_has_a_header_and_one_line_per_stage_that_recorded_something()
    {
        var lines = PerfFormat.SummaryLines(Summary(new FrameRateStats(17940, 17900, 299.2, 41.3)));
        foreach (var l in lines) output.WriteLine(l);
        Assert.Equal("perf: summary 300 s | steps 912, recognised 310, unchanged 590, reused 12 | game 59.8 fps (worst gap 41 ms)", lines[0]);
        Assert.Equal("perf: Capture n=912 mean 5.4 p50 5.1 p90 7.9 p99 14.2 max 31.5 ms", lines[1]);
        Assert.Equal("perf: Recognize n=310 mean 8.1 p50 7.4 p90 12.0 p99 31.0 max 44.2 ms", lines[2]);
        Assert.Equal("perf: HoverPoll n=6000 mean 0.04 p50 0.04 p90 0.07 p99 0.20 max 1.3 ms", lines[3]);
        Assert.Equal(4, lines.Count); // EventProbe recorded nothing: no line
    }

    [Fact]
    public void Without_game_frames_the_fps_clause_is_left_out()
    {
        var header = PerfFormat.SummaryLines(Summary(default))[0];
        Assert.DoesNotContain("fps", header);
        Assert.EndsWith("reused 12", header);
    }

    [Fact]
    public void Hunt_clause_follows_the_counters_when_anything_hunted()
    {
        var summary = Summary(new FrameRateStats(17940, 17900, 299.2, 41.3)) with { Hunt = new HuntCounts(212, 10.63, 4, 1, 1) };
        var header = PerfFormat.SummaryLines(summary)[0];
        output.WriteLine(header);
        Assert.Equal(
            "perf: summary 300 s | steps 912, recognised 310, unchanged 590, reused 12 | hunt 212 steps in 10.6 s (found 4, hint left 1, ran out 1) | game 59.8 fps (worst gap 41 ms)",
            header);
        Assert.True(default(HuntCounts).IsEmpty);
        Assert.DoesNotContain("hunt", PerfFormat.SummaryLines(Summary(default))[0]); // nothing hunted: no clause
    }

    [Fact]
    public void Summary_and_resource_lines_use_dots_whatever_the_culture()
    {
        var resources = new ResourceStats(60, 1.84, 6.1, 12, 23.4, 71.0, 140.2, 180.9, 412.3, 440.0, 388.0, 96.4, 14, 3, 0);
        var saved = CultureInfo.CurrentCulture;
        try
        {
            // The test host runs in invariant-globalization mode (no "de-DE"), so a comma-decimal culture is made by hand.
            var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            comma.NumberFormat.NumberGroupSeparator = ".";
            CultureInfo.CurrentCulture = comma;
            Assert.Equal("5,4", 5.4.ToString("0.0")); // the culture does bite on plain formatting
            var lines = PerfFormat.SummaryLines(Summary(new FrameRateStats(100, 99, 1.65, 41.3), resources));
            foreach (var l in lines) output.WriteLine(l);
            Assert.Contains("game 60.0 fps", lines[0]);
            Assert.Contains("in 10.6 s", PerfFormat.HuntClause(new HuntCounts(1, 10.63, 0, 0, 0)));
            Assert.Contains("mean 5.4 p50", lines[1]);
            Assert.Equal(
                "perf: process cpu avg 1.8% of a core (max 6.1%) = 0.15% of 12 cores, machine load avg 23% (max 71%), game cpu avg 140% of a core (max 181%), ws 412 MB (max 440), private 388 MB, managed 96 MB, gc 14/3/0 (60 samples)",
                lines[^1]);
            Assert.DoesNotContain(lines, l => l.Contains("5,4") || l.Contains("1,8"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Resource_line_leaves_out_what_was_not_measured()
    {
        var line = PerfFormat.ResourceLine(new ResourceStats(3, 2, 3, 8, null, null, null, null, 300, 310, 280, 90, 1, 0, 0));
        Assert.DoesNotContain("machine", line);
        Assert.DoesNotContain("game", line);
    }

    [Fact]
    public void Cpu_math()
    {
        Assert.Equal(50, CpuMath.PercentOfCore(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1))!.Value, 9);
        Assert.Equal(200, CpuMath.PercentOfCore(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))!.Value, 9); // two busy cores
        Assert.Null(CpuMath.PercentOfCore(TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Assert.Equal(25, CpuMath.MachineLoad(idleDelta: 750, kernelPlusUserDelta: 1000)!.Value, 9);
        Assert.Null(CpuMath.MachineLoad(0, 0));
    }

    // ---- PerfRecorder end to end (no timer, lines into a list) ----

    [Fact]
    public void Recorder_writes_a_summary_of_the_period_and_then_starts_over()
    {
        var log = new List<string>();
        using (var perf = new PerfRecorder(sampleResources: false, log: log.Add))
        {
            perf.CountStep(reused: false);
            perf.CountStep(reused: true);
            perf.CountRecognised();
            perf.CountUnchanged();
            perf.Record(PerfStage.Recognize, 8.0);
            perf.Record(PerfStage.Recognize, 12.0);
            perf.RecordBetween(PerfStage.UiApply, 0, System.Diagnostics.Stopwatch.Frequency / 1000 * 3); // 3 ms
            var lines = perf.WriteSummary("test");
            foreach (var l in lines) output.WriteLine(l);
            Assert.Equal(3, lines.Count);
            Assert.Matches(@"^perf: summary \d+ s \(test\) \| steps 2, recognised 1, unchanged 1, reused 1$", lines[0]);
            Assert.StartsWith("perf: Recognize n=2 mean 10.0 ", lines[1]);
            Assert.StartsWith("perf: UiApply n=1 mean 3.0 ", lines[2]);
            Assert.Equal(lines, log);

            Assert.Empty(perf.WriteSummary()); // nothing since: nothing written
        }
        Assert.Equal(3, log.Count); // nothing new at Dispose either
    }

    [Fact]
    public void Recorder_counts_steps_taken_while_hunting_and_how_hunts_ended()
    {
        using var perf = new PerfRecorder(sampleResources: false, log: _ => { });
        perf.CountStep(reused: false); // before the hunt
        perf.SetHunting(true);
        perf.SetHunting(true); // repeated: no new stretch
        perf.CountStep(reused: false);
        perf.CountStep(reused: true);
        perf.SetHunting(false);
        perf.CountHuntEnd(HuntEnd.Found);
        perf.CountStep(reused: false); // after it
        perf.SetHunting(true); // a second hunt, still running at the summary
        perf.CountStep(reused: false);
        perf.CountHuntEnd(HuntEnd.RanOut);
        perf.CountHuntEnd(HuntEnd.Off); // not counted

        var lines = perf.WriteSummary("test");
        output.WriteLine(lines[0]);
        Assert.Matches(@"\| steps 5, recognised 0, unchanged 0, reused 1 \| hunt 3 steps in \d+\.\d s \(found 1, hint left 0, ran out 1\)$", lines[0]);

        // The running hunt goes on into the next period; the end counts start over.
        perf.CountStep(reused: false);
        Assert.Matches(@"\| hunt 1 steps in \d+\.\d s \(found 0, hint left 0, ran out 0\)$", perf.WriteSummary()[0]);
        perf.SetHunting(false);
        perf.CountStep(reused: false);
        Assert.DoesNotContain("hunt 1", perf.WriteSummary()[0]);
    }

    // ---- game frames through WGC ----

    [Fact]
    public void Frame_rate_counts_running_time_only_and_the_worst_gap()
    {
        var c = new FrameRateCounter();
        const double freq = 1000; // timestamps in ms
        for (long t = 1000; t <= 2000; t += 10) c.Arrived(t); // 101 frames, 100 gaps of 10 ms: 100 fps
        c.Arrived(2050); // one slow frame
        c.Break(); // session suspended for a minute
        c.Arrived(62_050);
        c.Arrived(62_060);
        var s = c.SnapshotAndReset(freq);
        Assert.Equal(104, s.Frames);
        Assert.Equal(102, s.Gaps);
        Assert.Equal(1.06, s.RunningSeconds, 9);
        Assert.Equal(50, s.WorstGapMs, 9);
        Assert.Equal(102 / 1.06, s.Fps!.Value, 9);

        var empty = c.SnapshotAndReset(freq);
        Assert.Equal(0, empty.Frames);
        Assert.Null(empty.Fps);
    }

    // ---- frame-to-paint line of a newly shown offer ----

    /// <summary>Timestamps in milliseconds (tick frequency 1000).</summary>
    private static readonly FrameTiming Frame = new(PreviousLook: 10_000, CaptureStart: 10_331, CaptureDone: 10_336, RecognizeStart: 10_338, RecognizeDone: 10_346);

    [Fact]
    public void Offer_latency_parts_add_up_to_the_total()
    {
        var l = OfferLatency.From(Frame, uiStart: 10_347, uiDone: 10_350, painted: 10_357, ScreenHint.RewardLikely, hintChangedAt: 9_819, 1000);
        Assert.Equal(26, l.TotalMs, 9);
        Assert.Equal(l.TotalMs, l.CaptureMs + l.AnalyseMs + l.RecogniseMs + l.QueueMs + l.ApplyMs + l.RenderMs, 9);
        Assert.Equal(10_357 - 10_331, l.TotalMs, 9);
        var line = l.ToLogLine(ScreenKind.Reward, ScreenHint.RewardLikely);
        output.WriteLine(line);
        Assert.Equal(
            "perf: Reward shown 26 ms after its frame (capture 5, probe+map 2, recognise 8, ui queue 1, apply 3, render 7); last look before that frame was 331 ms earlier; hint RewardLikely came 512 ms before the frame",
            line);
    }

    [Theory]
    [InlineData(ScreenHint.None, 9_819L)] // not an offer hint
    [InlineData(ScreenHint.Combat, 9_819L)]
    [InlineData(ScreenHint.ShopLikely, 0L)] // never changed
    [InlineData(ScreenHint.ShopLikely, 10_400L)] // came after the frame
    [InlineData(ScreenHint.RewardLikely, 10_331L - 30_001L)] // too long ago: about an earlier screen
    public void Hint_clause_is_left_out_unless_an_offer_hint_came_shortly_before_the_frame(ScreenHint hint, long hintAt)
    {
        var l = OfferLatency.From(Frame, 10_347, 10_350, 10_357, hint, hintAt, 1000);
        Assert.Null(l.HintLeadMs);
        Assert.DoesNotContain("hint", l.ToLogLine(ScreenKind.Shop, hint));
    }

    [Fact]
    public void Shop_hint_counts_and_the_first_look_has_no_earlier_one()
    {
        var first = Frame with { PreviousLook = 0 };
        var l = OfferLatency.From(first, 10_347, 10_350, 10_357, ScreenHint.ShopLikely, 10_000, 1000);
        var line = l.ToLogLine(ScreenKind.Shop, ScreenHint.ShopLikely);
        output.WriteLine(line);
        Assert.DoesNotContain("last look", line);
        Assert.EndsWith("; hint ShopLikely came 331 ms before the frame", line);
    }
}
