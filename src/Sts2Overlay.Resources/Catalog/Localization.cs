using System.Text.Json;

namespace Sts2Overlay.Resources.Catalog;

/// <summary>
/// Optional card text from the extracted game localization: &lt;locDir&gt;/&lt;lang&gt;/cards.json,
/// a flat JSON object like { "STRIKE_DEFECT.title": "Strike", "STRIKE_DEFECT.description": "..." }.
/// Missing files are fine: lookups simply return null.
/// </summary>
public sealed class Localization
{
    private readonly Dictionary<string, Dictionary<string, string>> _byLang = new();

    public IReadOnlyCollection<string> LoadedLanguages => _byLang.Keys;

    public static Localization Load(string? locDir, IEnumerable<string> languages)
    {
        var loc = new Localization();
        if (locDir == null) return loc;

        foreach (string lang in languages)
        {
            string path = Path.Combine(locDir, lang, "cards.json");
            if (!File.Exists(path)) continue;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            var table = new Dictionary<string, string>();
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    table[p.Name] = p.Value.GetString()!;
            loc._byLang[lang] = table;
        }
        return loc;
    }

    /// <param name="entry">ModelId entry, e.g. "STRIKE_DEFECT" (the part after "CARD.").</param>
    /// <param name="suffix">"title" or "description".</param>
    public string? Get(string lang, string entry, string suffix) =>
        _byLang.TryGetValue(lang, out var t) && t.TryGetValue($"{entry}.{suffix}", out var v) ? v : null;
}
