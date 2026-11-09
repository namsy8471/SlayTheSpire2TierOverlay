using System.Text.Json.Serialization;

namespace Sts2Overlay.Core.Events;

/// <summary>
/// Point values of event effects, on the same scale as card scores and <see cref="Map.PathWeights"/>
/// (a basic card removal ≈ 14, a treasure relic ≈ 18, 100% max HP at full HP = 30). HP itself is priced by
/// <see cref="Map.PathWeights.HpValue"/> / <see cref="Map.PathWeights.LowHpValueBoost"/> so map and event advice agree.
/// [추정] values are estimates to be tuned against run history (tools/RunHistoryAnalyzer events).
/// Overridable from <c>weights.json</c> under <c>"events"</c> (merged like <c>skip</c>/<c>shop</c>).
/// </summary>
public sealed class EventWeights
{
    /// <summary>
    /// How much of the measured top-player choice rate (<c>events.stats.json</c>) is added to an option's score:
    /// <c>score = formula + MeasuredBlend × MeasuredScale × (rate − 1/k)</c>. 0 = the formula alone (no stats used).
    /// </summary>
    public double MeasuredBlend { get; set; } = 0.5;

    /// <summary>
    /// Points for a rate 1.0 above neutral, about the spread of the formula's own values (transform 16–28, relic 13–24).
    /// With the default blend, a 90% pick on a two-option page adds 0.5 × 20 × 0.4 = 4 points.
    /// </summary>
    public double MeasuredScale { get; set; } = 20;

    /// <summary>Points per gold. A 75-gold removal is worth ≈ 14 → 0.19; a 50–75-gold shop card ≈ 8–12 → 0.15. [추정]</summary>
    public double GoldValue { get; set; } = 0.15;

    /// <summary>Gold above this is worth half (nothing left to buy). [추정]</summary>
    public int GoldSaturation { get; set; } = 300;

    /// <summary>
    /// Max HP is permanent (and rest sites heal a share of it): one max HP is worth this many current HP, by act.
    /// Earlier is worth more because it lasts longer. [추정]
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> MaxHpFactor { get; set; } = new() { [1] = 2.2, [2] = 1.8, [3] = 1.4 };

    public double MaxHpFactorFor(int act)
        => MaxHpFactor.TryGetValue(act, out var f) ? f
            : MaxHpFactor.Count == 0 ? 1 : MaxHpFactor.Where(kv => kv.Key <= act).OrderByDescending(kv => kv.Key).Select(kv => kv.Value).DefaultIfEmpty(MaxHpFactor.Values.First()).First();

    /// <summary>Upgrading a chosen card (same as a rest-site smith, <see cref="Map.PathWeights.UpgradeValue"/>) and a random one. [추정]</summary>
    public double UpgradeChosen { get; set; } = 7;
    public double UpgradeRandom { get; set; } = 4.5;

    /// <summary>Removing a chosen basic card / curse is <see cref="Data.ShopWeights.BasicRemovalValue"/> / <see cref="Data.ShopWeights.CurseRemovalValue"/>.
    /// Removing a non-basic card the player chose (when no basics are left). [추정]</summary>
    public double RemoveOtherChosen { get; set; } = 4;

    /// <summary>Losing a random non-basic, non-curse card. [추정]</summary>
    public double RemoveOtherRandom { get; set; } = -12;

    /// <summary>Transforming a basic card / curse / other card (chosen); a random transform averages over the deck. [추정]</summary>
    public double TransformBasic { get; set; } = 16;
    public double TransformCurse { get; set; } = 28;
    public double TransformOther { get; set; } = -6;

    /// <summary>Gaining a curse (the cost of carrying it or paying to remove it). [추정]</summary>
    public double CursePenalty { get; set; } = 25;

    /// <summary>
    /// Per-curse multiplier on <see cref="CursePenalty"/> (card id → factor, 1 when absent). Ethereal curses exhaust
    /// themselves at the end of the turn they are drawn, so they cost one dead draw per combat instead of one per
    /// shuffle: CLUMSY (unplayable, ethereal), and FOLLY (also innate and eternal: always in the first hand, never removable).
    /// Top players take THIS_OR_THAT's relic + Clumsy over 41–68 gold 68% of the time (950 picks, 20 players). [추정]
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, double> CurseSeverity { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CARD.CLUMSY"] = 0.4, ["CARD.FOLLY"] = 0.5,
    };

    /// <summary><see cref="CursePenalty"/> for one copy of <paramref name="cardId"/> (null = an unknown curse).</summary>
    public double CursePenaltyFor(string? cardId)
        => CursePenalty * (cardId is not null && CurseSeverity.TryGetValue(cardId, out var m) ? m : 1);

    /// <summary>A card reward screen (pick 1 of N, may skip). Rare-only rewards are worth more. [추정]</summary>
    public double CardReward { get; set; } = 7;
    public double RareCardReward { get; set; } = 12;

    /// <summary>A random card of a pool added without choice (above skip on average only for good pools). [추정]</summary>
    public double RandomCardAdd { get; set; } = 1;

    /// <summary>
    /// Relic by tier; an unspecified/random relic uses <see cref="RelicDefault"/>. [추정]
    /// A relic with a stable measured rate in <c>relics.json</c> is scaled by <see cref="Data.RelicStats.ValueMultiplier"/>.
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, double> RelicTier { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["common"] = 13, ["uncommon"] = 16, ["rare"] = 20, ["shop"] = 16, ["event"] = 15, ["ancient"] = 24, ["boss"] = 24,
    };

    public double RelicDefault { get; set; } = 16;

    /// <summary>
    /// One relic's value: <see cref="RelicTier"/>[<paramref name="tier"/>] (else <see cref="RelicDefault"/>), times
    /// <see cref="Data.RelicStats.ValueMultiplier"/> when <paramref name="stats"/> has a usable rate for <paramref name="relicId"/>.
    /// Without stats (or for an unknown/random relic) it is the rarity value exactly.
    /// </summary>
    public double RelicValue(string? tier, string? relicId, Data.RelicStats? stats)
    {
        var value = tier is not null && RelicTier.TryGetValue(tier, out var tv) ? tv : RelicDefault;
        return stats is null ? value : value * stats.ValueMultiplier(relicId);
    }

    /// <summary>Losing a relic. [추정]</summary>
    public double RelicLoss { get; set; } = 14;

    public double PotionValue { get; set; } = 4;

    /// <summary>One enchantment on a card (e.g. a chosen attack). [추정]</summary>
    public double EnchantValue { get; set; } = 6;

    /// <summary>Copying a chosen deck card. [추정]</summary>
    public double DuplicateValue { get; set; } = 8;

    /// <summary>An event fight: expected HP loss as a fraction of max HP, and its rewards when the data names none. [추정]</summary>
    public double FightHpLossFraction { get; set; } = 0.12;
    public double FightReward { get; set; } = 6;

    /// <summary>An option that would kill the player (game flags it with ThatDoesDamage / WillKillPlayer).</summary>
    public double DeathCost { get; set; } = 150;

    /// <summary>Recommendation confidence gaps (points between the best and second-best option).</summary>
    public double SureGap { get; set; } = 8;
    public double CloseGap { get; set; } = 3;

    /// <summary>How deep "next page" options are looked ahead (Abyssal Baths style loops stop here).</summary>
    public int LookaheadDepth { get; set; } = 2;
}
