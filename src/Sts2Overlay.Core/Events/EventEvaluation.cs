using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Events;

/// <summary>The score of one event option.</summary>
public sealed record EventOptionEvaluation
{
    public required EventOptionDef Option { get; init; }

    /// <summary>Sum of the effect components (+ the best follow-up page when the option leads to one).</summary>
    public required ScoreBreakdown Score { get; init; }

    /// <summary>1-based rank among the offered options.</summary>
    public int Rank { get; init; }

    public bool IsBest { get; init; }

    /// <summary>Choosing it would kill the player (HP loss ≥ current HP).</summary>
    public bool IsLethal { get; init; }

    /// <summary>The player cannot pay for it (gold cost above current gold).</summary>
    public bool IsUnaffordable { get; init; }

    /// <summary>At least one effect could not be valued (an <c>other</c> effect or an unknown formula).</summary>
    public bool HasUnmodelled { get; init; }

    /// <summary>The option has a random outcome (an effect with a chance, or a random pick).</summary>
    public bool IsRandom { get; init; }

    /// <summary>Korean one-liners describing what the option does, in effect order (e.g. "체력 9 잃음").</summary>
    public IReadOnlyList<string> EffectTexts { get; init; } = [];

    public double Total => Score.Total;
}

public sealed record EventEvaluation
{
    public required EventDef Event { get; init; }

    /// <summary>Page whose options were scored (<c>INITIAL</c> unless the log said otherwise).</summary>
    public required string Page { get; init; }

    /// <summary>In display order (index 0 = top button).</summary>
    public required IReadOnlyList<EventOptionEvaluation> Options { get; init; }

    public EventOptionEvaluation? Best => Options.FirstOrDefault(o => o.IsBest);

    public RecommendationConfidence Confidence { get; init; }

    /// <summary>True when the offered options are exactly known (co-op log); false when they come from the event's data (all page options).</summary>
    public bool OptionsExact { get; init; }
}
