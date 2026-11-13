using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Shop;

public enum ShopItemKind
{
    Card,
    CardRemoval,
    Relic,
    Potion,
}

/// <summary>One purchasable thing at the merchant, valued relative to skipping.</summary>
public sealed record ShopItemEvaluation
{
    public required ShopItemKind Kind { get; init; }
    public CardRef? Card { get; init; }
    public required int Price { get; init; }

    /// <summary>Net value of buying (card score − skip, removal value, or the relic's base + build fit). Components sum to the total.</summary>
    public required ScoreBreakdown Value { get; init; }

    /// <summary><c>Value.Total / Price</c>.</summary>
    public double ValuePerGold => Price <= 0 ? Value.Total : Value.Total / Price;

    /// <summary>Card evaluation for card items.</summary>
    public CardEvaluation? CardEvaluation { get; init; }

    /// <summary>For relic items: <c>RELIC.X</c>.</summary>
    public string? RelicId { get; init; }

    public string? PotionId { get; init; }

    /// <summary>For relic items: the deck-aware relic valuation (<see cref="ShopScorer.ScoreRelic"/>); <see cref="Value"/> is its breakdown.</summary>
    public RelicEvaluation? RelicEvaluation { get; init; }

    /// <summary>For removal: the card id that should be removed.</summary>
    public string? RemovalTarget { get; init; }

    /// <summary>The selected deck copy, including upgrades and enchantment, when applying the run policy.</summary>
    public DeckCard? RemovalCopy { get; init; }

    public bool Affordable { get; init; }

    /// <summary>The price was not read from the screen; <see cref="Price"/> is the rarity-based estimate.</summary>
    public bool PriceEstimated { get; init; }

    /// <summary>The merchant's half-price card.</summary>
    public bool OnSale { get; init; }
}

public sealed record ShopEvaluation
{
    /// <summary>All items, best value per gold first.</summary>
    public required IReadOnlyList<ShopItemEvaluation> Items { get; init; }

    /// <summary>The chosen next purchase with the run policy, otherwise the best affordable bundle.</summary>
    public required IReadOnlyList<ShopItemEvaluation> RecommendedPurchase { get; init; }

    /// <summary>The recommendation is the next action among recognized offers; re-evaluate after a purchase.</summary>
    public bool SingleStep { get; init; }

    /// <summary>The gold planned with: the tracked gold, clamped to what the price colours on screen prove.</summary>
    public int Gold { get; init; }

    /// <summary>True when the price colours contradicted the tracked gold and <see cref="Gold"/> was corrected.</summary>
    public bool GoldCorrectedByScreen { get; init; }

    /// <summary>
    /// False when the run's gold is unknown (<see cref="RunState.VitalsKnown"/>): <see cref="Gold"/> is then only the most the
    /// red price labels allow (<see cref="ShopPricing.GoldCeiling"/>, <see cref="int.MaxValue"/> without one), affordability
    /// comes from the label colours alone, and <see cref="Gold"/> / <see cref="GoldLeft"/> must not be shown as the player's gold.
    /// </summary>
    public bool GoldKnown { get; init; } = true;

    public int GoldLeft { get; init; }

    public required ScoreBreakdown Skip { get; init; }
}
