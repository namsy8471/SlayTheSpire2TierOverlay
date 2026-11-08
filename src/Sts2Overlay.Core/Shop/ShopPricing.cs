using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Shop;

/// <summary>
/// Merchant card prices when the screen did not show one, and the gold the screen proves the player has.
/// Base prices: common 50, uncommon 75, rare 150; colourless ×1.15 (86 / 172). The game adds ±5% jitter,
/// one character card is half price, and relics can discount everything; the estimate ignores all three.
/// </summary>
public static class ShopPricing
{
    public const int CommonPrice = 50;
    public const int UncommonPrice = 75;
    public const int RarePrice = 150;
    public const double ColorlessFactor = 1.15;

    /// <summary>Base price for a card of the given rarity; unknown rarity counts as uncommon (the middle price).</summary>
    public static int BasePrice(string? rarity, bool colorless)
    {
        int b = rarity switch
        {
            "common" or "basic" => CommonPrice,
            "rare" => RarePrice,
            _ => UncommonPrice,
        };
        return colorless ? (int)Math.Round(b * ColorlessFactor, MidpointRounding.ToEven) : b;
    }

    /// <summary>Estimated price of <paramref name="card"/> for <paramref name="character"/>, from catalog/tier rarity and pool.</summary>
    public static int Estimate(DataPack pack, string character, CardRef card)
        => BasePrice(pack.CardRarity(character, card.Id), IsColorless(pack, character, card.Id));

    /// <summary>Colourless when the catalog says so, else when only the colourless tier pack rates it.</summary>
    public static bool IsColorless(DataPack pack, string character, string cardId)
    {
        var pool = pack.FindCatalog(cardId)?.Pool;
        if (pool is not null) return string.Equals(pool, DataPack.Colorless, StringComparison.Ordinal);
        var id = pack.Canonical(cardId);
        bool own = pack.Tiers.TryGetValue(character, out var p) && p.Cards.ContainsKey(id);
        return !own && pack.Tiers.TryGetValue(DataPack.Colorless, out var cl) && cl.Cards.ContainsKey(id);
    }

    /// <summary>
    /// The gold to plan with. The save file lags behind purchases made in the shop, but the price labels do not:
    /// the game draws an unaffordable price in red. A cream/green label with a known price proves gold ≥ price, a
    /// red one proves gold &lt; price. The tracked gold is clamped into that interval; contradictory evidence
    /// (a misread, or a stale frame) is ignored and the tracked gold kept.
    /// </summary>
    public static int EffectiveGold(int trackedGold, IEnumerable<ShopCardOffer> cards, out bool corrected)
    {
        var tracked = Math.Max(0, trackedGold);
        int lo = 0, hi = int.MaxValue;
        foreach (var c in cards)
        {
            if (!c.PriceKnown || c.AffordableOnScreen is not bool affordable) continue;
            if (affordable) lo = Math.Max(lo, c.Price);
            else hi = Math.Min(hi, c.Price - 1);
        }
        if (lo > hi)
        {
            corrected = false;
            return tracked;
        }
        var gold = Math.Clamp(tracked, lo, hi);
        corrected = gold != tracked;
        return gold;
    }
}
