using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Learning;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Learning;

public class RunBaselinePolicyTests
{
    private static readonly Lazy<DataPack> Pack = new(() => DataPackLoader.LoadDirectory(
        Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.111.0")));

    [Theory]
    [InlineData(0, false, true, true)]
    [InlineData(10, false, true, true)]
    [InlineData(9, false, true, false)]
    [InlineData(0, true, true, false)]
    [InlineData(10, false, false, false)]
    public void Scopes_reference_data_to_the_measured_runs(int ascension, bool coop, bool vitals, bool expected)
    {
        var run = new RunState { Character = "CHARACTER.IRONCLAD", Ascension = ascension, IsMultiplayer = coop, VitalsKnown = vitals };
        Assert.Equal(expected, RunBaselinePolicy.For(Pack.Value, run) is not null);
        Assert.Equal(expected, !ReferenceEquals(Pack.Value.ForRun(run), Pack.Value));
        Assert.Null(RunBaselinePolicy.For(Pack.Value, run with { Character = "CHARACTER.SILENT" }));
        Assert.Null(RunBaselinePolicy.For(Pack.Value, run with { SaveSchemaVersion = 16 }));
        Assert.Null(RunBaselinePolicy.For(Pack.Value, run with { IsStandardRun = false }));
        Assert.Equal(expected, RunBaselinePolicy.For(Pack.Value, run with { SaveSchemaVersion = 21 }) is not null);
    }

    [Fact]
    public void Reward_entry_point_reproduces_the_reference_fighter_starting_deck_scores()
    {
        var run = new RunState
        {
            Character = "CHARACTER.IRONCLAD", Act = 1, Floor = 2, Hp = 80, MaxHp = 80, Gold = 114,
            Deck = new[] { new DeckCard(new("CARD.BASH")) }
                .Concat(Enumerable.Repeat(new DeckCard(new("CARD.DEFEND_IRONCLAD")), 4))
                .Concat(Enumerable.Repeat(new DeckCard(new("CARD.STRIKE_IRONCLAD")), 5)).ToArray(),
            Relics = ["RELIC.BURNING_BLOOD", "RELIC.STONE_HUMIDIFIER"],
        };
        var offer = new RecommendationEngine(Pack.Value).EvaluateOffer(run, [new("CARD.TAUNT"), new("CARD.POMMEL_STRIKE"), new("CARD.RAGE")]);
        Assert.Equal("CARD.TAUNT", offer.Best?.Id);
        // The reference decision log prints scores rounded to six decimals.
        Assert.Equal(51.198918, offer.Cards[0].Total, 0.000001);
        Assert.Equal(45.803835, offer.Cards[1].Total, 0.000001);
        Assert.Equal(30.709212, offer.Cards[2].Total, 0.000001);
        Assert.Equal(16.293593, offer.Skip.Total, 0.000001);
    }

    [Fact]
    public void Legal_ties_keep_rule_then_offer_order()
    {
        var policy = new RunBaselinePolicy(new Dictionary<string, double>());
        var run = new RunState { Character = "CHARACTER.IRONCLAD" };
        BaselineCandidate[] options = [new("M"), new("R"), new("E", Legal: false)];
        Assert.Equal(1, policy.Pick("path", run, options, 1));
        Assert.Equal(0, policy.Pick("path", run, options, 2));
        Assert.Equal(-1, policy.Pick("path", run, [], 0));
    }

    [Fact]
    public void Event_rule_skips_locked_buttons_and_holds_the_repeat_guard_across_refreshes()
    {
        var scorer = new EventScorer(new DataPack(), EventCatalog.Empty);
        var run = new RunState { Character = "CHARACTER.IRONCLAD", Hp = 70, MaxHp = 80 };
        var state = new BaselineEventChoices();
        var ev = scorer.Evaluate(run, "EVENT.X", ["X_LOCKED", "X.LINGER", "X.EXIT"])!;
        Assert.Equal("X.LINGER", state.Apply(ev, 5, 70, null).Best?.Option.Key);
        Assert.Equal("X.EXIT", state.Apply(ev, 5, 60, "X.LINGER").Best?.Option.Key);
        Assert.Equal("X.EXIT", state.Apply(ev, 5, 60, "X.LINGER").Best?.Option.Key);
        Assert.Equal("X.LINGER", state.Apply(ev, 12, 60, null).Best?.Option.Key);
    }
}
