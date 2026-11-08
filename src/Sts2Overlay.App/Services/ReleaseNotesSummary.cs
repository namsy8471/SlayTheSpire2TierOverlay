using System.Text;
using System.Text.RegularExpressions;

namespace Sts2Overlay.App.Services;

/// <summary>
/// A one-line taste of a release's notes for the "다운로드 완료" tray balloon. The notes are the Markdown that the
/// release workflow passes to <c>vpk pack --releaseNotes</c> (the tag annotation), which Velopack hands back as
/// <c>VelopackAsset.NotesMarkdown</c>.
/// </summary>
internal static partial class ReleaseNotesSummary
{
    /// <summary>Windows cuts balloon text at 255 characters; the rest of the message needs ~80.</summary>
    public const int DefaultMaxLength = 120;

    /// <summary>
    /// The first <paramref name="maxItems"/> list items ("- …" or "* …"), cleaned of PR numbers and Markdown marks,
    /// joined with " · " and cut to <paramref name="maxLength"/>. Stops at a "---" rule (the English summary below it).
    /// Null when there is nothing to show.
    /// </summary>
    public static string? FromMarkdown(string? markdown, int maxItems = 2, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var items = new List<string>();
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("---", StringComparison.Ordinal)) break;
            if (!(line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))) continue;
            var text = Clean(line[2..]);
            if (text.Length == 0) continue;
            items.Add(text);
            if (items.Count == maxItems) break;
        }
        if (items.Count == 0) return null;
        var joined = string.Join(" · ", items);
        return joined.Length <= maxLength ? joined : joined[..(maxLength - 1)].TrimEnd() + "…";
    }

    private static string Clean(string text)
    {
        text = PrSuffix().Replace(text, "");          // "… (#17)" / "… (#13, #14)"
        text = Link().Replace(text, "$1");             // [text](url) → text
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '*' or '`' or '_') continue;      // bold, code, emphasis marks
            sb.Append(c);
        }
        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s*\((?:#\d+(?:,\s*)?)+\)\s*$")]
    private static partial Regex PrSuffix();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
