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

    /// <summary>
    /// The option costs HP (or leads to a page whose best option does) while the current HP is unknown
    /// (<see cref="Model.RunState.VitalsKnown"/> false): it can't be judged safe or lethal, so the UI shows a caution.
    /// </summary>
    public bool HpLossUnknown { get; init; }

    /// <summary>At least one effect could not be valued (an <c>other</c> effect or an unknown formula).</summary>
    public bool HasUnmodelled { get; init; }

    /// <summary>Nothing about the option could be valued (no priced effect, no measured choice rate): its 0 is "unknown", not "neutral".</summary>
    public bool HasNoData => HasUnmodelled && Score.Components.Count == 0;

    /// <summary>The option has a random outcome (an effect with a chance, or a random pick).</summary>
    public bool IsRandom { get; init; }

    /// <summary>One-liners in the current language (<see cref="Localization.Loc"/>) describing what the option does, in effect order (e.g. "체력 9 잃음" / "Lose 9 HP").</summary>
    public IReadOnlyList<string> EffectTexts { get; init; } = [];

    /// <summary>HP of the option's training-dummy fight after multiplayer scaling; null when it has none.</summary>
    public int? DummyHp { get; init; }

    /// <summary>Simulated chance to kill that dummy in time; null when unknown or there is no such fight.</summary>
    public double? WinChance { get; init; }

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
    public bool UsedRunBaseline { get; init; }

    /// <summary>
    /// An ancient's options that could have been drawn but weren't offered this time, ranked among themselves (rank 1 =
    /// best of them; never <see cref="EventOptionEvaluation.IsBest"/>). For reference only: they don't change
    /// <see cref="Options"/>' ranks or the recommendation. Empty unless the offered options are known.
    /// </summary>
    public IReadOnlyList<EventOptionEvaluation> NotOffered { get; init; } = [];

    /// <summary>The damage forecast used for a training-dummy fight (shown in the panel); null otherwise.</summary>
    public Combat.DamageForecast? Forecast { get; init; }

    public IReadOnlyList<string> ForecastNotes { get; init; } = [];
}
