using System.Text.Json;
using System.Text.Json.Serialization;

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
    public List<string> Unresolved { get; set; } = [];
}

public sealed class CardUpgrade
{
    /// <summary>Added to <see cref="CardVars.Vars"/> by one upgrade.</summary>
    public Dictionary<string, decimal> Vars { get; set; } = new(StringComparer.Ordinal);

    public int CostDelta { get; set; }

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
public sealed record CardText(CardFace Base, CardFace? Upgraded);

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
    // CardKeywordOrder.beforeDescription is inserted at index 0 one by one, so the shown order is reversed.
    private static readonly string[] BeforeDescription = ["UNPLAYABLE", "INNATE", "RETAIN", "SLY", "ETHEREAL"];
    private static readonly string[] AfterDescription = ["EXHAUST", "ETERNAL"];

    /// <summary>Builds both faces; <paramref name="missing"/> collects template variables that had no value.</summary>
    public static CardText? Compose(string cardId, CardTextFacts facts, CardVars? vars, CardLocalization loc, ICollection<string>? missing = null)
    {
        string entry = cardId.StartsWith("CARD.", StringComparison.Ordinal) ? cardId[5..] : cardId;
        if (!loc.Cards.TryGetValue(entry + ".title", out var title) || string.IsNullOrEmpty(title)) return null;
        loc.Cards.TryGetValue(entry + ".description", out var template);

        var baseVars = vars?.Vars ?? new Dictionary<string, decimal>(StringComparer.Ordinal);
        var keywords = facts.Keywords.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        IReadOnlyCollection<string> unresolved = vars?.Unresolved ?? [];

        var baseFace = Face(title, template, facts, cost: facts.Cost, costChanged: false, baseVars, null, false, keywords, loc, unresolved, missing);
        if (facts.MaxUpgrade <= 0) return new CardText(baseFace, null);

        var up = vars?.Upgrade;
        var upVars = new Dictionary<string, decimal>(baseVars, StringComparer.Ordinal);
        if (up != null)
        {
            foreach (var (k, d) in up.Vars) upVars[k] = upVars.TryGetValue(k, out var b) ? b + d : d;
        }
        int upCost = up?.SetCost ?? facts.Cost + (up?.CostDelta ?? 0);
        if (facts.Cost >= 0 && upCost < 0) upCost = 0;
        var upKeywords = new HashSet<string>(keywords, StringComparer.Ordinal);
        if (up != null)
        {
            foreach (var k in up.AddKeywords) upKeywords.Add(Normalize(k));
            foreach (var k in up.RemoveKeywords) upKeywords.Remove(Normalize(k));
        }
        var upFace = Face(title + "+", template, facts, upCost, upCost != facts.Cost, upVars, baseVars, true, upKeywords, loc, unresolved, missing);
        return new CardText(baseFace, upFace);
    }

    private static CardFace Face(string title, string? template, CardTextFacts facts, int cost, bool costChanged,
        IReadOnlyDictionary<string, decimal> vars, IReadOnlyDictionary<string, decimal>? baseVars, bool upgraded,
        HashSet<string> keywords, CardLocalization loc, IReadOnlyCollection<string> unresolved, ICollection<string>? missing)
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

        string? costText = facts.CostX ? "X" : cost < 0 ? null : cost.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
