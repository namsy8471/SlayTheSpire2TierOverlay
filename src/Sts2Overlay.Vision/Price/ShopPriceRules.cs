namespace Sts2Overlay.Vision;

/// <summary>
/// The game's shop card prices (v0.107.1, MerchantCardEntry / MerchantEntry; SPIKE §5), used to reject misreads.
///
/// <code>
/// base   = common 50 / uncommon 75 / rare 150; colourless: RoundToInt(base * 1.15f)  → 86 / 172
/// cost   = RoundToInt(base * Random(0.95, 1.05)); the one sale card (character cards only): cost / 2
/// shown  = (int)(cost * relic factor)   Membership Card 0.5, The Courier 0.8 (both: 0.4)
/// </code>
/// A reading is plausible when some (rarity, sale, relic factor) combination allows it. Knowing the card's
/// rarity (from the catalog) makes the check much tighter.
/// </summary>
public static class ShopPriceRules
{
    /// <summary>Relic price factors the reader accepts when nothing narrows them down (none, Courier, Membership Card, both).</summary>
    public static readonly IReadOnlyList<double> AllFactors = [1.0, 0.8, 0.5, 0.4];

    /// <summary>Base price before the random jitter, or null for rarities the merchant never sells.</summary>
    public static int? BasePrice(string? rarity, bool colorless)
    {
        int? b = rarity?.ToLowerInvariant() switch
        {
            "common" => 50,
            "uncommon" => 75,
            "rare" => 150,
            _ => null,
        };
        // Mathf.RoundToInt(150 * 1.15f) = 172: 1.15f is slightly below 1.15, so 172.4999 rounds down.
        return b is { } v && colorless ? (int)MathF.Round(v * 1.15f, MidpointRounding.ToEven) : b;
    }

    /// <summary>Lowest and highest price a card with this base price can show.</summary>
    public static (int Min, int Max) Range(int basePrice, bool onSale, double factor = 1.0)
    {
        int lo = (int)Math.Round(basePrice * 0.95, MidpointRounding.ToEven);
        int hi = (int)Math.Round(basePrice * 1.05, MidpointRounding.ToEven);
        if (onSale)
        {
            lo /= 2;
            hi /= 2;
        }
        return ((int)(lo * factor), (int)(hi * factor));
    }

    /// <summary>
    /// Can a card in this slot (and of this rarity, when known) cost <paramref name="price"/>?
    /// <paramref name="factors"/> are the relic price factors to allow (null = <see cref="AllFactors"/>).
    /// </summary>
    public static bool IsPlausible(int price, CardSlotKind kind, string? rarity = null, IReadOnlyList<double>? factors = null)
    {
        if (!kind.IsShop() || price <= 0) return false;
        bool colorless = kind == CardSlotKind.ShopColorless;
        IEnumerable<string> rarities = BasePrice(rarity, colorless) != null ? [rarity!]
            : colorless ? ["uncommon", "rare"] : ["common", "uncommon", "rare"];
        foreach (string r in rarities)
        {
            int b = BasePrice(r, colorless)!.Value;
            foreach (bool sale in colorless ? [false] : (bool[])[false, true])
                foreach (double f in factors ?? AllFactors)
                {
                    (int lo, int hi) = Range(b, sale, f);
                    if (price >= lo && price <= hi) return true;
                }
        }
        return false;
    }

    /// <summary>
    /// Relic base price by catalog rarity (RelicModel.MerchantCost): common 175, uncommon 225, rare 275, shop 200.
    /// Null for rarities the merchant never sells.
    /// </summary>
    public static int? RelicBasePrice(string? rarity) => rarity?.ToLowerInvariant() switch
    {
        "common" => 175,
        "uncommon" => 225,
        "rare" => 275,
        "shop" => 200,
        _ => null,
    };

    /// <summary>
    /// Lowest and highest price a relic with this base price can show: MerchantRelicEntry.CalcCost =
    /// round(base × Random(0.85, 1.15)), then the relic factor (Membership Card / The Courier) truncates.
    /// Floor/ceil keep the bounds safe whatever the float rounding did.
    /// </summary>
    public static (int Min, int Max) RelicRange(int basePrice, double factor = 1.0)
    {
        int lo = (int)Math.Floor(basePrice * 0.85), hi = (int)Math.Ceiling(basePrice * 1.15);
        return ((int)(lo * factor), (int)(hi * factor));
    }

    /// <summary>Can a merchant relic (of this rarity, when known) cost <paramref name="price"/>?</summary>
    public static bool IsPlausibleRelic(int price, string? rarity = null, IReadOnlyList<double>? factors = null)
    {
        if (price <= 0) return false;
        IEnumerable<string> rarities = RelicBasePrice(rarity) != null ? [rarity!] : ["common", "uncommon", "rare", "shop"];
        foreach (string r in rarities)
            foreach (double f in factors ?? AllFactors)
            {
                (int lo, int hi) = RelicRange(RelicBasePrice(r)!.Value, f);
                if (price >= lo && price <= hi) return true;
            }
        return false;
    }
}
