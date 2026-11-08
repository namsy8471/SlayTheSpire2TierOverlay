namespace Sts2Overlay.Core.Combat;

/// <summary>A deck card's enchantment (<c>ENCHANTMENT.X</c>) and its amount, as the save and the run history record them.</summary>
public sealed record Enchant(string Id, int Amount);

/// <summary>
/// What each enchantment does in a fight (<c>MegaCrit.Sts2.Core.Models.Enchantments</c>, v0.107.1). The simulator calls
/// these at the same points the game does: when the card is built (keywords, cost), when its damage is computed
/// (<c>Hook.ModifyDamage</c>: the enchantment's additive then multiplicative bonus, before Strength and the rest), and after
/// each play (<c>CardModel</c> runs <c>Enchantment.OnPlay</c> after the card's own effects).
/// </summary>
internal static class Enchantments
{
    public const string Adroit = "ENCHANTMENT.ADROIT";             // on play: gain Amount block
    public const string Clone = "ENCHANTMENT.CLONE";               // nothing in a fight (a marker for another effect)
    public const string Corrupted = "ENCHANTMENT.CORRUPTED";       // attack ×1.5; on play: lose 2 HP
    public const string Glam = "ENCHANTMENT.GLAM";                 // the first play of the combat is played twice
    public const string Goopy = "ENCHANTMENT.GOOPY";               // Exhaust; block only
    public const string Imbued = "ENCHANTMENT.IMBUED";             // starts at the bottom of the draw pile, auto-played on turn 1
    public const string Inky = "ENCHANTMENT.INKY";                 // attack +1; on play: Weak 1
    public const string Instinct = "ENCHANTMENT.INSTINCT";         // attack ×2
    public const string Momentum = "ENCHANTMENT.MOMENTUM";         // attack + Amount for every earlier play this combat
    public const string Nimble = "ENCHANTMENT.NIMBLE";             // block only
    public const string PerfectFit = "ENCHANTMENT.PERFECT_FIT";    // goes on top of the draw pile when it is reshuffled
    public const string RoyallyApproved = "ENCHANTMENT.ROYALLY_APPROVED"; // Innate, Retain
    public const string Sharp = "ENCHANTMENT.SHARP";               // attack + Amount
    public const string Slither = "ENCHANTMENT.SLITHER";           // when drawn: costs a random 0-3 this combat
    public const string SlumberingEssence = "ENCHANTMENT.SLUMBERING_ESSENCE"; // each end of turn in hand: −1 cost until played
    public const string SoulsPower = "ENCHANTMENT.SOULS_POWER";    // loses Exhaust
    public const string Sown = "ENCHANTMENT.SOWN";                 // the first play: gain Amount energy
    public const string Spiral = "ENCHANTMENT.SPIRAL";             // every play is played twice
    public const string Steady = "ENCHANTMENT.STEADY";             // Retain
    public const string Swift = "ENCHANTMENT.SWIFT";               // the first play: draw Amount
    public const string TezcatarasEmber = "ENCHANTMENT.TEZCATARAS_EMBER"; // costs 0, Eternal, attack +3
    public const string Vigorous = "ENCHANTMENT.VIGOROUS";         // attack + Amount on the first play only
    public const string Deprecated = "ENCHANTMENT.DEPRECATED_ENCHANTMENT";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        Adroit, Clone, Corrupted, Glam, Goopy, Imbued, Inky, Instinct, Momentum, Nimble, PerfectFit, RoyallyApproved,
        Sharp, Slither, SlumberingEssence, SoulsPower, Sown, Spiral, Steady, Swift, TezcatarasEmber, Vigorous, Deprecated,
    };

    /// <summary>The card as the enchantment leaves it (<c>OnEnchant</c>): keywords added or removed, cost set to 0.</summary>
    public static CardCombat Apply(CardCombat card, Enchant? e)
    {
        if (e is null) return card;
        return e.Id switch
        {
            SoulsPower => card with { Keywords = Without(card.Keywords, "Exhaust") },
            Goopy => card with { Keywords = With(card.Keywords, "Exhaust") },
            RoyallyApproved => card with { Keywords = With(With(card.Keywords, "Innate"), "Retain") },
            Steady => card with { Keywords = With(card.Keywords, "Retain") },
            TezcatarasEmber when !card.CostX && !card.Unplayable => card with { Cost = 0, CostExpr = null, Keywords = With(card.Keywords, "Eternal") },
            _ => card,
        };
    }

    /// <summary>The attack's base damage after the enchantment (<c>EnchantDamageAdditive</c>, then <c>…Multiplicative</c>).</summary>
    /// <param name="e">The card's enchantment.</param>
    /// <param name="dmg">The hit's damage before Strength and the other powers.</param>
    /// <param name="momentum">Damage Momentum has built up on this card.</param>
    /// <param name="used">Vigorous: the bonus is gone after the first play.</param>
    public static double Damage(Enchant e, double dmg, double momentum, bool used)
    {
        var add = e.Id switch
        {
            Sharp => e.Amount,
            Inky => 1,
            TezcatarasEmber => 3,
            Momentum => momentum,
            Vigorous when !used => e.Amount,
            _ => 0,
        };
        var mult = e.Id switch
        {
            Corrupted => 1.5,
            Instinct => 2,
            _ => 1,
        };
        return (dmg + add) * mult;
    }

    /// <summary>Extra plays of one card play (<c>EnchantPlayCount</c>): Spiral always, Glam the first time.</summary>
    public static int ExtraPlays(Enchant e, bool used) => e.Id switch
    {
        Spiral => 1,
        Glam when !used => 1,
        _ => 0,
    };

    private static IReadOnlySet<string> With(IReadOnlySet<string> keywords, string k)
        => keywords.Contains(k) ? keywords : new HashSet<string>(keywords, StringComparer.Ordinal) { k };

    private static IReadOnlySet<string> Without(IReadOnlySet<string> keywords, string k)
    {
        if (!keywords.Contains(k)) return keywords;
        var set = new HashSet<string>(keywords, StringComparer.Ordinal);
        set.Remove(k);
        return set;
    }
}
