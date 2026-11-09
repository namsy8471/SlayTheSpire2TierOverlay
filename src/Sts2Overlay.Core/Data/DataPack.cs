using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Data;

/// <summary>
/// The full curated data set the engine runs on: tiers, archetypes, combos, weights and (optionally) the local catalog.
/// Build one with <see cref="DataPackLoader"/> or the object initializer (tests).
/// </summary>
public sealed class DataPack
{
    public const string Colorless = "COLORLESS";

    public Weights Weights { get; init; } = new();

    /// <summary>Tier packs keyed by <see cref="TierPack.Character"/>.</summary>
    public IReadOnlyDictionary<string, TierPack> Tiers { get; init; } = new Dictionary<string, TierPack>(StringComparer.Ordinal);

    public IReadOnlyList<Archetype> Archetypes { get; init; } = [];
    public IReadOnlyList<Combo> Combos { get; init; } = [];
    public CardCatalog? Catalog { get; init; }

    /// <summary>Measured event/ancient choice rates (<c>events.stats.json</c>); null when the pack has none.</summary>
    public EventStats? EventStats { get; init; }

    /// <summary>Measured relic pick rates (<c>relics.json</c>); null when the pack has none.</summary>
    public RelicStats? RelicStats { get; init; }

    /// <summary>Measured relic pick rates per build (<c>relic-fit.json</c>); null when the pack has none.</summary>
    public RelicFit? RelicFit { get; init; }

    /// <summary>Old id → current id (e.g. ids renamed by the game's RunHistoryV8ToV9 migration).</summary>
    /// <summary>Learned card pick model (<c>model/card-policy.json</c>), null when the pack has none.</summary>
    public Learning.CardPolicyModel? CardPolicy { get; init; }

    public IReadOnlyDictionary<string, string> IdAliases { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    private Dictionary<string, CatalogCard>? _catalogIndex;

    /// <summary>Resolves aliases.</summary>
    public string Canonical(string cardId) => IdAliases.TryGetValue(cardId, out var to) ? to : cardId;

    /// <summary>
    /// Tier entry for a card: the character's pack first, then <see cref="Colorless"/>, then any other pack.
    /// Returns null when the card is unrated.
    /// </summary>
    public CardTierEntry? FindTier(string character, string cardId)
    {
        var id = Canonical(cardId);
        if (Tiers.TryGetValue(character, out var own) && own.Cards.TryGetValue(id, out var e)) return e;
        if (Tiers.TryGetValue(Colorless, out var cl) && cl.Cards.TryGetValue(id, out e)) return e;
        foreach (var pack in Tiers.Values)
        {
            if (pack.Cards.TryGetValue(id, out e)) return e;
        }
        return null;
    }

    public CatalogCard? FindCatalog(string cardId)
    {
        if (Catalog is null) return null;
        _catalogIndex ??= Catalog.Cards.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return _catalogIndex.TryGetValue(Canonical(cardId), out var c) ? c : null;
    }

    /// <summary>Card type from the catalog, else the tier entry, else a heuristic on the id. Null when unknown.</summary>
    public string? CardType(string character, string cardId)
    {
        var t = FindCatalog(cardId)?.Type ?? FindTier(character, cardId)?.Type;
        if (t is not null) return t.ToLowerInvariant();
        return CardIdHeuristics.IsBasicStrike(cardId) ? CardTypes.Attack
            : CardIdHeuristics.IsBasicDefend(cardId) ? CardTypes.Skill
            : null;
    }

    /// <summary>
    /// True when the catalog gives the card the <c>Eternal</c> keyword: "cannot be removed or transformed from your deck"
    /// (Ascender's Bane, Greed, Folly, …). False when the catalog does not know the card.
    /// </summary>
    public bool IsEternal(string cardId)
        => FindCatalog(cardId)?.Keywords.Contains(EternalKeyword, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>The catalog keyword of cards that cannot be removed or transformed.</summary>
    public const string EternalKeyword = "Eternal";

    /// <summary>Card rarity from the catalog, else the tier entry. Null when unknown.</summary>
    public string? CardRarity(string character, string cardId)
        => (FindCatalog(cardId)?.Rarity ?? FindTier(character, cardId)?.Rarity)?.ToLowerInvariant();

    /// <summary>
    /// Whose pool a card belongs to (<c>CHARACTER.X</c> or <see cref="Colorless"/>, see <see cref="CatalogCard.PoolOwner"/>);
    /// without a catalog entry, <see cref="Colorless"/> when only the colorless tier pack rates it. Null when unknown or shared (event, curse, ...).
    /// </summary>
    public string? PoolOwner(string cardId)
    {
        var known = FindCatalog(cardId);
        if (known is not null) return known.PoolOwner;
        var id = Canonical(cardId);
        if (Tiers.TryGetValue(Colorless, out var cl) && cl.Cards.ContainsKey(id)
            && !Tiers.Any(kv => kv.Key != Colorless && kv.Value.Cards.ContainsKey(id)))
        {
            return Colorless;
        }
        return null;
    }

    /// <summary>True when <see cref="PoolOwner"/> is <see cref="Colorless"/>.</summary>
    public bool IsColorless(string cardId) => PoolOwner(cardId) == Colorless;

    public IEnumerable<Archetype> ArchetypesFor(string character)
        => Archetypes.Where(a => string.Equals(a.Character, character, StringComparison.Ordinal));

    public IEnumerable<Combo> CombosFor(string character)
        => Combos.Where(c => c.Character is null || string.Equals(c.Character, character, StringComparison.Ordinal));
}

/// <summary>Id-based fallbacks used before a catalog exists.</summary>
public static class CardIdHeuristics
{
    public static bool IsBasicStrike(string id) => id.StartsWith("CARD.STRIKE_", StringComparison.Ordinal);
    public static bool IsBasicDefend(string id) => id.StartsWith("CARD.DEFEND_", StringComparison.Ordinal);
    public static bool IsBasicStrikeOrDefend(string id) => IsBasicStrike(id) || IsBasicDefend(id);
}
