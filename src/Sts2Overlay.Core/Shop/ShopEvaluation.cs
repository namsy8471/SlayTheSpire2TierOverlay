using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Shop;

public enum ShopItemKind
{
    Card,
    CardRemoval,
}

/// <summary>One purchasable thing at the merchant, valued relative to skipping.</summary>
public sealed record ShopItemEvaluation
{
    public required ShopItemKind Kind { get; init; }
    public CardRef? Card { get; init; }
    public required int Price { get; init; }

    /// <summary>Net value of buying (card score − skip, or removal value). Components sum to the total.</summary>
    public required ScoreBreakdown Value { get; init; }

    /// <summary><c>Value.Total / Price</c>.</summary>
    public double ValuePerGold => Price <= 0 ? Value.Total : Value.Total / Price;

    /// <summary>Card evaluation for card items.</summary>
    public CardEvaluation? CardEvaluation { get; init; }

    /// <summary>For removal: the card id that should be removed.</summary>
    public string? RemovalTarget { get; init; }

    public bool Affordable { get; init; }
}

public sealed record ShopEvaluation
{
    /// <summary>All items, best value per gold first.</summary>
    public required IReadOnlyList<ShopItemEvaluation> Items { get; init; }

    /// <summary>Best affordable combination of positive-value items.</summary>
    public required IReadOnlyList<ShopItemEvaluation> RecommendedPurchase { get; init; }

    public int GoldLeft { get; init; }

    public required ScoreBreakdown Skip { get; init; }
}
