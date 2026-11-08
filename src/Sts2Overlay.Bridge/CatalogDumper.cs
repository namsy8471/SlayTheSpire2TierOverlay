using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Overlay.Bridge;

/// <summary>
/// Dumps every card in ModelDb to %LOCALAPPDATA%\Sts2Overlay\catalog.&lt;version&gt;.json.
///
/// Why a Postfix on ExecuteDeferred: mods are loaded in ExecuteVeryEarly, BEFORE ModelDb.Init()
/// runs in ExecuteEssential. ExecuteDeferred runs after the main menu is shown, when ModelDb,
/// localization and card pools are all ready.
///
/// The output contains game text, so it stays on the user's PC (gitignored) and is never committed.
/// </summary>
[HarmonyPatch(typeof(OneTimeInitialization), nameof(OneTimeInitialization.ExecuteDeferred))]
internal static class CatalogDumper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Keep Korean readable in the file instead of \uXXXX escapes.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static void Postfix()
    {
        try
        {
            Dump(force: false);
        }
        catch (Exception ex)
        {
            BridgeLog.Error("Catalog dump failed", ex);
        }
    }

    public static void Dump(bool force)
    {
        ReleaseInfo? info = ReleaseInfoManager.Instance.ReleaseInfo;
        string version = info?.Version ?? "unknown";
        string path = Path.Combine(BridgeLog.Dir, $"catalog.{version}.json");
        if (!force && File.Exists(path))
        {
            BridgeLog.Info($"Catalog already exists for {version}, skipping dump");
            return;
        }

        // Raw loc tables (no UI side effects, unlike LocManager.SetLanguage which refreshes the whole UI).
        Dictionary<string, string> kor = LoadLocTable("kor", "cards");
        Dictionary<string, string> eng = LoadLocTable("eng", "cards");

        var poolOwner = new Dictionary<string, string>();
        foreach (CharacterModel ch in ModelDb.AllCharacters)
        {
            poolOwner[ch.CardPool.Id.ToString()] = ch.Id.ToString();
        }

        var cards = new List<object>();
        int failed = 0;
        foreach (CardModel c in ModelDb.AllCards)
        {
            try
            {
                string entry = c.Id.Entry;
                string poolId = SafeGet(() => c.Pool.Id.ToString()) ?? "";
                cards.Add(new
                {
                    id = c.Id.ToString(),
                    pool = poolId,
                    character = poolOwner.GetValueOrDefault(poolId),
                    colorless = SafeGet(() => c.Pool.IsColorless),
                    names = new
                    {
                        kor = kor.GetValueOrDefault(entry + ".title"),
                        eng = eng.GetValueOrDefault(entry + ".title"),
                    },
                    type = c.Type.ToString(),
                    rarity = c.Rarity.ToString(),
                    cost = c.EnergyCost.Canonical,
                    costX = c.EnergyCost.CostsX,
                    starCost = c.CanonicalStarCost,
                    keywords = c.CanonicalKeywords.Select(k => k.ToString()).ToArray(),
                    tags = c.Tags.Select(t => t.ToString()).ToArray(),
                    target = c.TargetType.ToString(),
                    gainsBlock = c.GainsBlock,
                    multiplayer = c.MultiplayerConstraint.ToString(),
                    maxUpgrade = c.MaxUpgradeLevel,
                    inLibrary = c.ShouldShowInCardLibrary,
                    // Description in the CURRENT game language, fully formatted (numbers filled in).
                    description = SafeGet(() => c.GetDescriptionForPile(PileType.None).StripBbCode()),
                    // Raw templates for both languages, useful for keyword-based auto tagging.
                    descriptionTemplate = new
                    {
                        kor = kor.GetValueOrDefault(entry + ".description"),
                        eng = eng.GetValueOrDefault(entry + ".description"),
                    },
                });
            }
            catch (Exception ex)
            {
                failed++;
                BridgeLog.Error($"Catalog: failed on {c.Id}", ex);
            }
        }

        var doc = new
        {
            gameVersion = version,
            commit = info?.Commit,
            dumpedAt = DateTime.UtcNow.ToString("o"),
            cardCount = cards.Count,
            cards,
        };

        Directory.CreateDirectory(BridgeLog.Dir);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions));
        BridgeLog.Info($"Catalog dumped: {cards.Count} cards ({failed} failed) -> {path}");
    }

    private static Dictionary<string, string> LoadLocTable(string lang, string table)
    {
        string res = $"res://localization/{lang}/{table}.json";
        try
        {
            using Godot.FileAccess? f = Godot.FileAccess.Open(res, Godot.FileAccess.ModeFlags.Read);
            if (f is null)
            {
                BridgeLog.Error($"Cannot open {res}: {Godot.FileAccess.GetOpenError()}");
                return new Dictionary<string, string>();
            }
            return JsonSerializer.Deserialize<Dictionary<string, string>>(f.GetAsText())
                   ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            BridgeLog.Error($"Cannot parse {res}", ex);
            return new Dictionary<string, string>();
        }
    }

    private static T? SafeGet<T>(Func<T> f)
    {
        try { return f(); } catch { return default; }
    }
}
