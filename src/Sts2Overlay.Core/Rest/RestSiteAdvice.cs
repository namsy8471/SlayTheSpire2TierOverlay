using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Rest;

/// <summary>The two rest-site options the advisor compares. Relic options (dig, cook, …) are not modelled.</summary>
public enum RestAction
{
    /// <summary>휴식: heal about 30% of max HP.</summary>
    Heal,

    /// <summary>강화: upgrade one card.</summary>
    Smith,
}

/// <summary>How clear the call is (UI: 확신 / 박빙).</summary>
public enum RestConfidence
{
    /// <summary>확신: the options differ by at least <see cref="Data.Weights.ConfidenceCloseGap"/> points (or one option is impossible).</summary>
    Sure,

    /// <summary>박빙: the options are within <see cref="Data.Weights.ConfidenceCloseGap"/> points; either is fine.</summary>
    Close,
}

/// <summary>Result of <see cref="RestSiteAdvisor.Advise(Model.RunState, MapGraph?)"/>.</summary>
public sealed record RestSiteAdvice
{
    public required RestAction Action { get; init; }

    public required RestConfidence Confidence { get; init; }

    /// <summary>One line in the current language saying why, e.g. <c>HP 38%, 보스까지 4칸·엘리트 1 → 휴식이 더 안전 (보스 도착 HP 예상 20%→45%)</c>.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Value of healing, in points, relative to smithing and walking the same rest of the act: how much better the
    /// simulated rest of the act goes with the extra HP (less boss risk, less death risk, …). Null when no map was given
    /// (the HP-threshold fallback has no point values).
    /// </summary>
    public ScoreBreakdown? HealScore { get; init; }

    /// <summary>Value of smithing in points (the best upgrade's <see cref="UpgradeCandidate.Total"/>). Null without a map.</summary>
    public ScoreBreakdown? SmithScore { get; init; }

    /// <summary>Every upgradable card, best upgrade first (the UI typically shows the first 3). Empty when nothing can be upgraded.</summary>
    public IReadOnlyList<UpgradeCandidate> Upgrades { get; init; } = [];

    /// <summary>The card to upgrade if smithing, or null when nothing can be upgraded.</summary>
    public UpgradeCandidate? BestUpgrade => Upgrades.Count > 0 ? Upgrades[0] : null;

    /// <summary>What lies ahead on the recommended path, or null when no map was available.</summary>
    public RestOutlook? Outlook { get; init; }

    /// <summary>The first <paramref name="count"/> upgrade candidates.</summary>
    public IReadOnlyList<UpgradeCandidate> TopUpgrades(int count = 3) => Upgrades.Take(Math.Max(0, count)).ToArray();
}

/// <summary>The rest of the act as the advisor saw it (the path it simulated for both options).</summary>
public sealed record RestOutlook
{
    /// <summary>Room types from the room after the rest site up to and including the boss.</summary>
    public required IReadOnlyList<MapNodeType> PathAhead { get; init; }

    /// <summary>Rooms between this rest site and the boss (0 = the boss is next).</summary>
    public int RoomsBeforeBoss { get; init; }

    public int ElitesAhead { get; init; }

    /// <summary>Other rest sites before the boss on the path.</summary>
    public int RestsAhead { get; init; }

    /// <summary>Simulated HP ratio on arrival at the boss if healing now / if smithing now.</summary>
    public double BossHpIfHeal { get; init; }

    public double BossHpIfSmith { get; init; }

    /// <summary>HP ratio right after healing (the heal is capped at max HP).</summary>
    public double HpAfterHeal { get; init; }
}

/// <summary>Component keys of <see cref="RestSiteAdvice.HealScore"/> / <see cref="RestSiteAdvice.SmithScore"/> that are not path keys.</summary>
public static class RestComponentKeys
{
    /// <summary>Smith: the best upgrade's value.</summary>
    public const string Upgrade = "rest_upgrade";

    /// <summary>Heal: rewards of the rooms ahead that are more likely to be collected alive (elite, monster, ...).</summary>
    public const string Rewards = "rest_rewards";
}
