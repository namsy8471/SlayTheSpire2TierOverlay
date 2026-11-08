using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Scoring;

/// <summary>
/// After the core: a core-secured build's essential cards beat generic cards one tier higher (<c>secured_essential</c>), the weak-deck
/// build cap no longer crushes a secured build, emergencies (block nearly missing, low HP) still win, and on-theme cards
/// (<c>onTheme</c> or a needed axis tag) are not off-build. Synthetic "venom" pack (ids <c>CARD.T_*</c>), profiles from the real
/// <see cref="DeckAnalyzer"/>; every test runs with two archetype lists (one core, or a core-heavy list) so it does not depend on
/// which cards a data pack calls core.
/// </summary>
public class SecuredBuildTests
{
    private const string Hero = "CHARACTER.SILENT";
    private const string Strike = "CARD.STRIKE_SILENT";
    private const string Defend = "CARD.DEFEND_SILENT";
    private const string VenomCore = "CARD.T_VENOM_CORE";
    private const string AltCore = "CARD.T_VENOM_ALT_CORE";
    private const string Enabler = "CARD.T_VENOM_ENABLER";
    private const string Enabler2 = "CARD.T_VENOM_ENABLER2";
    private const string Payoff = "CARD.T_VENOM_PAYOFF";
    private const string Payoff2 = "CARD.T_VENOM_PAYOFF2";
    private const string Support = "CARD.T_VENOM_SUPPORT";
    private const string Drawer = "CARD.T_DRAWER";
    private const string BigDrawer = "CARD.T_BIG_DRAWER";
    private const string Blocker = "CARD.T_BLOCKER";
    private const string Wall = "CARD.T_WALL";
    private const string Cycler = "CARD.T_CYCLER";
    private const string AxisTheme = "CARD.T_AXIS_THEME";
    private const string ListedTheme = "CARD.T_LISTED_THEME";
    private const string Plain = "CARD.T_PLAIN";
    private const string Star = "CARD.T_STAR";
    private const string Venom = "t.venom";
    private const string Blades = "t.blades";
    private const string BladeCore = "CARD.T_BLADE_CORE";
    private const string BladeEnabler = "CARD.T_BLADE_ENABLER";

    private static readonly Weights W = new();
    private static double Step => W.TierValue[Tier.A] - W.TierValue[Tier.B];

    public static IEnumerable<object[]> Lists => [["single-core"], ["core-heavy"]];

    /// <summary>
    /// Venom build: needs the POISON tag. "single-core": core VenomCore. "core-heavy": cores VenomCore + AltCore (coreTarget 1), as a
    /// ranker-sourced list might do. Essentials (C tier) and the generic Drawer (B, draw + block, cycles) mirror the real Silent case.
    /// </summary>
    private static DataPack Pack(string list, Weights? weights = null)
    {
        static Dictionary<string, double> R(params (string Role, double S)[] r) => r.ToDictionary(x => x.Role, x => x.S, StringComparer.Ordinal);
        var cards = new Dictionary<string, CardTierEntry>(StringComparer.Ordinal)
        {
            [Strike] = new() { Tier = Tier.D, Type = "attack", Roles = R((Roles.Frontload, 0.3)) },
            [Defend] = new() { Tier = Tier.D, Type = "skill", Roles = R((Roles.Block, 0.3)) },
            [VenomCore] = new() { Tier = Tier.A, Type = "power", Roles = R((Roles.Scaling, 0.7), (Roles.Aoe, 0.5)), Provides = ["POISON"] },
            [AltCore] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Scaling, 0.5)), Provides = ["POISON"] },
            [Enabler] = new() { Tier = Tier.C, Type = "skill", Roles = R((Roles.Scaling, 0.4)), Provides = ["POISON"] },
            [Enabler2] = new() { Tier = Tier.C, Type = "skill", Roles = R((Roles.Scaling, 0.4)), Provides = ["POISON"] },
            [Payoff] = new() { Tier = Tier.C, Type = "skill", Roles = R((Roles.Scaling, 0.4)), Consumes = ["POISON"], Requires = ["POISON"] },
            [Payoff2] = new() { Tier = Tier.C, Type = "skill", Roles = R((Roles.Block, 0.5)), Consumes = ["POISON"], Requires = ["POISON"] },
            [Support] = new() { Tier = Tier.C, Type = "skill", Roles = R((Roles.Block, 0.5)) },
            [Drawer] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Block, 0.45), (Roles.Draw, 0.45)), Provides = ["DRAW"], Cycle = 0.6 },
            [BigDrawer] = new() { Tier = Tier.A, Type = "skill", Roles = R((Roles.Draw, 0.6)), Provides = ["DRAW"], Cycle = 0.6 },
            [Blocker] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Block, 0.6)) },
            [Wall] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Block, 0.6)) },
            [Cycler] = new() { Tier = Tier.C, Type = "skill", Roles = R((Roles.Draw, 0.8)), Cycle = 1 },
            [AxisTheme] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Aoe, 0.6)), Consumes = ["POISON"] },
            [ListedTheme] = new() { Tier = Tier.B, Type = "skill" },
            [Plain] = new() { Tier = Tier.B, Type = "skill" },
            [Star] = new() { Tier = Tier.A, Type = "attack" },
            [BladeCore] = new() { Tier = Tier.B, Type = "power", Roles = R((Roles.Scaling, 0.5)) },
            [BladeEnabler] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Frontload, 0.5)) },
        };
        var venomCards = list == "core-heavy"
            ? new ArchetypeCards { Core = [VenomCore, AltCore], Enabler = [Enabler, Enabler2], Payoff = [Payoff, Payoff2], Support = [Support] }
            : new ArchetypeCards { Core = [VenomCore], Enabler = [Enabler, Enabler2, AltCore], Payoff = [Payoff, Payoff2], Support = [Support] };
        return new DataPack
        {
            Weights = weights ?? new Weights(),
            Tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal) { [Hero] = new TierPack { Character = Hero, Cards = cards } },
            Archetypes =
            [
                new Archetype
                {
                    Id = Venom, Character = Hero, Names = new() { ["kor"] = "독" }, Cards = venomCards,
                    Needs = [new ArchetypeNeed { Tag = "POISON", Min = 3 }, new ArchetypeNeed { Role = Roles.Block, Min = 3 }],
                    OnTheme = [ListedTheme],
                },
                new Archetype
                {
                    Id = Blades, Character = Hero, Names = new() { ["kor"] = "칼날" },
                    Cards = new ArchetypeCards { Core = [BladeCore], Enabler = [BladeEnabler] },
                },
            ],
        };
    }

    private static RunState State(IEnumerable<string> deck, int act = 2, int hp = 70)
        => new() { Character = Hero, Act = act, Hp = hp, MaxHp = 70, Deck = deck.Select(id => new DeckCard(new CardRef(id))).ToArray() };

    /// <summary>
    /// Act-2 poison deck like the user's run: starter cards, the core, one payoff, two blockers (block met), one cycling draw card
    /// (draw short: weak, not an emergency). 15 cards, below the act-2 ideal size, so dilution plays no part unless a test adds cards.
    /// </summary>
    private static List<string> PoisonDeck(params string[] extra)
    {
        var deck = new List<string>();
        deck.AddRange(Enumerable.Repeat(Strike, 5));
        deck.AddRange(Enumerable.Repeat(Defend, 5));
        deck.AddRange([VenomCore, Payoff, Blocker, Blocker, Cycler]);
        deck.AddRange(extra);
        return deck;
    }

    private static (CardScorer Scorer, RunState State, DeckProfile Profile) Setup(DataPack pack, List<string> deck, int act = 2, int hp = 70)
    {
        var state = State(deck, act, hp);
        return (new CardScorer(pack), state, DeckAnalyzer.Analyze(state, pack));
    }

    private static CardEvaluation Score(CardScorer scorer, RunState state, DeckProfile profile, string card)
    {
        var e = scorer.Score(state, profile, new CardRef(card));
        ScoreAssert.ConsistentCard(e);
        return e;
    }

    private static double Synergy(CardEvaluation e)
        => e.Score.ValueOf(ComponentKeys.Build) + e.Score.ValueOf(ComponentKeys.CoreMissing) + e.Score.ValueOf(ComponentKeys.SecuredEssential);

    // ---------------- the deck under test ----------------

    [Theory]
    [MemberData(nameof(Lists))]
    public void PoisonDeck_IsSecuredLeaningWeakDrawWithoutEmergency(string list)
    {
        var pack = Pack(list);
        var (_, state, profile) = Setup(pack, PoisonDeck());
        var venom = profile.Archetypes.Single(a => a.Archetype.Id == Venom);
        Assert.True(venom.CoreSecured);
        Assert.Same(venom, profile.Archetypes[0]);
        Assert.True(venom.Commit >= pack.Weights.CorePriority.LeanFull, $"commit {venom.Commit}");
        Assert.Contains(Roles.Draw, profile.WeakRoles);
        Assert.DoesNotContain(Roles.Block, profile.WeakRoles);
        Assert.False(CardScorer.IsEmergency(state, profile, pack.Weights));
    }

    // ---------------- secured essential floor ----------------

    [Theory]
    [MemberData(nameof(Lists))]
    public void SecuredEssential_RaisesEssentialSynergyToTheFloor_SupportAndOtherBuildsGetNothing(string list)
    {
        var pack = Pack(list);
        var (scorer, state, profile) = Setup(pack, PoisonDeck());
        var floor = pack.Weights.CorePriority.SecuredEssentialFloor;

        foreach (var essential in new[] { Enabler, Payoff2 })
        {
            var e = Score(scorer, state, profile, essential);
            var build = e.Score.ValueOf(ComponentKeys.Build);
            Assert.True(build > 0 && build < floor);
            Assert.Equal(floor - build, e.Score.ValueOf(ComponentKeys.SecuredEssential), 9);
            Assert.Equal(floor, Synergy(e), 9);
            var c = e.Score.Components.Single(x => x.Key == ComponentKeys.SecuredEssential);
            Assert.Equal(ScoringLabels.SecuredEssential, c.Label);
            Assert.Contains("독 코어 확보", c.Detail);
        }
        Assert.Equal(0, Score(scorer, state, profile, Support).Score.ValueOf(ComponentKeys.SecuredEssential));
        Assert.Equal(0, Score(scorer, state, profile, BladeEnabler).Score.ValueOf(ComponentKeys.SecuredEssential));
        Assert.Equal(0, Score(scorer, state, profile, Drawer).Score.ValueOf(ComponentKeys.SecuredEssential));
    }

    /// <summary>Guardrail (a): an essential beats a generic card one tier higher with a typical need and a cycle edge.</summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public void SecuredEssential_BeatsGenericDrawCardOneTierHigher(string list)
    {
        var pack = Pack(list);
        // 24 cards in act 2 (ideal 20): the drawer's cycle saves it part of the dilution, as in the real run.
        var (scorer, state, profile) = Setup(pack, PoisonDeck(Strike, Strike, Defend, Defend, Plain, Plain, Plain, Plain, Plain));
        var essential = Score(scorer, state, profile, Enabler);
        var drawer = Score(scorer, state, profile, Drawer);

        Assert.Equal(Step, drawer.Score.ValueOf(ComponentKeys.Base) - essential.Score.ValueOf(ComponentKeys.Base), 9);
        var needEdge = drawer.Score.ValueOf(ComponentKeys.Need) - essential.Score.ValueOf(ComponentKeys.Need);
        var cycleEdge = drawer.Score.ValueOf(ComponentKeys.Dilution) - essential.Score.ValueOf(ComponentKeys.Dilution);
        Assert.True(needEdge > 3, $"need edge {needEdge}");
        Assert.True(cycleEdge > 3, $"cycle edge {cycleEdge}");
        Assert.True(essential.Total > drawer.Total, $"{essential.Score} ≤ {drawer.Score}");

        // Without the floor the generic card one tier higher wins, as it did in the replayed run.
        var off = Pack(list, new Weights { CorePriority = { SecuredEssentialFloor = 0 } });
        var (s0, st0, p0) = Setup(off, PoisonDeck(Strike, Strike, Defend, Defend, Plain, Plain, Plain, Plain, Plain));
        Assert.True(Score(s0, st0, p0, Drawer).Total > Score(s0, st0, p0, Enabler).Total);
    }

    /// <summary>Guardrail (a): never beats a generic card two tiers higher (the floor stays below two tier steps).</summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public void SecuredEssential_LosesToGenericCardTwoTiersHigher(string list)
    {
        var pack = Pack(list);
        var (scorer, state, profile) = Setup(pack, PoisonDeck());
        var essential = Score(scorer, state, profile, Enabler);   // C
        var bigDrawer = Score(scorer, state, profile, BigDrawer); // A
        Assert.Equal(2 * Step, bigDrawer.Score.ValueOf(ComponentKeys.Base) - essential.Score.ValueOf(ComponentKeys.Base), 9);
        Assert.True(pack.Weights.CorePriority.SecuredEssentialFloor < 2 * Step);
        // Even with no need at all the two-tier card is ahead on base alone.
        Assert.True(essential.Score.ValueOf(ComponentKeys.Base) + Synergy(essential) < bigDrawer.Score.ValueOf(ComponentKeys.Base));
        Assert.True(bigDrawer.Total > essential.Total);
    }

    /// <summary>
    /// Guardrail (a) in a committed deck: a role-less card two tiers higher with no need takes the off-build penalty, which is capped so
    /// that penalty + floor stays below two tier steps; the two-tier card still wins (without the cap it would lose 55 to 62).
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public void SecuredEssential_LosesToNoNeedCardTwoTiersHigher_InACommittedDeck(string list)
    {
        var pack = Pack(list);
        var (scorer, state, profile) = Setup(pack, PoisonDeck(Enabler, Payoff2, Blocker, Blocker, Blocker));
        var venom = profile.Archetypes.Single(a => a.Archetype.Id == Venom);
        Assert.True(venom.Commit >= venom.Archetype.CommitThreshold);
        Assert.False(CardScorer.IsEmergency(state, profile, pack.Weights));
        var essential = Score(scorer, state, profile, Enabler2);
        var star = Score(scorer, state, profile, Star);
        Assert.Equal(2 * Step, star.Score.ValueOf(ComponentKeys.Base) - essential.Score.ValueOf(ComponentKeys.Base), 9);
        Assert.True(star.Score.ValueOf(ComponentKeys.Need) <= 0);
        Assert.True(star.Score.ValueOf(ComponentKeys.OffBuild) < 0);
        Assert.Contains("티어 두 칸 미만", star.Score.Components.Single(c => c.Key == ComponentKeys.OffBuild).Detail);
        Assert.True(star.Total > essential.Total, $"{star.Score} ≤ {essential.Score}");
        Assert.True(star.Total - essential.Total >= CardScorer.TwoTierMargin - 1e-9);
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public void SecuredEssential_RampsWithCommit_OnlyForTheLeaningBuild(string list)
    {
        var pack = Pack(list);
        var (scorer, state, profile) = Setup(pack, PoisonDeck());
        var cp = pack.Weights.CorePriority;
        var venom = profile.Archetypes.Single(a => a.Archetype.Id == Venom);

        // Halfway from leanMin to leanFull: half the floor (when that is above the card's own build value).
        var half = profile with { Archetypes = profile.Archetypes.Select(a => a == venom ? a with { Commit = (cp.LeanMin + cp.LeanFull) / 2 } : a).ToArray() };
        var e = Score(scorer, state, half, Enabler);
        Assert.Equal(Math.Max(e.Score.ValueOf(ComponentKeys.Build), cp.SecuredEssentialFloor / 2), Synergy(e), 9);
        // At leanMin (core plus little else): nothing yet.
        var start = profile with { Archetypes = profile.Archetypes.Select(a => a == venom ? a with { Commit = cp.LeanMin } : a).ToArray() };
        Assert.Equal(0, Score(scorer, state, start, Enabler).Score.ValueOf(ComponentKeys.SecuredEssential));

        // Another build far ahead: venom is not leaning, no floor.
        var behind = profile with
        {
            Archetypes = profile.Archetypes.Select(a => a.Archetype.Id == Blades ? a with { Commit = 1.0 } : a with { Commit = 0.5 }).ToArray(),
        };
        Assert.Equal(0, Score(scorer, state, behind, Enabler).Score.ValueOf(ComponentKeys.SecuredEssential));
    }

    /// <summary>Guardrail (c): once the build holds essentialTarget + extra essentials, the floor tapers so needed generic cards win again.</summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public void SecuredEssential_TapersOnceTheBuildIsDone(string list)
    {
        var pack = Pack(list);
        var cp = pack.Weights.CorePriority;
        // Owned essentials: Payoff (deck) + n more.
        double FloorWith(params string[] more)
        {
            var (scorer, state, profile) = Setup(pack, PoisonDeck(more));
            var venom = profile.Archetypes.Single(a => a.Archetype.Id == Venom);
            var e = Score(scorer, state, profile, Enabler2);
            return e.Score.ValueOf(ComponentKeys.SecuredEssential) > 0 ? Synergy(e) : -venom.OwnedEssential;
        }
        var (_, _, p) = Setup(pack, PoisonDeck(Enabler, Payoff2));
        var target = p.Archetypes.Single(a => a.Archetype.Id == Venom).EssentialTarget;
        Assert.Equal(2, target);
        // 3 essentials owned (≤ target + extra = 4): full floor.
        Assert.Equal(cp.SecuredEssentialFloor, FloorWith(Enabler, Payoff2), 9);
        if (list == "single-core")
        {
            // AltCore is an essential here: 4 owned → still full; Enabler2 itself would be the 5th.
            Assert.Equal(cp.SecuredEssentialFloor, FloorWith(Enabler, Payoff2, AltCore), 9);
        }
        // Explicit taper check with a tiny extra: 3 owned, target 2, extra 0 → excess 1 → ×0.5.
        var tight = Pack(list, new Weights { CorePriority = { SecuredEssentialExtra = 0 } });
        var (s2, st2, p2) = Setup(tight, PoisonDeck(Enabler, Payoff2));
        var e2 = Score(s2, st2, p2, Enabler2);
        Assert.Equal(Math.Max(e2.Score.ValueOf(ComponentKeys.Build), cp.SecuredEssentialFloor * 0.5), Synergy(e2), 9);
        Assert.Contains("감쇠 ×0.5", e2.Score.Components.Single(c => c.Key == ComponentKeys.SecuredEssential).Detail);
        // 4 owned, target 2, extra 0 → excess 2 → no floor at all; the card is back to its plain build value.
        var (s3, st3, p3) = Setup(tight, PoisonDeck(Enabler, Payoff2, Enabler2));
        Assert.Equal(4, p3.Archetypes.Single(a => a.Archetype.Id == Venom).OwnedEssential);
        var e3 = Score(s3, st3, p3, Enabler);
        Assert.Equal(0, e3.Score.ValueOf(ComponentKeys.SecuredEssential));
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public void SecuredEssential_NotBeforeTheCore_NotWithoutTheRequiredAxis(string list)
    {
        var pack = Pack(list);
        // No core: synergy locked as before, no floor.
        var deck = PoisonDeck();
        deck.Remove(VenomCore);
        deck.AddRange([Enabler2, Payoff2]);
        var (scorer, state, profile) = Setup(pack, deck);
        Assert.False(profile.Archetypes.Single(a => a.Archetype.Id == Venom).CoreSecured);
        Assert.Equal(0, Score(scorer, state, profile, Enabler).Score.ValueOf(ComponentKeys.SecuredEssential));

        // Core owned but no POISON producer in the deck: a payoff that requires POISON gets "엔진 없음" and no floor.
        var dry = new List<string>(Enumerable.Repeat(Strike, 5)) { Defend, Defend, Defend, Defend, Defend, Blocker, Blocker, Cycler };
        var coreWithoutAxis = list == "core-heavy" ? AltCore : VenomCore;
        var pack2 = Pack(list);
        pack2.Tiers[Hero].Cards[coreWithoutAxis].Provides = [];
        var (s2, st2, p2) = Setup(pack2, [.. dry, coreWithoutAxis, Enabler]);
        pack2.Tiers[Hero].Cards[Enabler].Provides = [];
        p2 = DeckAnalyzer.Analyze(st2, pack2);
        var payoff = Score(s2, st2, p2, Payoff);
        Assert.NotEqual(0, payoff.Score.ValueOf(ComponentKeys.EngineMissing));
        Assert.Equal(0, payoff.Score.ValueOf(ComponentKeys.SecuredEssential));
    }

    // ---------------- emergencies still win (guardrail b) ----------------

    [Theory]
    [MemberData(nameof(Lists))]
    public void Emergency_NoBlock_HalvesTheFloorAndBlockCardWins(string list)
    {
        var pack = Pack(list);
        // Same deck without any block card: block gap 1 (emergency).
        var deck = PoisonDeck();
        deck.RemoveAll(id => id is Defend or Blocker);
        deck.AddRange(Enumerable.Repeat(Strike, 3));
        var (scorer, state, profile) = Setup(pack, deck);
        Assert.True(CardScorer.IsEmergency(state, profile, pack.Weights));
        var cp = pack.Weights.CorePriority;

        var essential = Score(scorer, state, profile, Payoff2); // C, block 0.5
        // Halved, and on a weak deck never above the weak build cap that is back in force.
        Assert.True(profile.IsWeak);
        Assert.Equal(Math.Min(cp.SecuredEssentialFloor * cp.EmergencyFactor, pack.Weights.BuildCapWeak), Synergy(essential), 9);
        Assert.Contains("위급", essential.Score.Components.Single(c => c.Key == ComponentKeys.SecuredEssential).Detail);

        // A same-tier essential (raise Payoff2 to B) still loses to a B-tier block card when there is no block at all.
        pack.Tiers[Hero].Cards[Payoff2].Tier = Tier.B;
        pack.Tiers[Hero].Cards[Payoff2].Roles = new Dictionary<string, double>(StringComparer.Ordinal);
        var sameTier = Score(scorer, state, profile, Payoff2);
        var blocker = Score(scorer, state, profile, Blocker);
        Assert.True(blocker.Total > sameTier.Total, $"{blocker.Score} ≤ {sameTier.Score}");
        // ...which needs the emergency factor: at the full floor the essential would have won.
        Assert.True(sameTier.Total - Synergy(sameTier) + cp.SecuredEssentialFloor > blocker.Total);
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public void Emergency_LowHp_BlockCardOneTierHigherWins(string list)
    {
        var pack = Pack(list);
        var (scorer, state, profile) = Setup(pack, PoisonDeck(), hp: 14); // 20% HP, block itself covered
        Assert.True(CardScorer.IsEmergency(state, profile, pack.Weights));
        var essential = Score(scorer, state, profile, Enabler);
        var wall = Score(scorer, state, profile, Wall); // a new B-tier block card
        Assert.True(wall.Score.ValueOf(ComponentKeys.Situation) > 0); // low-HP block boost
        Assert.True(wall.Total > essential.Total, $"{wall.Score} ≤ {essential.Score}");

        // Same deck at full HP: the essential wins.
        var (s2, st2, p2) = Setup(pack, PoisonDeck());
        Assert.True(Score(s2, st2, p2, Enabler).Total > Score(s2, st2, p2, Wall).Total);
    }

    // ---------------- weak build cap ----------------

    [Theory]
    [MemberData(nameof(Lists))]
    public void WeakCap_NotAppliedToASecuredBuild_BackInAnEmergency(string list)
    {
        var weights = new Weights { WArch = 60 }; // a support card's build value lands between the weak cap (12) and the cap (25)
        var pack = Pack(list, weights);
        var deck = PoisonDeck(Enabler); // core + 2 essentials: support counts fully
        var (scorer, state, profile) = Setup(pack, deck);
        Assert.True(profile.IsWeak);
        var venom = profile.Archetypes.Single(a => a.Archetype.Id == Venom);
        var ungated = weights.WArch * venom.Commit * venom.Archetype.WeightOf(Support);
        Assert.InRange(ungated, weights.BuildCapWeak + 1, weights.BuildCap - 1);
        Assert.Equal(ungated, Score(scorer, state, profile, Support).Score.ValueOf(ComponentKeys.Build), 9);

        // Unsecured build on the same weak deck: weak cap.
        var noCore = PoisonDeck(Enabler, Payoff2);
        noCore.Remove(VenomCore);
        var (s2, st2, p2) = Setup(pack, noCore);
        Assert.Equal(weights.BuildCapWeak, Score(s2, st2, p2, Support).Score.ValueOf(ComponentKeys.Build), 9);

        // Secured but low HP (emergency): weak cap again.
        var (s3, st3, p3) = Setup(pack, deck, hp: 14);
        Assert.Equal(weights.BuildCapWeak, Score(s3, st3, p3, Support).Score.ValueOf(ComponentKeys.Build), 9);
    }

    // ---------------- on-theme cards ----------------

    [Theory]
    [MemberData(nameof(Lists))]
    public void OnTheme_AxisTagAndListedCards_AreNotOffBuild_AndGetNoSynergy(string list)
    {
        var pack = Pack(list);
        // Committed (commit ≥ 0.7) and secured, block and frontload covered so `need` does not hide the off-build penalty.
        var deck = PoisonDeck(Enabler, Payoff2, Blocker, Blocker, Blocker);
        var (scorer, state, profile) = Setup(pack, deck);
        var venom = profile.Archetypes.Single(a => a.Archetype.Id == Venom);
        Assert.True(venom.Commit >= venom.Archetype.CommitThreshold, $"commit {venom.Commit}");

        var plain = Score(scorer, state, profile, Plain);
        // Off-build, capped so that penalty + the venom floor stays below two tier steps.
        Assert.Equal(-Math.Min(pack.Weights.OffBuildPenalty, 2 * Step - pack.Weights.CorePriority.SecuredEssentialFloor - CardScorer.TwoTierMargin),
            plain.Score.ValueOf(ComponentKeys.OffBuild), 9);
        foreach (var themed in new[] { AxisTheme, ListedTheme })
        {
            var e = Score(scorer, state, profile, themed);
            Assert.Equal(0, e.Score.ValueOf(ComponentKeys.OffBuild));
            Assert.Equal(0, Synergy(e));
            Assert.Empty(e.Synergy);
            Assert.Empty(e.BuildRoles);
            Assert.True(venom.Archetype.IsOnTheme(themed, pack.FindTier(Hero, themed), pack.Canonical));
        }
        // Role-list cards and unrelated cards are not "on-theme"; on-theme adds nothing to commit.
        Assert.False(venom.Archetype.IsOnTheme(Enabler, pack.FindTier(Hero, Enabler), pack.Canonical));
        Assert.False(venom.Archetype.IsOnTheme(Plain, pack.FindTier(Hero, Plain), pack.Canonical));
        var (_, _, withThemes) = Setup(pack, PoisonDeck(Enabler, Payoff2, Blocker, Blocker, Blocker, AxisTheme, ListedTheme));
        Assert.Equal(venom.Commit, withThemes.Archetypes.Single(a => a.Archetype.Id == Venom).Commit, 9);
    }

    [Fact]
    public void OnTheme_Validator_RejectsRoleOverlapAndMalformedIds()
    {
        var pack = Pack("single-core");
        Assert.DoesNotContain(DataPackValidator.Validate(pack), i => i.Where.Contains("onTheme"));

        pack.Archetypes[0].OnTheme.AddRange([Enabler, "not-a-card", ListedTheme]);
        var issues = DataPackValidator.Validate(pack).Where(i => i.Where.Contains("onTheme")).ToList();
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where.Contains(Enabler) && i.Message.Contains("role list"));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("Malformed"));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning && i.Message.Contains("twice"));
    }

    [Fact]
    public void Weights_SecuredEssentialAndEmergency_AreValidated()
    {
        var pack = Pack("single-core", new Weights
        {
            CorePriority = { SecuredEssentialFloor = 31, SecuredEssentialTaper = 2, EmergencyGap = -1, EmergencyFactor = 1.5, SecuredEssentialExtra = -1, EmergencyRoles = ["nope"] },
        });
        var issues = DataPackValidator.Validate(pack);
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning && i.Where == "weights.corePriority.securedEssentialFloor");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == "weights.corePriority.securedEssentialTaper");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == "weights.corePriority.emergencyGap");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == "weights.corePriority.emergencyFactor");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == "weights.corePriority.securedEssentialExtra");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning && i.Where == "weights.corePriority.emergencyRoles.nope");
    }

    [Fact]
    public void OnTheme_LoadsFromArchetypeJson()
    {
        const string json = """
            { "character": "CHARACTER.SILENT", "archetypes": [ { "id": "t.venom", "cards": { "core": ["CARD.T_VENOM_CORE"] },
              "onTheme": ["CARD.T_LISTED_THEME"] } ] }
            """;
        var a = DataPackLoader.LoadArchetypes(json).Single();
        Assert.Equal([ListedTheme], a.OnTheme);
    }
}
