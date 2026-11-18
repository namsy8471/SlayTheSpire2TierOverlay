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
    [InlineData(1, false, true, true)]
    [InlineData(11, false, true, false)]
    [InlineData(0, true, true, true)] // co-op: the solo layers ("1인 기준") until a co-op mode is measured
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

    /// <summary>The four characters with A8-learned adjustments: their own pack (v0.107.1 tables, own card model) at A0
    /// to A10.</summary>
    [Theory]
    [InlineData("CHARACTER.DEFECT")]
    [InlineData("CHARACTER.SILENT")]
    [InlineData("CHARACTER.NECROBINDER")]
    [InlineData("CHARACTER.REGENT")]
    public void Characters_with_their_own_adjustments_use_them_at_the_measured_ascensions(string character)
    {
        var pack = Pack.Value;
        var run = new RunState { Character = character, VitalsKnown = true };
        foreach (var (asc, expected) in new[] { (0, true), (1, true), (8, true), (10, true), (11, false) })
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

    /// <summary>Slippery Bridge (11-18 bug): the learned adjustment liked Hold On, and the guard only watched the rule's
    /// button and the exact key (HOLD_ON_0, HOLD_ON_1 ...), so Hold On was recommended until death.</summary>
    [Fact]
    public void Slippery_bridge_guard_and_low_hp_floor_stop_hold_on()
    {
        var dir = Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.111.0");
        var scorer = new EventScorer(Pack.Value, EventCatalog.LoadDirectory(dir));
        var policy = RunBaselinePolicy.Load("""
            {"version":1,"weights":{"event:rule":0.001,"event:SLIPPERY_BRIDGE|OVERCOME":-0.01,"event:SLIPPERY_BRIDGE|HOLD_ON_0":0.01}}
            """);
        const string overcome = "SLIPPERY_BRIDGE.pages.INITIAL.options.OVERCOME";
        string Hold(int page) => page < 0 ? "SLIPPERY_BRIDGE.pages.INITIAL.options.HOLD_ON_0" : $"SLIPPERY_BRIDGE.pages.HOLD_ON_{page}.options.HOLD_ON_{page + 1}";
        var run = new RunState { Character = "CHARACTER.SILENT", Act = 2, Hp = 60, MaxHp = 70, Gold = 100, Floor = 20 };
        string? Pick(BaselineEventChoices state, int page, int hp, string? last)
        {
            var at = run with { Hp = hp };
            var ev = scorer.Evaluate(at, "EVENT.SLIPPERY_BRIDGE", [overcome, Hold(page)], page < 0 ? "INITIAL" : $"HOLD_ON_{page}")!;
            return state.Apply(ev, at.Floor, hp, last, policy, at, at.MaxHp).Best?.Option.Key;
        }
        var state = new BaselineEventChoices();
        Assert.Equal(Hold(-1), Pick(state, -1, 60, null));        // the adjustment may hold on once
        Assert.Equal(overcome, Pick(state, 0, 57, Hold(-1)));     // HP dropped: Hold On is the same button on the next page
        Assert.Equal(overcome, Pick(state, 0, 57, Hold(-1)));     // a refresh keeps the guard
        Assert.Equal(overcome, Pick(new BaselineEventChoices(), -1, 23, null)); // 23 - 3 < 30 % of 70: no paying HP
        Assert.Equal(Hold(-1), Pick(new BaselineEventChoices(), -1, 24, null)); // 24 - 3 = 21 = 30 % of 70: allowed
        Assert.Equal("HOLD_ON", BaselineEventChoices.Repeat("SLIPPERY_BRIDGE.pages.HOLD_ON_6.options.HOLD_ON_LOOP"));
    }

    /// <summary>The five v0.113 events added to events.json (11-18): their buttons line up and the simulator's learned
    /// event adjustment, not pick statistics, ranks them.</summary>
    [Theory]
    [InlineData("EVENT.MUSEUM_OF_TORTURE", "DISPLAY", 2)]
    [InlineData("EVENT.SHRINE_OF_THE_WARLORD", "INITIAL", 3)]
    [InlineData("EVENT.THERMAE_NONUPEIPE", "INITIAL", 3)]
    [InlineData("EVENT.WATERFALL_OF_OBVIOUS_SECRETS", "ENTER", 1)]
    [InlineData("EVENT.WUPPY_IN_TROUBLE", "INITIAL", 1)]
    public void V113_events_are_ranked_by_the_run_baseline(string eventId, string page, int act)
    {
        var catalog = EventCatalog.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.111.0"));
        var scorer = new EventScorer(Pack.Value, catalog) { UseLearnedModel = true };
        var run = new RunState { Character = "CHARACTER.NECROBINDER", Act = act, Hp = 50, MaxHp = 80, Gold = 200, Floor = 5 + 17 * act, VitalsKnown = true };
        var def = catalog.Find(eventId)!.FindPage(page)!;
        var keys = EventSlots.Keys(def, EventSlots.Displayed(def).Count)!;
        var ev = scorer.Evaluate(run, eventId, keys, page)!;
        var picked = new BaselineEventChoices().Apply(ev, run.Floor, run.Hp, null, RunBaselinePolicy.For(Pack.Value, run), run, run.MaxHp);
        Assert.True(picked.OptionsExact, eventId);
        Assert.True(picked.UsedRunBaseline, eventId);
        Assert.NotNull(picked.Best);
    }
}
