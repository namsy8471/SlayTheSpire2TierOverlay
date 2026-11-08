using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Shop;

/// <summary>
/// Merchant card prices from <see cref="ShopWeights"/> (SPIKE §5, code-confirmed): base price per rarity (common 50 / uncommon 75 / rare 150),
/// colorless × 1.15, a random factor 0.95..1.05, and one card on sale at half price.
/// Used when the overlay cannot read a price from the screen. The game's exact rounding is not confirmed, so values are estimates.
/// </summary>
public static class ShopPrices
{
    /// <summary>Expected price (random factor 1.0), rounded to the nearest gold; null for rarities the shop does not sell (basic, ancient, ...).</summary>
    public static int? Estimated(ShopWeights w, string? rarity, bool colorless, bool onSale = false)
    {
        var b = Base(w, rarity, colorless);
        if (b is null) return null;
        return (int)Math.Round(b.Value * (onSale ? w.SaleMultiplier : 1.0), MidpointRounding.AwayFromZero);
    }

    /// <summary>Lowest and highest possible price (floor/ceil of the random factor range); null for rarities the shop does not sell.</summary>
    public static (int Min, int Max)? Range(ShopWeights w, string? rarity, bool colorless, bool onSale = false)
    {
        var b = Base(w, rarity, colorless);
        if (b is null) return null;
        var sale = onSale ? w.SaleMultiplier : 1.0;
        return ((int)Math.Floor(b.Value * w.PriceJitterMin * sale + 1e-9), (int)Math.Ceiling(b.Value * w.PriceJitterMax * sale - 1e-9));
    }

    private static double? Base(ShopWeights w, string? rarity, bool colorless)
    {
        if (rarity is null || !w.CardPrice.TryGetValue(rarity.ToLowerInvariant(), out var price) || price <= 0) return null;
        return price * (colorless ? w.ColorlessPriceMultiplier : 1.0);
    }
}
