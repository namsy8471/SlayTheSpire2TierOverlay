using System.Globalization;
using System.IO;
using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.App.Infrastructure;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Display names and card types for the UI. Korean names come from the locally generated catalog
/// (<c>data/local/catalog.&lt;version&gt;.json</c>, gitignored, built by tools/CatalogBuilder + PckTool) or the pack's own
/// catalog; without either, ids are prettified (<c>CARD.BALL_LIGHTNING</c> → <c>Ball Lightning</c>).
/// </summary>
public sealed class CardInfoProvider
{
    private readonly Dictionary<string, (string? Kor, string? Type, string? Rarity)> _local;
    private readonly DataPack _pack;

    private CardInfoProvider(DataPack pack, Dictionary<string, (string?, string?, string?)> local)
    {
        _pack = pack;
        _local = local;
    }

    public int LocalCount => _local.Count;

    public static CardInfoProvider Load(string? catalogPath, DataPack pack)
    {
        var map = new Dictionary<string, (string?, string?, string?)>(StringComparer.Ordinal);
        if (catalogPath is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(catalogPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cards.EnumerateArray())
                    {
                        var id = Str(c, "id");
                        if (id is null) continue;
                        string? kor = null;
                        if (c.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Object) kor = Str(names, "kor");
                        map[id] = (kor, Str(c, "type")?.ToLowerInvariant(), Str(c, "rarity")?.ToLowerInvariant());
                    }
                }
                AppLog.Info($"card names: {map.Count} cards from {catalogPath}");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                AppLog.Warn($"card names: could not read {catalogPath} ({ex.Message})");
            }
        }
        return new CardInfoProvider(pack, map);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Korean name, with "+" (or "+2") for upgrades.</summary>
    public string Name(CardRef card)
        => card.Upgrade switch
        {
            0 => Name(card.Id),
            1 => Name(card.Id) + "+",
            _ => Name(card.Id) + "+" + card.Upgrade.ToString(CultureInfo.InvariantCulture),
        };

    public string Name(string id)
    {
        var canonical = _pack.Canonical(id);
        if (_local.TryGetValue(canonical, out var info) && !string.IsNullOrEmpty(info.Kor)) return info.Kor;
        if (_pack.FindCatalog(canonical)?.Names.TryGetValue("kor", out var k) == true && !string.IsNullOrEmpty(k)) return k;
        return Prettify(canonical);
    }

    /// <summary>attack | skill | power | status | curse | quest, or null.</summary>
    public string? Type(string character, string id)
        => _pack.CardType(character, id) ?? (_local.TryGetValue(_pack.Canonical(id), out var info) ? info.Type : null);

    public string? Rarity(string character, string id)
        => _pack.CardRarity(character, id) ?? (_local.TryGetValue(_pack.Canonical(id), out var info) ? info.Rarity : null);

    /// <summary>Rarity display name. TEMPORARY stand-in (app-controllers branch) until the language-aware version lands.</summary>
    public static string RarityName(string? rarity) => RarityKorean(rarity);

    public static string RarityKorean(string? rarity) => rarity switch
    {
        "basic" => "기본",
        "common" => "일반",
        "uncommon" => "고급",
        "rare" => "희귀",
        "ancient" => "고대",
        "special" => "특수",
        "curse" => "저주",
        _ => "?",
    };

    private static string Prettify(string id)
    {
        var dot = id.IndexOf('.', StringComparison.Ordinal);
        var raw = dot >= 0 ? id[(dot + 1)..] : id;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Replace('_', ' ').ToLowerInvariant());
    }
}
