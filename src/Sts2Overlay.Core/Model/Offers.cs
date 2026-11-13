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

/// <summary>A relic for sale on the merchant's rug.</summary>
/// <param name="RelicId"><c>RELIC.X</c>.</param>
/// <param name="Rarity">common / uncommon / rare / shop (catalog), or null when unknown.</param>
/// <param name="Price">Price in gold; ignored when <paramref name="PriceKnown"/> is false.</param>
/// <param name="PriceKnown">False when the price label could not be read: the scorer estimates it from the rarity.</param>
/// <param name="AffordableOnScreen">The label colour: false when the game drew the price red (not enough gold), null when unknown.</param>
public sealed record ShopRelicOffer(string RelicId, string? Rarity, int Price, bool PriceKnown = true, bool? AffordableOnScreen = null);

/// <summary>A recognized potion and its screen price. An unreadable price is null.</summary>
public sealed record ShopPotionOffer(string PotionId, int? Price, bool? AffordableOnScreen = null);

/// <summary>The cards, relics and card removal service offered at a merchant.</summary>
public sealed record ShopOffer(IReadOnlyList<ShopCardOffer> Cards, int? RemovalPrice = null)
{
    /// <summary>The relics on the rug still for sale (bought ones are gone from the screen).</summary>
    public IReadOnlyList<ShopRelicOffer> Relics { get; init; } = [];
    public IReadOnlyList<ShopPotionOffer> Potions { get; init; } = [];

    /// <summary>
    /// The card-removal service can still be bought. False once it was used this visit (the screen shows it crossed out or
    /// without a price): then removal is left out of the items, the recommended purchase and the gold plan.
    /// </summary>
    public bool RemovalAvailable { get; init; } = true;
}
