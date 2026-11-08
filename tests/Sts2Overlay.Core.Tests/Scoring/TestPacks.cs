using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Scoring;

/// <summary>Small in-code data packs for scoring tests (synthetic ids; no loader involved).</summary>
internal static class TestPacks
{
    public const string Defect = "CHARACTER.DEFECT";

    public const string Strike = "CARD.STRIKE_DEFECT";
    public const string Defend = "CARD.DEFEND_DEFECT";
    public const string OrbCore = "CARD.T_ORB_CORE";
    public const string OrbEnabler = "CARD.T_ORB_ENABLER";
    public const string OrbPayoff = "CARD.T_ORB_PAYOFF";
    public const string OrbSupport = "CARD.T_ORB_SUPPORT";
    public const string PowerCore = "CARD.T_POWER_CORE";
    public const string Blocker = "CARD.T_BLOCKER";
    public const string AoeHit = "CARD.T_AOE_HIT";
    public const string Filler = "CARD.T_FILLER";
    public const string Drawer = "CARD.T_DRAWER";
    public const string ShivGen = "CARD.T_SHIV_GEN";
    public const string Star = "CARD.T_STAR";
    public const string LowConf = "CARD.T_LOW_CONF";
    public const string Patched = "CARD.T_PATCHED";
    public const string DupeOk = "CARD.T_DUPE_OK";
    public const string Curse = "CARD.T_CURSE";
    public const string CatalogCurse = "CARD.T_CATALOG_CURSE";
    public const string ComboA = "CARD.T_COMBO_A";
    public const string ComboB = "CARD.T_COMBO_B";
    public const string ComboC = "CARD.T_COMBO_C";
    public const string TrioA = "CARD.T_TRIO_A";
    public const string TrioB = "CARD.T_TRIO_B";
    public const string TrioC = "CARD.T_TRIO_C";
    public const string OldId = "CARD.T_OLD_BLOCKER";
    public const string Unknown = "CARD.T_NOT_IN_PACK";

    public const string OrbArchetype = "defect.orb";
    public const string PowerArchetype = "defect.power";

    /// <summary>A Defect pack with one card per scoring feature, default weights unless <paramref name="weights"/> is given.</summary>
    public static DataPack DefectPack(Weights? weights = null, IReadOnlyList<Combo>? combos = null) => new()
    {
        Weights = weights ?? new Weights(),
        Tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal)
        {
            [Defect] = new TierPack
            {
                Character = Defect,
                Cards = new Dictionary<string, CardTierEntry>(StringComparer.Ordinal)
                {
                    [Strike] = new() { Tier = Tier.D, Type = "attack", Roles = R((Roles.Frontload, 0.3)) },
                    [Defend] = new() { Tier = Tier.D, Type = "skill", Roles = R((Roles.Block, 0.3)) },
                    [OrbCore] = new() { Tier = Tier.A, Type = "power", Roles = R((Roles.Scaling, 0.5)), Provides = ["ORB"] },
                    [OrbEnabler] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Block, 0.4)), Provides = ["ORB"] },
                    [OrbPayoff] = new() { Tier = Tier.B, Type = "attack", Roles = R((Roles.Frontload, 0.5)), Consumes = ["ORB"], Requires = ["ORB"] },
                    [OrbSupport] = new() { Tier = Tier.C, Type = "skill", Roles = R((Roles.Draw, 0.6)), Cycle = 0.5 },
                    [PowerCore] = new() { Tier = Tier.B, Type = "power", Roles = R((Roles.Scaling, 0.6)) },
                    [Blocker] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Block, 1.0)) },
                    [AoeHit] = new() { Tier = Tier.B, Type = "attack", ActTier = new() { [1] = Tier.A }, UpgradedDelta = 5, Roles = R((Roles.Aoe, 1.0)) },
                    [Filler] = new() { Tier = Tier.C, Type = "skill" },
                    [Drawer] = new() { Tier = Tier.C, Type = "skill", Roles = R((Roles.Draw, 1.0)), Cycle = 1.0 },
                    [ShivGen] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Frontload, 0.5)), Provides = ["SHIV"] },
                    [Star] = new() { Tier = Tier.S, Type = "attack" },
                    [LowConf] = new() { Tier = Tier.S, Type = "attack", Confidence = 0.3 },
                    [Patched] = new() { Tier = Tier.A, Type = "attack", ChangedIn = "v0.200.0" },
                    [DupeOk] = new() { Tier = Tier.B, Type = "skill", DupFactor = 0 },
                    [Curse] = new() { Tier = Tier.D, Type = "curse" },
                    [ComboA] = new() { Tier = Tier.C, Type = "skill" },
                    [ComboB] = new() { Tier = Tier.C, Type = "skill" },
                    [ComboC] = new() { Tier = Tier.C, Type = "skill" },
                    [TrioA] = new() { Tier = Tier.C, Type = "skill" },
                    [TrioB] = new() { Tier = Tier.C, Type = "skill" },
                    [TrioC] = new() { Tier = Tier.C, Type = "skill" },
                },
            },
        },
        Archetypes =
        [
            new Archetype
            {
                Id = OrbArchetype, Character = Defect, Names = new() { ["kor"] = "구체 덱" },
                Cards = new ArchetypeCards { Core = [OrbCore], Enabler = [OrbEnabler], Payoff = [OrbPayoff], Support = [OrbSupport] },
            },
            new Archetype
            {
                Id = PowerArchetype, Character = Defect, Names = new() { ["kor"] = "파워 덱" },
                Cards = new ArchetypeCards { Core = [PowerCore], Payoff = [OrbCore] },
            },
        ],
        Combos = combos ??
        [
            new Combo { Character = Defect, Cards = [ComboA, ComboB], Name = "pair-ab", W = 6 },
            new Combo { Character = Defect, Cards = [ComboA, ComboC], Name = "pair-ac", W = 12 },
            new Combo { Character = Defect, Cards = [TrioA, TrioB, TrioC], Name = "trio", W = 6 },
            new Combo { Character = Defect, Cards = [OrbPayoff, OrbEnabler], Name = "orb-pair", W = 6 },
        ],
        Catalog = new CardCatalog { Cards = [new CatalogCard { Id = CatalogCurse, Rarity = "curse" }] },
        IdAliases = new Dictionary<string, string>(StringComparer.Ordinal) { [OldId] = Blocker },
    };

    public static Dictionary<string, double> R(params (string Role, double Strength)[] roles)
        => roles.ToDictionary(r => r.Role, r => r.Strength, StringComparer.Ordinal);

    public static RunState State(int act = 1, IEnumerable<string>? deck = null, int hp = 50, int maxHp = 50, int gold = 0, int ascension = 0, int removals = 0)
        => new()
        {
            Character = Defect,
            Act = act,
            Hp = hp,
            MaxHp = maxHp,
            Gold = gold,
            Ascension = ascension,
            CardRemovalsBought = removals,
            Deck = (deck ?? []).Select(id => new DeckCard(Parse(id))).ToArray(),
        };

    /// <summary>"CARD.X+1" → upgraded CardRef.</summary>
    public static CardRef Parse(string id)
    {
        var plus = id.IndexOf('+');
        return plus < 0 ? new CardRef(id) : new CardRef(id[..plus], int.Parse(id[(plus + 1)..]));
    }

    public static ArchetypeStatus Status(DataPack pack, string archetypeId, double commit)
        => new() { Archetype = pack.Archetypes.Single(a => a.Id == archetypeId), Commit = commit };

    /// <summary>Profile with archetype statuses attached (highest commit first), everything else as given.</summary>
    public static DeckProfile WithArchetypes(this DeckProfile profile, DataPack pack, params (string Id, double Commit)[] statuses)
        => profile with { Archetypes = statuses.OrderByDescending(s => s.Commit).Select(s => Status(pack, s.Id, s.Commit)).ToArray() };

    /// <summary>
    /// The documented ArchetypeDetector contract (commit = min(1, (Σ distinct owned roleWeight + key relic w) / fullWeight)),
    /// used by tests to build realistic profiles without depending on the detector implementation.
    /// </summary>
    public static IReadOnlyList<ArchetypeStatus> ContractStatuses(RunState state, DataPack pack)
    {
        var owned = state.Deck.Select(d => pack.Canonical(d.Id)).ToHashSet(StringComparer.Ordinal);
        return pack.ArchetypesFor(state.Character)
            .Select(a => new ArchetypeStatus
            {
                Archetype = a,
                Commit = Math.Min(1.0, (a.Cards.All.Distinct().Where(owned.Contains).Sum(a.WeightOf)
                    + a.KeyRelics.Where(r => state.Relics.Contains(r.Id)).Sum(r => r.W)) / a.FullWeight),
            })
            .OrderByDescending(s => s.Commit)
            .ToArray();
    }

    /// <summary>Deck analysis with contract archetype statuses (no ArchetypeDetector call).</summary>
    public static DeckProfile ProfileFor(RunState state, DataPack pack)
        => DeckAnalyzer.AnalyzeDeck(state, pack) with { Archetypes = ContractStatuses(state, pack) };
}

internal static class ScoreAssert
{
    /// <summary>Total equals the component sum, every value is finite and only the base may be zero.</summary>
    public static void Consistent(ScoreBreakdown score)
    {
        Assert.NotEmpty(score.Components);
        foreach (var c in score.Components)
        {
            Assert.True(double.IsFinite(c.Value), $"{c.Key} is not finite: {c.Value}");
        }
        Assert.True(double.IsFinite(score.Total));
        Assert.Equal(score.Components.Sum(c => c.Value), score.Total, 9);
    }

    public static void ConsistentCard(CardEvaluation eval)
    {
        Consistent(eval.Score);
        Assert.Equal(ComponentKeys.Base, eval.Score.Components[0].Key);
        Assert.All(eval.Score.Components.Skip(1), c => Assert.NotEqual(0.0, c.Value));
        Assert.True(eval.Chips.Count <= 3);
        Assert.DoesNotContain(eval.Chips, c => c.Key == ComponentKeys.Base);
    }
}
