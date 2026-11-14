using System.IO;
using System.Text.Json;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Text;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Card effect text for hover tooltips: the game's own description templates
/// (<c>data/local/pck/localization/&lt;lang&gt;/cards.json</c> + <c>card_keywords.json</c>, gitignored, extracted from the pck)
/// filled with the names-free numbers of <c>data/catalog/card-vars.&lt;version&gt;.json</c>.
/// </summary>
/// <remarks>
/// Language-aware: every language in <see cref="Languages"/> is loaded and <see cref="For"/> takes the language per call, so a live
/// language switch only has to rebuild the view models. Without the extracted localization, <see cref="For"/> returns null and
/// tooltips show no effect text.
/// </remarks>
public sealed class CardTextProvider
{
    /// <summary>Language used until the overlay has a language setting.</summary>
    public const string DefaultLanguage = "kor";

    /// <summary>Game localization codes loaded at start.</summary>
    public static readonly string[] Languages = ["kor", "eng"];

    private readonly DataPack _pack;
    private readonly Dictionary<string, CardLocalization> _locs;
    private readonly Dictionary<string, CardVars> _vars;
    private readonly Dictionary<(string Lang, string Id), CardText?> _cache = new();

    private CardTextProvider(DataPack pack, Dictionary<string, CardLocalization> locs, Dictionary<string, CardVars> vars)
    {
        _pack = pack;
        _locs = locs;
        _vars = vars;
    }

    public static CardTextProvider Empty { get; } = new(new DataPack(), new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    /// <summary>Languages whose card text was found.</summary>
    public IReadOnlyCollection<string> LoadedLanguages => _locs.Keys;

    /// <summary>
    /// Reads the card-vars file for the loaded catalog version, which can differ from the tier pack version,
    /// and <paramref name="localizationDir"/>/&lt;lang&gt;/{cards,card_keywords}.json for each of <see cref="Languages"/>.
    /// Missing files are fine.
    /// </summary>
    public static CardTextProvider Load(DataPack pack, string packDir, string localizationDir)
    {
        var vars = new Dictionary<string, CardVars>(StringComparer.Ordinal);
        var catalogVersion = pack.Catalog?.GameVersion;
        var varsPath = CardVarsPath(packDir, catalogVersion);
        if (varsPath is not null && File.Exists(varsPath))
        {
            try
            {
                var file = CardVarsFile.Parse(File.ReadAllText(varsPath));
                if (catalogVersion is null || file.GameVersion is null || file.GameVersion == catalogVersion)
                {
                    foreach (var c in file.Cards) vars[c.Id] = c;
                }
                else AppLog.Warn($"card text: {varsPath} declares {file.GameVersion}, expected {catalogVersion}");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                AppLog.Warn($"card text: could not read {varsPath} ({ex.Message})");
            }
        }

        var locs = new Dictionary<string, CardLocalization>(StringComparer.Ordinal);
        foreach (var lang in Languages)
        {
            var cards = ReadTable(Path.Combine(localizationDir, lang, "cards.json"));
            if (cards.Count > 0) locs[lang] = new CardLocalization(lang, cards, ReadTable(Path.Combine(localizationDir, lang, "card_keywords.json")));
        }
        AppLog.Info($"card text: languages [{string.Join(", ", locs.Keys)}], numbers for {vars.Count} cards");
        return new CardTextProvider(pack, locs, vars);
    }

    /// <summary>The default location of the extracted localization (<see cref="AppPaths.LocalDataDir"/>).</summary>
    public static CardTextProvider ForLocalData(DataPack pack, string packDir)
        => Load(pack, packDir, Path.Combine(AppPaths.LocalDataDir, "pck", "localization"));

    /// <summary>Legacy catalogs with no version use the pack directory's name.</summary>
    internal static string? CardVarsPath(string packDir, string? catalogVersion = null)
        => AppPaths.CatalogTablePath(packDir, "card-vars", catalogVersion);

    /// <summary>
    /// Base and upgraded text of <paramref name="cardId"/> in <paramref name="lang"/> (a game code such as <c>kor</c>/<c>eng</c>;
    /// falls back to English, then Korean), or null when the card or the localization is unknown.
    /// </summary>
    public CardText? For(string cardId, string lang = DefaultLanguage)
    {
        var loc = Localization(lang);
        if (loc is null) return null;
        var id = _pack.Canonical(cardId);
        if (_cache.TryGetValue((loc.Lang, id), out var cached)) return cached;

        var text = Compose(id, loc, 0, null);
        _cache[(loc.Lang, id)] = text;
        return text;
    }

    public CardText? ForDeck(DeckCard copy, string lang)
        => Localization(lang) is { } loc ? Compose(_pack.Canonical(copy.Id), loc, copy.Upgrade, copy.SavedProperties) : null;

    private CardLocalization? Localization(string lang)
        => _locs.GetValueOrDefault(lang) ?? _locs.GetValueOrDefault("eng") ?? _locs.GetValueOrDefault("kor");

    private CardText? Compose(string id, CardLocalization loc, int level, IReadOnlyDictionary<string, decimal>? saved)
    {
        if (_pack.FindCatalog(id) is not { } card) return null;
        var facts = new CardTextFacts(card.Cost ?? -1, card.CostX, card.StarCost, card.Keywords, card.MaxUpgrade ?? 0)
        {
            StarCostX = card.StarCostX,
            GainsBlock = card.GainsBlock,
            Target = card.Target,
        };
        return CardTextComposer.Compose(id, facts, _vars.GetValueOrDefault(id), loc, level: level, savedProperties: saved);
    }

    private static Dictionary<string, string> ReadTable(string path)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return table;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) table[p.Name] = p.Value.GetString() ?? "";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn($"card text: could not read {path} ({ex.Message})");
        }
        return table;
    }
}
