namespace Sts2Overlay.Core.Localization;

/// <summary>Languages the overlay can show. The game has more; every other game language shows English.</summary>
public enum Lang
{
    Kor,
    Eng,
}

/// <summary>
/// The overlay's display language and the one helper every user-facing string goes through.
/// <para>
/// Strings are written as Korean/English pairs right where they are used: <c>Loc.T("광역 부족", "Lacks AoE")</c>.
/// Keeping both texts side by side means a translation can never go missing or drift away from the code that uses it,
/// and the compiler checks that the same values are interpolated into both.
/// </para>
/// <para>
/// The language is process-wide (<see cref="SetLanguage"/>, set by the app from the game's settings or the user's choice)
/// with an optional per-async-flow override (<see cref="Use"/>) so tests can run Korean and English side by side in
/// parallel without touching the global value.
/// </para>
/// </summary>
public static class Loc
{
    private static volatile int s_global = (int)Lang.Kor;
    private static readonly AsyncLocal<Lang?> s_scope = new();

    /// <summary>The language strings are produced in right now (the scoped override if any, else the global one).</summary>
    public static Lang Current => s_scope.Value ?? (Lang)s_global;

    /// <summary>True when <see cref="Current"/> is English.</summary>
    public static bool IsEnglish => Current == Lang.Eng;

    /// <summary>Raised after the global language changed (on the caller's thread). View models re-render on it.</summary>
    public static event Action? Changed;

    /// <summary>Sets the process-wide language. No-op (and no <see cref="Changed"/>) when it is already that language.</summary>
    public static void SetLanguage(Lang lang)
    {
        if (Interlocked.Exchange(ref s_global, (int)lang) == (int)lang) return;
        Changed?.Invoke();
    }

    /// <summary>Overrides the language for the current async flow until the returned scope is disposed (tests, previews).</summary>
    public static IDisposable Use(Lang lang)
    {
        var previous = s_scope.Value;
        s_scope.Value = lang;
        return new Scope(previous);
    }

    /// <summary>The text for <see cref="Current"/>.</summary>
    public static string T(string kor, string eng) => Current == Lang.Eng ? eng : kor;

    /// <summary>The game's localization folder / name key for a language: <c>kor</c> or <c>eng</c>.</summary>
    public static string Code(Lang lang) => lang == Lang.Eng ? "eng" : "kor";

    /// <summary>Game localization code of <see cref="Current"/>.</summary>
    public static string CurrentCode => Code(Current);

    /// <summary>
    /// Maps the game's <c>settings.save</c> <c>language</c> value to an overlay language: <c>kor</c> → Korean, any other
    /// known game language → English, null/empty → null (unknown, keep the current choice).
    /// </summary>
    public static Lang? FromGameCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return code.Trim().Equals("kor", StringComparison.OrdinalIgnoreCase) ? Lang.Kor : Lang.Eng;
    }

    /// <summary>
    /// Picks the name for <see cref="Current"/> from a <c>{"kor": …, "eng": …}</c> map: the current language, then English,
    /// then Korean, then any value; null when the map has nothing.
    /// </summary>
    public static string? Pick(IReadOnlyDictionary<string, string>? names)
    {
        if (names is null || names.Count == 0) return null;
        if (names.TryGetValue(CurrentCode, out var v) && !string.IsNullOrEmpty(v)) return v;
        if (names.TryGetValue("eng", out v) && !string.IsNullOrEmpty(v)) return v;
        if (names.TryGetValue("kor", out v) && !string.IsNullOrEmpty(v)) return v;
        return names.Values.FirstOrDefault(s => !string.IsNullOrEmpty(s));
    }

    private sealed class Scope(Lang? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            s_scope.Value = previous;
        }
    }
}
