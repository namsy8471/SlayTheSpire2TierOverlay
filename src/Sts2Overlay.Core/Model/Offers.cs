namespace Sts2Overlay.Core.Model;

/// <summary>Where a card offer comes from. Affects situation bonuses.</summary>
public enum OfferContext
{
    CardReward,
    EliteReward,
    BossReward,
    Event,
    Shop,
}

/// <summary>A card for sale.</summary>
/// <param name="Card">The card.</param>
/// <param name="Price">Price in gold; ignored when <paramref name="PriceKnown"/> is false.</param>
/// <param name="PriceKnown">False when the price label could not be read: the scorer estimates it from the rarity.</param>
/// <param name="AffordableOnScreen">The label colour: false when the game drew the price red (not enough gold), null when unknown.</param>
/// <param name="OnSale">The green half-price label.</param>
public sealed record ShopCardOffer(CardRef Card, int Price, bool PriceKnown = true, bool? AffordableOnScreen = null, bool OnSale = false);

/// <summary>The cards (and card removal service) offered at a merchant.</summary>
public sealed record ShopOffer(IReadOnlyList<ShopCardOffer> Cards, int? RemovalPrice = null);
