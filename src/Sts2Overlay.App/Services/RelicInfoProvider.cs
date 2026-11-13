using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Text;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Relic icon + effect text for the panel, like <see cref="CardArtProvider"/> and <see cref="CardTextProvider"/> do for cards.
/// <list type="bullet">
/// <item>Icons: the small relic icons cut from the installed game's relic atlas into <c>data/local/pck/relic_icons/&lt;id lower&gt;.png</c>
/// (gitignored, prepared on first start by LocalResourceService, also used by the shop relic reader). Nothing is shipped.</item>
/// <item>Text: the game's own <c>localization/&lt;lang&gt;/relics.json</c> templates (extracted, gitignored) filled with the names-free
/// numbers of <c>data/catalog/relic-vars.&lt;version&gt;.json</c>.</item>
/// </list>
/// Missing files simply give no icon / no text; <see cref="For"/> still returns the id's prettified name.
/// </summary>
public sealed class RelicInfoProvider
{
    /// <summary>Icon sizes in pixels: the game's icons are 85x85 (transparent border included).</summary>
    public const int ThumbSize = 48, LargeSize = 85;

    private readonly string _iconDir;
    private readonly Dictionary<string, RelicLocalization> _locs;
    private readonly Dictionary<string, RelicVars> _vars;
    private readonly ConcurrentDictionary<(string Entry, bool Large), ImageSource?> _icons = new();
    private readonly ConcurrentDictionary<(string Lang, string Entry), RelicText?> _texts = new();

    private RelicInfoProvider(string iconDir, Dictionary<string, RelicLocalization> locs, Dictionary<string, RelicVars> vars)
    {
        _iconDir = iconDir;
        _locs = locs;
        _vars = vars;
    }

    public static RelicInfoProvider Empty { get; } = new("", new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    /// <summary>Languages whose relic text was found.</summary>
    public IReadOnlyCollection<string> LoadedLanguages => _locs.Keys;

    /// <summary>The visible enchantment on a shop removal target.</summary>
    public string EnchantmentName(string id, string lang)
    {
        var entry = id.StartsWith("ENCHANTMENT.", StringComparison.Ordinal) ? id[12..] : id;
        var key = entry + ".title";
        return _locs.GetValueOrDefault(lang)?.Enchantments.GetValueOrDefault(key)
            ?? _locs.GetValueOrDefault("eng")?.Enchantments.GetValueOrDefault(key) ?? Prettify(entry);
    }

    /// <summary>
    /// Reads <c>relic-vars.&lt;version&gt;.json</c> next to the pack's catalog, the relic tables of
    /// <paramref name="localizationDir"/>/&lt;lang&gt;/relics.json and indexes nothing up front (icons are decoded on demand).
    /// </summary>
    public static RelicInfoProvider Load(string packDir, string localizationDir, string iconDir)
    {
        var vars = new Dictionary<string, RelicVars>(StringComparer.Ordinal);
        var varsPath = RelicVarsPath(packDir);
        if (varsPath is not null && File.Exists(varsPath))
        {
            try
            {
                foreach (var r in RelicVarsFile.Parse(File.ReadAllText(varsPath)).Relics) vars[RelicTextComposer.Entry(r.Id)] = r;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                AppLog.Warn($"relic text: could not read {varsPath} ({ex.Message})");
            }
        }

        var locs = new Dictionary<string, RelicLocalization>(StringComparer.Ordinal);
        foreach (var lang in CardTextProvider.Languages)
        {
            var table = ReadTable(Path.Combine(localizationDir, lang, "relics.json"));
            if (table.Count > 0)
                locs[lang] = new RelicLocalization(lang, table) { Enchantments = ReadTable(Path.Combine(localizationDir, lang, "enchantments.json")) };
        }
        AppLog.Info($"relic text: languages [{string.Join(", ", locs.Keys)}], numbers for {vars.Count} relics, icons {(Directory.Exists(iconDir) ? "found" : "missing")}");
        return new RelicInfoProvider(iconDir, locs, vars);
    }

    /// <summary>The default locations under <see cref="AppPaths.LocalDataDir"/>.</summary>
    public static RelicInfoProvider ForLocalData(string packDir)
    {
        var local = AppPaths.LocalDataDir;
        return Load(packDir, Path.Combine(local, "pck", "localization"), new Sts2Overlay.Resources.LocalLayout(local).RelicIconDir);
    }

    /// <summary><c>…/data/packs/v0.107.1</c> → <c>…/data/catalog/relic-vars.v0.107.1.json</c>.</summary>
    internal static string? RelicVarsPath(string packDir)
    {
        var catalog = DataPackLoader.SiblingCatalogPath(packDir);
        return catalog is null ? null : Path.Combine(Path.GetDirectoryName(catalog)!, "relic-vars." + Path.GetFileName(catalog)["cards.".Length..]);
    }

    /// <summary>
    /// Icon + text of <paramref name="relicId"/> (<c>RELIC.ANCHOR</c> or <c>ANCHOR</c>) in <paramref name="lang"/>
    /// (falls back to English, then Korean). Never null: without icon or text the view shows just the name.
    /// </summary>
    /// <remarks>The <c>name</c> argument is shown when the localization has no title (the caller's own fallback).</remarks>
    public RelicTipVm For(string relicId, string lang, string? name = null)
    {
        string entry = RelicTextComposer.Entry(relicId);
        var text = Text(entry, lang);
        return new RelicTipVm(this, entry, text?.Title ?? name ?? Prettify(entry), text?.Body ?? []);
    }

    /// <summary>The relic's text, or null when the localization doesn't know it.</summary>
    public RelicText? Text(string relicId, string lang)
    {
        var loc = _locs.GetValueOrDefault(lang) ?? _locs.GetValueOrDefault("eng") ?? _locs.GetValueOrDefault("kor");
        if (loc is null) return null;
        string entry = RelicTextComposer.Entry(relicId);
        return _texts.GetOrAdd((loc.Lang, entry), k => RelicTextComposer.Compose(k.Entry, _vars.GetValueOrDefault(k.Entry), loc));
    }

    internal ImageSource? Icon(string entry, bool large)
        => _icons.GetOrAdd((entry, large), key =>
        {
            if (_iconDir.Length == 0) return null;
            var file = Path.Combine(_iconDir, key.Entry.ToLowerInvariant() + ".png");
            if (!File.Exists(file)) return null;
            try
            {
                int size = key.Large ? LargeSize : ThumbSize;
                return CardArtProvider.Decode(file, size, size);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AppLog.Warn($"relic icon: could not read {file} ({ex.Message})");
                return null;
            }
        });

    /// <summary><c>LEAD_PAPERWEIGHT</c> → <c>Lead Paperweight</c>.</summary>
    internal static string Prettify(string entry)
        => string.Join(' ', entry.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

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
            AppLog.Warn($"relic text: could not read {path} ({ex.Message})");
        }
        return table;
    }
}

/// <summary>
/// One relic for a panel row: a small icon next to the name and, on hover, the large icon, title and effect text.
/// Icons are decoded when first drawn (the large one only when the tooltip opens).
/// </summary>
public sealed class RelicTipVm(RelicInfoProvider provider, string entry, string title, IReadOnlyList<CardTextRun> body)
{
    /// <summary>Localization key prefix, e.g. <c>ANCHOR</c>.</summary>
    public string Entry { get; } = entry;

    public string Title { get; } = title;

    /// <summary>Effect text; empty when the localization isn't extracted.</summary>
    public IReadOnlyList<CardTextRun> Body { get; } = body;

    public bool HasBody => Body.Count > 0;

    public ImageSource? Thumb => provider.Icon(Entry, large: false);

    public ImageSource? Large => provider.Icon(Entry, large: true);
}
