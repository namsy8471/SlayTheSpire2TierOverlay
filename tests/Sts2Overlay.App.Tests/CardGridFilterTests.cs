using Sts2Overlay.App.Integration;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.App.Tests;

/// <summary>Event card grids (offers) versus the deck screens that share the grid layout.</summary>
public class CardGridFilterTests
{
    private static CardRef C(string id, int up = 0) => new(id, up);

    private static readonly CardRef[] Deck =
    [
        C("CARD.STRIKE_IRONCLAD"), C("CARD.STRIKE_IRONCLAD"), C("CARD.STRIKE_IRONCLAD"), C("CARD.STRIKE_IRONCLAD"),
        C("CARD.DEFEND_IRONCLAD"), C("CARD.DEFEND_IRONCLAD"), C("CARD.BASH"), C("CARD.TREMBLE"),
    ];

    [Fact]
    public void Deck_screen_is_not_an_offer()
    {
        // Upgrade screen: deck cards, one shown with its upgrade preview.
        CardRef[] shown = [C("CARD.STRIKE_IRONCLAD"), C("CARD.STRIKE_IRONCLAD"), C("CARD.DEFEND_IRONCLAD"), C("CARD.BASH", 1), C("CARD.TREMBLE")];
        Assert.Equal(0, CardGridFilter.Foreign(shown, Deck));
        Assert.False(CardGridFilter.IsOffer(shown, Deck));
    }

    [Fact]
    public void Brain_leech_five_new_cards_is_an_offer()
    {
        // Captured 23:14:31: "pick 1 of 5" from the Brain Leech event; Tremble is already in the deck.
        CardRef[] shown = [C("CARD.FORGOTTEN_RITUAL"), C("CARD.TREMBLE"), C("CARD.ARMAMENTS"), C("CARD.INFERNO"), C("CARD.BLOOD_WALL")];
        Assert.Equal(4, CardGridFilter.Foreign(shown, Deck));
        Assert.True(CardGridFilter.IsOffer(shown, Deck));
    }

    [Fact]
    public void Copies_are_counted()
    {
        // Two Bashes on screen but only one in the deck: one foreign card, not enough for an offer.
        CardRef[] shown = [C("CARD.BASH"), C("CARD.BASH"), C("CARD.STRIKE_IRONCLAD")];
        Assert.Equal(1, CardGridFilter.Foreign(shown, Deck));
        Assert.False(CardGridFilter.IsOffer(shown, Deck));
    }

    [Fact]
    public void Unknown_deck_means_no_offer()
    {
        CardRef[] shown = [C("CARD.INFERNO"), C("CARD.ARMAMENTS")];
        Assert.False(CardGridFilter.IsOffer(shown, null));
    }
}
