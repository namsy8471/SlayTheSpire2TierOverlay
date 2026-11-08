using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Integration;

/// <summary>
/// End-to-end: real .run fixtures → deck timeline → decisions → engine → replay report.
/// The test pack has placeholder values, so these tests check invariants, not advice quality.
/// </summary>
public class ReplayIntegrationTests
{
    private static readonly Lazy<DataPack> Pack = new(() => DataPackLoader.LoadDirectory(PackDir));

    private static string PackDir => Path.Combine(TestPaths.RepoRoot, "tests", "Sts2Overlay.Core.Tests", "TestData", "pack-defect");

    public static IEnumerable<object[]> Runs => TestPaths.RunFileNames;

    [Fact]
    public void TestPack_LoadsAndValidatesWithoutErrors()
    {
        var issues = DataPackValidator.Validate(Pack.Value);
        Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);
        Assert.NotNull(Pack.Value.FindTier("CHARACTER.DEFECT", "CARD.GLACIER"));
        Assert.Equal(2, Pack.Value.ArchetypesFor("CHARACTER.DEFECT").Count());
        Assert.Equal(2, Pack.Value.CombosFor("CHARACTER.DEFECT").Count());
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public void Replay_EveryFixture_ProducesConsistentEvaluations(string file)
    {
        var run = RunHistoryReader.ReadFile(TestPaths.Run(file));
        var replay = new ReplayAnalyzer(Pack.Value).Replay(run);

        Assert.NotEmpty(replay.Decisions);
        foreach (var d in replay.Decisions)
        {
            if (d.Offer is { } offer)
            {
                Assert.Equal(d.Decision.Offered.Count, offer.Cards.Count);
                AssertFinite(offer.Skip);
                foreach (var c in offer.Cards)
                {
                    AssertFinite(c.Score);
                    Assert.Equal(c.Score.Components.Sum(x => x.Value), c.Total, 9);
                    Assert.InRange(c.Rank, 1, offer.Cards.Count);
                    Assert.True(c.Chips.Count <= 3);
                }
                Assert.Equal(offer.SkipRecommended, offer.Best is null);
                Assert.True(offer.Cards.Count(c => c.IsBest) == (offer.SkipRecommended ? 0 : 1));
                Assert.True(d.Regret >= 0);
            }
            else
            {
                Assert.NotNull(d.Shop);
                Assert.Equal(ReplayVerdict.Informational, d.Verdict);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public void Replay_FriendPlayer_AlsoWorksInCoop(string file)
    {
        var run = RunHistoryReader.ReadFile(TestPaths.Run(file));
        if (!run.IsMultiplayer) return;

        var friend = run.Players.Single(p => p.Id != LocalPlayer.FixtureSelfId);
        var replay = new ReplayAnalyzer(Pack.Value).Replay(run, friend.Id);
        Assert.Equal(friend.Id, replay.Player.Id);
        Assert.All(replay.Decisions, d => Assert.Equal(friend.Character, d.Decision.StateBefore.Character));
    }

    [Fact]
    public void Replay_ReportCoversAllRuns()
    {
        var analyzer = new ReplayAnalyzer(Pack.Value);
        var replays = TestPaths.AllRuns.Select(RunHistoryReader.ReadFile).Select(r => analyzer.Replay(r)).ToArray();

        var md = ReplayReport.ToMarkdown(replays);
        Assert.Contains("# 런 기록 리플레이 리포트", md);
        foreach (var r in replays)
        {
            Assert.Contains(r.Run.StartTime.ToString(System.Globalization.CultureInfo.InvariantCulture), md);
        }

        // The user's real picks give the engine a non-trivial number of comparable decisions.
        Assert.True(replays.Sum(r => r.ComparableCount) > 300);
    }

    [Fact]
    public void Replay_DefectRuns_UseTheDefectPack()
    {
        var analyzer = new ReplayAnalyzer(Pack.Value);
        var defect = TestPaths.AllRuns.Select(RunHistoryReader.ReadFile)
            .Where(r => LocalPlayer.Resolve(r).Character == "CHARACTER.DEFECT")
            .Select(r => analyzer.Replay(r))
            .ToArray();

        Assert.Equal(17, defect.Length);
        var rated = defect.SelectMany(r => r.Decisions).Where(d => d.Offer is not null)
            .SelectMany(d => d.Offer!.Cards).Count(c => c.IsRated);
        Assert.True(rated > 100, $"expected many rated Defect offers, got {rated}");

        // Archetype detection runs on reconstructed decks: some state must show frost commitment.
        var commits = defect.SelectMany(r => r.Decisions)
            .Where(d => d.Offer?.Profile is not null)
            .Select(d => d.Offer!.Profile!.MaxCommit);
        Assert.Contains(commits, c => c > 0);
    }

    [Fact]
    public void Engine_OnReconstructedStates_NeverRecommendsLowConfidenceCard()
    {
        var engine = new RecommendationEngine(Pack.Value);
        var state = new RunState
        {
            Character = "CHARACTER.DEFECT",
            Act = 1,
            Floor = 3,
            Hp = 70,
            MaxHp = 75,
            Deck = Starter(),
        };

        // CARD.STORM has confidence 0.4 in the test pack: it may score high but cannot be "best".
        var offer = engine.EvaluateOffer(state, [new CardRef("CARD.STORM"), new CardRef("CARD.STRIKE_DEFECT"), new CardRef("CARD.DEFEND_DEFECT")]);
        Assert.NotEqual("CARD.STORM", offer.Best?.Id);
    }

    [Fact]
    public void PersonalStats_MatchFixtureFacts()
    {
        var stats = PersonalStats.Compute(TestPaths.AllRuns.Select(RunHistoryReader.ReadFile));
        Assert.Equal(24, stats.Runs);
        Assert.Equal(20, stats.MultiplayerRuns);
        var defect = stats.Characters.Single(c => c.Character == "CHARACTER.DEFECT");
        Assert.Equal(17, defect.Runs);
        Assert.Equal("CHARACTER.DEFECT", stats.Characters[0].Character);
        Assert.All(stats.Cards, c => Assert.InRange(c.Picked, 0, c.Offered));
    }

    private static IReadOnlyList<DeckCard> Starter()
    {
        var deck = new List<DeckCard>();
        for (var i = 0; i < 4; i++) deck.Add(new DeckCard(new CardRef("CARD.STRIKE_DEFECT"), 1));
        for (var i = 0; i < 4; i++) deck.Add(new DeckCard(new CardRef("CARD.DEFEND_DEFECT"), 1));
        deck.Add(new DeckCard(new CardRef("CARD.ZAP"), 1));
        deck.Add(new DeckCard(new CardRef("CARD.DUALCAST"), 1));
        return deck;
    }

    private static void AssertFinite(ScoreBreakdown s)
    {
        Assert.False(double.IsNaN(s.Total) || double.IsInfinity(s.Total), s.ToString());
        Assert.All(s.Components, c => Assert.False(double.IsNaN(c.Value) || double.IsInfinity(c.Value), c.Key));
    }
}
