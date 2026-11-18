using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.Events;

public class EventScorerTests
{
    private const string Ironclad = "CHARACTER.IRONCLAD";

    // A tiny catalog in the events.json format (numbers mirror ABYSSAL_BATHS / a remove-or-gold event).
    private const string Json = """
    {
      "events": [
        {
          "id": "EVENT.TEST_BATHS",
          "vars": { "Damage": 3, "MaxHp": 2, "Heal": 10 },
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "TEST_BATHS.pages.INITIAL.options.IMMERSE", "effects": [ { "type": "maxHp", "amount": "var.MaxHp" }, { "type": "hpLoss", "amount": "Damage" } ], "next": "ALL" },
              { "key": "TEST_BATHS.pages.INITIAL.options.ABSTAIN", "effects": [ { "type": "heal", "amount": "Heal" } ] }
            ]},
            { "page": "ALL", "options": [
              { "key": "TEST_BATHS.pages.ALL.options.LINGER", "effects": [ { "type": "maxHp", "amount": 2 }, { "type": "hpLoss", "amount": 4 } ], "next": "ALL" },
              { "key": "TEST_BATHS.pages.ALL.options.EXIT", "effects": [], "leave": true }
            ]}
          ]
        },
        {
          "id": "EVENT.TEST_SHRINE",
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "TEST_SHRINE.pages.INITIAL.options.PURGE", "effects": [ { "type": "cardRemove", "count": 1, "how": "choose" } ] },
              { "key": "TEST_SHRINE.pages.INITIAL.options.PAY", "effects": [ { "type": "gold", "amount": -100 }, { "type": "relic", "tier": "rare" } ] },
              { "key": "TEST_SHRINE.pages.INITIAL.options.BLEED", "effects": [ { "type": "hpLoss", "amount": "floor(0.5*maxHp)" }, { "type": "gold", "amount": 200 } ] },
              { "key": "TEST_SHRINE.pages.INITIAL.options.LEAVE", "effects": [], "leave": true }
            ]}
          ]
        }
      ]
    }
    """;

    private static readonly EventCatalog Catalog = EventCatalog.Parse(Json);
    private static readonly EventScorer Scorer = new(new DataPack(), Catalog);

    private static RunState State(int hp, int maxHp = 80, int gold = 99, int strikes = 5, int curses = 0)
        => new()
        {
            Character = Ironclad,
            Hp = hp,
            MaxHp = maxHp,
            Gold = gold,
            Deck = Enumerable.Repeat(new DeckCard(new CardRef("CARD.STRIKE_IRONCLAD")), strikes)
                .Concat(Enumerable.Repeat(new DeckCard(new CardRef("CARD.DEFEND_IRONCLAD")), 4))
                .Concat(Enumerable.Repeat(new DeckCard(new CardRef("CARD.DOUBT")), curses))
                .ToArray(),
        };

    [Fact]
    public void Formula_EvaluatesFunctionsAndVariables()
    {
        var vars = new Dictionary<string, double> { ["maxHp"] = 81, ["gold"] = 40 };
        Assert.True(EventFormula.TryEvaluate("floor(0.25*maxHp)", vars, out var a));
        Assert.Equal(20, a);
        Assert.True(EventFormula.TryEvaluate("-gold", vars, out var b));
        Assert.Equal(-40, b);
        Assert.True(EventFormula.TryEvaluate("max(5, min(3, 9)) + 2*(1+1)", vars, out var c));
        Assert.Equal(9, c);
        Assert.False(EventFormula.TryEvaluate("rng.NextInt(1,50)", vars, out _));
        Assert.False(EventFormula.TryEvaluate("3 +", vars, out _));
    }

    [Fact]
    public void Heal_IsWorthNothingAtFullHp_AndTheBathWinsThere()
    {
        var full = Scorer.Evaluate(State(80), "EVENT.TEST_BATHS")!;
        var abstain = full.Options.Single(o => o.Option.ShortName == "ABSTAIN");
        Assert.Equal(0, abstain.Total, 6);
        Assert.Equal("IMMERSE", full.Best!.Option.ShortName);
    }

    [Fact]
    public void Heal_WinsAtLowHp()
    {
        var low = Scorer.Evaluate(State(20), "EVENT.TEST_BATHS")!;
        Assert.Equal("ABSTAIN", low.Best!.Option.ShortName);
    }

    [Fact]
    public void HpLoss_CostsMoreAtLowHp()
    {
        var high = Scorer.Evaluate(State(80, gold: 0), "EVENT.TEST_SHRINE")!.Options.Single(o => o.Option.ShortName == "BLEED");
        var low = Scorer.Evaluate(State(50, gold: 0), "EVENT.TEST_SHRINE")!.Options.Single(o => o.Option.ShortName == "BLEED");
        Assert.True(low.Score.ValueOf(EventComponentKeys.Hp) < high.Score.ValueOf(EventComponentKeys.Hp));
    }

    [Fact]
    public void LethalOption_IsFlaggedAndNeverBest()
    {
        var eval = Scorer.Evaluate(State(30, gold: 0), "EVENT.TEST_SHRINE")!; // BLEED = 40 damage
        var bleed = eval.Options.Single(o => o.Option.ShortName == "BLEED");
        Assert.True(bleed.IsLethal);
        Assert.False(bleed.IsBest);
    }

    [Fact]
    public void UnaffordableOption_IsFlaggedAndNeverBest()
    {
        var eval = Scorer.Evaluate(State(80, gold: 50), "EVENT.TEST_SHRINE")!;
        var pay = eval.Options.Single(o => o.Option.ShortName == "PAY");
        Assert.True(pay.IsUnaffordable);
        Assert.False(pay.IsBest);
    }

    [Fact]
    public void Removal_IsWorthMoreWithACurse()
    {
        var shop = new Weights().Shop;
        var clean = Scorer.Evaluate(State(80), "EVENT.TEST_SHRINE")!.Options.Single(o => o.Option.ShortName == "PURGE");
        Assert.Equal(shop.BasicRemovalValue, clean.Total, 6);
        var noBasics = Scorer.Evaluate(State(80, strikes: 0) with { Deck = [new DeckCard(new CardRef("CARD.BASH"))] }, "EVENT.TEST_SHRINE")!
            .Options.Single(o => o.Option.ShortName == "PURGE");
        Assert.True(noBasics.Total < clean.Total);
    }

    [Fact]
    public void NextPage_AddsTheBestFollowUp()
    {
        var eval = Scorer.Evaluate(State(80), "EVENT.TEST_BATHS")!;
        var immerse = eval.Options.Single(o => o.Option.ShortName == "IMMERSE");
        Assert.True(immerse.Score.ValueOf(EventComponentKeys.Next) >= 0); // EXIT (0) is always available
        Assert.Equal(immerse.Score.Components.Sum(c => c.Value), immerse.Total, 9);
    }

    [Fact]
    public void OfferedKeys_LimitAndOrderTheOptions()
    {
        var keys = new[] { "TEST_SHRINE.pages.INITIAL.options.LEAVE", "TEST_SHRINE.pages.INITIAL.options.PURGE" };
        var eval = Scorer.Evaluate(State(80), "EVENT.TEST_SHRINE", keys)!;
        Assert.True(eval.OptionsExact);
        Assert.Equal(["LEAVE", "PURGE"], eval.Options.Select(o => o.Option.ShortName));
        Assert.Equal("PURGE", eval.Best!.Option.ShortName);
    }

    // An ancient (random draw from a pool of options): gold amounts give a clear order A > B > C > D > E.
    private static readonly EventCatalog AncientCatalog = EventCatalog.Parse("""
    {
      "events": [
        {
          "id": "EVENT.TEST_ANCIENT", "kind": "ancient",
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "TEST_ANCIENT.pages.INITIAL.options.A", "effects": [ { "type": "gold", "amount": 250 } ] },
              { "key": "TEST_ANCIENT.pages.INITIAL.options.B", "effects": [ { "type": "gold", "amount": 200 } ] },
              { "key": "TEST_ANCIENT.pages.INITIAL.options.C", "effects": [ { "type": "gold", "amount": 150 } ] },
              { "key": "TEST_ANCIENT.pages.INITIAL.options.D", "effects": [ { "type": "gold", "amount": 100 } ] },
              { "key": "TEST_ANCIENT.pages.INITIAL.options.E", "effects": [ { "type": "gold", "amount": 50 } ] }
            ]}
          ]
        }
      ]
    }
    """);

    [Fact]
    public void Ancient_RanksOnlyTheOfferedOptions_AndListsTheRestBelow()
    {
        var scorer = new EventScorer(new DataPack(), AncientCatalog);
        var keys = new[] { "TEST_ANCIENT.pages.INITIAL.options.D", "TEST_ANCIENT.pages.INITIAL.options.B", "TEST_ANCIENT.pages.INITIAL.options.E" };
        var eval = scorer.Evaluate(State(80), "EVENT.TEST_ANCIENT", keys)!;

        // The offered options keep their button order and are ranked among themselves: B is the best of them.
        Assert.Equal(["D", "B", "E"], eval.Options.Select(o => o.Option.ShortName));
        Assert.Equal([2, 1, 3], eval.Options.Select(o => o.Rank));
        Assert.Equal("B", eval.Best!.Option.ShortName);

        // The options not drawn this time: ranked among themselves, best first, never the recommendation.
        Assert.Equal(["A", "C"], eval.NotOffered.Select(o => o.Option.ShortName));
        Assert.Equal([1, 2], eval.NotOffered.Select(o => o.Rank));
        Assert.DoesNotContain(eval.NotOffered, o => o.IsBest);
        Assert.True(eval.NotOffered[0].Total > eval.Best!.Total); // A outscores B but isn't on offer
    }

    [Fact]
    public void Ancient_PossibleKeys_DropOptionsThisRunCannotDraw()
    {
        var scorer = new EventScorer(new DataPack(), AncientCatalog);
        var keys = new[] { "TEST_ANCIENT.pages.INITIAL.options.D", "TEST_ANCIENT.pages.INITIAL.options.B", "TEST_ANCIENT.pages.INITIAL.options.E" };
        var possible = keys.Append("TEST_ANCIENT.pages.INITIAL.options.C").ToArray(); // A can't appear in this run
        var eval = scorer.Evaluate(State(80), "EVENT.TEST_ANCIENT", keys, possibleKeys: possible)!;
        Assert.Equal(["C"], eval.NotOffered.Select(o => o.Option.ShortName));
        Assert.Equal(1, eval.NotOffered[0].Rank);
    }

    [Fact]
    public void Ancient_WithoutOfferedKeys_ScoresTheWholePool_AndNothingBelow()
    {
        var scorer = new EventScorer(new DataPack(), AncientCatalog);
        var eval = scorer.Evaluate(State(80), "EVENT.TEST_ANCIENT")!;
        Assert.Equal(5, eval.Options.Count);
        Assert.Empty(eval.NotOffered);
    }

    [Fact]
    public void RegularEvent_HasNoNotOfferedBlock()
    {
        var keys = new[] { "TEST_SHRINE.pages.INITIAL.options.LEAVE", "TEST_SHRINE.pages.INITIAL.options.PURGE" };
        Assert.Empty(Scorer.Evaluate(State(80), "EVENT.TEST_SHRINE", keys)!.NotOffered);
    }

    [Fact]
    public void UnknownKey_IsScoredAsUnmodelled()
    {
        var eval = Scorer.Evaluate(State(80), "EVENT.NOT_IN_DATA", ["NOT_IN_DATA.pages.INITIAL.options.X"])!;
        Assert.True(eval.Options[0].HasUnmodelled);
        Assert.Null(Scorer.Evaluate(State(80), "EVENT.NOT_IN_DATA"));
    }

    [Fact]
    public void PageOfKey()
    {
        Assert.Equal("INITIAL", EventScorer.PageOfKey("NEOW.pages.INITIAL.options.NEW_LEAF"));
        Assert.Null(EventScorer.PageOfKey("NEOW"));
    }


    [Fact]
    public void RelicEffect_IsScaledByTheMeasuredRelicRate()
    {
        var catalog = EventCatalog.Parse("""
        { "events": [ { "id": "EVENT.TEST_IDOLS", "kind": "ancient", "pages": [ { "page": "INITIAL", "options": [
          { "key": "TEST_IDOLS.pages.INITIAL.options.LOVED",  "effects": [ { "type": "relic", "relic": "RELIC.TEST_LOVED",  "tier": "ancient" } ] },
          { "key": "TEST_IDOLS.pages.INITIAL.options.MEH",    "effects": [ { "type": "relic", "relic": "RELIC.TEST_MEH",    "tier": "ancient" } ] },
          { "key": "TEST_IDOLS.pages.INITIAL.options.UNSURE", "effects": [ { "type": "relic", "relic": "RELIC.TEST_UNSURE", "tier": "ancient" } ] }
        ]}]}]}
        """);
        var relics = DataPackLoader.LoadRelicStats("""
        { "shopMedianPickRate": 0.327, "ancientNeutralPickRate": 0.333, "relics": {
          "RELIC.TEST_LOVED":  { "signal": "ancient:TEST_IDOLS", "use": true,  "pickRate": 0.6 },
          "RELIC.TEST_MEH":    { "signal": "ancient:TEST_IDOLS", "use": true,  "pickRate": 0.25 },
          "RELIC.TEST_UNSURE": { "signal": "ancient:TEST_IDOLS", "use": false, "pickRate": 0.9 } } }
        """);
        var eval = new EventScorer(new DataPack { RelicStats = relics }, catalog).Evaluate(State(80), "EVENT.TEST_IDOLS")!;
        double Relic(string name) => eval.Options.Single(o => o.Option.ShortName == name).Score.ValueOf(EventComponentKeys.Relic);

        var ancient = new EventWeights().RelicTier["ancient"];
        Assert.Equal(ancient * 1.5, Relic("LOVED"), 9);              // 0.6 / 0.333 = 1.8 → clamped to 1.5
        Assert.Equal(ancient * (0.25 / 0.333), Relic("MEH"), 9);     // 0.75
        Assert.Equal(ancient, Relic("UNSURE"));                      // use: false → rarity value
    }

    [Fact]
    public void AncientRelic_TheLeaningBuildsKeyRelicGetsTheFitAndBecomesTheBest()
    {
        var catalog = EventCatalog.Parse("""
        { "events": [ { "id": "EVENT.TEST_IDOLS", "kind": "ancient", "pages": [ { "page": "INITIAL", "options": [
          { "key": "TEST_IDOLS.pages.INITIAL.options.PLAIN", "effects": [ { "type": "relic", "relic": "RELIC.TEST_PLAIN", "tier": "rare" } ] },
          { "key": "TEST_IDOLS.pages.INITIAL.options.KEY",   "effects": [ { "type": "relic", "relic": "RELIC.TEST_KEY",   "tier": "uncommon" } ] }
        ]}]}]}
        """);
        var build = new Archetype { Id = "t.build", Character = "CHARACTER.T", KeyRelics = [new KeyRelic { Id = "RELIC.TEST_KEY", W = 1.0 }] };
        var pack = new DataPack { Archetypes = [build] };
        var scorer = new EventScorer(pack, catalog);
        var w = new EventWeights();

        // No build: rarity decides (rare 20 > uncommon 16), no fit component anywhere.
        var neutral = scorer.Evaluate(State(80), new Sts2Overlay.Core.Scoring.DeckProfile(), "EVENT.TEST_IDOLS")!;
        Assert.Equal("PLAIN", neutral.Best!.Option.ShortName);
        Assert.All(neutral.Options, o => Assert.Equal(0, o.Score.ValueOf(EventComponentKeys.RelicFit)));

        // Committed to the build whose ranker key relic is on offer: 16 × 1.5 = 24 > 20.
        var profile = new Sts2Overlay.Core.Scoring.DeckProfile
        {
            Archetypes = [new Sts2Overlay.Core.Archetypes.ArchetypeStatus { Archetype = build, Commit = 1.0, OwnedCore = 1 }],
        };
        var leaning = scorer.Evaluate(State(80), profile, "EVENT.TEST_IDOLS")!;
        var key = leaning.Options.Single(o => o.Option.ShortName == "KEY");
        Assert.Equal("KEY", leaning.Best!.Option.ShortName);
        Assert.Equal(w.RelicTier["uncommon"], key.Score.ValueOf(EventComponentKeys.Relic), 9);
        Assert.Equal(w.RelicTier["uncommon"] * 0.5, key.Score.ValueOf(EventComponentKeys.RelicFit), 9);
    }
}
