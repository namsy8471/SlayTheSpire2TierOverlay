using System.Text.Json;

namespace Sts2Overlay.Vision;

/// <summary>Minimal card info the recogniser needs (no names: the committed catalog is names-free).</summary>
public sealed record CatalogCard(string Id, string Pool, string? Character, string? Rarity, string? Type)
{
    /// <summary>Folder name the game uses for the pool's portraits, e.g. CARD_POOL.IRONCLAD_CARD_POOL → "ironclad".</summary>
    public string PoolFolder => VisionCatalog.PoolFolder(Pool);
}

/// <summary>
/// Reads <c>data/catalog/cards.&lt;version&gt;.json</c> (written by tools/CatalogBuilder). Only the fields used
/// for art lookup and pool hints are read, so the reader keeps working if the catalog grows.
/// </summary>
public sealed class VisionCatalog
{
    public const string ColorlessPool = "CARD_POOL.COLORLESS_CARD_POOL";

    /// <summary>Pools whose cards can appear in card rewards and the shop.</summary>
    public static readonly IReadOnlySet<string> OfferablePools = new HashSet<string>(StringComparer.Ordinal)
    {
        "CARD_POOL.IRONCLAD_CARD_POOL", "CARD_POOL.SILENT_CARD_POOL", "CARD_POOL.DEFECT_CARD_POOL",
        "CARD_POOL.NECROBINDER_CARD_POOL", "CARD_POOL.REGENT_CARD_POOL", "CARD_POOL.FOOL_CARD_POOL", ColorlessPool,
    };

    public IReadOnlyList<CatalogCard> Cards { get; }
    private readonly Dictionary<string, CatalogCard> _byId;

    public VisionCatalog(IEnumerable<CatalogCard> cards)
    {
        Cards = cards.ToList();
        _byId = Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    public CatalogCard? Find(string id) => _byId.GetValueOrDefault(id);

    public static VisionCatalog Load(string path)
    {
        using FileStream fs = File.OpenRead(path);
        using JsonDocument doc = JsonDocument.Parse(fs);
        var cards = new List<CatalogCard>();
        foreach (JsonElement c in doc.RootElement.GetProperty("cards").EnumerateArray())
        {
            cards.Add(new CatalogCard(
                c.GetProperty("id").GetString()!,
                c.GetProperty("pool").GetString() ?? "",
                Str(c, "character"),
                Str(c, "rarity"),
                Str(c, "type")));
        }
        return new VisionCatalog(cards);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string PoolFolder(string pool)
    {
        string s = pool.StartsWith("CARD_POOL.", StringComparison.Ordinal) ? pool["CARD_POOL.".Length..] : pool;
        if (s.EndsWith("_CARD_POOL", StringComparison.Ordinal)) s = s[..^"_CARD_POOL".Length];
        return s.ToLowerInvariant();
    }

    /// <summary>The card pool of a character id such as CHARACTER.IRONCLAD.</summary>
    public static string PoolOfCharacter(string characterId)
    {
        string name = characterId.StartsWith("CHARACTER.", StringComparison.Ordinal) ? characterId["CHARACTER.".Length..] : characterId;
        return $"CARD_POOL.{name.ToUpperInvariant()}_CARD_POOL";
    }
}
