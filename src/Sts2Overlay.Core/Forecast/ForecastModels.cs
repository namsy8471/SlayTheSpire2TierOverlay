using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Forecast;

/// <summary>A future chance to gain a card.</summary>
public enum RewardKind : byte
{
    /// <summary>Normal fight reward: 3 cards, normal rarity odds, moves the rare offset.</summary>
    Normal,

    /// <summary>Elite reward: 3 cards, elite odds, moves the rare offset.</summary>
    Elite,

    /// <summary>Act 1/2 boss reward: 3 rare cards, resets the rare offset.</summary>
    Boss,

    /// <summary>Merchant: 5 character cards + 2 colorless; at most one is bought. Does not move the rare offset.</summary>
    Shop,

    /// <summary>The act 3 boss: the run ends, no card. Marks the "next boss" horizon in act 3.</summary>
    FinalBoss,
}

/// <param name="Kind">What kind of reward.</param>
/// <param name="Act">1-based act the reward happens in (selects tiers, role targets and skip threshold).</param>
public readonly record struct RewardEvent(RewardKind Kind, int Act);

/// <summary>
/// Card rarity odds from game code (docs/SPIKE.md §5, <c>Odds/CardRarityOdds.cs</c>).
/// Kept here, not in <see cref="Weights"/>, until the engine branch that moves SPIKE §5 into <c>Weights.cs</c> lands;
/// then these constants should be read from there.
/// </summary>
public static class ForecastOdds
{
    public const double NormalRare = 0.03;
    public const double NormalRareScarcity = 0.0149;
    public const double NormalUncommon = 0.37;

    public const double EliteRare = 0.10;
    public const double EliteRareScarcity = 0.05;
    public const double EliteUncommon = 0.40;

    public const double ShopRare = 0.09;
    public const double ShopRareScarcity = 0.045;
    public const double ShopUncommon = 0.37;

    /// <summary>Rare offset at run start and after every rare (and boss) card.</summary>
    public const double OffsetReset = -0.05;

    /// <summary>Added to the offset for each non-rare reward card (A7+: <see cref="OffsetStepScarcity"/>).</summary>
    public const double OffsetStep = 0.01;

    public const double OffsetStepScarcity = 0.005;
    public const double OffsetMax = 0.40;

    /// <summary>Ascension from which the "scarcity" (halved) rare odds apply.</summary>
    public const int ScarcityAscension = 7;
}

public enum PickPolicy
{
    /// <summary>Fast proxy of the engine's main score terms (base tier, build, need, dilution, duplicates, skip). Default.</summary>
    Greedy,

    /// <summary>The real <see cref="Scoring.RecommendationEngine"/> per decision. Accurate but ~100× slower; for validation.</summary>
    Engine,
}

/// <summary>The run to forecast.</summary>
public sealed record ForecastRequest
{
    public required RunState State { get; init; }

    /// <summary>Current act's map (remaining path is sampled from it). Null → typical act layout.</summary>
    public MapGraph? Map { get; init; }

    /// <summary>Current rare offset from the save/log (<c>-0.05 … 0.4</c>); null → guessed.</summary>
    public double? RarityOffset { get; init; }

    /// <summary>Replaces the generated reward schedule (tests, calibration with the run's real future).</summary>
    public IReadOnlyList<RewardEvent>? FixedSchedule { get; init; }
}

public sealed record ForecastOptions
{
    public int Simulations { get; init; } = 1500;

    /// <summary>Fixed seed. Null → derived from the request, so the same state always gives the same numbers.</summary>
    public int? Seed { get; init; }

    public PickPolicy Policy { get; init; } = PickPolicy.Greedy;

    /// <summary>Std-dev of Gaussian noise added to each greedy card value (models a player who doesn't always follow the engine).</summary>
    public double PickNoise { get; init; }

    /// <summary>Chance that a shop visit buys a card at all (gold is not simulated). [추정]</summary>
    public double ShopBuyChance { get; init; } = 0.5;

    /// <summary>Chance that a "?" room is a fight (user history: 28 of 177 "?" rooms). [추정]</summary>
    public double UnknownFightChance { get; init; } = 0.16;

    /// <summary>Chance that a "?" room is a shop (user history: 9 of 177). [추정]</summary>
    public double UnknownShopChance { get; init; } = 0.05;

    public int CardsPerReward { get; init; } = 3;

    public static ForecastOptions Default { get; } = new();
}

/// <summary>Where one archetype ends up at a horizon, over all simulations.</summary>
public sealed record HorizonOutcome
{
    /// <summary>P(commit ≥ the archetype's <c>commitThreshold</c>) — "완성" (direction locked in).</summary>
    public double PComplete { get; init; }

    /// <summary>P(this archetype has the highest commit, and it is &gt; 0).</summary>
    public double PPrimary { get; init; }

    /// <summary>P(all core and payoff cards owned).</summary>
    public double PAllCore { get; init; }

    /// <summary>Index k (1..5) = P(flame stage ≥ k). Index 0 is always 1.</summary>
    public IReadOnlyList<double> PStageAtLeast { get; init; } = [];

    public double MeanCommit { get; init; }
    public double MeanCompletionPercent { get; init; }
}

public sealed record ArchetypeForecast
{
    public required Archetype Archetype { get; init; }
    public double CurrentCommit { get; init; }
    public double CurrentCompletionPercent { get; init; }
    public bool IsCompleteNow { get; init; }

    /// <summary>Deck as it goes into the current act's boss fight (that boss's reward not included).</summary>
    public required HorizonOutcome NextBoss { get; init; }

    /// <summary>Deck at the end of act 3 (before the final boss; it gives no card).</summary>
    public required HorizonOutcome EndOfRun { get; init; }
}

/// <summary>Mean number of future reward chances per kind.</summary>
public sealed record RewardCounts(double Normal, double Elite, double Boss, double Shop)
{
    public double Total => Normal + Elite + Boss + Shop;
}

public sealed record ForecastResult
{
    /// <summary>Archetypes of the character, most likely to complete by the end of the run first.</summary>
    public required IReadOnlyList<ArchetypeForecast> Archetypes { get; init; }

    public int Simulations { get; init; }
    public int Seed { get; init; }
    public PickPolicy Policy { get; init; }
    public double ElapsedMs { get; init; }
    public bool FromCache { get; init; }

    /// <summary>True when the current act's remaining path came from the map.</summary>
    public bool UsedMap { get; init; }

    public bool UsedFixedSchedule { get; init; }
    public double? RarityOffsetUsed { get; init; }
    public RewardCounts ExpectedUntilNextBoss { get; init; } = new(0, 0, 0, 0);
    public RewardCounts ExpectedUntilEnd { get; init; } = new(0, 0, 0, 0);

    /// <summary>Character cards available for rewards (0 = no pool data: probabilities only reflect the current deck).</summary>
    public int PoolSize { get; init; }

    public string Key { get; init; } = "";

    public static ForecastResult Empty { get; } = new() { Archetypes = [] };
}
