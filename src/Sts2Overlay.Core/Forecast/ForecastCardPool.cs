using System.Text.Json;
using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Forecast;

/// <summary>Rarity of a card that can show up in a card reward or shop.</summary>
public enum PoolRarity
{
    Common,
    Uncommon,
    Rare,
}

/// <summary>One card that can be offered.</summary>
/// <param name="Id">Game model id, e.g. <c>CARD.GLACIER</c>.</param>
/// <param name="Character">Owning character (<c>CHARACTER.DEFECT</c>), or null for a colorless card.</param>
/// <param name="Rarity">Reward rarity.</param>
/// <param name="Type">attack | skill | power (lower case), or null when unknown.</param>
/// <param name="MultiplayerOnly">Only offered in co-op runs.</param>
/// <param name="SingleplayerOnly">Only offered in single-player runs.</param>
public sealed record PoolCard(string Id, string? Character, PoolRarity Rarity, string? Type, bool MultiplayerOnly = false, bool SingleplayerOnly = false)
{
    public bool IsColorless => Character is null;
}

/// <summary>
/// Reward-eligible cards per character plus the colorless pool (shops only), with rarities.
/// Sources, best first: the committed id-only catalog (<c>data/catalog/cards.&lt;version&gt;.json</c>: game facts, no text),
/// a <see cref="CardCatalog"/> (pack or local), or the tier pack's own <c>rarity</c> fields.
/// </summary>
/// <remarks>Basic, ancient, event, status, curse, token and quest cards never appear in rewards and are left out.</remarks>
public sealed class ForecastCardPool(IEnumerable<PoolCard> cards)
{
    public IReadOnlyList<PoolCard> All { get; } = cards.ToArray();

    /// <summary>Cards of <paramref name="character"/> the run can be offered (co-op-only cards only in co-op, and vice versa).</summary>
    public IEnumerable<PoolCard> CharacterCards(string character, bool multiplayer)
        => Eligible(All.Where(c => string.Equals(c.Character, character, StringComparison.Ordinal)), multiplayer);

    /// <summary>Colorless cards (the shop's two colorless slots).</summary>
    public IEnumerable<PoolCard> ColorlessCards(bool multiplayer) => Eligible(All.Where(c => c.IsColorless), multiplayer);

    private static IEnumerable<PoolCard> Eligible(IEnumerable<PoolCard> cards, bool multiplayer)
        => cards.Where(c => multiplayer ? !c.SingleplayerOnly : !c.MultiplayerOnly);

    /// <summary>
    /// Parses a catalog JSON. Understands both the committed CatalogBuilder format
    /// (<c>pool: "CARD_POOL.DEFECT_CARD_POOL", character: "CHARACTER.DEFECT", multiplayer: "MultiplayerOnly"</c>)
    /// and the <see cref="CardCatalog"/> format (<c>pool: "CHARACTER.DEFECT" | "COLORLESS", multiplayerOnly: true</c>).
    /// </summary>
    public static ForecastCardPool FromCatalogJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var list = new List<PoolCard>();
        if (!doc.RootElement.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array)
        {
            return new ForecastCardPool(list);
        }

        foreach (var c in cards.EnumerateArray())
        {
            var id = Str(c, "id");
            var rarity = ParseRarity(Str(c, "rarity"));
            if (id is null || rarity is null) continue;
            var pool = Str(c, "pool") ?? "";
            var colorless = pool.Contains("COLORLESS", StringComparison.OrdinalIgnoreCase);
            var character = Str(c, "character");
            if (character is null && pool.StartsWith("CHARACTER.", StringComparison.Ordinal)) character = pool;
            if (!colorless && character is null) continue; // event / token / curse pools
            var mp = Str(c, "multiplayer");
            var mpOnly = string.Equals(mp, "MultiplayerOnly", StringComparison.OrdinalIgnoreCase)
                         || (c.TryGetProperty("multiplayerOnly", out var m) && m.ValueKind == JsonValueKind.True);
            var spOnly = string.Equals(mp, "SingleplayerOnly", StringComparison.OrdinalIgnoreCase);
            list.Add(new PoolCard(id, colorless ? null : character, rarity.Value, Str(c, "type")?.ToLowerInvariant(), mpOnly, spOnly));
        }
        return new ForecastCardPool(list);
    }

    /// <summary>From a <see cref="CardCatalog"/> whose <c>Pool</c> is a character id or <see cref="DataPack.Colorless"/>.</summary>
    public static ForecastCardPool FromCatalog(CardCatalog catalog)
    {
        var list = new List<PoolCard>();
        foreach (var c in catalog.Cards)
        {
            var rarity = ParseRarity(c.Rarity);
            if (rarity is null || c.Pool is null) continue;
            var colorless = c.Pool.Contains("COLORLESS", StringComparison.OrdinalIgnoreCase);
            if (!colorless && !c.Pool.StartsWith("CHARACTER.", StringComparison.Ordinal)) continue;
            list.Add(new PoolCard(c.Id, colorless ? null : c.Pool, rarity.Value, c.Type?.ToLowerInvariant(), c.MultiplayerOnly));
        }
        return new ForecastCardPool(list);
    }

    /// <summary>Fallback without any catalog: every tier entry that names a reward rarity.</summary>
    public static ForecastCardPool FromTierPacks(DataPack pack)
    {
        var list = new List<PoolCard>();
        foreach (var (owner, tiers) in pack.Tiers)
        {
            var colorless = string.Equals(owner, DataPack.Colorless, StringComparison.Ordinal);
            foreach (var (id, entry) in tiers.Cards)
            {
                var rarity = ParseRarity(entry.Rarity);
                if (rarity is null) continue;
                list.Add(new PoolCard(id, colorless ? null : owner, rarity.Value, entry.Type?.ToLowerInvariant()));
            }
        }
        return new ForecastCardPool(list);
    }

    /// <summary>The pack's catalog when it has one, else the tier packs.</summary>
    public static ForecastCardPool FromPack(DataPack pack)
        => pack.Catalog is { Cards.Count: > 0 } catalog ? FromCatalog(catalog) : FromTierPacks(pack);

    public static PoolRarity? ParseRarity(string? rarity) => rarity?.ToLowerInvariant() switch
    {
        "common" => PoolRarity.Common,
        "uncommon" => PoolRarity.Uncommon,
        "rare" => PoolRarity.Rare,
        _ => null,
    };

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
