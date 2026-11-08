using System.Globalization;
using System.IO;
using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.App.Infrastructure;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Display names and card types for the UI. Names follow the overlay language (<see cref="Loc.Current"/>, looked up on
/// every call so a language switch needs no reload). Korean/English names come from the locally generated catalog
/// (<c>data/local/catalog.&lt;version&gt;.json</c>, gitignored, built by tools/CatalogBuilder + PckTool) or the pack's own
/// catalog; without either, ids are prettified (<c>CARD.BALL_LIGHTNING</c> → <c>Ball Lightning</c>).
/// </summary>
public sealed class CardInfoProvider
{
    private readonly Dictionary<string, (string? Kor, string? Eng, string? Type, string? Rarity)> _local;
    private readonly DataPack _pack;

    private CardInfoProvider(DataPack pack, Dictionary<string, (string?, string?, string?, string?)> local)
    {
        _pack = pack;
        _local = local;
    }

    public int LocalCount => _local.Count;

    public static CardInfoProvider Load(string? catalogPath, DataPack pack)
    {
        var map = new Dictionary<string, (string?, string?, string?, string?)>(StringComparer.Ordinal);
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
                        string? kor = null, eng = null;
                        if (c.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Object)
                        {
                            kor = Str(names, "kor");
                            eng = Str(names, "eng");
                        }
                        map[id] = (kor, eng, Str(c, "type")?.ToLowerInvariant(), Str(c, "rarity")?.ToLowerInvariant());
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

    /// <summary>Name in the overlay language, with "+" (or "+2") for upgrades.</summary>
    public string Name(CardRef card)
        => card.Upgrade switch
        {
            0 => Name(card.Id),
            1 => Name(card.Id) + "+",
            _ => Name(card.Id) + "+" + card.Upgrade.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// Name in the overlay language: the local catalog, then the pack catalog, in the current language first and the
    /// other language only when neither has it; the prettified id last.
    /// </summary>
    public string Name(string id)
    {
        var canonical = _pack.Canonical(id);
        var english = Loc.IsEnglish;
        _local.TryGetValue(canonical, out var info);
        var packNames = _pack.FindCatalog(canonical)?.Names;
        string? PackName(string code) => packNames is not null && packNames.TryGetValue(code, out var n) && !string.IsNullOrEmpty(n) ? n : null;
        var current = english ? info.Eng.NullIfEmpty() ?? PackName("eng") : info.Kor.NullIfEmpty() ?? PackName("kor");
        if (current is not null) return current;
        // English players get the prettified id (already English) before any Korean text.
        if (english) return Prettify(canonical);
        return info.Eng.NullIfEmpty() ?? PackName("eng") ?? Prettify(canonical);
    }

    /// <summary>attack | skill | power | status | curse | quest, or null.</summary>
    public string? Type(string character, string id)
        => _pack.CardType(character, id) ?? (_local.TryGetValue(_pack.Canonical(id), out var info) ? info.Type : null);

    public string? Rarity(string character, string id)
        => _pack.CardRarity(character, id) ?? (_local.TryGetValue(_pack.Canonical(id), out var info) ? info.Rarity : null);

    /// <summary>Rarity label in the overlay language (the game's own terms), "?" when unknown.</summary>
    public static string RarityName(string? rarity) => rarity switch
    {
        "basic" => Loc.T("기본", "Basic"),
        "common" => Loc.T("일반", "Common"),
        "uncommon" => Loc.T("고급", "Uncommon"),
        "rare" => Loc.T("희귀", "Rare"),
        "ancient" => Loc.T("고대", "Ancient"),
        "special" => Loc.T("특수", "Special"),
        "curse" => Loc.T("저주", "Curse"),
        _ => "?",
    };


    private static string Prettify(string id)
    {
        var dot = id.IndexOf('.', StringComparison.Ordinal);
        var raw = dot >= 0 ? id[(dot + 1)..] : id;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Replace('_', ' ').ToLowerInvariant());
    }
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string? s) => string.IsNullOrEmpty(s) ? null : s;
}
