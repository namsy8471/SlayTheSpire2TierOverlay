using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Text;

/// <summary>
/// Names-free description numbers of every card (<c>data/catalog/card-vars.&lt;version&gt;.json</c>, from
/// <c>tools/CatalogBuilder --card-vars</c>): base values of the template variables and what an upgrade adds.
/// </summary>
public sealed class CardVarsFile
{
    public string? GameVersion { get; set; }
    public string? Source { get; set; }
    public List<CardVars> Cards { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static CardVarsFile Parse(string json) => JsonSerializer.Deserialize<CardVarsFile>(json, Json) ?? new CardVarsFile();
}

public sealed class CardVars
{
    public string Id { get; set; } = "";
    public Dictionary<string, decimal> Vars { get; set; } = new(StringComparer.Ordinal);
    public CardUpgrade? Upgrade { get; set; }
    /// <summary>Variable name to the game's per-copy save property containing its current value.</summary>
    public Dictionary<string, string> SavedProperties { get; set; } = new(StringComparer.Ordinal);
    public List<string> Unresolved { get; set; } = [];
}

public sealed class CardUpgrade
{
    /// <summary>Added to <see cref="CardVars.Vars"/> by one upgrade.</summary>
    public Dictionary<string, decimal> Vars { get; set; } = new(StringComparer.Ordinal);

    public int CostDelta { get; set; }

    /// <summary>Change to a non-X star cost on the first upgrade.</summary>
    public int? StarCostDelta { get; set; }

    /// <summary>Cost after the upgrade when the game sets it outright instead of adding a delta.</summary>
    public int? SetCost { get; set; }

    [JsonPropertyName("addKeywords")]
    public List<string> AddKeywords { get; set; } = [];

    [JsonPropertyName("removeKeywords")]
    public List<string> RemoveKeywords { get; set; } = [];
}

/// <summary>Everything a card tooltip shows for one form (base or upgraded) of a card.</summary>
/// <param name="Title">Localized name (with "+" when upgraded).</param>
/// <param name="Cost">Energy cost; null for unplayable cards (cost -1), "X" for X-cost cards.</param>
/// <param name="CostChanged">The upgrade changed the cost.</param>
/// <param name="Body">Description runs, keyword lines included, as the game composes them.</param>
/// <param name="StarCost">Star cost (Regent), "X", or null when the card has none.</param>
public sealed record CardFace(string Title, string? Cost, bool CostChanged, IReadOnlyList<CardTextRun> Body, string? StarCost = null);

/// <summary>A card's text in one language: base face and, when the card can be upgraded, the upgraded face.</summary>
public sealed record CardText(CardFace Base, CardFace? Upgraded, bool IsDeckCopy = false);

/// <summary>
/// Card facts the composer needs that are not text: cost, keywords and whether the card upgrades.
/// Taken from the names-free card catalog.
/// </summary>
public sealed record CardTextFacts(int Cost, bool CostX, int StarCost, IReadOnlyList<string> Keywords, int MaxUpgrade)
{
    public bool StarCostX { get; init; }

    /// <summary>Catalog <c>gainsBlock</c> (the game's <c>GainsBlock</c> switch).</summary>
    public bool GainsBlock { get; init; }

    /// <summary>Catalog <c>target</c> (the game's <c>TargetType</c> string).</summary>
    public string? Target { get; init; }
}

/// <summary>
/// Localized game strings for one language (from <c>localization/&lt;lang&gt;/cards.json</c> and <c>card_keywords.json</c>).
/// </summary>
/// <param name="Lang">Language code such as <c>kor</c> or <c>eng</c>.</param>
/// <param name="Cards">Flat table: <c>ANGER.title</c>, <c>ANGER.description</c>, …</param>
/// <param name="Keywords">Flat table: <c>EXHAUST.title</c>, <c>PERIOD</c>, …</param>
public sealed record CardLocalization(string Lang, IReadOnlyDictionary<string, string> Cards, IReadOnlyDictionary<string, string> Keywords);

/// <summary>
/// Composes a card's tooltip text the way the game's <c>CardModel.GetDescriptionForPile</c> does outside combat:
/// keyword lines before the description (Unplayable, Innate, Retain, Sly, Ethereal), the rendered template, then
/// Exhaust and Eternal; each keyword line is <c>[gold]Title[/gold].</c>.
/// </summary>
public static class CardTextComposer
{
    /// <summary>Localization key for a card entry (without the CARD. prefix) at the given upgrade level.</summary>
    public static string TitleKey(string entry, int level)
        => entry == "DINGY_DAGGER" ? entry + (level < 3 ? ".title.dingy" : level < 6 ? ".title.fine" : ".title.pristine") : entry + ".title";

    // CardKeywordOrder.beforeDescription is inserted at index 0 one by one, so the shown order is reversed.
    private static readonly string[] BeforeDescription = ["UNPLAYABLE", "INNATE", "RETAIN", "SLY", "ETHEREAL"];
    private static readonly string[] AfterDescription = ["EXHAUST", "ETERNAL"];

    /// <summary>Builds a library card or a saved deck copy, followed by its next available upgrade.</summary>
    public static CardText? Compose(string cardId, CardTextFacts facts, CardVars? vars, CardLocalization loc,
        ICollection<string>? missing = null, int level = 0, IReadOnlyDictionary<string, decimal>? savedProperties = null)
    {
        string entry = cardId.StartsWith("CARD.", StringComparison.Ordinal) ? cardId[5..] : cardId;
        loc.Cards.TryGetValue(entry + ".description", out var template);
        var values = vars is null ? new Dictionary<string, decimal>(StringComparer.Ordinal) : new(vars.Vars, StringComparer.Ordinal);
        IReadOnlyCollection<string> unresolved = vars?.Unresolved ?? [];
        Dictionary<string, string>? strings = null;
        if (savedProperties is not null && vars is not null)
        {
            foreach (var (variable, property) in vars.SavedProperties)
            {
                if (savedProperties.TryGetValue(property, out var value)) values[variable] = value;
                else values.Remove(variable);
            }
            if (entry == "MAD_SCIENCE") strings = MadScienceProperties(savedProperties, values);
        }
        var currentValues = ValuesAtLevel(level);
        var current = AtLevel(level, currentValues, null);
        if (current is null) return null;
        var next = vars is not null && level < facts.MaxUpgrade ? AtLevel(level + 1, ValuesAtLevel(level + 1), currentValues) : null;
        return new CardText(current, next, savedProperties is not null);

        Dictionary<string, decimal> ValuesAtLevel(int upgradeLevel)
        {
            var result = new Dictionary<string, decimal>(values, StringComparer.Ordinal);
            if (upgradeLevel <= 0 || vars?.Upgrade is not { } change) return result;
            foreach (var (name, delta) in change.Vars)
            {
                if (savedProperties is not null && vars.SavedProperties.ContainsKey(name)) continue;
                if (!values.TryGetValue(name, out var initial)) continue;
                // Dingy Dagger gains one more damage on each successive upgrade than on the one before it.
                if (entry == "DINGY_DAGGER" && name == "Damage")
                    result[name] = initial + delta * upgradeLevel + (decimal)upgradeLevel * (upgradeLevel - 1) / 2;
                else if (upgradeLevel == 1) result[name] = initial + delta;
                else result.Remove(name); // later upgrade rules for an unknown multi-upgrade card are unavailable
            }
            return result;
        }

        CardFace? AtLevel(int upgradeLevel, IReadOnlyDictionary<string, decimal> levelValues, IReadOnlyDictionary<string, decimal>? previousValues)
        {
            if (!loc.Cards.TryGetValue(TitleKey(entry, upgradeLevel), out var title) || string.IsNullOrEmpty(title)) return null;
            var keywords = facts.Keywords.Select(Normalize).ToHashSet(StringComparer.Ordinal);
            var costs = facts;
            if (upgradeLevel > 0)
            {
                title += facts.MaxUpgrade > 1 ? "+" + upgradeLevel : "+";
                if (vars?.Upgrade is { } change)
                {
                    foreach (var k in change.AddKeywords) keywords.Add(Normalize(k));
                    foreach (var k in change.RemoveKeywords) keywords.Remove(Normalize(k));
                    costs = facts with
                    {
                        Cost = change.SetCost ?? (facts.Cost < 0 ? facts.Cost : Math.Max(0, facts.Cost + change.CostDelta)),
                        StarCost = facts.StarCost < 0 ? facts.StarCost : Math.Max(0, facts.StarCost + (change.StarCostDelta ?? 0)),
                    };
                }
            }
            return Face(title, template, costs, previousValues is not null && upgradeLevel == 1 && costs.Cost != facts.Cost,
                levelValues, previousValues, upgradeLevel > 0, keywords, loc, unresolved, strings, missing);
        }
    }

    private static Dictionary<string, string>? MadScienceProperties(IReadOnlyDictionary<string, decimal> saved, Dictionary<string, decimal> values)
    {
        var type = DataPack.MadScienceType(saved);
        if (type is null) return null;
        string[] riders = ["None", "Sapping", "Violence", "Choking", "Energized", "Wisdom", "Chaos", "Expertise", "Curious", "Improvement"];
        var rider = saved.GetValueOrDefault("TinkerTimeRider");
        if (rider == decimal.Truncate(rider) && rider >= 0 && rider < riders.Length)
        {
            values["HasRider"] = rider == 0 ? 0 : 1;
            for (int i = 0; i < riders.Length; i++) values[riders[i]] = i == rider ? 1 : 0;
        }
        return new(StringComparer.Ordinal) { ["CardType"] = type };
    }

    private static CardFace Face(string title, string? template, CardTextFacts facts, bool costChanged,
        IReadOnlyDictionary<string, decimal> vars, IReadOnlyDictionary<string, decimal>? baseVars, bool upgraded,
        HashSet<string> keywords, CardLocalization loc, IReadOnlyCollection<string> unresolved,
        IReadOnlyDictionary<string, string>? strings, ICollection<string>? missing)
    {
        var lines = new List<IReadOnlyList<CardTextRun>>();
        foreach (var k in BeforeDescription)
            if (keywords.Contains(k)) lines.Add(KeywordLine(k, loc));

        if (!string.IsNullOrEmpty(template))
        {
            var ctx = new CardTextContext(vars, baseVars, upgraded, loc.Lang)
            {
                Unresolved = unresolved,
                GainsBlock = facts.GainsBlock,
                TargetType = facts.Target,
            };
            if (strings is not null) ctx = ctx with { Strings = strings };
            var body = CardTextRenderer.Render(template, ctx, missing);
            if (body.Count > 0) lines.Add(body);
        }
        foreach (var k in AfterDescription)
            if (keywords.Contains(k)) lines.Add(KeywordLine(k, loc));

        var runs = new List<CardTextRun>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) runs.Add(new CardTextRun("\n", CardTextStyle.Plain));
            runs.AddRange(lines[i]);
        }

        string? costText = facts.CostX ? "X" : facts.Cost < 0 ? null : facts.Cost.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? starText = facts.StarCostX ? "X" : facts.StarCost < 0 ? null : facts.StarCost.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new CardFace(title, costText, costChanged, Merge(runs), starText);
    }

    private static IReadOnlyList<CardTextRun> KeywordLine(string keyword, CardLocalization loc)
    {
        string name = loc.Keywords.TryGetValue(keyword + ".title", out var t) && t.Length > 0 ? t : keyword;
        string period = loc.Keywords.TryGetValue("PERIOD", out var p) ? p : ".";
        return [new CardTextRun(name, CardTextStyle.Keyword), new CardTextRun(period, CardTextStyle.Plain)];
    }

    private static List<CardTextRun> Merge(List<CardTextRun> runs)
    {
        var merged = new List<CardTextRun>(runs.Count);
        foreach (var r in runs)
        {
            if (r.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].Style == r.Style && r.Style is not (CardTextStyle.Energy or CardTextStyle.Star))
                merged[^1] = merged[^1] with { Text = merged[^1].Text + r.Text };
            else merged.Add(r);
        }
        return merged;
    }

    /// <summary><c>Exhaust</c> / <c>CardKeyword.Exhaust</c> → <c>EXHAUST</c> (the loc key prefix).</summary>
    internal static string Normalize(string keyword)
    {
        int dot = keyword.LastIndexOf('.');
        string k = dot >= 0 ? keyword[(dot + 1)..] : keyword;
        var sb = new System.Text.StringBuilder(k.Length + 4);
        for (int i = 0; i < k.Length; i++)
        {
            char c = k[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(k[i - 1])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
