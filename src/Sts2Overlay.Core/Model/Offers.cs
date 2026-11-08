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
public sealed record ShopCardOffer(CardRef Card, int Price);

/// <summary>The cards (and card removal service) offered at a merchant.</summary>
public sealed record ShopOffer(IReadOnlyList<ShopCardOffer> Cards, int? RemovalPrice = null);
