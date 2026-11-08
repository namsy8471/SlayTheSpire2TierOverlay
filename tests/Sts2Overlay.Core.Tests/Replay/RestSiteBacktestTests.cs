using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Replay;
using Sts2Overlay.Core.Replay.Validation;
using Sts2Overlay.Core.Rest;
using Sts2Overlay.Core.Tests.Data;

namespace Sts2Overlay.Core.Tests.Replay;

/// <summary>
/// The rest-site backtest on the anonymised fixture runs with the real v0.107.1 pack. Checks bookkeeping invariants,
/// not advice quality (agreement numbers are reported by <c>RunHistoryAnalyzer rest-advice</c>).
/// </summary>
public class RestSiteBacktestTests
{
    private static readonly Lazy<(IReadOnlyList<RestDecision> Decisions, RestBacktestSummary Summary)> Result = new(() =>
    {
        var pack = DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1"), RealCatalog.Instance);
        var files = TestPaths.AllRuns.Select(p => (p, RunHistoryReader.ReadFile(p))).ToArray();
        var streams = ReplayStreams.Build(files, allPlayers: true, LocalPlayer.FixtureSelfId);
        var decisions = RestSiteBacktest.Run(streams, pack);
        return (decisions, RestSiteBacktest.Summarize(decisions, pack.Weights.Path));
    });

    [Fact]
    public void Every_rest_site_visit_is_replayed_once()
    {
        var (decisions, summary) = Result.Value;

        Assert.NotEmpty(decisions);
        Assert.Equal(decisions.Count, summary.Visits);
        Assert.Equal(summary.Visits, summary.ChoiceKinds.Values.Sum());
        Assert.Equal(decisions.Count, decisions.Select(d => (d.RunKey, d.PersonId, d.Floor)).Distinct().Count());
        Assert.Contains("SMITH", summary.ChoiceKinds.Keys);
        Assert.Contains("HEAL", summary.ChoiceKinds.Keys);
    }

    [Fact]
    public void Only_single_heal_or_smith_visits_are_compared()
    {
        var (decisions, summary) = Result.Value;

        Assert.All(decisions, d => Assert.Equal(
            d.Choices.Contains("HEAL") != d.Choices.Contains("SMITH"), d.PlayerAction is not null));
        Assert.Equal(decisions.Count(d => d.PlayerAction is not null), summary.Decided);
        Assert.InRange(summary.Agreed, 0, summary.Decided);
        Assert.InRange(summary.SureAgreed, 0, summary.SureDecided);
    }

    [Fact]
    public void Every_smithed_card_is_found_among_the_candidates()
    {
        var (decisions, summary) = Result.Value;
        var smiths = decisions.Where(d => d.PlayerAction == RestAction.Smith).ToArray();

        Assert.NotEmpty(smiths);
        Assert.All(smiths, d => Assert.NotNull(d.UpgradedCard));
        Assert.Equal(0, summary.SmithCardMissing);
        Assert.All(smiths, d => Assert.InRange(d.UpgradeRank!.Value.Best, 1, d.Candidates));
        Assert.InRange(summary.Top1, 0, summary.Top3);
        Assert.InRange(summary.Top3, 0, summary.SmithWithCard);
    }

    [Fact]
    public void Advice_is_complete_for_every_visit()
    {
        var (decisions, _) = Result.Value;

        Assert.All(decisions, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Advice.Reason));
            if (d.Advice.HealScore is not null) Assert.NotNull(d.Advice.Outlook);
            Assert.All(d.Advice.Upgrades, c => Assert.Equal(0, c.Card.Upgrade)); // v0.107.1: every card upgrades once
        });
    }

    [Theory]
    [InlineData(1, 1, 1, 1.0)]     // alone in first place
    [InlineData(2, 1, 1, 0.0)]     // second place, no tie
    [InlineData(1, 4, 1, 0.25)]    // four cards share first place
    [InlineData(2, 4, 3, 0.5)]     // places 2–5 shared, top 3 takes two of them
    [InlineData(5, 2, 3, 0.0)]
    public void Tied_rank_splits_hits_evenly(int best, int tied, int k, double expected)
        => Assert.Equal(expected, new TiedRank(best, tied).HitWithin(k), 9);
}
