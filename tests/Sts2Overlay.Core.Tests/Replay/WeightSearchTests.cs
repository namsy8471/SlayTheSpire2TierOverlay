using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Replay.Validation;

namespace Sts2Overlay.Core.Tests.Replay;

/// <summary>Regularized weight search: copies weights safely, moves only for real gains, and learns from the train decisions only.</summary>
public class WeightSearchTests
{
    private static readonly Lazy<DataPack> Pack = new(() => DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "tests", "Sts2Overlay.Core.Tests", "TestData", "pack-defect")));

    [Fact]
    public void Clone_IsADeepCopyWithTheSameValues()
    {
        var original = new Weights();
        var copy = WeightSearch.Clone(original);

        Assert.Equal(JsonSerializer.Serialize(original, DataPackLoader.JsonOptions), JsonSerializer.Serialize(copy, DataPackLoader.JsonOptions));
        copy.Skip.T0[1] = 1;
        copy.TierValue[Tier.A] = 1;
        Assert.Equal(45, original.Skip.T0[1]);
        Assert.Equal(65, original.TierValue[Tier.A]);
    }

    [Fact]
    public void Build_AppliesKnobsToACopy()
    {
        var pack = Pack.Value;
        var knobs = ScoringKnobs.Default(pack.Weights);
        var search = new WeightSearch(pack, knobs, [], lambda: 0.01);
        var candidate = search.Current;
        candidate[knobs.ToList().FindIndex(k => k.Name == "wNeed")] = 0;
        candidate[knobs.ToList().FindIndex(k => k.Name.StartsWith("skip.t0", StringComparison.Ordinal))] = 1; // −5
        candidate[knobs.ToList().FindIndex(k => k.Name.StartsWith("tierSpread", StringComparison.Ordinal))] = 4; // ×2

        var w = search.Build(candidate);

        Assert.Equal(0, w.WNeed);
        Assert.Equal(pack.Weights.Skip.T0[2] - 5, w.Skip.T0[2]);
        Assert.Equal(50 + (pack.Weights.TierValue[Tier.D] - 50) * 2, w.TierValue[Tier.D]);
        Assert.Equal(pack.Weights.WNeed, search.Build(search.Current).WNeed);
        Assert.Equal((2 + 1 + 2) * 0.01, search.Penalty(candidate), 9); // 5 grid steps × λ
        Assert.Contains("wNeed", search.Describe(candidate), StringComparison.Ordinal);
        Assert.Equal("(변경 없음)", search.Describe(search.Current));
    }

    [Fact]
    public void Scaled_RequiresTheCurrentValueInTheGrid()
    {
        Assert.Throws<ArgumentException>(() => WeightKnob.Scaled("x", 1, [0.5, 2], (_, _) => { }));
        Assert.Equal(1, WeightKnob.Scaled("x", 10, [0.5, 1, 2], (_, _) => { }).CurrentIndex);
    }

    [Fact]
    public void Fit_WithAHugePenalty_KeepsTheCurrentWeights()
    {
        var (search, decisions) = Setup(lambda: 10);
        Assert.Equal(search.Current, search.Fit(decisions));
    }

    [Fact]
    public void Fit_DependsOnlyOnTheTrainDecisions()
    {
        var (_, decisions) = Setup(lambda: 0.002);
        var train = decisions.Take(decisions.Count / 2).ToArray();
        var narrow = new WeightSearch(Pack.Value, ScoringKnobs.Default(Pack.Value.Weights), train, 0.002);
        var wide = new WeightSearch(Pack.Value, ScoringKnobs.Default(Pack.Value.Weights), decisions, 0.002);

        // Whether the other half exists (and is cached) must not change what is learned from the first half.
        var a = narrow.Fit(train);
        var b = wide.Fit(train);
        Assert.Equal(a, b);
        Assert.True(wide.Objective(b, train) >= wide.Objective(wide.Current, train));
    }

    private static (WeightSearch Search, IReadOnlyList<LabeledDecision> Decisions) Setup(double lambda)
    {
        var files = TestPaths.AllRuns.Take(6).Select(p => (p, RunHistoryReader.ReadFile(p))).ToArray();
        var streams = ReplayStreams.Build(files, allPlayers: false, LocalPlayer.FixtureSelfId);
        var decisions = streams.SelectMany(LabeledDecision.Extract).ToArray();
        return (new WeightSearch(Pack.Value, ScoringKnobs.Default(Pack.Value.Weights), decisions, lambda), decisions);
    }
}
