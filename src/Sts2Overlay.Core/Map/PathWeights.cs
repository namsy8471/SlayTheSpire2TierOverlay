using System.Text.Json.Serialization;

namespace Sts2Overlay.Core.Map;

/// <summary>
/// Tunable numbers of the path advisor (PLAN §7.6), exposed as <c>weights.json → "path"</c>.
/// Units: "points" on roughly the same scale as card scores (an S-tier card is worth ~80, skip ~45), so a relic from
/// an elite (~22) is "about one good card pick". HP amounts are fractions of max HP (0.1 = 10%).
/// Numbers marked [측정] come from run history (<c>tools/PathStats</c>, PLAN §7.7): 38 runs (18 single player, 20 co-op,
/// A0–A4, v0.98–v0.107), each value shrunk toward the old guess as if the guess were 8 extra samples
/// (<see cref="Replay.PathStats.Shrink"/>). Point values (rewards, costs) cannot be measured that way and stay [추정].
/// </summary>
public sealed class PathWeights
{
    // ---- Search ----

    /// <summary>How many paths the recommendation lists.</summary>
    public int TopK { get; set; } = 3;

    /// <summary>
    /// Beam width of the k-best DP: each node keeps at most this many best suffixes. When the number of paths from the
    /// current node is at or below this, every path is scored exactly (real maps have ~50–500 paths).
    /// </summary>
    public int CandidateLimit { get; set; } = 1000;

    // ---- Deck strength 0..1 (PLAN §7.6: (1 − mean role gap) × 0.6 + max commit × 0.4) ----

    public double StrengthGapWeight { get; set; } = 0.6;
    public double StrengthCommitWeight { get; set; } = 0.4;

    /// <summary>Used in place of (1 − mean gap) when the deck has no role targets at all (no data). [추정]</summary>
    public double StrengthUnknown { get; set; } = 0.5;

    // ---- Expected HP loss per room, fraction of max HP ----

    /// <summary>
    /// Expected HP loss per room type and act: single player, and co-op too when <see cref="CoopHpLoss"/> is null.
    /// [측정] Mean <c>damage_taken ÷ max HP</c> per room and act over every player (old guess → measured → used, n):
    /// <code>
    ///            act 1                       act 2                       act 3
    /// monster    0.08 → 0.078 → 0.078 (265)  0.10 → 0.145 → 0.142 (129)  0.116 → 0.134 → 0.132 (63)
    /// elite      0.20 → 0.295 → 0.285 (65)   0.25 → 0.307 → 0.298 (43)   0.29  → 0.392 → 0.360 (18)
    /// "?"        0.03 → 0.038 → 0.037 (169)  0.0375 → 0.044 → 0.043 (97) 0.0435 → 0.028 → 0.029 (64)
    /// boss       0.35 → 0.395 → 0.389 (50)   0.4375 → 0.445 → 0.443 (27) 0.5075 → 0.555 → 0.540 (17)
    /// </code>
    /// The old model was "act-1 value × act multiplier 1.0/1.25/1.45"; measured, hallway fights roughly double from
    /// act 1 to 2 while elites barely change, so each act now has its own number. A death censors that room's damage at
    /// the HP on entry, so elite/boss means are slight underestimates. Spread is wide (elite p90 ≈ 0.61, boss p90 ≈ 0.77).
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public PathHpLoss HpLoss { get; set; } = new()
    {
        Monster = new() { [1] = 0.078, [2] = 0.142, [3] = 0.132 },
        Elite = new() { [1] = 0.285, [2] = 0.298, [3] = 0.360 },
        Unknown = new() { [1] = 0.037, [2] = 0.043, [3] = 0.029 },
        Boss = new() { [1] = 0.389, [2] = 0.443, [3] = 0.540 },
    };

    /// <summary>
    /// Co-op (2+ players) table; null = use <see cref="HpLoss"/> for co-op as well. [측정] null: on act-normalised damage
    /// co-op/solo was 0.84 (monster, z −1.4), 1.00 (elite), 0.50 ("?", z −1.9, n solo 68), 0.91 (boss) — none both
    /// |z| ≥ 2 and ≥ 15% apart, so one table serves both. Set this if more co-op data shows a real gap.
    /// </summary>
    public PathHpLoss? CoopHpLoss { get; set; }

    /// <summary>The HP-loss table for a single-player or co-op run.</summary>
    public PathHpLoss HpLossFor(bool multiplayer) => multiplayer && CoopHpLoss is { } coop ? coop : HpLoss;

    /// <summary>
    /// Damage multiplier = <c>1 + slope × (0.5 − strength)</c> (slope 1: a strong deck takes half, a weak one 1.5×).
    /// [측정] 0 (was 1.0 [추정]). Within the same room type, act and player count, damage did not fall with deck strength:
    /// fitted slope −0.14 ± 0.16 over 677 fights. Combined with the old guess (1.0 ± 0.5) by precision weighting that is
    /// ≈ 0. Deck strength as computed here rises through a run (act 1 ≈ 0.4, act 3 ≈ 0.8) while fights get harder, so
    /// it does not predict damage; it still raises card-reward value (<see cref="WeakDeckCardBonus"/>). Selection bias
    /// (weak decks avoid elites) may hide part of a real effect.
    /// </summary>
    public double StrengthDamageSlope { get; set; } = 0.0;

    /// <summary>
    /// Expected rest-site heal, fraction of max HP. [측정] Mean of uncapped HEAL choices 0.323 (n = 105) → 0.321.
    /// The plain heal is 30% (median 0.298, integer rounding); the mean includes relics that heal extra, and the
    /// simulation wants the expected amount (leave-one-run-out: boss-arrival MAE about 0.2 points lower than with the median).
    /// </summary>
    public double RestHeal { get; set; } = 0.321;

    /// <summary>
    /// Rest-site choice the simulation assumes: heal below this HP ratio, otherwise upgrade (always heal when nothing
    /// is left to upgrade). Null = pick whichever is worth more in points (the old rule).
    /// [측정] 0.466 with <see cref="RestHealSoftness"/> 0.112: logistic regression of the 256 recorded HEAL/SMITH choices
    /// on HP. Players heal 84% of the time below 40% HP, 31% at 40–70%, 6% above 70%. The old value rule healed at any
    /// HP up to ~80% and matched the recorded choice only 60% of the time, which made the simulated HP at the boss
    /// 10 points too optimistic.
    /// </summary>
    public double? RestHealBelowHp { get; set; } = 0.466;

    /// <summary>
    /// How soft the <see cref="RestHealBelowHp"/> boundary is: the chance to heal is
    /// <c>1 / (1 + exp((hp - RestHealBelowHp) / RestHealSoftness))</c>, so 50% at the threshold and about 73% / 27% one
    /// softness below / above it. 0 = hard threshold. The simulation carries the expected HP, not every possible HP,
    /// so a soft rule (an expected heal) fits it better than an all-or-nothing switch. [측정]
    /// </summary>
    public double RestHealSoftness { get; set; } = 0.112;

    /// <summary>
    /// Shape of the damage distribution per room type (gamma with integer shape; 0 = the old uniform on [0, 2 × mean]).
    /// Used for the death chance and for the HP kept after surviving.
    /// [측정] shape = 1 / variance of damage ÷ its act mean, rounded: monster 1 (n 457), elite 2 (n 126), "?" 1 (n 330,
    /// really a mix of mostly 0 and an occasional fight), boss 3 (n 94). Elite p90 is ≈ 2 × mean and the worst elite took
    /// 3 × mean, which the uniform model (at most 2 × mean) could not produce: it predicted 3.8 of 15 elite deaths.
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public PathDamageShape DamageShape { get; set; } = new() { Monster = 1, Elite = 2, Unknown = 1, Boss = 3 };

    /// <summary>
    /// Expected healing per room outside rest sites (fraction of max HP), applied after the room. [측정] mean
    /// <c>hp_healed ÷ max HP</c>: monster 0.018 (n 457), elite 0.043 → 0.041 (n 126), "?" 0.025 (n 330), shop 0.021 →
    /// 0.020 (n 153), treasure 0.006 → 0.005 (n 108); prior 0 (not modelled before). About a quarter of max HP per act
    /// comes back this way, as much as two thirds of what rest sites give.
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public PathRoomHeal RoomHeal { get; set; } = new()
    {
        Monster = 0.018,
        Elite = 0.041,
        Unknown = 0.025,
        Shop = 0.020,
        Treasure = 0.005,
    };

    // ---- What HP is worth ----

    /// <summary>Points per 100% max HP lost at full HP. [추정]</summary>
    public double HpValue { get; set; } = 30;

    /// <summary>
    /// HP is worth more when you have less of it: one HP lost at ratio h costs <c>HpValue × (1 + boost × (1 − h))</c>,
    /// i.e. up to 2× at the default when nearly dead. [추정]
    /// </summary>
    public double LowHpValueBoost { get; set; } = 1.0;

    /// <summary>Cost of dying in a hallway/elite fight, multiplied by the estimated death probability. [추정]</summary>
    public double DeathCost { get; set; } = 150;

    /// <summary>Cost of dying to the boss, multiplied by the estimated death probability at arrival HP. [추정]</summary>
    public double BossDeathCost { get; set; } = 100;

    // ---- Room rewards (points) ----

    /// <summary>Card reward + gold of a hallway fight. [추정]</summary>
    public double MonsterReward { get; set; } = 6;

    /// <summary>Extra card-reward value for a weak deck: added × (1 − strength) to monster and elite rewards. [추정]</summary>
    public double WeakDeckCardBonus { get; set; } = 6;

    /// <summary>Card rewards matter less once the deck has taken shape. [추정]</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> CardRewardActFactor { get; set; } = new() { [1] = 1.0, [2] = 0.85, [3] = 0.7 };

    /// <summary>Relic + better card + gold of an elite (on top of the card reward above). [추정]</summary>
    public double EliteReward { get; set; } = 22;

    /// <summary>An early relic works for longer, so elites are worth more in act 1. [추정]</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> EliteActFactor { get; set; } = new() { [1] = 1.0, [2] = 0.85, [3] = 0.65 };

    /// <summary>Elites per act that are worth full value; guides disagree (1–2 vs 2–3, PLAN §7.6). [추정]</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, int> TargetElites { get; set; } = new() { [1] = 2, [2] = 2, [3] = 1 };

    /// <summary>Elite reward multiplier beyond <see cref="TargetElites"/> (diminishing returns). [추정]</summary>
    public double ExtraEliteFactor { get; set; } = 0.5;

    /// <summary>"?" room expected value (events, sometimes a shop/treasure). [추정]</summary>
    public double UnknownReward { get; set; } = 7;

    /// <summary>"?" rooms are low-variance: extra value × (1 − HP ratio) (PLAN §7.6). [추정]</summary>
    public double UnknownLowHpBonus { get; set; } = 4;

    /// <summary>Treasure room: a free relic. [추정]</summary>
    public double TreasureReward { get; set; } = 18;

    /// <summary>Rest-site upgrade value when there are at least <see cref="UpgradeSaturation"/> upgradable cards. [추정]</summary>
    public double UpgradeValue { get; set; } = 7;

    /// <summary>Below this many unupgraded cards the upgrade value scales down linearly. [추정]</summary>
    public int UpgradeSaturation { get; set; } = 4;

    // ---- Gold and shops ----

    /// <summary>
    /// Gold income per room type, used to know whether a later shop is affordable. [측정] Mean <c>gold_gained</c>:
    /// monster 13.6 (n 457, was 15), elite 34.3 (n 126, was 30), "?" 16.1 (n 330, was 8; events such as sunken
    /// treasures pay a lot).
    /// </summary>
    public int MonsterGold { get; set; } = 14;
    public int EliteGold { get; set; } = 34;
    public int UnknownGold { get; set; } = 16;

    /// <summary>
    /// Gold found in a treasure room (the chest also holds the relic of <see cref="TreasureReward"/>). [측정] mean 46.3
    /// (n 108); new, so its prior was 0 (not modelled before) → 43.
    /// </summary>
    public int TreasureGold { get; set; } = 43;

    /// <summary>Gold at which a shop visit is worth it for cards/relics (beyond a removal). [추정]</summary>
    public int ShopGoldThreshold { get; set; } = 150;

    /// <summary>Value of shopping with enough gold (removal value comes from <c>Weights.Shop</c>). [추정]</summary>
    public double ShopValue { get; set; } = 12;

    /// <summary>Value of a shop you cannot afford anything in (potions, looking). [추정]</summary>
    public double ShopPoorValue { get; set; } = 1;

    // ---- Risk patterns (Silver Compass style) ----

    /// <summary>Below this HP ratio the "HP 낮음" chips appear. [추정]</summary>
    public double LowHpThreshold { get; set; } = 0.4;

    /// <summary>Entering an elite below this HP ratio without a rest close behind is penalised (PLAN: 60–70%). [추정]</summary>
    public double EliteHpThreshold { get; set; } = 0.6;

    /// <summary>"Close behind" = a rest site within this many rooms after the elite. [추정]</summary>
    public int EliteRestLookahead { get; set; } = 2;

    public double EliteNoRestPenalty { get; set; } = 8;

    /// <summary>Two elites in a row. [추정]</summary>
    public double EliteChainPenalty { get; set; } = 6;
}

/// <summary>
/// Expected HP loss of one room, as a fraction of max HP, per act (key 1..3), for a deck of strength 0.5.
/// The scorer multiplies it by the deck-strength factor <c>1 + StrengthDamageSlope × (0.5 − strength)</c>.
/// Missing acts fall back like <see cref="Data.Weights.ForAct{T}"/> (closest lower act).
/// </summary>
public sealed class PathHpLoss
{
    /// <summary>Hallway fight.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> Monster { get; set; } = new();

    /// <summary>Elite fight.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> Elite { get; set; } = new();

    /// <summary>"?" room: mostly events, sometimes a fight. Expected value over everything it resolved into.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> Unknown { get; set; } = new();

    /// <summary>Boss fight; only used for the death-risk term at the end of the path.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> Boss { get; set; } = new();

    /// <summary>Expected loss for <paramref name="type"/> in <paramref name="act"/>; 0 for rooms without a fight.</summary>
    public double For(MapNodeType type, int act) => type switch
    {
        MapNodeType.Monster => Data.Weights.ForAct(Monster, act, 0.0),
        MapNodeType.Elite => Data.Weights.ForAct(Elite, act, 0.0),
        MapNodeType.Unknown => Data.Weights.ForAct(Unknown, act, 0.0),
        MapNodeType.Boss => Data.Weights.ForAct(Boss, act, 0.0),
        _ => 0.0,
    };

    /// <summary>
    /// The first guesses (before run-history measurement): act-1 values 0.08 / 0.20 / 0.03 / 0.35 for monster / elite /
    /// "?" / boss, times an act multiplier 1.0 / 1.25 / 1.45. Kept as the prior that small samples shrink toward
    /// (<c>tools/PathStats</c>) and as the "old" side of the backtest.
    /// </summary>
    public static PathHpLoss Legacy() => new()
    {
        Monster = ByAct(0.08),
        Elite = ByAct(0.20),
        Unknown = ByAct(0.03),
        Boss = ByAct(0.35),
    };

    private static Dictionary<int, double> ByAct(double act1) => new()
    {
        [1] = act1,
        [2] = Math.Round(act1 * 1.25, 4),
        [3] = Math.Round(act1 * 1.45, 4),
    };
}

/// <summary>
/// Expected HP healed in one room outside rest sites, as a fraction of max HP: post-combat relics (e.g. Burning Blood),
/// healing events, potions and relics bought. All zero = not modelled (the old behaviour).
/// </summary>
public sealed class PathRoomHeal
{
    public double Monster { get; set; }
    public double Elite { get; set; }
    public double Unknown { get; set; }
    public double Shop { get; set; }
    public double Treasure { get; set; }

    public double For(MapNodeType type) => type switch
    {
        MapNodeType.Monster => Monster,
        MapNodeType.Elite => Elite,
        MapNodeType.Unknown => Unknown,
        MapNodeType.Shop => Shop,
        MapNodeType.Treasure => Treasure,
        _ => 0,
    };
}

/// <summary>
/// Integer gamma shape k of the damage of one room (mean from <see cref="PathHpLoss"/>): k = 1 is exponential (many
/// small hits, a long tail), larger k is more predictable (standard deviation = mean / √k). 0 = uniform on
/// [0, 2 × mean], which can never deal more than twice the mean and so cannot kill above that HP.
/// </summary>
public sealed class PathDamageShape
{
    public int Monster { get; set; }
    public int Elite { get; set; }
    public int Unknown { get; set; }
    public int Boss { get; set; }
}
