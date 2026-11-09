using System.Text.Json;

namespace Sts2Overlay.Core.Text;

/// <summary>
/// Names-free description numbers of every relic (<c>data/catalog/relic-vars.&lt;version&gt;.json</c>, from
/// <c>tools/CatalogBuilder --relic-vars</c>): base values of the template variables (<c>RelicModel.CanonicalVars</c>).
/// </summary>
public sealed class RelicVarsFile
{
    public string? GameVersion { get; set; }
    public string? Source { get; set; }
    public List<RelicVars> Relics { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static RelicVarsFile Parse(string json) => JsonSerializer.Deserialize<RelicVarsFile>(json, Json) ?? new RelicVarsFile();
}

public sealed class RelicVars
{
    public string Id { get; set; } = "";
    public Dictionary<string, decimal> Vars { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Variables only known in a run (they print <c>?</c> or take a switch's "otherwise" branch).</summary>
    public List<string> Unresolved { get; set; } = [];

    /// <summary>
    /// String variables whose text is another model's title, as a localization reference such as <c>ENCHANTMENT.GOOPY.title</c>
    /// (<c>enchantments.json</c> key <c>GOOPY.title</c>).
    /// </summary>
    public Dictionary<string, string>? Strings { get; set; }
}

/// <summary>A relic's hover text in one language.</summary>
/// <param name="Title">Localized name.</param>
/// <param name="Body">Description runs (gold terms, blue numbers, energy icons) as the game renders them out of combat.</param>
public sealed record RelicText(string Title, IReadOnlyList<CardTextRun> Body);

/// <summary>
/// Localized relic strings for one language: the flat <c>localization/&lt;lang&gt;/relics.json</c> table (<c>ANCHOR.title</c>, …) and
/// <c>enchantments.json</c> for the enchantment names some relic texts mention.
/// </summary>
public sealed record RelicLocalization(string Lang, IReadOnlyDictionary<string, string> Relics)
{
    public IReadOnlyDictionary<string, string> Enchantments { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Composes a relic's hover text: its title and the <c>description</c> template rendered with the same SmartFormat dialect
/// as cards (<see cref="CardTextRenderer"/>), filled with the relic's base numbers.
/// </summary>
public static class RelicTextComposer
{
    /// <summary><c>RELIC.ANCHOR</c> or <c>ANCHOR</c> → <c>ANCHOR</c> (the localization key prefix).</summary>
    public static string Entry(string relicId) => relicId.StartsWith("RELIC.", StringComparison.Ordinal) ? relicId[6..] : relicId;

    /// <summary>The relic's text, or null when the localization has no title for it. Collects template variables that had no value in <paramref name="missing"/>.</summary>
    public static RelicText? Compose(string relicId, RelicVars? vars, RelicLocalization loc, ICollection<string>? missing = null)
    {
        string entry = Entry(relicId);
        if (!loc.Relics.TryGetValue(entry + ".title", out var title) || string.IsNullOrWhiteSpace(title)) return null;
        title = StripTags(title);
        if (!loc.Relics.TryGetValue(entry + ".description", out var template) || string.IsNullOrEmpty(template))
            return new RelicText(title, []);

        var unresolved = new HashSet<string>(vars?.Unresolved ?? [], StringComparer.Ordinal);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, reference) in vars?.Strings ?? [])
        {
            if (ResolveReference(reference, loc) is { } s) strings[name] = s;
            else unresolved.Add(name);
        }
        var ctx = new CardTextContext(vars?.Vars ?? new Dictionary<string, decimal>(StringComparer.Ordinal), null, false, loc.Lang)
        {
            Unresolved = unresolved,
            Strings = strings,
        };
        return new RelicText(title, CardTextRenderer.Render(template, ctx, missing));
    }

    /// <summary><c>ENCHANTMENT.GOOPY.title</c> → the enchantments table's <c>GOOPY.title</c>; <c>RELIC.X.title</c> → the relic table; else null.</summary>
    private static string? ResolveReference(string reference, RelicLocalization loc)
    {
        int dot = reference.IndexOf('.');
        if (dot <= 0) return null;
        var table = reference[..dot] switch
        {
            "ENCHANTMENT" => loc.Enchantments,
            "RELIC" => loc.Relics,
            _ => null,
        };
        return table is not null && table.TryGetValue(reference[(dot + 1)..], out var v) && v.Length > 0 ? StripTags(v) : null;
    }

    /// <summary>Removes BBCode-like tags (<c>[gold]</c>) from a title.</summary>
    internal static string StripTags(string s)
    {
        if (s.IndexOf('[') < 0) return s.Trim();
        var sb = new System.Text.StringBuilder(s.Length);
        int depth = 0;
        foreach (var c in s)
        {
            if (c == '[') depth++;
            else if (c == ']' && depth > 0) depth--;
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
