using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Scoring;

public class UpgradeAdvisorTests
{
    private const string Big = "CARD.U_BIG";          // A, upgradedDelta 6
    private const string Mid = "CARD.U_MID";          // B, upgradedDelta 4, frontload
    private const string Wall = "CARD.U_WALL";        // B, upgradedDelta 4, strong block
    private const string Twice = "CARD.U_TWICE";      // catalog maxUpgrade 2
    private const string Never = "CARD.U_NEVER";      // catalog maxUpgrade 0 (e.g. a quest card)
    private const string Status = "CARD.U_STATUS";    // status type
    private const string Unrated = "CARD.U_UNRATED";  // not in the tier pack

    /// <summary>A pack whose upgradedDelta values average 4.25 over the rated cards (Strike 3, Big 6, Mid 4, Wall 4).</summary>
    private static DataPack Pack() => new()
    {
        Tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal)
        {
            [TestPacks.Defect] = new TierPack
            {
                Character = TestPacks.Defect,
                Cards = new Dictionary<string, CardTierEntry>(StringComparer.Ordinal)
                {
                    [TestPacks.Strike] = new() { Tier = Tier.D, Type = "attack", UpgradedDelta = 3, Roles = TestPacks.R((Roles.Frontload, 0.3)) },
                    [Big] = new() { Tier = Tier.A, Type = "power", UpgradedDelta = 6, Roles = TestPacks.R((Roles.Scaling, 0.8)) },
                    [Mid] = new() { Tier = Tier.B, Type = "attack", UpgradedDelta = 4, Roles = TestPacks.R((Roles.Frontload, 0.5)) },
                    [Wall] = new() { Tier = Tier.B, Type = "skill", UpgradedDelta = 4, Roles = TestPacks.R((Roles.Block, 1.0)) },
                    [Twice] = new() { Tier = Tier.C, Type = "attack" },
                    [Status] = new() { Tier = Tier.D, Type = "status" },
                },
            },
        },
        Catalog = new CardCatalog
        {
            Cards =
            [
                new CatalogCard { Id = Twice, Type = "Attack", MaxUpgrade = 2 },
                new CatalogCard { Id = Never, Type = "Skill", MaxUpgrade = 0 },
                new CatalogCard { Id = TestPacks.CatalogCurse, Rarity = "Curse" },
            ],
        },
    };

    private static RunState State(params string[] deck) => TestPacks.State(act: 1, deck: deck, hp: 40, maxHp: 50);

    [Fact]
    public void Skips_upgraded_curse_status_and_unupgradable_cards_and_groups_copies()
    {
        var pack = Pack();
        var ranked = new UpgradeAdvisor(pack).Rank(State(
            TestPacks.Strike, TestPacks.Strike, TestPacks.Strike + "+1", Mid + "+1", TestPacks.CatalogCurse, Status, Never, Big));

        Assert.Equal([Big, TestPacks.Strike], ranked.Select(c => c.Card.Id));
        var strike = ranked.Single(c => c.Card.Id == TestPacks.Strike);
        Assert.Equal(2, strike.Copies); // the already upgraded Strike is not a candidate
        Assert.Equal(0, strike.Card.Upgrade);
    }

    [Fact]
    public void Catalog_max_upgrade_above_one_keeps_an_upgraded_card_a_candidate()
    {
        var ranked = new UpgradeAdvisor(Pack()).Rank(State(Twice + "+1"));

        var only = Assert.Single(ranked);
        Assert.Equal(new CardRef(Twice, 1), only.Card);
    }

    [Fact]
    public void Ranks_by_the_packs_upgraded_delta_first()
    {
        // Act 3: role gaps are small next to the delta steps (in act 1 a big frontload gap can lift Mid above Big —
        // that is the role part working as designed, see A_missing_role_breaks_a_tie_between_equal_deltas).
        var ranked = new UpgradeAdvisor(Pack()).Rank(State(TestPacks.Strike, Mid, Big) with { Act = 3 });

        Assert.Equal([Big, Mid, TestPacks.Strike], ranked.Select(c => c.Card.Id));
        Assert.Equal([1, 2, 3], ranked.Select(c => c.Rank));
        Assert.Equal(6, ranked[0].Score.ValueOf(UpgradeComponentKeys.Upgrade));
        Assert.Equal("강화 +6", ranked[0].Chips[0].Label);
        Assert.Equal(Tier.A, ranked[0].DisplayTier);
    }

    [Fact]
    public void A_missing_role_breaks_a_tie_between_equal_deltas()
    {
        // Act 2 (block urgency 1.3): no block in the deck except Wall, so upgrading Wall (block 1.0) closes more of the
        // block gap than upgrading Mid closes the frontload gap.
        var pack = Pack();
        var state = State(Mid, Wall) with { Act = 2 };
        var ranked = new UpgradeAdvisor(pack).Rank(state);

        Assert.Equal(Wall, ranked[0].Card.Id);
        var need = ranked[0].Score.Components.Single(c => c.Key == ComponentKeys.Need);
        Assert.Equal("방어 부족", need.Label);
        Assert.True(need.Value > 0);
        // Only 20% of the pick-time need counts (UpgradedSupplyMultiplier 1.2 − 1).
        var pickNeed = new CardScorer(pack).Score(state, DeckAnalyzer.Analyze(state, pack), new CardRef(Wall))
            .Score.ValueOf(ComponentKeys.Need);
        Assert.Equal(pickNeed * 0.2, need.Value, 6);
    }

    [Fact]
    public void Archetype_and_combo_parts_are_not_part_of_the_upgrade_value()
    {
        // The default test pack puts OrbCore in a committed archetype; upgrading does not change commit, so no build part.
        var pack = TestPacks.DefectPack();
        var state = TestPacks.State(deck: [TestPacks.OrbCore, TestPacks.OrbEnabler, TestPacks.OrbPayoff, TestPacks.ComboA, TestPacks.ComboB]);
        var ranked = new UpgradeAdvisor(pack).Rank(state);

        Assert.All(ranked, c => Assert.DoesNotContain(c.Score.Components, p => p.Key is ComponentKeys.Build or ComponentKeys.Combo
            or ComponentKeys.EngineMissing or ComponentKeys.OffBuild or ComponentKeys.Duplicate or ComponentKeys.Dilution));
    }

    [Fact]
    public void Unrated_card_gets_the_character_average_delta_and_is_an_estimate()
    {
        var ranked = new UpgradeAdvisor(Pack()).Rank(State(Unrated));

        var c = Assert.Single(ranked);
        Assert.Equal((3 + 6 + 4 + 4) / 4.0, c.Score.ValueOf(UpgradeComponentKeys.Upgrade), 6);
        Assert.True(c.IsEstimate);
        Assert.Null(c.DisplayTier);
    }

    [Fact]
    public void Total_is_the_sum_of_components_and_chips_are_at_most_three()
    {
        var ranked = new UpgradeAdvisor(Pack()).Rank(State(TestPacks.Strike, Mid, Wall, Big, Unrated) with { Hp = 5 });

        Assert.All(ranked, c =>
        {
            Assert.Equal(c.Score.Components.Sum(p => p.Value), c.Total, 9);
            Assert.InRange(c.Chips.Count, 1, 3);
        });
    }

    [Fact]
    public void Empty_deck_has_no_candidates()
        => Assert.Empty(new UpgradeAdvisor(Pack()).Rank(State()));
}
