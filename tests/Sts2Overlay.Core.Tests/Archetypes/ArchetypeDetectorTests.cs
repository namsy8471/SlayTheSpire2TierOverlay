using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.Archetypes;

public class ArchetypeDetectorTests
{
    private const string Defect = "CHARACTER.DEFECT";
    private const int Precision = 9;

    // core 1.0, enabler 0.7, payoff 0.9, support 0.5; fullWeight 4.
    private static Archetype Orbs() => new()
    {
        Id = "defect.orbs",
        Character = Defect,
        Cards = new ArchetypeCards
        {
            Core = ["CARD.C1", "CARD.C2"],
            Payoff = ["CARD.P1"],
            Enabler = ["CARD.E1"],
            Support = ["CARD.S1"],
        },
        FullWeight = 4,
        KeyRelics = [new KeyRelic { Id = "RELIC.K", W = 1.0 }],
    };

    private static DataPack Pack(IReadOnlyList<Archetype>? archetypes = null, CardCatalog? catalog = null, Weights? weights = null,
        Dictionary<string, string>? aliases = null)
    {
        var tiers = new TierPack
        {
            Character = Defect,
            Cards = new Dictionary<string, CardTierEntry>(StringComparer.Ordinal)
            {
                ["CARD.C1"] = new() { Rarity = "common" },
                ["CARD.C2"] = new() { Rarity = "rare" },
                ["CARD.P1"] = new() { Rarity = "uncommon" },
            },
        };
        return new DataPack
        {
            Weights = weights ?? new Weights(),
            Tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal) { [Defect] = tiers },
            Archetypes = archetypes ?? [Orbs()],
            Catalog = catalog,
            IdAliases = aliases ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private static RunState State(IEnumerable<string> deck, IEnumerable<string>? relics = null, bool multiplayer = false) => new()
    {
        Character = Defect,
        Deck = deck.Select(id => new DeckCard(new CardRef(id))).ToList(),
        Relics = (relics ?? []).ToList(),
        IsMultiplayer = multiplayer,
    };

    private static ArchetypeStatus Single(RunState s, DataPack p) => Assert.Single(ArchetypeDetector.Detect(s, p));

    [Fact]
    public void Commit_SumsRoleWeightsOverFullWeight()
    {
        var st = Single(State(["CARD.C1", "CARD.E1", "CARD.STRIKE_DEFECT"]), Pack());
        Assert.Equal((1.0 + 0.7) / 4, st.Commit, Precision);
        Assert.Equal(["CARD.C1", "CARD.E1"], st.OwnedCards);
        Assert.False(st.IsCommitted);
    }

    [Fact]
    public void Commit_UsesArchetypeRoleWeights()
    {
        var a = Orbs();
        a.RoleWeight["payoff"] = 2.0;
        var st = Single(State(["CARD.P1", "CARD.S1"]), Pack([a]));
        Assert.Equal((2.0 + 0.5) / 4, st.Commit, Precision);
    }

    [Fact]
    public void Commit_MissingRoleWeightKey_FallsBackToDefault()
    {
        var a = Orbs();
        a.RoleWeight = new Dictionary<string, double>(StringComparer.Ordinal) { ["core"] = 2.0 };
        var st = Single(State(["CARD.C1", "CARD.E1"]), Pack([a]));
        Assert.Equal((2.0 + 0.7) / 4, st.Commit, Precision);
    }

    [Fact]
    public void Commit_SecondCopyCountsHalf_FurtherCopiesZero()
    {
        var p = Pack();
        Assert.Equal(1.0 / 4, Single(State(["CARD.C1"]), p).Commit, Precision);
        Assert.Equal(1.5 / 4, Single(State(["CARD.C1", "CARD.C1"]), p).Commit, Precision);
        Assert.Equal(1.5 / 4, Single(State(["CARD.C1", "CARD.C1", "CARD.C1", "CARD.C1"]), p).Commit, Precision);
    }

    [Fact]
    public void Commit_UpgradedCopiesAreTheSameCard()
    {
        var state = new RunState
        {
            Character = Defect,
            Deck = [new DeckCard(new CardRef("CARD.C1")), new DeckCard(new CardRef("CARD.C1", 1))],
        };
        Assert.Equal(1.5 / 4, Single(state, Pack()).Commit, Precision);
    }

    [Fact]
    public void Commit_AddsOwnedKeyRelics()
    {
        var st = Single(State(["CARD.C1"], ["RELIC.K", "RELIC.OTHER"]), Pack());
        Assert.Equal(2.0 / 4, st.Commit, Precision);
        Assert.Equal(["RELIC.K"], st.OwnedRelics);
    }

    [Fact]
    public void Commit_IsCappedAtOne()
    {
        var st = Single(State(["CARD.C1", "CARD.C2", "CARD.P1", "CARD.E1", "CARD.S1"], ["RELIC.K"]), Pack());
        Assert.Equal(1.0, st.Commit);
        Assert.True(st.IsCommitted);
    }

    [Fact]
    public void Aliases_AreResolvedForDeckAndArchetypeIds()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CARD.OLD_C1"] = "CARD.C1",
            ["CARD.OLD_E1"] = "CARD.E1",
        };
        var a = Orbs();
        a.Cards.Enabler = ["CARD.OLD_E1"];
        var st = Single(State(["CARD.OLD_C1", "CARD.E1"]), Pack([a], aliases: aliases));
        Assert.Equal((1.0 + 0.7) / 4, st.Commit, Precision);
        Assert.Equal(["CARD.C1", "CARD.E1"], st.OwnedCards);
    }

    [Fact]
    public void CardInTwoRoles_CountsOnceWithHigherPriorityRole()
    {
        var a = Orbs();
        a.Cards.Support = ["CARD.S1", "CARD.C1"];
        var st = Single(State(["CARD.C1"]), Pack([a]));
        Assert.Equal(1.0 / 4, st.Commit, Precision);
        // completion total still 3×100 + 2×56
        Assert.Equal(100.0 / 412 * 100, st.CompletionPercent, Precision);
    }

    [Fact]
    public void Completion_WeightsCoreAndPayoff100_Others56()
    {
        var p = Pack();
        var none = Single(State([]), p);
        Assert.Equal(0, none.CompletionPercent);
        Assert.Equal(0, none.Stage);
        Assert.Equal(0, none.Commit);

        var some = Single(State(["CARD.C1", "CARD.E1", "CARD.E1"]), p);
        Assert.Equal(156.0 / 412 * 100, some.CompletionPercent, Precision);
        Assert.Equal(2, some.Stage);

        var all = Single(State(["CARD.C1", "CARD.C2", "CARD.P1", "CARD.E1", "CARD.S1"]), p);
        Assert.Equal(100, all.CompletionPercent, Precision);
        Assert.Equal(5, all.Stage);
    }

    [Fact]
    public void Completion_UsesConfiguredWeights()
    {
        var w = new Weights();
        w.Completion.CoreWeight = 1;
        w.Completion.SupportWeight = 1;
        var st = Single(State(["CARD.S1"]), Pack(weights: w));
        Assert.Equal(20, st.CompletionPercent, Precision);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.01, 1)]
    [InlineData(20, 1)]
    [InlineData(20.01, 2)]
    [InlineData(40, 2)]
    [InlineData(60.000000000001, 3)]
    [InlineData(80.5, 5)]
    [InlineData(100, 5)]
    public void StageFor(double pct, int stage) => Assert.Equal(stage, ArchetypeDetector.StageFor(pct));

    [Fact]
    public void MissingCards_AreUnownedCoreAndPayoff_WithRarity()
    {
        var st = Single(State(["CARD.C1"]), Pack());
        Assert.Equal(
            [new MissingCard("CARD.C2", "core", "rare"), new MissingCard("CARD.P1", "payoff", "uncommon")],
            st.MissingCards);
    }

    [Fact]
    public void MissingCards_RarityFromCatalogWins()
    {
        var catalog = new CardCatalog { Cards = [new CatalogCard { Id = "CARD.C2", Pool = Defect, Rarity = "Uncommon" }] };
        var st = Single(State(["CARD.C1", "CARD.P1"]), Pack(catalog: catalog));
        Assert.Equal("uncommon", Assert.Single(st.MissingCards).Rarity);
    }

    [Fact]
    public void UnmetNeeds_RoleAndTag_SkippedWhenDictionaryNull()
    {
        var a = Orbs();
        var draw = new ArchetypeNeed { Role = "draw", Min = 2 };
        var frost = new ArchetypeNeed { Tag = "ORB_FROST", Min = 1 };
        a.Needs = [draw, frost];
        var p = Pack([a]);
        var s = State(["CARD.C1"]);

        Assert.Empty(Single(s, p).UnmetNeeds);

        var both = Assert.Single(ArchetypeDetector.Detect(s, p,
            new Dictionary<string, double> { ["draw"] = 1.5 },
            new Dictionary<string, int>())).UnmetNeeds;
        Assert.Equal([draw, frost], both);

        var met = Assert.Single(ArchetypeDetector.Detect(s, p,
            new Dictionary<string, double> { ["draw"] = 2.0 },
            new Dictionary<string, int> { ["ORB_FROST"] = 1 })).UnmetNeeds;
        Assert.Empty(met);

        var roleOnly = Assert.Single(ArchetypeDetector.Detect(s, p, new Dictionary<string, double>(), null)).UnmetNeeds;
        Assert.Equal([draw], roleOnly);
    }

    [Fact]
    public void Detect_OnlyRunCharacter_SortedByCommitThenCompletionThenId()
    {
        Archetype Make(string id, string[] core, string[] support, string character = Defect) => new()
        {
            Id = id,
            Character = character,
            Cards = new ArchetypeCards { Core = [.. core], Support = [.. support] },
        };
        var archetypes = new List<Archetype>
        {
            Make("z.low", ["CARD.X"], []),
            Make("b.tie", ["CARD.A"], ["CARD.N1", "CARD.N2"]),     // commit 0.25, completion 100/212
            Make("a.tie", ["CARD.A"], ["CARD.N1", "CARD.N2"]),     // identical → id order
            Make("c.morecomplete", ["CARD.A"], []),                // commit 0.25, completion 100
            Make("d.high", ["CARD.A", "CARD.B"], []),              // commit 0.5
            Make("silent.x", ["CARD.A", "CARD.B"], [], "CHARACTER.SILENT"),
        };
        var statuses = ArchetypeDetector.Detect(State(["CARD.A", "CARD.B"]), Pack(archetypes));
        Assert.Equal(["d.high", "c.morecomplete", "a.tie", "b.tie", "z.low"], statuses.Select(s => s.Archetype.Id));
    }

    [Fact]
    public void Detect_NoArchetypesForCharacter_IsEmpty()
    {
        var report = ArchetypeDetector.Report(State(["CARD.C1"]) with { Character = "CHARACTER.SILENT" }, Pack(), 5);
        Assert.Empty(report.Statuses);
        Assert.Null(report.Primary);
        Assert.False(report.IsHybrid);
    }

    private static Archetype Simple(string id, string core) => new()
    {
        Id = id,
        Character = Defect,
        Cards = new ArchetypeCards { Core = [core, "CARD.MISSING_" + id.ToUpperInvariant().Replace('.', '_')] },
        FullWeight = 1,
    };

    [Fact]
    public void Report_Hybrid_WhenTopTwoWithinMargin()
    {
        var a = Simple("a", "CARD.A");
        var b = Simple("b", "CARD.B");
        b.RoleWeight["core"] = 0.9; // 1.0 vs 0.9 → within 0.15
        var report = ArchetypeDetector.Report(State(["CARD.A", "CARD.B"]), Pack([a, b]), 3);
        Assert.True(report.IsHybrid);
        Assert.Equal("a", report.Primary!.Archetype.Id);

        b.RoleWeight["core"] = 0.5; // 1.0 vs 0.5
        Assert.False(ArchetypeDetector.Report(State(["CARD.A", "CARD.B"]), Pack([a, b]), 3).IsHybrid);
    }

    [Fact]
    public void Report_Hybrid_ExactMarginCounts_ZeroSecondDoesNot()
    {
        var a = Simple("a", "CARD.A");
        var b = Simple("b", "CARD.B");
        a.RoleWeight["core"] = 0.3;
        b.RoleWeight["core"] = 0.15;
        Assert.True(ArchetypeDetector.Report(State(["CARD.A", "CARD.B"]), Pack([a, b]), 3).IsHybrid);

        b.RoleWeight["core"] = 0.3;
        Assert.False(ArchetypeDetector.Report(State(["CARD.A"]), Pack([a, b]), 3).IsHybrid);
    }

    [Fact]
    public void Report_FillsProbabilitiesAndRemainingRewards()
    {
        var report = ArchetypeDetector.Report(State(["CARD.C1"]), Pack(), 4);
        Assert.Equal(4, report.RemainingRewards);
        var st = Assert.Single(report.Statuses);
        Assert.Equal(ArchetypeDetector.CoreOfferProbability(st, State(["CARD.C1"]), Pack(), 4), report.CoreOfferProbability["defect.orbs"], Precision);
        Assert.Equal(0, ArchetypeDetector.Report(State(["CARD.C1"]), Pack(), -2).RemainingRewards);
    }

    [Fact]
    public void Probability_UsesFallbackPoolSizesWithoutCatalog()
    {
        var p = Pack();
        var s = State(["CARD.C1"]);
        var st = Single(s, p);
        // missing: C2 rare (0.03/16), P1 uncommon (0.37/36); 3 slots per reward
        var perSlot = 0.03 / 16 + 0.37 / 36;
        var expected = 1 - Math.Pow(1 - perSlot, 3 * 5);
        Assert.Equal(expected, ArchetypeDetector.CoreOfferProbability(st, s, p, 5), Precision);
    }

    [Fact]
    public void Probability_ZeroRewardsIsZero_CompleteIsOne()
    {
        var p = Pack();
        var partial = State(["CARD.C1"]);
        Assert.Equal(0, ArchetypeDetector.CoreOfferProbability(Single(partial, p), partial, p, 0));

        var complete = State(["CARD.C1", "CARD.C2", "CARD.P1"]);
        var st = Single(complete, p);
        Assert.Empty(st.MissingCards);
        Assert.Equal(1.0, ArchetypeDetector.CoreOfferProbability(st, complete, p, 0));
    }

    [Fact]
    public void Probability_IsMonotonicInRewards()
    {
        var p = Pack();
        var s = State([]);
        var st = Single(s, p);
        var previous = -1.0;
        for (var n = 0; n <= 40; n++)
        {
            var prob = ArchetypeDetector.CoreOfferProbability(st, s, p, n);
            Assert.InRange(prob, 0, 1);
            Assert.True(prob > previous, $"N={n}: {prob} <= {previous}");
            previous = prob;
        }
    }

    [Fact]
    public void Probability_UsesCatalogPoolSizes_RespectingMultiplayerOnly()
    {
        var catalog = new CardCatalog
        {
            Cards =
            [
                new CatalogCard { Id = "CARD.C1", Pool = Defect, Rarity = "common" },
                new CatalogCard { Id = "CARD.C2", Pool = Defect, Rarity = "uncommon" },
                new CatalogCard { Id = "CARD.P1", Pool = Defect, Rarity = "uncommon" },
                new CatalogCard { Id = "CARD.U3", Pool = Defect, Rarity = "uncommon" },
                new CatalogCard { Id = "CARD.U4", Pool = Defect, Rarity = "uncommon", MultiplayerOnly = true },
                new CatalogCard { Id = "CARD.OTHER", Pool = "CHARACTER.SILENT", Rarity = "uncommon" },
                new CatalogCard { Id = "CARD.CL", Pool = DataPack.Colorless, Rarity = "uncommon" },
            ],
        };
        var p = Pack(catalog: catalog);

        var single = State(["CARD.C1"]);
        var st = Single(single, p); // missing C2, P1 — both uncommon per catalog
        Assert.All(st.MissingCards, m => Assert.Equal("uncommon", m.Rarity));
        Assert.Equal(1 - Math.Pow(1 - 2 * 0.37 / 3, 3), ArchetypeDetector.CoreOfferProbability(st, single, p, 1), Precision);

        var multi = State(["CARD.C1"], multiplayer: true);
        Assert.Equal(1 - Math.Pow(1 - 2 * 0.37 / 4, 3), ArchetypeDetector.CoreOfferProbability(Single(multi, p), multi, p, 1), Precision);
    }

    [Fact]
    public void Probability_UnknownRarityCountsAsUncommon_UnrewardableRarityCountsZero()
    {
        var p = Pack();
        var s = State([]);
        var status = new ArchetypeStatus
        {
            Archetype = Orbs(),
            MissingCards = [new MissingCard("CARD.Q", "core", null), new MissingCard("CARD.ANC", "core", "ancient")],
        };
        Assert.Equal(1 - Math.Pow(1 - 0.37 / 36, 6), ArchetypeDetector.CoreOfferProbability(status, s, p, 2), Precision);
    }

    [Fact]
    public void Probability_PerSlotSumAboveOne_IsCertain()
    {
        var w = new Weights();
        w.Completion.PoolSize["uncommon"] = 1;
        w.Completion.RarityOdds["uncommon"] = 1;
        var status = new ArchetypeStatus
        {
            Archetype = Orbs(),
            MissingCards = [new MissingCard("CARD.Q1", "core", "uncommon"), new MissingCard("CARD.Q2", "core", "uncommon")],
        };
        Assert.Equal(1.0, ArchetypeDetector.CoreOfferProbability(status, State([]), Pack(weights: w), 1));
    }
}
