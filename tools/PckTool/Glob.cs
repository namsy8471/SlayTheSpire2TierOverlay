using System.Text;
using System.Text.RegularExpressions;

namespace PckTool;

/// <summary>
/// Tiny glob matcher for pack paths (always '/'-separated, case-insensitive).
///   *   any characters except '/'      e.g. "localization/kor/*.json"
///   **  any characters including '/'   e.g. "images/**/card_*.png"
///   ?   exactly one character (not '/')
/// A pattern with no wildcard is treated as a plain substring filter ("portrait" matches any path containing it).
/// </summary>
public sealed class Glob
{
    private readonly Regex? _regex;
    private readonly string _pattern;

    public Glob(string pattern)
    {
        _pattern = pattern.Replace('\\', '/');
        if (HasWildcard(_pattern))
            _regex = new Regex(ToRegex(_pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public bool IsMatch(string path) =>
        _regex?.IsMatch(path) ?? path.Contains(_pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The literal folder part in front of the first wildcard, e.g. "localization/" for "localization/**/*.json".
    /// Extract commands strip it, so "localization/kor/cards.json" is written as "&lt;outDir&gt;/kor/cards.json".
    /// </summary>
    public string LiteralDirectoryPrefix
    {
        get
        {
            if (_regex is null) return ""; // substring filter: keep full paths
            int firstWildcard = _pattern.IndexOfAny(['*', '?']);
            int lastSlash = _pattern.LastIndexOf('/', firstWildcard);
            return lastSlash < 0 ? "" : _pattern[..(lastSlash + 1)];
        }
    }

    private static bool HasWildcard(string s) => s.IndexOfAny(['*', '?']) >= 0;

    private static string ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // "**/" also matches zero folders, so "a/**/b" matches "a/b".
                bool slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                sb.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.Append('$').ToString();
    }
}
