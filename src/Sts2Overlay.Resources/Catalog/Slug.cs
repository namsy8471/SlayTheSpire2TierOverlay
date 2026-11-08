using System.Text.RegularExpressions;

namespace Sts2Overlay.Resources.Catalog;

/// <summary>
/// Re-implementation of the game's ModelId naming (see decompiled StringHelper.Slugify and
/// ModelId.SlugifyCategory). The regexes are copied verbatim so IDs match the game exactly:
///   StrikeDefect      -> STRIKE_DEFECT
///   CardPoolModel     -> CARD_POOL   (category: "_MODEL" suffix removed)
/// </summary>
public static partial class Slug
{
    // "a letter/digit followed by an uppercase letter" -> insert '_' between them.
    // \G(?!^) also continues a run of capitals (ABCDef -> A_B_C_Def).
    [GeneratedRegex(@"([A-Za-z0-9]|\G(?!^))([A-Z])")]
    private static partial Regex CamelCase();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^A-Z0-9_]")]
    private static partial Regex SpecialChar();

    public static string Slugify(string text)
    {
        string snake = CamelCase().Replace(text.Trim(), "$1_$2");
        string upper = Whitespace().Replace(snake.ToUpperInvariant(), "_");
        return SpecialChar().Replace(upper, "");
    }

    public static string SlugifyCategory(string categoryTypeName)
    {
        string text = Slugify(categoryTypeName);
        return text.EndsWith("_MODEL") ? text[..^"_MODEL".Length] : text;
    }
}
