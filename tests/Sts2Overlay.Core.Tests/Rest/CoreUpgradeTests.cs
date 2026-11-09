using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Tests.Data;

namespace Sts2Overlay.Core.Tests.Rest;

/// <summary>
/// Rest-site upgrades with the real v0.107.1 pack (upgradeValue + weights.json "upgrade", docs/research/UPGRADE_CALIBRATION.md).
/// Regression: in an Ironclad deck built around 지옥검무 (Hellraiser), the overlay advised upgrading 천둥 (Thunderclap): both had
/// upgradedDelta 4 and Thunderclap's AoE gap tipped it. Top players upgrade Hellraiser 30 of the 75 times it is a candidate
/// (cost 2 → 1, the build's core) and Thunderclap 4 of 196.
/// </summary>
public class CoreUpgradeTests
{
    private const string Ironclad = "CHARACTER.IRONCLAD";
    private const string Hellraiser = "CARD.HELLRAISER";
    private const string Thunderclap = "CARD.THUNDERCLAP";

    private static readonly Lazy<DataPack> AppPack = new(() => DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1")));

    private static RunState Ironclad1(int act, params string[] deck) => new()
    {
        Character = Ironclad,
        Act = act,
        Hp = 60,
        MaxHp = 80,
        Deck = deck.Select(id => new DeckCard(new CardRef(id))).ToArray(),
    };

    private static readonly string[] StrikeDeck =
    [
        "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD",
        "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.BASH",
        Hellraiser, "CARD.TWIN_STRIKE", "CARD.POMMEL_STRIKE", "CARD.PERFECTED_STRIKE", Thunderclap,
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Hellraiser_build_upgrades_its_core_before_Thunderclap(int act)
    {
        var ranked = new UpgradeAdvisor(AppPack.Value).Rank(Ironclad1(act, StrikeDeck));

        var hell = ranked.Single(c => c.Card.Id == Hellraiser);
        var thunder = ranked.Single(c => c.Card.Id == Thunderclap);
        Assert.True(hell.Rank < thunder.Rank, $"act {act}: Hellraiser #{hell.Rank} {hell.Total:0.00}, Thunderclap #{thunder.Rank} {thunder.Total:0.00}");
        Assert.Equal(1, hell.Rank);
        // The build part is there and says which build: the core of the committed Hellraiser build.
        var build = hell.Score.Components.Single(c => c.Key == UpgradeComponentKeys.Build);
        Assert.True(build.Value > 0);
        Assert.Contains("코어", build.Label);
        Assert.Contains(hell.Chips, ch => ch.Key == UpgradeComponentKeys.Build);
    }

    [Fact]
    public void Without_the_build_Hellraiser_still_beats_Thunderclap_on_its_own_upgrade_value()
    {
        // Hellraiser's upgrade itself (cost 2 → 1 on a power) is worth more to top players than +3 AoE damage.
        var pack = AppPack.Value;
        Assert.True(pack.FindTier(Ironclad, Hellraiser)!.UpgradeValue > pack.FindTier(Ironclad, Thunderclap)!.UpgradeValue);
        var ranked = new UpgradeAdvisor(pack).Rank(Ironclad1(1, "CARD.STRIKE_IRONCLAD", "CARD.DEFEND_IRONCLAD", Hellraiser, Thunderclap));
        Assert.True(ranked.Single(c => c.Card.Id == Hellraiser).Rank < ranked.Single(c => c.Card.Id == Thunderclap).Rank);
    }

    [Fact]
    public void Every_card_of_the_real_pack_has_a_calibrated_upgrade_value_and_role_weights_are_set()
    {
        var pack = AppPack.Value;
        var missing = pack.Tiers.Values.SelectMany(t => t.Cards.Where(kv => kv.Value.UpgradeValue is null).Select(kv => kv.Key)).ToArray();
        Assert.Empty(missing);
        Assert.All(new[] { ArchetypeRoles.Core, ArchetypeRoles.Payoff, ArchetypeRoles.Enabler, ArchetypeRoles.Support },
            r => Assert.True(pack.Weights.Upgrade.BuildRole.GetValueOrDefault(r) > 0, r));
        // Core and payoff count more than enabler and support (fitted: 0.71, 0.64 vs 0.36, 0.39).
        var w = pack.Weights.Upgrade.BuildRole;
        Assert.True(Math.Min(w[ArchetypeRoles.Core], w[ArchetypeRoles.Payoff]) > Math.Max(w[ArchetypeRoles.Enabler], w[ArchetypeRoles.Support]));
    }

    [Fact]
    public void Build_part_reads_in_english()
    {
        using var _ = Loc.Use(Lang.Eng);
        var hell = new UpgradeAdvisor(AppPack.Value).Rank(Ironclad1(2, StrikeDeck)).Single(c => c.Card.Id == Hellraiser);
        var build = hell.Score.Components.Single(c => c.Key == UpgradeComponentKeys.Build);
        Assert.Equal("Hellraiser core", build.Label);
        Assert.DoesNotMatch("[가-힣]", build.Label + build.Detail + hell.Score.Components[0].Label + hell.Score.Components[0].Detail);
    }
}
