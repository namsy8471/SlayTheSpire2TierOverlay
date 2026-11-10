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

    private static readonly CardRef[] BrainLeech =
        [C("CARD.FORGOTTEN_RITUAL"), C("CARD.TREMBLE"), C("CARD.ARMAMENTS"), C("CARD.INFERNO"), C("CARD.BLOOD_WALL")];

    [Fact]
    public void An_event_offer_grid_stays_an_offer_in_an_event_room_and_on_the_map()
    {
        Assert.True(CardGridFilter.IsOffer(BrainLeech, Deck, ScreenHint.None)); // event rooms map to None
        Assert.True(CardGridFilter.IsOffer(BrainLeech, Deck, ScreenHint.Map));
        Assert.True(CardGridFilter.IsOffer(BrainLeech, Deck, ScreenHint.RewardLikely));
    }

    [Fact]
    public void Grids_in_combat_and_on_the_main_menu_are_never_offers()
    {
        // Draw / discard pile with cards generated this fight; the main menu's card library.
        Assert.False(CardGridFilter.IsOffer(BrainLeech, Deck, ScreenHint.Combat));
        Assert.False(CardGridFilter.IsOffer(BrainLeech, Deck, ScreenHint.MainMenu));
    }

    [Theory]
    [InlineData(ScreenHint.ShopLikely)]
    [InlineData(ScreenHint.RestSite)]
    public void At_a_shop_or_rest_site_a_deck_grid_with_cards_just_bought_is_not_an_offer(ScreenHint hint)
    {
        // Card removal after buying two cards: the tracked deck does not have them yet (saved on the next map move).
        CardRef[] removal = [C("CARD.STRIKE_IRONCLAD"), C("CARD.STRIKE_IRONCLAD"), C("CARD.DEFEND_IRONCLAD"), C("CARD.BASH"), C("CARD.INFERNO"), C("CARD.ARMAMENTS")];
        Assert.True(CardGridFilter.IsOffer(removal, Deck, ScreenHint.None)); // the old rule would call it an offer
        Assert.False(CardGridFilter.IsOffer(removal, Deck, hint));
        // Only the two new cards read (the rest scrolled or covered): still not enough there.
        Assert.False(CardGridFilter.IsOffer([C("CARD.INFERNO"), C("CARD.ARMAMENTS")], Deck, hint));
        // A grid of (nearly) all new cards is still an offer.
        Assert.True(CardGridFilter.IsOffer(BrainLeech, Deck, hint));
    }
}
