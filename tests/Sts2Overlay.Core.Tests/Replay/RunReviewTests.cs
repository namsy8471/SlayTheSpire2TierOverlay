using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Replay;

namespace Sts2Overlay.Core.Tests.Replay;

public class RunReviewTests
{
    private static readonly Lazy<DataPack> Pack = new(() =>
        DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1")));

    public static IEnumerable<object[]> Runs => TestPaths.RunFileNames;

    [Theory]
    [MemberData(nameof(Runs))]
    public void Review_IsConsistentWithReplay(string file)
    {
        var run = RunHistoryReader.ReadFile(TestPaths.Run(file));
        var replay = new ReplayAnalyzer(Pack.Value).Replay(run);
        var review = RunReviewBuilder.FromReplay(replay, Pack.Value);

        Assert.Equal(replay.ComparableCount, review.Decisions);
        Assert.Equal(replay.AgreementCount, review.Agreements);
        Assert.InRange(review.BiggestDisagreements.Count, 0, RunReviewBuilder.MaxDisagreements);
        Assert.Equal(Math.Min(RunReviewBuilder.MaxDisagreements, review.Decisions - review.Agreements), review.BiggestDisagreements.Count);

        // Largest gap first, every gap equals the replay's regret, and each one really is a disagreement.
        var gaps = review.BiggestDisagreements.Select(d => d.Gap).ToArray();
        Assert.Equal(gaps.OrderByDescending(g => g), gaps);
        var regrets = replay.Decisions.Where(d => d.IsComparable && !d.IsAgreement).Select(d => d.Regret).OrderByDescending(r => r).Take(3).ToArray();
        Assert.Equal(regrets, gaps, (a, b) => Math.Abs(a - b) < 1e-6);
        foreach (var d in review.BiggestDisagreements)
        {
            Assert.False(d.EnginePick is { } e && d.UserPick is { } u && e == u);
            Assert.False(d.EnginePick is null && d.UserPick is null);
            Assert.NotEmpty(d.Offered);
        }

        Assert.Equal(run.Nodes.Count, review.FloorReached);
        Assert.Equal(review.Player.Deck.Count, review.Build.DeckSize);
        if (review.Build.CommittedAtFloor is { } floor)
        {
            Assert.NotNull(review.Build.Primary);
            Assert.True(review.Build.Primary!.IsCommitted);
            Assert.InRange(floor, 1, run.Nodes.Count);
        }
    }

    [Fact]
    public void Review_SomeFixtureFormsABuild()
    {
        var reviews = TestPaths.AllRuns.Select(p => RunReviewBuilder.Build(RunHistoryReader.ReadFile(p), Pack.Value)).ToArray();
        Assert.Contains(reviews, r => r.Build.Primary is not null);
        Assert.Contains(reviews, r => r.BiggestDisagreements.Count == RunReviewBuilder.MaxDisagreements);
    }
}
