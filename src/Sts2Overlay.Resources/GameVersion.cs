using System.Globalization;

namespace Sts2Overlay.Resources;

/// <summary>
/// A game version like <c>v0.107.1</c> (the format of <c>release_info.json</c> → <c>"version"</c>).
/// <list type="bullet">
/// <item>The leading <c>v</c> is optional and missing trailing parts count as 0, so <c>v0.108</c> == <c>0.108.0</c>.</item>
/// <item>Parts are compared as NUMBERS, not text: <c>v0.99</c> &lt; <c>v0.107</c> (as strings "0.99" &gt; "0.107"!).</item>
/// <item>A pre-release suffix sorts before the release (<c>v0.108-beta</c> &lt; <c>v0.108</c>), like SemVer.</item>
/// </list>
/// </summary>
public sealed class GameVersion : IComparable<GameVersion>, IEquatable<GameVersion>
{
    private readonly int[] _parts;

    private GameVersion(int[] parts, string? preRelease, string text)
    {
        _parts = parts;
        PreRelease = preRelease;
        Text = text;
    }

    /// <summary>Numeric parts with trailing zeros removed (so equal versions have equal arrays).</summary>
    public IReadOnlyList<int> Parts => _parts;

    /// <summary>Text after '-', e.g. "beta" for <c>v0.108.0-beta</c>; null for a release.</summary>
    public string? PreRelease { get; }

    /// <summary>Display form, always with a leading "v" (e.g. <c>v0.108</c>), as the user wrote it otherwise.</summary>
    public string Text { get; }

    public static GameVersion Parse(string text)
        => TryParse(text, out var v) ? v : throw new FormatException($"Not a game version: '{text}'");

    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GameVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];

        // "0.107.1+build5" → build metadata is ignored; "0.108-beta" → pre-release "beta".
        int plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        int dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0) return false;
        }

        var pieces = s.Split('.');
        if (pieces.Length is < 1 or > 4) return false;
        var parts = new int[pieces.Length];
        for (int i = 0; i < pieces.Length; i++)
        {
            if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return false;
        }

        int len = parts.Length;
        while (len > 1 && parts[len - 1] == 0) len--;
        version = new GameVersion(parts[..len], pre, "v" + s + (pre is null ? "" : "-" + pre));
        return true;
    }

    public int CompareTo(GameVersion? other)
    {
        if (other is null) return 1;
        int n = Math.Max(_parts.Length, other._parts.Length);
        for (int i = 0; i < n; i++)
        {
            int a = i < _parts.Length ? _parts[i] : 0;
            int b = i < other._parts.Length ? other._parts[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        // Same numbers: a release is newer than any pre-release of it.
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public bool Equals(GameVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is GameVersion v && Equals(v);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var p in _parts) h.Add(p);
        h.Add(PreRelease, StringComparer.Ordinal);
        return h.ToHashCode();
    }

    public override string ToString() => Text;

    public static bool operator ==(GameVersion? a, GameVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(GameVersion? a, GameVersion? b) => !(a == b);
    public static bool operator <(GameVersion a, GameVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(GameVersion a, GameVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(GameVersion a, GameVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(GameVersion a, GameVersion b) => a.CompareTo(b) >= 0;
}
