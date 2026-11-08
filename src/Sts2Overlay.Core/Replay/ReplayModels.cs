using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;

namespace Sts2Overlay.Core.Replay;

/// <summary>How a recorded choice compares with what the engine would have recommended.</summary>
public enum ReplayVerdict
{
    /// <summary>Picked the recommended card.</summary>
    Agreed,

    /// <summary>Skipped and the engine also recommended Skip.</summary>
    AgreedSkip,

    /// <summary>Picked a different card than recommended.</summary>
    PickedOther,

    /// <summary>Picked a card while the engine recommended Skip.</summary>
    PickedButSkipRecommended,

    /// <summary>Skipped while the engine recommended a card.</summary>
    SkippedButCardRecommended,

    /// <summary>Not comparable 1:1 (shops, multi-pick groups).</summary>
    Informational,
}

/// <summary>One recorded decision replayed through the engine.</summary>
public sealed record DecisionReplay
{
    public required CardDecision Decision { get; init; }

    /// <summary>Reward/event evaluation (null for shops).</summary>
    public OfferEvaluation? Offer { get; init; }

    /// <summary>Shop evaluation (null for rewards).</summary>
    public ShopEvaluation? Shop { get; init; }

    public ReplayVerdict Verdict { get; init; }

    /// <summary>Engine's recommendation; null = Skip (or nothing for shops).</summary>
    public CardRef? Recommended { get; init; }

    /// <summary>1-based engine rank of the picked card among the offered cards, null if skipped / not found.</summary>
    public int? PickedRank { get; init; }

    /// <summary>Score of the engine's best candidate (card or skip) minus the score of what was actually chosen. ≥ 0.</summary>
    public double Regret { get; init; }

    public bool IsComparable => Verdict != ReplayVerdict.Informational;

    public bool IsAgreement => Verdict is ReplayVerdict.Agreed or ReplayVerdict.AgreedSkip;
}

/// <summary>A whole run replayed for one player.</summary>
public sealed record RunReplay
{
    public required RunHistory Run { get; init; }
    public required RunPlayer Player { get; init; }
    public required IReadOnlyList<DecisionReplay> Decisions { get; init; }
    public IReadOnlyList<string> TimelineWarnings { get; init; } = [];

    public int ComparableCount => Decisions.Count(d => d.IsComparable);
    public int AgreementCount => Decisions.Count(d => d.IsAgreement);

    /// <summary>Share of comparable decisions where the player did what the engine recommends (0..1), or null when none.</summary>
    public double? AgreementRate => ComparableCount == 0 ? null : (double)AgreementCount / ComparableCount;

    public double MeanRegret => Decisions.Where(d => d.IsComparable).Select(d => d.Regret).DefaultIfEmpty(0).Average();
}
