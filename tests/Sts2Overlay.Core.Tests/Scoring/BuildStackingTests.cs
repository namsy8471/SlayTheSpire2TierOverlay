using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Scoring;

/// <summary>
/// Rules from the deck simulation (docs/research/DECK_SIM.md), on a synthetic pack (ids <c>CARD.T_*</c>):
/// a build without core cards is secured by its essential target (secured floor, weak-cap exemption); an axis a leaning build needs
/// is never "over-produced"; another copy of a card the deck's build lists loses only <see cref="Weights.BuildDuplicateFactor"/> of the
/// duplicate penalty.
/// </summary>
public class BuildStackingTests
{
    private const string Hero = "CHARACTER.SILENT";
    private const string Strike = "CARD.STRIKE_SILENT";
    private const string Defend = "CARD.DEFEND_SILENT";
    private const string Maker = "CARD.T_BLADE_MAKER";
    private const string Maker2 = "CARD.T_BLADE_MAKER2";
    private const string Payoff = "CARD.T_BLADE_PAYOFF";
    private const string Support = "CARD.T_BLADE_SUPPORT";
    private const string Drawer = "CARD.T_DRAWER";
    private const string Plain = "CARD.T_PLAIN";
    private const string Blades = "t.blades";

    /// <summary>
    /// Blades: no core list, needs the BLADE tag. Makers provide BLADE, nothing consumes it (as with DISCARD or ORB_DARK in the real pack).
    /// Full weight 2: two essentials (0.7 + 0.9) make commit 0.8, a third essential commit 1.
    /// </summary>
    private static DataPack Pack(Weights? weights = null)
    {
        static Dictionary<string, double> R(params (string Role, double S)[] r) => r.ToDictionary(x => x.Role, x => x.S, StringComparer.Ordinal);
        var cards = new Dictionary<string, CardTierEntry>(StringComparer.Ordinal)
        {
            [Strike] = new() { Tier = Tier.D, Type = "attack", Rarity = "basic", Roles = R((Roles.Frontload, 0.3)) },
            [Defend] = new() { Tier = Tier.D, Type = "skill", Rarity = "basic", Roles = R((Roles.Block, 0.3)) },
            [Maker] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Frontload, 0.5)), Provides = ["BLADE"] },
            [Maker2] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Frontload, 0.5)), Provides = ["BLADE"] },
            [Payoff] = new() { Tier = Tier.B, Type = "power", Roles = R((Roles.Scaling, 0.5)) },
            [Support] = new() { Tier = Tier.C, Type = "skill", Roles = R((Roles.Block, 0.4)), Provides = ["BLADE"] },
            [Drawer] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Draw, 0.5)), Provides = ["DRAW"] },
            [Plain] = new() { Tier = Tier.B, Type = "skill", Roles = R((Roles.Block, 0.6)) },
        };
        return new DataPack
        {
            Weights = weights ?? new Weights(),
            Tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal) { [Hero] = new TierPack { Character = Hero, Cards = cards } },
            Archetypes =
            [
                new Archetype
                {
                    Id = Blades, Character = Hero, Names = new() { ["kor"] = "칼날" }, FullWeight = 2.0,
                    Cards = new ArchetypeCards { Enabler = [Maker, Maker2], Payoff = [Payoff], Support = [Support] },
                    Needs = [new ArchetypeNeed { Tag = "BLADE", Min = 3 }],
                },
            ],
        };
    }

    private static List<string> Starter()
    {
        var deck = new List<string>();
        deck.AddRange(Enumerable.Repeat(Strike, 5));
        deck.AddRange(Enumerable.Repeat(Defend, 5));
        return deck;
    }

    private static CardEvaluation Score(DataPack pack, IEnumerable<string> deck, string card, int act = 2)
    {
        var state = new RunState
        {
            Character = Hero, Act = act, Hp = 70, MaxHp = 70,
            Deck = deck.Select(id => new DeckCard(new CardRef(id))).ToArray(),
        };
        return new CardScorer(pack).Score(state, DeckAnalyzer.Analyze(state, pack), new CardRef(card));
    }

    private static Dictionary<string, int> Copies(params string[] ids)
        => ids.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    [Fact]
    public void Coreless_build_is_secured_by_its_essential_target()
    {
        var pack = Pack();
        Assert.False(CardScorer.BuildSecured(pack.Archetypes[0], pack, Copies(Maker)));
        Assert.True(CardScorer.BuildSecured(pack.Archetypes[0], pack, Copies(Maker, Payoff)));
    }

    [Fact]
    public void Coreless_build_essentials_get_the_secured_floor_once_the_build_stands()
    {
        var pack = Pack();
        var before = Score(pack, [.. Starter(), Maker], Maker2);
        Assert.Equal(0, before.Score.ValueOf(ComponentKeys.SecuredEssential));

        var after = Score(pack, [.. Starter(), Maker, Payoff], Maker2);
        var floor = pack.Weights.CorePriority.SecuredEssentialFloor;
        // build + core_missing + secured_essential reaches the floor (before saturation/duplicates, which do not apply here).
        Assert.True(after.Score.ValueOf(ComponentKeys.SecuredEssential) > 0);
        Assert.Equal(floor, after.Score.ValueOf(ComponentKeys.Build) + after.Score.ValueOf(ComponentKeys.CoreMissing)
            + after.Score.ValueOf(ComponentKeys.SecuredEssential), 6);
    }

    [Fact]
    public void An_axis_the_leaning_build_needs_is_not_saturated()
    {
        // Four BLADE makers, nothing consumes BLADE: 4 > 2 × max(1, 0) would cut the synergy without the exemption.
        var deck = Starter().Concat([Maker, Maker, Maker2, Maker2, Payoff]).ToList();
        var exempt = Score(Pack(), deck, Support);
        Assert.Equal(0, exempt.Score.ValueOf(ComponentKeys.Saturation));

        var w = new Weights();
        w.Saturation.ExemptBuildAxes = false;
        var ruled = Score(Pack(w), deck, Support);
        Assert.True(ruled.Score.ValueOf(ComponentKeys.Saturation) < 0);
    }

    [Fact]
    public void An_axis_no_leaning_build_needs_still_saturates()
    {
        var deck = Starter().Concat([Drawer, Drawer, Drawer, Maker, Payoff]).ToList();
        var eval = Score(Pack(), deck, Drawer, act: 3); // short on draw: the need is the value the rule cuts
        Assert.True(eval.Score.ValueOf(ComponentKeys.Saturation) < 0);
    }

    [Fact]
    public void Another_copy_of_a_build_card_loses_only_part_of_the_duplicate_penalty()
    {
        var pack = Pack();
        var deck = Starter().Concat([Maker, Maker2, Payoff]).ToList(); // commit 1, no core list → gate 1
        var w = pack.Weights;

        var buildCopy = Score(pack, deck, Maker);
        Assert.Equal(-w.RhoDuplicate * w.BuildDuplicateFactor, buildCopy.Score.ValueOf(ComponentKeys.Duplicate), 6);

        var generic = Score(pack, [.. deck, Plain], Plain);
        Assert.Equal(-w.RhoDuplicate, generic.Score.ValueOf(ComponentKeys.Duplicate), 6);
    }

    [Fact]
    public void Duplicate_relief_scales_with_the_build_commit()
    {
        var pack = Pack();
        // One essential: commit 0.35 → penalty × (1 − 0.6 × 0.35).
        var eval = Score(pack, [.. Starter(), Maker], Maker);
        var commit = DeckAnalyzer.Analyze(new RunState
        {
            Character = Hero, Act = 2, Deck = Starter().Append(Maker).Select(id => new DeckCard(new CardRef(id))).ToArray(),
        }, pack).Archetypes.Single().Commit;
        var w = pack.Weights;
        Assert.Equal(-w.RhoDuplicate * (1 - (1 - w.BuildDuplicateFactor) * commit), eval.Score.ValueOf(ComponentKeys.Duplicate), 6);
    }

    [Fact]
    public void Build_duplicate_factor_one_restores_the_full_penalty()
    {
        var pack = Pack(new Weights { BuildDuplicateFactor = 1.0 });
        var eval = Score(pack, Starter().Concat([Maker, Maker2, Payoff]), Maker);
        Assert.Equal(-pack.Weights.RhoDuplicate, eval.Score.ValueOf(ComponentKeys.Duplicate), 6);
    }
}
