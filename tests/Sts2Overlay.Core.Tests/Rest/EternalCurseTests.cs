using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;
using Sts2Overlay.Core.Tests.Data;
using Sts2Overlay.Core.Tests.Scoring;

namespace Sts2Overlay.Core.Tests.Rest;

/// <summary>
/// Eternal cards ("cannot be removed or transformed from your deck": Ascender's Bane, Greed, …) are never a removal or
/// transform target, and curses are never counted as upgradable, with the real pack and catalog. Ascender's Bane is in
/// every deck from ascension 5, so a shop that "removes" it would be advised in every such run.
/// </summary>
public class EternalCurseTests
{
    private const string Silent = "CHARACTER.SILENT";
    private const string Bane = "CARD.ASCENDERS_BANE";
    private const string Greed = "CARD.GREED";
    private const string Doubt = "CARD.DOUBT"; // a curse that can be removed

    private static readonly Lazy<DataPack> Pack = new(() =>
        DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1")));

    private static RunState Deck(params string[] cards) => TestPacks.State(deck: cards) with { Character = Silent };

    [Fact]
    public void Eternal_cards_are_exactly_the_catalog_cards_with_the_keyword()
    {
        var pack = Pack.Value;
        var eternal = pack.Catalog!.Cards.Where(c => pack.IsEternal(c.Id)).Select(c => c.Id).Order().ToArray();

        Assert.Contains(Bane, eternal);
        Assert.Contains(Greed, eternal);
        Assert.DoesNotContain(Doubt, eternal);
        Assert.Equal(pack.Catalog.Cards.Where(c => c.Keywords.Contains("Eternal")).Select(c => c.Id).Order(), eternal);
        Assert.False(pack.IsEternal("CARD.NOT_A_CARD"));
    }

    [Theory]
    [InlineData(new[] { Bane, Doubt, "CARD.STRIKE_SILENT" }, Doubt, true)]
    [InlineData(new[] { Bane, Greed, "CARD.STRIKE_SILENT", "CARD.DEFEND_SILENT" }, "CARD.STRIKE_SILENT", false)]
    [InlineData(new[] { Greed, "CARD.DEFEND_SILENT" }, "CARD.DEFEND_SILENT", false)]
    public void Shop_removal_skips_eternal_curses(string[] deck, string expected, bool expectedCurse)
    {
        var target = new ShopScorer(Pack.Value).RemovalTarget(Deck(deck), out var isCurse);

        Assert.Equal(expected, target);
        Assert.Equal(expectedCurse, isCurse);
    }

    [Fact]
    public void Event_removal_and_transform_never_take_an_eternal_curse()
    {
        var pack = Pack.Value;
        var state = Deck(Bane, Greed, "CARD.STRIKE_SILENT", "CARD.DEFEND_SILENT", "CARD.NEUTRALIZE");
        var profile = DeckAnalyzer.Analyze(state, pack);
        var w = new EventWeights();

        // Chosen removal of 1: the Strike (14), not a curse (30).
        Assert.Equal(pack.Weights.Shop.BasicRemovalValue, new DeckPool(pack, state, profile).Remove(1, choose: true, null, w), 9);
        // Chosen transform of 1: the Strike, not a curse.
        Assert.Equal(w.TransformBasic, new DeckPool(pack, state, profile).Transform(1, choose: true, null, w), 9);
        // Random removal: expected value over the three removable cards only.
        var expected = (2 * pack.Weights.Shop.BasicRemovalValue + w.RemoveOtherRandom) / 3;
        Assert.Equal(expected, new DeckPool(pack, state, profile).Remove(1, choose: false, null, w), 9);
    }

    [Fact]
    public void Upgradable_cards_for_events_exclude_curses_statuses_and_quest_cards()
    {
        var pack = Pack.Value;
        var state = Deck(Doubt, Greed, "CARD.BYRDONIS_EGG", "CARD.STRIKE_SILENT", "CARD.NEUTRALIZE", "CARD.SURVIVOR+1");
        var pool = new DeckPool(pack, state, DeckAnalyzer.Analyze(state, pack));

        Assert.Equal(2, pool.Upgradable(null)); // Strike and Neutralize
    }

    [Fact]
    public void Free_text_event_filters_pick_the_right_kinds()
    {
        var pack = Pack.Value;
        var state = Deck(Doubt, "CARD.STRIKE_SILENT", "CARD.NEUTRALIZE");
        var w = new EventWeights();
        DeckPool Pool() => new(pack, state, DeckAnalyzer.Analyze(state, pack));

        // PAEL_TOOTH removes upgradable cards: never the curse, so the Strike goes first.
        Assert.Equal(pack.Weights.Shop.BasicRemovalValue, Pool().Remove(1, true, "upgradable cards; removed cards stored in relic", w), 9);
        // SLIPPERY_BRIDGE removes a random non-Basic card: the curse or Neutralize, never the Strike.
        Assert.Equal((pack.Weights.Shop.CurseRemovalValue + w.RemoveOtherRandom) / 2,
            Pool().Remove(1, false, "displayed card: random non-Basic removable card (basic only if nothing else)", w), 9);
        // ARCHAIC_TOOTH transforms the starter card (Neutralize), not the curse.
        Assert.Equal(w.TransformOther, Pool().Transform(1, true,
            "first deck starter card among Bash/Neutralize/Unleash/Falling Star/Dualcast becomes Break/Suppress/Protector/Meteor Shower/Quadcast", w), 9);
    }
}
