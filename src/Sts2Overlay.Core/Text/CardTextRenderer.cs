using System.Globalization;
using System.Text;

namespace Sts2Overlay.Core.Text;

/// <summary>How a piece of rendered card text should look.</summary>
public enum CardTextStyle
{
    Plain,

    /// <summary>The game's <c>[gold]</c> tag: keywords and game terms.</summary>
    Keyword,

    Blue,
    Purple,

    /// <summary>A number that the upgrade changed (the game draws it green).</summary>
    Changed,

    /// <summary>Energy icons; <see cref="CardTextRun.Text"/> is how many (or the number shown next to one icon).</summary>
    Energy,

    /// <summary>Star icons (Regent); same convention as <see cref="Energy"/>.</summary>
    Star,
}

/// <summary>One run of rendered text: <see cref="Text"/> may contain <c>\n</c>.</summary>
public readonly record struct CardTextRun(string Text, CardTextStyle Style);

/// <summary>Values a template is rendered with.</summary>
/// <param name="Vars">Variable values of the card as shown (already upgraded when <paramref name="Upgraded"/>).</param>
/// <param name="BaseVars">Values before the upgrade, so changed numbers can be marked; null = nothing is marked.</param>
/// <param name="Upgraded">Value of the game's <c>IfUpgraded</c> switch.</param>
/// <param name="Lang">Game language code (<c>kor</c>, <c>eng</c>, …): picks the plural rule.</param>
public sealed record CardTextContext(
    IReadOnlyDictionary<string, decimal> Vars,
    IReadOnlyDictionary<string, decimal>? BaseVars,
    bool Upgraded,
    string Lang)
{
    /// <summary>Out of combat the game's <c>InCombat</c> switch is false (the overlay never shows cards mid-fight).</summary>
    public bool InCombat { get; init; }

    /// <summary>
    /// Variables only known at run time (e.g. Mad Science's Tinker Time choice). They are not reported as missing: a switch
    /// on them takes its "otherwise" branch (what a card that was never set up shows) and a number prints <c>?</c>.
    /// </summary>
    public IReadOnlyCollection<string> Unresolved { get; init; } = [];

    /// <summary>The game's <c>GainsBlock</c> switch (<c>CardModel.GainsBlock</c>, from the catalog).</summary>
    public bool GainsBlock { get; init; }

    /// <summary>The game's <c>TargetType</c> string (<c>AnyEnemy</c>, <c>AllEnemies</c>, …), matched by <c>choose(AllEnemies)</c>.</summary>
    public string? TargetType { get; init; }

    /// <summary>
    /// String variables (the game's <c>StringVar</c>, e.g. a relic's enchantment name), already localized. A template reads them as
    /// <c>{Name}</c> or <c>{Name.StringValue:cond:…|…}</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Strings { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Renders the game's card description templates (a SmartFormat dialect) to styled runs, outside of combat.
/// Template syntax (made-up examples in the game's format; the real text stays in the extracted localization):
/// <c>Hit for {Damage:diff()}.</c>, <c>{IfUpgraded:show:every card|one card}</c>,
/// <c>{Cards:plural:card|cards}</c>, <c>{Buffer:choose(1):|{Buffer:diff()} times }</c>,
/// <c>{InCombat:(now {CalculatedDamage:diff()})|}</c>, <c>{Attacks:cond:&gt;1?…|…}</c> and BBCode-like <c>[gold]…[/gold]</c>.
/// </summary>
/// <remarks>
/// Pure and language-agnostic: the language only changes the plural rule. Unknown variables render as <c>?</c>
/// and are reported through <see cref="Render(string, CardTextContext, ICollection{string}?)"/>'s <c>missing</c> list.
/// </remarks>
public static class CardTextRenderer
{
    private static readonly HashSet<string> Formatters = new(StringComparer.Ordinal)
    {
        "diff", "inverseDiff", "energyIcons", "starIcons", "plural", "show", "choose", "cond",
    };

    public static IReadOnlyList<CardTextRun> Render(string template, CardTextContext ctx, ICollection<string>? missing = null)
    {
        var nodes = new Parser(template).ParseSequence(topLevel: true);
        var w = new Writer(ctx, missing);
        w.Write(nodes, current: null, currentName: null);
        return w.Finish();
    }

    /// <summary>The runs as plain text (icons as their count followed by a marker such as <c>[E]</c>).</summary>
    public static string ToPlainText(IEnumerable<CardTextRun> runs)
    {
        var sb = new StringBuilder();
        foreach (var r in runs)
        {
            sb.Append(r.Style switch
            {
                CardTextStyle.Energy => r.Text + "[E]",
                CardTextStyle.Star => r.Text + "[S]",
                _ => r.Text,
            });
        }
        return sb.ToString();
    }

    /// <summary>The game prints <c>(int)value</c>: fractions are truncated.</summary>
    internal static string FormatNumber(decimal v) => decimal.Truncate(v).ToString(CultureInfo.InvariantCulture);

    // ---- syntax tree -------------------------------------------------------------------------------------------

    private abstract record Node;

    private sealed record TextNode(string Text) : Node;

    private sealed record TagNode(string Name, bool Closing) : Node;

    private sealed record PlaceholderNode(string Selector, string? Formatter, string? Args, List<List<Node>>? Options) : Node;

    private sealed class Parser(string s)
    {
        private int _i;

        /// <summary>Parses until the end, or (inside a placeholder) until an unmatched <c>|</c> or <c>}</c>.</summary>
        public List<Node> ParseSequence(bool topLevel)
        {
            var nodes = new List<Node>();
            var text = new StringBuilder();
            void Flush()
            {
                if (text.Length > 0) nodes.Add(new TextNode(text.ToString()));
                text.Clear();
            }

            while (_i < s.Length)
            {
                char c = s[_i];
                if (!topLevel && (c == '|' || c == '}')) break;
                if (c == '{')
                {
                    Flush();
                    nodes.Add(ParsePlaceholder());
                    continue;
                }
                if (c == '[' && TryParseTag(out var tag))
                {
                    Flush();
                    nodes.Add(tag);
                    continue;
                }
                text.Append(c);
                _i++;
            }
            Flush();
            return nodes;
        }

        private bool TryParseTag(out TagNode tag)
        {
            tag = null!;
            int end = s.IndexOf(']', _i);
            if (end < 0) return false;
            string body = s[(_i + 1)..end];
            bool closing = body.StartsWith('/');
            string name = closing ? body[1..] : body;
            int eq = name.IndexOf('='); // [color=#fff]
            if (eq >= 0) name = name[..eq];
            if (name.Length == 0 || !name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')) return false;
            tag = new TagNode(name.ToLowerInvariant(), closing);
            _i = end + 1;
            return true;
        }

        private PlaceholderNode ParsePlaceholder()
        {
            _i++; // '{'
            int start = _i;
            while (_i < s.Length && s[_i] != ':' && s[_i] != '}') _i++;
            string selector = s[start.._i].Trim();
            if (_i >= s.Length || s[_i] == '}')
            {
                _i = Math.Min(_i + 1, s.Length);
                return new PlaceholderNode(selector, null, null, null);
            }

            _i++; // ':'
            string? formatter = null, args = null;
            int save = _i;
            int j = _i;
            while (j < s.Length && char.IsAsciiLetter(s[j])) j++;
            string name = s[_i..j];
            if (name.Length > 0 && Formatters.Contains(name))
            {
                string? a = null;
                int k = j;
                if (k < s.Length && s[k] == '(')
                {
                    int close = s.IndexOf(')', k);
                    if (close > 0)
                    {
                        a = s[(k + 1)..close];
                        k = close + 1;
                    }
                }
                if (k < s.Length && (s[k] == ':' || s[k] == '}'))
                {
                    formatter = name;
                    args = a;
                    _i = k;
                    if (s[_i] == '}')
                    {
                        _i++;
                        return new PlaceholderNode(selector, formatter, args, null);
                    }
                    _i++; // ':'
                }
                else _i = save;
            }

            var options = new List<List<Node>>();
            while (true)
            {
                options.Add(ParseSequence(topLevel: false));
                if (_i >= s.Length) break;
                if (s[_i] == '|')
                {
                    _i++;
                    continue;
                }
                _i++; // '}'
                break;
            }
            return new PlaceholderNode(selector, formatter, args, options);
        }
    }

    // ---- evaluation --------------------------------------------------------------------------------------------

    private sealed class Writer(CardTextContext ctx, ICollection<string>? missing)
    {
        private readonly List<CardTextRun> _runs = [];
        private readonly List<string> _tags = [];

        private int _forceChanged;

        private CardTextStyle CurrentStyle
        {
            get
            {
                if (_forceChanged > 0) return CardTextStyle.Changed;
                for (int i = _tags.Count - 1; i >= 0; i--)
                {
                    switch (_tags[i])
                    {
                        case "gold": return CardTextStyle.Keyword;
                        case "blue": return CardTextStyle.Blue;
                        case "purple": return CardTextStyle.Purple;
                    }
                }
                return CardTextStyle.Plain;
            }
        }

        public IReadOnlyList<CardTextRun> Finish()
        {
            // Merge neighbours of the same style (not icons: each icon run is one group).
            var merged = new List<CardTextRun>(_runs.Count);
            foreach (var r in _runs)
            {
                if (r.Text.Length == 0) continue;
                if (merged.Count > 0 && merged[^1].Style == r.Style && r.Style is not (CardTextStyle.Energy or CardTextStyle.Star))
                    merged[^1] = merged[^1] with { Text = merged[^1].Text + r.Text };
                else merged.Add(r);
            }
            return merged;
        }

        private void Emit(string text, CardTextStyle style) => _runs.Add(new CardTextRun(text, style));

        public void Write(List<Node> nodes, decimal? current, string? currentName)
        {
            foreach (var n in nodes)
            {
                switch (n)
                {
                    case TextNode t:
                        Emit(t.Text, CurrentStyle);
                        break;
                    case TagNode tag when tag.Closing:
                        int idx = _tags.LastIndexOf(tag.Name);
                        if (idx >= 0) _tags.RemoveAt(idx);
                        break;
                    case TagNode tag:
                        _tags.Add(tag.Name);
                        break;
                    case PlaceholderNode p:
                        WritePlaceholder(p, current, currentName);
                        break;
                }
            }
        }

        private decimal? Lookup(string selector, decimal? current, string? currentName, out string? name)
        {
            name = selector;
            if (selector.Length == 0)
            {
                name = currentName;
                return current;
            }
            // Context switches CardModel.GetDescriptionForPile adds to every card; the overlay shows cards out of combat,
            // not in hand and not aimed (OnTable / IsTargeting / IsOstyAlive false).
            switch (selector)
            {
                case "IfUpgraded": return ctx.Upgraded ? 1 : 0;
                case "InCombat": return ctx.InCombat ? 1 : 0;
                case "GainsBlock": return ctx.GainsBlock ? 1 : 0;
                case "OnTable" or "IsTargeting" or "IsOstyAlive": return 0;
                case "energyPrefix" or "singleStarIcon" or "TargetType": return null; // handled by WritePlaceholder
            }
            if (ctx.Vars.TryGetValue(selector, out var v)) return v;
            return null;
        }

        /// <summary>
        /// The game's upgrade preview prints a just-upgraded value green (diff and inverseDiff alike); unchanged values keep
        /// the surrounding style.
        /// </summary>
        private CardTextStyle NumberStyle(string? name, decimal value)
        {
            if (name == null || ctx.BaseVars == null || !ctx.BaseVars.TryGetValue(name, out var b) || b == value)
                return CurrentStyle;
            return CardTextStyle.Changed;
        }

        private void WritePlaceholder(PlaceholderNode p, decimal? current, string? currentName)
        {
            if (p.Selector == "singleStarIcon")
            {
                Emit("1", CardTextStyle.Star); // one star icon image
                return;
            }
            if (p.Selector == "TargetType")
            {
                WriteString(p, ctx.TargetType ?? "");
                return;
            }

            // StringVar: "{Name}" or "{Name.StringValue:cond:…}" (".StringValue" is the property SmartFormat reads).
            string varName = p.Selector.EndsWith(".StringValue", StringComparison.Ordinal) ? p.Selector[..^".StringValue".Length] : p.Selector;
            if (ctx.Strings.TryGetValue(varName, out var str))
            {
                WriteString(p, str);
                return;
            }

            decimal? value = Lookup(p.Selector, current, currentName, out var name);
            bool known = value.HasValue || p.Selector == "energyPrefix";
            if (!known)
            {
                bool runtimeOnly = ctx.Unresolved.Contains(p.Selector) || ctx.Unresolved.Contains(varName);
                if (!runtimeOnly) missing?.Add(p.Selector);
                if (runtimeOnly && p.Options is { Count: > 0 } opts)
                {
                    // No value: choose/plural fall to the last branch, a switch to its "false" branch (or nothing).
                    WriteOption(p, p.Formatter is "choose" or "plural" ? opts.Count - 1 : 1, null, name);
                    return;
                }
                Emit("?", CurrentStyle);
                return;
            }

            switch (p.Formatter)
            {
                case null when p.Options == null:
                case "diff":
                case "inverseDiff":
                    Emit(FormatNumber(value!.Value), NumberStyle(name, value.Value));
                    return;

                case "energyIcons":
                case "starIcons":
                {
                    decimal count = decimal.TryParse(p.Args, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : value ?? 1;
                    Emit(FormatNumber(count), p.Formatter == "energyIcons" ? CardTextStyle.Energy : CardTextStyle.Star);
                    return;
                }

                case "plural":
                    WriteOption(p, PluralIndex(value!.Value, p.Options!.Count), value, name);
                    return;

                case "choose":
                {
                    var choices = (p.Args ?? "").Split('|');
                    int pick = p.Options!.Count - 1;
                    for (int i = 0; i < choices.Length; i++)
                    {
                        if (decimal.TryParse(choices[i], NumberStyles.Number, CultureInfo.InvariantCulture, out var c) && c == value)
                        {
                            pick = i;
                            break;
                        }
                    }
                    WriteOption(p, pick, value, name);
                    return;
                }

                default: // "show", "cond", or SmartFormat's implicit conditional: first option when true / non-zero
                {
                    if (TryWriteComparison(p, value!.Value, name)) return;
                    bool on = value.GetValueOrDefault() != 0;
                    // The upgrade preview prints the upgraded branch of {IfUpgraded:show:...} green.
                    bool mark = on && p.Selector == "IfUpgraded" && ctx.BaseVars != null;
                    if (mark) _forceChanged++;
                    WriteOption(p, on ? 0 : 1, value, name);
                    if (mark) _forceChanged--;
                    return;
                }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex Comparison =
            new(@"^(>=|<=|!=|>|<|=)(-?\d+(?:\.\d+)?)\?", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// SmartFormat's comparison form of the conditional formatter, <c>{Attacks:cond:&gt;1?… {Attacks:diff()} …|…}</c>:
        /// the first branch whose test holds, else the first branch without a test. False when no branch has a test.
        /// </summary>
        private bool TryWriteComparison(PlaceholderNode p, decimal value, string? name)
        {
            if (p.Options is not { } options) return false;
            bool any = false;
            int fallback = -1;
            for (int i = 0; i < options.Count; i++)
            {
                var m = options[i] is [TextNode first, ..] ? Comparison.Match(first.Text) : System.Text.RegularExpressions.Match.Empty;
                if (!m.Success)
                {
                    if (fallback < 0) fallback = i;
                    continue;
                }
                any = true;
                decimal x = decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                bool hit = m.Groups[1].Value switch
                {
                    ">=" => value >= x,
                    "<=" => value <= x,
                    "!=" => value != x,
                    ">" => value > x,
                    "<" => value < x,
                    _ => value == x,
                };
                if (!hit) continue;
                var rest = new List<Node>(options[i]);
                rest[0] = new TextNode(((TextNode)rest[0]).Text[m.Length..]);
                Write(rest, value, name);
                return true;
            }
            if (any && fallback >= 0) Write(options[fallback], value, name);
            return any;
        }

        /// <summary>A string variable: <c>choose(a|b)</c> matches it by name; without options it prints itself.</summary>
        private void WriteString(PlaceholderNode p, string value)
        {
            if (p.Options is not { Count: > 0 } options)
            {
                Emit(value, CurrentStyle);
                return;
            }
            int pick = p.Formatter == "choose" ? options.Count - 1 : value.Length > 0 ? 0 : 1;
            if (p.Formatter == "choose")
            {
                var choices = (p.Args ?? "").Split('|');
                int i = Array.IndexOf(choices, value);
                if (i >= 0) pick = i;
            }
            WriteOption(p, pick, null, null);
        }

        /// <summary>Writes branch <paramref name="index"/>; a branch that doesn't exist (a one-option switch that is off) prints nothing.</summary>
        private void WriteOption(PlaceholderNode p, int index, decimal? value, string? name)
        {
            var options = p.Options!;
            if (index < 0 || index >= options.Count) return;
            Write(options[index], value, name);
        }

        /// <summary>
        /// SmartFormat's plural rule for the language: Korean (and the other East Asian languages) have one form, so the
        /// first branch always; otherwise one / other.
        /// </summary>
        private int PluralIndex(decimal value, int forms)
        {
            if (forms <= 1 || ctx.Lang is "kor" or "jpn" or "zhs" or "zht") return 0;
            return value == 1 ? 0 : 1;
        }
    }
}
