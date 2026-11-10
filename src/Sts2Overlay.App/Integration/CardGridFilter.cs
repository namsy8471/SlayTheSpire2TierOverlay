using Sts2Overlay.Core.Model;

namespace Sts2Overlay.App.Integration;

/// <summary>
/// A card grid on screen is either an offer (an event's "pick 1 of 5", Sea Glass, ...) or the player's own cards
/// (remove / upgrade / transform / enchant from the deck, the deck and pile views). The game draws both with the same
/// grid, so the recogniser cannot tell them apart; the deck can.
/// </summary>
public static class CardGridFilter
{
    /// <summary>
    /// Cards beyond what the deck holds (by id, counting copies: three Strikes on screen are covered by a deck with
    /// five). Upgrades are ignored: the upgrade screen previews the upgraded version of a deck card.
    /// </summary>
    public static int Foreign(IReadOnlyList<CardRef> shown, IEnumerable<CardRef> deck)
    {
        var left = deck.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Count());
        int foreign = 0;
        foreach (var c in shown)
        {
            if (left.TryGetValue(c.Id, out int n) && n > 0) left[c.Id] = n - 1;
            else foreign++;
        }
        return foreign;
    }

    /// <summary>
    /// True when the grid is an offer, by where the player is (the log's screen hint):
    /// <list type="bullet">
    /// <item>No run (run over, nothing loaded) or the main menu: never; there is no pick to make, and the main menu's
    /// card library is a grid of cards the deck does not hold.</item>
    /// <item>Combat: never. Grids in combat are the draw / discard / exhaust piles (with cards generated this fight)
    /// and in-combat "choose a card" effects; none of it is a permanent deck pick.</item>
    /// <item>Shop or rest site: the grids there are deck screens (card removal, Smith, Toke, a relic bought in the shop
    /// that transforms or copies a deck card); event "pick 1 of N" grids happen in event rooms. But the tracked deck
    /// lags behind purchases (the save is written on the next map move), so a removal grid opened after buying two
    /// cards shows two "foreign" cards. Stronger evidence: at least three foreign cards AND at least three quarters of
    /// the cards shown. A real offer grid is (nearly) all new cards; a deck grid is mostly owned cards.</item>
    /// <item>Elsewhere (event rooms, map): at least two cards the deck does not hold. One stray card is not enough,
    /// since a card added a moment ago may not be in the tracked deck yet.</item>
    /// </list>
    /// Without a known deck the grid is assumed to be the player's own cards (deck screens are far more common than offers).
    /// </summary>
    public static bool IsOffer(IReadOnlyList<CardRef> shown, IEnumerable<CardRef>? deck, ScreenHint hint = ScreenHint.None)
    {
        if (deck is null || hint is ScreenHint.Combat or ScreenHint.MainMenu) return false;
        int foreign = Foreign(shown, deck);
        if (hint is ScreenHint.ShopLikely or ScreenHint.RestSite) return foreign >= 3 && foreign * 4 >= shown.Count * 3;
        return foreign >= Math.Min(2, shown.Count);
    }
}
