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
        Assert.Null(RunBaselinePolicy.For(Pack.Value, run with { Character = "CHARACTER.FOOL" }));
        Assert.Null(RunBaselinePolicy.For(Pack.Value, run with { SaveSchemaVersion = 16 }));
        Assert.Null(RunBaselinePolicy.For(Pack.Value, run with { IsStandardRun = false }));
        Assert.Equal(expected, RunBaselinePolicy.For(Pack.Value, run with { SaveSchemaVersion = 21 }) is not null);
    }

    /// <summary>The four characters with A8-learned adjustments: their own pack (v0.107.1 tables, own card model) at A0,
    /// A8 and A10; Ironclad's adopted policy stays A0/A10.</summary>
    [Theory]
    [InlineData("CHARACTER.DEFECT")]
    [InlineData("CHARACTER.SILENT")]
    [InlineData("CHARACTER.NECROBINDER")]
    [InlineData("CHARACTER.REGENT")]
    public void Characters_with_their_own_adjustments_use_them_at_the_measured_ascensions(string character)
    {
        var pack = Pack.Value;
        var run = new RunState { Character = character, VitalsKnown = true };
        foreach (var (asc, expected) in new[] { (0, true), (8, true), (10, true), (5, false) })
        {
            var policy = RunBaselinePolicy.For(pack, run with { Ascension = asc });
            Assert.True(expected == policy is not null, $"{character} A{asc}");
            if (!expected) continue;
            Assert.True(policy!.HasEventWeights, character);
            var own = pack.ForRun(run with { Ascension = asc });
            Assert.NotSame(pack.ForRun(run with { Character = "CHARACTER.IRONCLAD", Ascension = 0 }), own);
            Assert.Same(own.CardPolicyFor(character), own.CardPolicy);
            Assert.Equal(pack.CardPolicyFor(character)!.PointUtility, own.CardPolicy!.PointUtility);
        }
        Assert.Null(RunBaselinePolicy.For(pack, run with { Character = "CHARACTER.IRONCLAD", Ascension = 8 }));
        // Defect's A0 file (rest/shop learned at A0) replaces its A8 file at A0 only.
        Assert.Equal(character == "CHARACTER.DEFECT", !ReferenceEquals(RunBaselinePolicy.For(pack, run with { Ascension = 0 }),
            RunBaselinePolicy.For(pack, run with { Ascension = 10 })));
    }

    /// <summary>Event keys as sts2-agent runs/adjust.rs builds them; an exact tie keeps the rule's button.</summary>
    [Fact]
    public void Event_adjustment_reads_the_simulator_keys_and_ties_keep_the_rule()
    {
        var policy = RunBaselinePolicy.Load("""
            {"version":1,"weights":{"event:rule":0.1,"event:TRIAL|NOBLE_GUILTY|h4":0.05,"event:TRIAL|NOBLE_GUILTY|g1":0.04,
             "event:TRIAL|NOBLE_GUILTY|a2":0.02,"event:NEOW|LostCoffer|silent":0.3,"event:relic|LOST_COFFER":-0.1}}
            """);
        var run = new RunState { Character = "CHARACTER.SILENT", Act = 2, Hp = 70, MaxHp = 70, Gold = 100 };
        EventCandidate[] trial = [new("NOBLE_INNOCENT", null, true), new("NOBLE_GUILTY", null, true), new("LEAVE", null, true)];
        Assert.Equal(1, policy.PickEvent(run, "EVENT.TRIAL", trial, 0));             // 0.11 > 0.1
        Assert.Equal(0, policy.PickEvent(run with { Gold = 10 }, "EVENT.TRIAL", trial, 0)); // 0.07 < 0.1
        Assert.Equal(1, policy.PickEvent(run, "EVENT.TRIAL", [trial[0], trial[1] with { Legal = false }, trial[1]], 1)); // illegal rule: kept
        EventCandidate[] neow = [new("Pomander", "POMANDER", true), new("LostCoffer", "LOST_COFFER", true)];
        Assert.Equal(1, policy.PickEvent(run with { Act = 1 }, "EVENT.NEOW", neow, 0)); // 0.3 - 0.1 > 0.1
        Assert.Equal(0, policy.PickEvent(run with { Act = 1, Character = "CHARACTER.DEFECT" }, "EVENT.NEOW", neow, 0));
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
