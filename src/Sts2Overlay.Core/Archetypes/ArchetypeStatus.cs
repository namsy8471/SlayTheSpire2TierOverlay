using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Archetypes;

public sealed record MissingCard(string CardId, string Role, string? Rarity);

/// <summary>How far the deck is into one archetype (PLAN §7.5).</summary>
public sealed record ArchetypeStatus
{
    public required Archetype Archetype { get; init; }

    /// <summary><c>min(1, (Σ owned roleWeight + key relic w) / fullWeight)</c>.</summary>
    public double Commit { get; init; }

    /// <summary>0..100, core/payoff weighted 100, enabler/support 56.</summary>
    public double CompletionPercent { get; init; }

    /// <summary>1..5 flame stage for the UI.</summary>
    public int Stage { get; init; }

    public IReadOnlyList<string> OwnedCards { get; init; } = [];
    public IReadOnlyList<string> OwnedRelics { get; init; } = [];

    /// <summary>Missing core and payoff cards.</summary>
    public IReadOnlyList<MissingCard> MissingCards { get; init; } = [];

    public IReadOnlyList<ArchetypeNeed> UnmetNeeds { get; init; } = [];

    public bool IsCommitted => Commit >= Archetype.CommitThreshold;
}

/// <summary>Everything the build-collection tab shows.</summary>
public sealed record ArchetypeReport
{
    /// <summary>All archetypes of the character, highest commit first.</summary>
    public required IReadOnlyList<ArchetypeStatus> Statuses { get; init; }

    public ArchetypeStatus? Primary => Statuses.Count > 0 && Statuses[0].Commit > 0 ? Statuses[0] : null;

    /// <summary>Top two archetypes are within <c>HybridMargin</c> and both non-zero.</summary>
    public bool IsHybrid { get; init; }

    /// <summary>Archetype id → probability that at least one missing core/payoff card is offered in the next N rewards. [추정]</summary>
    public IReadOnlyDictionary<string, double> CoreOfferProbability { get; init; } = new Dictionary<string, double>();

    public int RemainingRewards { get; init; }
}
