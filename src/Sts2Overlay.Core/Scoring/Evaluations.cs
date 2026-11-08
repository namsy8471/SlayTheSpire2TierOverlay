using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>A short Korean reason shown under a card, e.g. <c>광역 부족 +24</c>.</summary>
public sealed record ReasonChip(string Key, string Label, double Value);

/// <summary>
/// What one archetype membership adds to a card's build synergy, before the build cap: <paramref name="Value"/> after the core gate
/// and staged-synergy factor, <paramref name="Ungated"/> without the core gate. Both 0 behind the <c>requires</c> gate.
/// </summary>
public sealed record ArchetypeSynergy(string ArchetypeId, string Role, double Value, double Ungated);

/// <summary>What a card is for a build, from the archetype JSON roles: core, essential synergy (payoff + enabler), sub synergy (support).</summary>
public enum BuildRoleKind
{
    Core,
    Essential,
    Sub,
}

/// <summary>One build a card belongs to, shown before picking ("번개 필수 시너지").</summary>
public sealed record BuildRole(string ArchetypeId, string ArchetypeName, BuildRoleKind Kind);

/// <summary>The score of one offered card.</summary>
public sealed record CardEvaluation
{
    public required CardRef Card { get; init; }

    /// <summary>Tier letter shown on the badge (act tier if present).</summary>
    public required Tier DisplayTier { get; init; }

    public required ScoreBreakdown Score { get; init; }

    /// <summary>0..1 data confidence of the card's rating.</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>True when the rating is a guess (unrated, changed in a patch, or low confidence) → "추정" badge.</summary>
    public bool IsEstimate { get; init; }

    public bool IsRated { get; init; } = true;

    /// <summary>Top reasons (max 3), signed.</summary>
    public IReadOnlyList<ReasonChip> Chips { get; init; } = [];

    /// <summary>Per-archetype synergy of this card (only archetypes that list it), for explanations and tests.</summary>
    public IReadOnlyList<ArchetypeSynergy> Synergy { get; init; } = [];

    /// <summary>
    /// Builds this card is core / essential / sub synergy for, highest current commit first, at most
    /// <see cref="CardScorer.MaxBuildRoles"/>; empty for a card in no build. Text: <see cref="ScoringLabels.BuildRoleText"/>.
    /// </summary>
    public IReadOnlyList<BuildRole> BuildRoles { get; init; } = [];

    /// <summary>1-based rank among the offered cards (ties broken by offer order).</summary>
    public int Rank { get; init; }

    /// <summary>True for the single recommended card (false for all when Skip is recommended).</summary>
    public bool IsBest { get; init; }

    public double Total => Score.Total;
}

public enum RecommendationConfidence
{
    /// <summary>1st beats 2nd by ≥ <see cref="Weights.ConfidenceSureGap"/>.</summary>
    Sure,
    Normal,
    /// <summary>1st and 2nd within <see cref="Weights.ConfidenceCloseGap"/>.</summary>
    Close,
    /// <summary>The recommended card is an estimate (unrated / patched / low confidence).</summary>
    Estimate,
}

/// <summary>Result for a card reward screen (3 cards + skip).</summary>
public sealed record OfferEvaluation
{
    /// <summary>Evaluations in the order the cards were offered.</summary>
    public required IReadOnlyList<CardEvaluation> Cards { get; init; }

    public required ScoreBreakdown Skip { get; init; }

    public bool SkipRecommended { get; init; }

    /// <summary>The recommended card, null when Skip is recommended.</summary>
    public CardRef? Best { get; init; }

    public RecommendationConfidence Confidence { get; init; }

    /// <summary>One line explaining what would flip a close call (Korean), or null.</summary>
    public string? FlipHint { get; init; }

    public DeckProfile? Profile { get; init; }
}
