using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Overlay.Core.Data;

/// <summary>
/// English text for a pack's hand-written Korean strings: <c>&lt;pack&gt;/text.eng.json</c>.
/// <code>
/// {
///   "tips":       { "CARD.ARMAMENTS": "Upgraded, it upgrades your whole hand" },
///   "combos":     { "CARD.CORRUPTION+CARD.FEEL_NO_PAIN": { "name": "…", "why": "…" } },
///   "archetypes": { "ironclad.strength": { "desc": "…" } }
/// }
/// </code>
/// A separate file (instead of an <c>eng</c> field next to every Korean one) keeps the tier/archetype/combo files that
/// other work edits untouched, and lets one test check that every Korean string has its English twin
/// (<see cref="Missing"/>). Archetype names already carry both languages in <see cref="Archetype.Names"/>.
/// </summary>
public sealed class PackText
{
    public const string EnglishFile = "text.eng.json";

    /// <summary>Card id → English tip.</summary>
    public Dictionary<string, string> Tips { get; set; } = new(StringComparer.Ordinal);

    /// <summary><see cref="Combo.TextKey"/> → English name and reason.</summary>
    public Dictionary<string, ComboText> Combos { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Archetype id → English description.</summary>
    public Dictionary<string, ArchetypeText> Archetypes { get; set; } = new(StringComparer.Ordinal);

    public sealed class ComboText
    {
        public string? Name { get; set; }
        public string? Why { get; set; }
    }

    public sealed class ArchetypeText
    {
        public string? Desc { get; set; }
    }

    /// <summary>Parses <c>text.eng.json</c> (comments and trailing commas allowed).</summary>
    public static PackText Parse(string json)
        => JsonSerializer.Deserialize<PackText>(json, DataPackLoader.JsonOptions) ?? new PackText();

    /// <summary>Copies the English strings onto the loaded pack objects (<c>TipEng</c>, <c>NameEng</c>, …).</summary>
    public void ApplyTo(IEnumerable<TierPack> tiers, IEnumerable<Archetype> archetypes, IEnumerable<Combo> combos)
    {
        foreach (var pack in tiers)
        {
            foreach (var (id, entry) in pack.Cards)
            {
                if (Tips.TryGetValue(id, out var tip)) entry.TipEng = tip;
            }
        }
        foreach (var a in archetypes)
        {
            if (Archetypes.TryGetValue(a.Id, out var t)) a.DescEng = t.Desc;
        }
        foreach (var c in combos)
        {
            if (!Combos.TryGetValue(c.TextKey, out var t)) continue;
            c.NameEng = t.Name;
            c.WhyEng = t.Why;
        }
    }

    /// <summary>
    /// Every Korean pack string that has no English twin, as readable locations (e.g. <c>tip CARD.X</c>,
    /// <c>combo CARD.A+CARD.B why</c>, <c>archetype a.b names.eng</c>). Empty = fully translated.
    /// </summary>
    public static IReadOnlyList<string> Missing(DataPack pack)
    {
        var missing = new List<string>();
        foreach (var tier in pack.Tiers.Values)
        {
            foreach (var (id, e) in tier.Cards)
            {
                if (!string.IsNullOrWhiteSpace(e.Tip) && string.IsNullOrWhiteSpace(e.TipEng)) missing.Add($"tip {id}");
            }
        }
        foreach (var a in pack.Archetypes)
        {
            if (!string.IsNullOrWhiteSpace(a.Desc) && string.IsNullOrWhiteSpace(a.DescEng)) missing.Add($"archetype {a.Id} desc");
            if (!a.Names.TryGetValue("eng", out var n) || string.IsNullOrWhiteSpace(n)) missing.Add($"archetype {a.Id} names.eng");
        }
        foreach (var c in pack.Combos)
        {
            if (!string.IsNullOrWhiteSpace(c.Name) && string.IsNullOrWhiteSpace(c.NameEng)) missing.Add($"combo {c.TextKey} name");
            if (!string.IsNullOrWhiteSpace(c.Why) && string.IsNullOrWhiteSpace(c.WhyEng)) missing.Add($"combo {c.TextKey} why");
        }
        return missing;
    }

    [JsonIgnore]
    public bool IsEmpty => Tips.Count == 0 && Combos.Count == 0 && Archetypes.Count == 0;
}
