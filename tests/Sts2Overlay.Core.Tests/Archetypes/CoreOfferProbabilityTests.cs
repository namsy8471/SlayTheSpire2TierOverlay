using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Tests.Data;

namespace Sts2Overlay.Core.Tests.Archetypes;

/// <summary><see cref="ArchetypeDetector.CoreOfferProbability(ArchetypeStatus, RunState, DataPack, int)"/> with the real catalog and SPIKE §5 odds.</summary>
public class CoreOfferProbabilityTests
{
    private const string Defect = "CHARACTER.DEFECT";
    private const int Precision = 9;

    // Real Defect ids (v0.107.1 catalog): ECHO_FORM rare, CAPACITOR uncommon, BALL_LIGHTNING common,
    // IGNITION rare multiplayer-only; AUTOMATION colorless uncommon.
    private const string Rare = "CARD.ECHO_FORM";
    private const string Uncommon = "CARD.CAPACITOR";
    private const string Common = "CARD.BALL_LIGHTNING";

    private static DataPack Pack(Weights? weights = null) => new()
    {
        Weights = weights ?? new Weights(),
        Catalog = RealCatalog.Instance,
    };

    private static RunState State(int ascension = 0, bool multiplayer = false) => new()
    {
        Character = Defect,
        Ascension = ascension,
        IsMultiplayer = multiplayer,
    };

    private static ArchetypeStatus Missing(params string[] ids)
    {
        var pack = Pack();
        return new ArchetypeStatus
        {
            Archetype = new Archetype { Id = "defect.test", Character = Defect, Cards = new ArchetypeCards { Core = ids.ToList() } },
            MissingCards = ids.Select(id => new MissingCard(id, ArchetypeRoles.Core, pack.CardRarity(Defect, id))).ToList(),
        };
    }

    private static double Closed(double perSlot, int rewards) => 1 - Math.Pow(1 - perSlot, 3 * rewards);

    [Fact]
    public void UsesCatalogPoolSizes_AndCardRewardOdds()
    {
        var st = Missing(Rare, Uncommon, Common);
        var expected = Closed(0.03 / 25 + 0.37 / 35 + 0.60 / 20, 4);
        Assert.Equal(expected, ArchetypeDetector.CoreOfferProbability(st, State(), Pack(), 4), Precision);
    }

    [Fact]
    public void Ascension7_UsesExplicitRareValue()
    {
        var st = Missing(Rare);
        Assert.Equal(Closed(0.03 / 25, 5), ArchetypeDetector.CoreOfferProbability(st, State(ascension: 6), Pack(), 5), Precision);
        Assert.Equal(Closed(0.0149 / 25, 5), ArchetypeDetector.CoreOfferProbability(st, State(ascension: 7), Pack(), 5), Precision);
    }

    [Fact]
    public void Multiplayer_UsesMultiplayerPoolSizes()
    {
        var st = Missing(Rare, Uncommon);
        Assert.Equal(Closed(0.03 / 26 + 0.37 / 36, 2), ArchetypeDetector.CoreOfferProbability(st, State(multiplayer: true), Pack(), 2), Precision);
    }

    [Fact]
    public void CardsNotInTheRunPool_CountZero()
    {
        // Multiplayer-only card in single player, a colorless card, a basic card and another character's card never show up in Defect rewards.
        var st = Missing("CARD.IGNITION", "CARD.AUTOMATION", "CARD.STRIKE_DEFECT", "CARD.ANGER");
        Assert.Equal(0.0, ArchetypeDetector.CoreOfferProbability(st, State(), Pack(), 10));
        // The multiplayer-only rare does show up in multiplayer.
        Assert.Equal(Closed(0.03 / 26, 10), ArchetypeDetector.CoreOfferProbability(st, State(multiplayer: true), Pack(), 10), Precision);
    }

    [Fact]
    public void Contexts_EliteAndBossRaiseRareChance()
    {
        var st = Missing(Rare);
        var normal = ArchetypeDetector.CoreOfferProbability(st, State(), Pack(), 1, OfferContext.CardReward, null);
        var elite = ArchetypeDetector.CoreOfferProbability(st, State(), Pack(), 1, OfferContext.EliteReward, null);
        var boss = ArchetypeDetector.CoreOfferProbability(st, State(), Pack(), 1, OfferContext.BossReward, null);
        Assert.Equal(Closed(0.10 / 25, 1), elite, Precision);
        Assert.Equal(Closed(1.0 / 25, 1), boss, Precision);
        Assert.True(normal < elite && elite < boss);
        Assert.Equal(ArchetypeDetector.CoreOfferProbability(st, State(), Pack(), 1), normal);
    }

    [Fact]
    public void Pity_ExactForTwoCards()
    {
        var w = new Weights();
        w.Completion.CardsPerReward = 1;
        var st = Missing(Rare);
        // Card 1 at offset 0.10: rare 13% → hit 0.13/25; a non-rare (87%) moves the offset to 0.11; a rare miss resets it to −0.05 (rare 0%).
        // Card 2 at 0.11: rare 14% → hit 0.14/25.
        var expected = 0.13 / 25 + 0.87 * (0.14 / 25);
        Assert.Equal(expected, ArchetypeDetector.CoreOfferProbability(st, State(), Pack(w), 2, OfferContext.CardReward, 0.10), Precision);
    }

    [Fact]
    public void Pity_RaisesProbability_AndCapsAt40Percent()
    {
        var st = Missing(Rare);
        var p = Pack();
        var fresh = ArchetypeDetector.CoreOfferProbability(st, State(), p, 3, OfferContext.CardReward, -0.05);
        var table = ArchetypeDetector.CoreOfferProbability(st, State(), p, 3);
        var high = ArchetypeDetector.CoreOfferProbability(st, State(), p, 3, OfferContext.CardReward, 0.30);
        var capped = ArchetypeDetector.CoreOfferProbability(st, State(), p, 3, OfferContext.CardReward, 0.40);
        var beyond = ArchetypeDetector.CoreOfferProbability(st, State(), p, 3, OfferContext.CardReward, 0.95);
        Assert.True(fresh < table, $"{fresh} < {table}");
        Assert.True(table < high, $"{table} < {high}");
        Assert.True(high < capped, $"{high} < {capped}");
        Assert.Equal(capped, beyond, Precision);

        var w = new Weights();
        w.Completion.CardsPerReward = 1;
        Assert.Equal(0.43 / 25, ArchetypeDetector.CoreOfferProbability(st, State(), Pack(w), 1, OfferContext.CardReward, 0.95), Precision);
    }

    [Fact]
    public void Pity_GrowsOverManyRewards_AboveTableOddsForRares()
    {
        // From the starting offset, the pity build-up makes rares more common over a long horizon than the 3% table value.
        var st = Missing(Rare);
        var p = Pack();
        var pity = ArchetypeDetector.CoreOfferProbability(st, State(), p, 15, OfferContext.CardReward, -0.05);
        var table = ArchetypeDetector.CoreOfferProbability(st, State(), p, 15);
        Assert.True(pity > table, $"{pity} > {table}");
        Assert.InRange(pity, 0, 1);
    }

    [Fact]
    public void Pity_NonRareMissingCards_MatchShiftModel()
    {
        var w = new Weights();
        w.Completion.CardsPerReward = 1;
        var st = Missing(Uncommon, Common);
        // At the starting offset: uncommon 35%, common 65%.
        Assert.Equal(0.35 / 35 + 0.65 / 20,
            ArchetypeDetector.CoreOfferProbability(st, State(), Pack(w), 1, OfferContext.CardReward, -0.05), Precision);
    }

    [Fact]
    public void Report_UsesRealCatalogPools()
    {
        var pack = new DataPack
        {
            Catalog = RealCatalog.Instance,
            Archetypes = [new Archetype { Id = "defect.x", Character = Defect, Cards = new ArchetypeCards { Core = [Rare, Common] } }],
        };
        var state = State() with { Deck = [new DeckCard(new CardRef(Common))] };
        var report = ArchetypeDetector.Report(state, pack, 3);
        Assert.Equal(Closed(0.03 / 25, 3), report.CoreOfferProbability["defect.x"], Precision);
    }
}
