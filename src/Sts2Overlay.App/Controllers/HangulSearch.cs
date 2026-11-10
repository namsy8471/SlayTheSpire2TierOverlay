namespace Sts2Overlay.App.Controllers;

/// <summary>
/// Card-name search of the 티어표 tab (pure, unit-tested): a substring of the name, where a Korean initial consonant typed on
/// its own (초성, e.g. "ㅂ") stands for any syllable that starts with it, so "ㅂㅇ" finds "방어". Initials of the words also
/// match, so "ㅂㅂ" finds "방어 본능". Spaces and letter case are ignored.
/// </summary>
internal static class HangulSearch
{
    /// <summary>Compatibility jamo of the 19 initial consonants, in the order of the Hangul syllable block.</summary>
    private const string Initials = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";

    private const int SyllableFirst = 0xAC00, SyllableLast = 0xD7A3, SyllablesPerInitial = 21 * 28;

    /// <summary>
    /// The initial consonant of a Hangul syllable as a compatibility jamo ("방" → 'ㅂ'); a lone initial jamo (either form) maps
    /// to its compatibility jamo; any other character → null.
    /// </summary>
    public static char? Choseong(char c)
    {
        if (c is >= (char)SyllableFirst and <= (char)SyllableLast) return Initials[(c - SyllableFirst) / SyllablesPerInitial];
        if (Initials.Contains(c, StringComparison.Ordinal)) return c;
        // Hangul Jamo block initials (U+1100..U+1112), as some IMEs send while composing.
        if (c is >= 'ᄀ' and <= 'ᄒ') return Initials[c - 0x1100];
        return null;
    }

    /// <summary>True when <paramref name="query"/> (trimmed, spaces ignored) finds <paramref name="name"/>; an empty query finds every name.</summary>
    public static bool Matches(string name, string? query)
    {
        var q = Squash(query);
        if (q.Length == 0) return true;
        if (Contains(Squash(name), q)) return true;
        // Word initials: "ㅂㅂ" → "방어 본능", "sio" → "Shrug It Off".
        var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToLowerInvariant(w[0])));
        return Contains(initials, q);
    }

    private static string Squash(string? s)
        => s is null ? "" : string.Concat(s.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant));

    private static bool Contains(string text, string q)
    {
        for (var start = 0; start + q.Length <= text.Length; start++)
        {
            var i = 0;
            while (i < q.Length && CharMatches(q[i], text[start + i])) i++;
            if (i == q.Length) return true;
        }
        return false;
    }

    /// <summary>Same character, or a lone initial consonant against a syllable (or jamo) that starts with it.</summary>
    private static bool CharMatches(char q, char c)
    {
        if (q == c) return true;
        if (!IsLoneInitial(q)) return false;
        return Choseong(c) == Choseong(q);
    }

    private static bool IsLoneInitial(char c) => c is not (>= (char)SyllableFirst and <= (char)SyllableLast) && Choseong(c) is not null;
}
