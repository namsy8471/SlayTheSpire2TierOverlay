using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Data;

/// <summary>Chance that one generated card has each rarity. The three values sum to 1.</summary>
public readonly record struct RarityChances(double Common, double Uncommon, double Rare)
{
    /// <summary>Chance for a rarity name (any case); 0 for rarities a source never rolls (basic, ancient, ...).</summary>
    public double For(string? rarity) => rarity?.ToLowerInvariant() switch
    {
        "common" => Common,
        "uncommon" => Uncommon,
        "rare" => Rare,
        _ => 0.0,
    };
}

/// <summary>
/// Evaluates <see cref="CardOddsWeights"/>: rarity odds per source with ascension and the rare pity offset, the pity update,
/// and the reward upgrade chance. Numbers are code-confirmed (SPIKE §5); the uncommon/common split under a non-zero offset is an assumption
/// (see <see cref="Rarity"/>).
/// </summary>
public static class CardOdds
{
    /// <summary>The odds table of a source. <see cref="OfferContext.Event"/> uses <see cref="CardOddsWeights.CardReward"/>.</summary>
    public static SourceRarityOdds Source(CardOddsWeights w, OfferContext context) => context switch
    {
        OfferContext.EliteReward => w.EliteReward,
        OfferContext.BossReward => w.BossReward,
        OfferContext.Shop => w.Shop,
        _ => w.CardReward,
    };

    public static bool IsHighAscension(CardOddsWeights w, int ascension) => ascension >= w.HighAscension;

    /// <summary>Rare odds of a source before the pity offset (high-ascension value when <paramref name="ascension"/> ≥ <see cref="CardOddsWeights.HighAscension"/>).</summary>
    public static double BaseRare(CardOddsWeights w, OfferContext context, int ascension)
    {
        var src = Source(w, context);
        if (!IsHighAscension(w, ascension)) return src.Rare;
        return src.RareHighAscension ?? src.Rare * w.RareMultiplierHighAscension;
    }

    /// <summary>
    /// Rarity chances of one card from <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// The game rolls <c>r ∈ [0, 1)</c> and makes the card rare when <c>r &lt; rare + offset</c> (log: <c>Card rarity: Rolled 0.455, need &lt; -0.02 for rare</c>
    /// = 0.03 − 0.05). <b>Assumption [추정]</b>: like Slay the Spire 1's <c>roll + cardBlizzRandomizer</c>, the offset shifts the whole roll, so the
    /// uncommon threshold moves with it: uncommon = <c>clamp(rare + offset + uncommon) − clamp(rare + offset)</c> and common takes the rest. With the
    /// starting offset (−5%) a normal combat card is therefore 0% rare, 35% uncommon, 65% common.
    /// The offset is capped at <see cref="RarePityWeights.Cap"/>. A source whose base rare odds are ≥ 1 (boss) is always rare.
    /// Pass <paramref name="pityOffset"/> = 0 for the plain table odds.
    /// </remarks>
    public static RarityChances Rarity(CardOddsWeights w, OfferContext context, int ascension, double pityOffset = 0)
    {
        var rare = BaseRare(w, context, ascension);
        if (rare >= 1.0) return new RarityChances(0, 0, 1);

        var uncommon = Source(w, context).Uncommon;
        var offset = double.IsFinite(pityOffset) ? Math.Min(pityOffset, w.Pity.Cap) : 0.0;
        var rareThreshold = Math.Clamp(rare + offset, 0.0, 1.0);
        var uncommonThreshold = Math.Clamp(rare + offset + Math.Max(0.0, uncommon), rareThreshold, 1.0);
        return new RarityChances(1.0 - uncommonThreshold, uncommonThreshold - rareThreshold, rareThreshold);
    }

    /// <summary>Pity step per non-rare card (smaller at high ascension).</summary>
    public static double PityStep(CardOddsWeights w, int ascension)
        => IsHighAscension(w, ascension) ? w.Pity.StepHighAscension : w.Pity.Step;

    /// <summary>Offset after one card: reset to <see cref="RarePityWeights.Start"/> on a rare, otherwise + step, capped.</summary>
    public static double NextPityOffset(CardOddsWeights w, double offset, bool rolledRare, int ascension)
        => rolledRare ? w.Pity.Start : Math.Min(w.Pity.Cap, offset + PityStep(w, ascension));

    /// <summary>
    /// Chance that a reward card is upgraded: <c>act × UpgradeChancePerAct</c> (high ascension: <c>UpgradeChancePerActHighAscension</c>),
    /// clamped to 0..1. Rare cards and shop cards are never upgraded.
    /// </summary>
    public static double UpgradeChance(CardOddsWeights w, OfferContext context, int act, int ascension, string? rarity)
    {
        if (context == OfferContext.Shop || string.Equals(rarity, "rare", StringComparison.OrdinalIgnoreCase)) return 0.0;
        var perAct = IsHighAscension(w, ascension) ? w.UpgradeChancePerActHighAscension : w.UpgradeChancePerAct;
        return Math.Clamp(Math.Max(0, act) * perAct, 0.0, 1.0);
    }
}
