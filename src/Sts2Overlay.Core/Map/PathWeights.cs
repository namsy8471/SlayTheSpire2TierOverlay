using System.Text.Json.Serialization;

namespace Sts2Overlay.Core.Map;

/// <summary>
/// Tunable numbers of the path advisor (PLAN §7.6), exposed as <c>weights.json → "path"</c>.
/// Units: "points" on roughly the same scale as card scores (an S-tier card is worth ~80, skip ~45), so a relic from
/// an elite (~22) is "about one good card pick". HP amounts are fractions of max HP (0.1 = 10%).
/// Almost every number here is a first guess marked [추정]; they are meant to be tuned from run history (PLAN §7.7).
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

    // ---- Expected HP loss per room, fraction of max HP, for a deck of strength 0.5 in act 1 ----

    /// <summary>Hallway fight. [추정]</summary>
    public double MonsterHpLoss { get; set; } = 0.08;

    /// <summary>Elite fight. [추정]</summary>
    public double EliteHpLoss { get; set; } = 0.20;

    /// <summary>"?" room: mostly events, sometimes a fight. Expected value, so small. [추정]</summary>
    public double UnknownHpLoss { get; set; } = 0.03;

    /// <summary>Boss fight; only used for the death-risk term at the end of the path. [추정]</summary>
    public double BossHpLoss { get; set; } = 0.35;

    /// <summary>Damage multiplier per act (enemies hit harder later). [추정]</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> ActDamage { get; set; } = new() { [1] = 1.0, [2] = 1.25, [3] = 1.45 };

    /// <summary>Damage multiplier = <c>1 + slope × (0.5 − strength)</c>: a strong deck (1.0) takes half, a weak one (0.0) 1.5×. [추정]</summary>
    public double StrengthDamageSlope { get; set; } = 1.0;

    /// <summary>Rest site heal, fraction of max HP. [추정] STS1 value; not yet confirmed in STS2 code.</summary>
    public double RestHeal { get; set; } = 0.30;

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

    /// <summary>Gold income per room type, used to know whether a later shop is affordable. [추정]</summary>
    public int MonsterGold { get; set; } = 15;
    public int EliteGold { get; set; } = 30;
    public int UnknownGold { get; set; } = 8;

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
