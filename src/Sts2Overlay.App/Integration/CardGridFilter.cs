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
    /// True when the grid is an offer: at least two cards the deck does not hold. One stray card is not enough, since a
    /// card added a moment ago may not be in the tracked deck yet. Without a known deck the grid is assumed to be the
    /// player's own cards (deck screens are far more common than offers).
    /// </summary>
    public static bool IsOffer(IReadOnlyList<CardRef> shown, IEnumerable<CardRef>? deck)
        => deck is not null && Foreign(shown, deck) >= Math.Min(2, shown.Count);
}
