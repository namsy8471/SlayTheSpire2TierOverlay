using System.Text.Json.Serialization;

namespace Sts2Overlay.Core.Data;

/// <summary>
/// All tunable numbers of the scoring model (<c>weights.json</c>, PLAN §6.5).
/// Every property has a default so a partial JSON file only overrides what it names.
/// Dictionaries keyed by act use the act number (1..3); a missing act falls back to the highest defined act below it.
/// </summary>
public sealed class Weights
{
    // ---- Base (PLAN §7.1) ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<Tier, double> TierValue { get; set; } = new()
    {
        [Tier.S] = 80, [Tier.A] = 65, [Tier.B] = 50, [Tier.C] = 35, [Tier.D] = 20,
    };

    /// <summary>Tier assumed for cards missing from the tier pack.</summary>
    public Tier UnknownTier { get; set; } = Tier.C;

    /// <summary>Confidence assigned to cards missing from the tier pack.</summary>
    public double UnknownConfidence { get; set; } = 0.3;

    /// <summary>Role supply multiplier for upgraded deck cards.</summary>
    public double UpgradedSupplyMultiplier { get; set; } = 1.2;

    // ---- Build / archetype ----
    public double WArch { get; set; } = 20;
    public double BuildCap { get; set; } = 25;

    /// <summary>Build cap used when a weak role (block/draw) is below <see cref="WeakThreshold"/> of its target (DECK_COMPASS rule 3).</summary>
    public double BuildCapWeak { get; set; } = 12;

    public double WeakThreshold { get; set; } = 0.5;
    public List<string> WeakRoles { get; set; } = [Roles.Block, Roles.Draw];
    public double OffBuildPenalty { get; set; } = 10;
    public double CommitThreshold { get; set; } = 0.7;

    /// <summary>Two archetypes within this commit distance are shown as a hybrid.</summary>
    public double HybridMargin { get; set; } = 0.15;

    // ---- Combo ----
    public double ComboCap { get; set; } = 15;

    // ---- Need ----
    public double WNeed { get; set; } = 25;

    /// <summary>Role supply targets: character id (or "default") → act → role → target supply.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, Dictionary<int, Dictionary<string, double>>> Targets { get; set; } = new(StringComparer.Ordinal)
    {
        ["default"] = new()
        {
            [1] = new() { [Roles.Frontload] = 3.0, [Roles.Aoe] = 1.0, [Roles.Block] = 2.5, [Roles.Scaling] = 0.5, [Roles.Draw] = 1.0 },
            [2] = new() { [Roles.Frontload] = 3.5, [Roles.Aoe] = 1.5, [Roles.Block] = 3.5, [Roles.Scaling] = 1.5, [Roles.Draw] = 2.0 },
            [3] = new() { [Roles.Frontload] = 3.5, [Roles.Aoe] = 2.0, [Roles.Block] = 4.0, [Roles.Scaling] = 2.5, [Roles.Draw] = 3.0 },
        },
    };

    /// <summary>Urgency multiplier per act and role ("Jobs" framework: act 1 damage, act 2 block, later scaling/draw).</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, Dictionary<string, double>> Urgency { get; set; } = new()
    {
        [1] = new() { [Roles.Frontload] = 1.4, [Roles.Aoe] = 1.0, [Roles.Block] = 0.8, [Roles.Scaling] = 0.5, [Roles.Draw] = 0.6 },
        [2] = new() { [Roles.Frontload] = 1.0, [Roles.Aoe] = 1.1, [Roles.Block] = 1.3, [Roles.Scaling] = 1.1, [Roles.Draw] = 0.9 },
        [3] = new() { [Roles.Frontload] = 0.8, [Roles.Aoe] = 1.0, [Roles.Block] = 1.2, [Roles.Scaling] = 1.2, [Roles.Draw] = 1.2 },
    };

    // ---- Dilution / duplicates ----
    public double LambdaDilution { get; set; } = 1.5;
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, int> IdealDeckSize { get; set; } = new() { [1] = 15, [2] = 20, [3] = 25 };
    public double RhoDuplicate { get; set; } = 15;

    // ---- DECK_COMPASS lessons ----
    /// <summary>Penalty when a card's <c>requires</c> axis is missing from the deck ("엔진 없음").</summary>
    public double RequiresPenalty { get; set; } = 15;

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]

    public SaturationWeights Saturation { get; set; } = new();
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public PowerSaturationWeights PowerSaturation { get; set; } = new();

    // ---- Situation ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SituationWeights Situation { get; set; } = new();

    // ---- Skip (PLAN §7.2) ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SkipWeights Skip { get; set; } = new();

    // ---- Confidence UX (PLAN §7.4) ----
    public double ConfidenceSureGap { get; set; } = 10;
    public double ConfidenceCloseGap { get; set; } = 5;

    // ---- Shop (PLAN §7.3) ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ShopWeights Shop { get; set; } = new();

    // ---- Archetype completion / probability (PLAN §7.5) ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public CompletionWeights Completion { get; set; } = new();

    // ---- Map path advisor (PLAN §7.6) ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Map.PathWeights Path { get; set; } = new();

    public static Weights Default => new();

    /// <summary>Looks up an act-keyed value, falling back to the closest lower act, then the lowest act.</summary>
    public static T ForAct<T>(IReadOnlyDictionary<int, T> byAct, int act, T fallback)
    {
        if (byAct.Count == 0) return fallback;
        if (byAct.TryGetValue(act, out var v)) return v;
        var lower = byAct.Keys.Where(k => k <= act).DefaultIfEmpty(int.MinValue).Max();
        if (lower != int.MinValue) return byAct[lower];
        return byAct[byAct.Keys.Min()];
    }
}

/// <summary>Diminishing returns when a resource is over-produced (e.g. Regent stars).</summary>
public sealed class SaturationWeights
{
    /// <summary>Producers above <c>Ratio × max(1, consumers)</c> count as excess.</summary>
    public double Ratio { get; set; } = 2.0;

    /// <summary>Each excess producer removes this fraction of positive build/need/combo value.</summary>
    public double PerExcess { get; set; } = 0.15;

    /// <summary>Positive synergy never drops below this fraction.</summary>
    public double MinFactor { get; set; } = 0.3;
}

public sealed class PowerSaturationWeights
{
    /// <summary>Power cards in deck at or above which another power is penalised.</summary>
    public int Threshold { get; set; } = 6;

    public double PenaltyPerExtra { get; set; } = 4;
}

public sealed class SituationWeights
{
    public double BossRewardBuildBonus { get; set; } = 8;
    public double LowHpThreshold { get; set; } = 0.3;

    /// <summary>Extra fraction of the block need added when HP is below the threshold.</summary>
    public double LowHpBlockBoost { get; set; } = 0.5;
}

public sealed class SkipWeights
{
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> T0 { get; set; } = new() { [1] = 45, [2] = 52, [3] = 58 };
    public double LambdaSize { get; set; } = 1.5;
    public double LambdaCommit { get; set; } = 5;

    /// <summary>A card must beat Skip by at least this margin to be recommended.</summary>
    public double Margin { get; set; } = 0.5;

    /// <summary>Cards with confidence below this cannot beat Skip.</summary>
    public double LowConfidence { get; set; } = 0.5;
}

public sealed class ShopWeights
{
    public int RemovalBase { get; set; } = 75;
    public int RemovalStep { get; set; } = 25;
    public int RemovalBaseHighAscension { get; set; } = 100;
    public int RemovalStepHighAscension { get; set; } = 50;
    public int HighAscension { get; set; } = 6;

    /// <summary>Score-equivalent value of removing a curse.</summary>
    public double CurseRemovalValue { get; set; } = 30;

    /// <summary>Score-equivalent value of removing a basic Strike/Defend.</summary>
    public double BasicRemovalValue { get; set; } = 14;
}

public sealed class CompletionWeights
{
    /// <summary>Completion % weight of core and payoff cards (reference site "must" = 100).</summary>
    public double CoreWeight { get; set; } = 100;

    /// <summary>Completion % weight of enabler and support cards (reference site "rec" = 56).</summary>
    public double SupportWeight { get; set; } = 56;

    /// <summary>Approximate rarity odds per reward slot. [추정] until confirmed from game code.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, double> RarityOdds { get; set; } = new(StringComparer.Ordinal)
    {
        ["common"] = 0.60, ["uncommon"] = 0.37, ["rare"] = 0.03,
    };

    /// <summary>Fallback pool size per rarity for one character when no catalog is loaded.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, int> PoolSize { get; set; } = new(StringComparer.Ordinal)
    {
        ["common"] = 20, ["uncommon"] = 36, ["rare"] = 16,
    };

    public int CardsPerReward { get; set; } = 3;
}
