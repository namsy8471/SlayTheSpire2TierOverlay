using System.Text.Json;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.Resources;

/// <summary>The few catalog fields the version guard needs.</summary>
public sealed record GuardCard(string Id, string? Pool, string? Rarity);

/// <summary>Outcome of <see cref="VersionGuard.Evaluate"/>.</summary>
/// <param name="Comparison">&lt;0 game older than the pack, 0 same, &gt;0 game newer; null when either version is unknown.</param>
/// <param name="UnratedCards">Offerable cards of the installed game with no tier entry (they get the default tier).</param>
/// <param name="RemovedCards">Tier entries for cards the installed game no longer has (ignored by the engine).</param>
public sealed record VersionGuardResult(
    GameVersion? Game,
    GameVersion? Pack,
    int? Comparison,
    IReadOnlyList<string> UnratedCards,
    IReadOnlyList<string> RemovedCards)
{
    public bool VersionMismatch => Comparison is not null and not 0;

    /// <summary>
    /// Panel lines in the current overlay language; empty when everything matches.
    /// e.g. "게임 버전 v0.108이 데이터 팩 v0.107.1보다 새롭습니다: 티어가 맞지 않을 수 있음" /
    /// "Game version v0.108 is newer than data pack v0.107.1: tiers may be off"
    /// </summary>
    public IReadOnlyList<string> PanelLines(string defaultTier, int maxNames = 6)
    {
        if (!VersionMismatch) return [];
        var lines = new List<string>
        {
            Loc.T(
                $"게임 버전 {Subject(Game!.Text)} 데이터 팩 {Pack!.Text}보다 {(Comparison > 0 ? "새롭습니다" : "오래되었습니다")}: 티어가 맞지 않을 수 있음",
                $"Game version {Game!.Text} is {(Comparison > 0 ? "newer" : "older")} than data pack {Pack!.Text}: tiers may be off"),
        };
        if (UnratedCards.Count > 0)
        {
            var names = string.Join(", ", UnratedCards.Take(maxNames).Select(Short));
            var rest = UnratedCards.Count - maxNames;
            var more = rest > 0 ? Loc.T($" 외 {rest}장", $" and {rest} more") : "";
            lines.Add(Loc.T(
                $"티어 없는 카드 {UnratedCards.Count}장 (기본 티어 {defaultTier}로 계산): {names}{more}",
                $"{Cards(UnratedCards.Count)} without a tier (scored as {defaultTier}): {names}{more}"));
        }
        if (RemovedCards.Count > 0)
        {
            lines.Add(Loc.T(
                $"게임에서 빠진 카드 {RemovedCards.Count}장의 티어는 무시합니다",
                $"Ignoring tiers of {Cards(RemovedCards.Count)} no longer in the game"));
        }
        return lines;
    }

    /// <summary>English count: "1 card", "2 cards".</summary>
    private static string Cards(int n) => n == 1 ? "1 card" : $"{n} cards";

    private static string Short(string id) => id.StartsWith("CARD.", StringComparison.Ordinal) ? id[5..] : id;

    /// <summary>
    /// Adds the Korean subject particle 이/가 by how the last digit is read: 0 영, 1 일, 3 삼, 6 육, 7 칠, 8 팔 end in a
    /// consonant (→ 이); 2 이, 4 사, 5 오, 9 구 end in a vowel (→ 가). "v0.108" → "v0.108이", "v0.109" → "v0.109가".
    /// </summary>
    internal static string Subject(string version)
    {
        char last = version.Length > 0 ? version[^1] : ' ';
        return version + (last is '2' or '4' or '5' or '9' ? "가" : "이");
    }
}

/// <summary>
/// Compares the installed game version with the data pack's version, and the installed game's card list with the pack's
/// tier entries. Pure function, so it is unit-testable without game files.
/// </summary>
public static class VersionGuard
{
    /// <summary>Rarities that never appear as card rewards / in shops, so they have no tier entry by design.</summary>
    private static readonly HashSet<string> UnofferedRarities = new(StringComparer.OrdinalIgnoreCase)
    {
        "Basic", "Curse", "Status", "Token", "Quest", "Event", "Ancient", "None",
    };

    /// <param name="gameVersion">Installed game, e.g. "v0.108" (null/garbage = unknown).</param>
    /// <param name="packVersion">Data pack's version, e.g. "v0.107.1".</param>
    /// <param name="catalog">Every card of the installed game (from the freshly generated catalog).</param>
    /// <param name="offerablePools">Pools whose cards can be offered (character pools + colorless).</param>
    /// <param name="isRated">True when the pack has a tier entry for the id (aliases resolved by the caller).</param>
    /// <param name="tieredIds">All ids with a tier entry in the pack.</param>
    public static VersionGuardResult Evaluate(
        string? gameVersion,
        string? packVersion,
        IEnumerable<GuardCard> catalog,
        IReadOnlySet<string> offerablePools,
        Func<string, bool> isRated,
        IEnumerable<string> tieredIds)
    {
        GameVersion.TryParse(gameVersion, out var game);
        GameVersion.TryParse(packVersion, out var pack);
        int? cmp = game is not null && pack is not null ? Math.Sign(game.CompareTo(pack)) : null;

        var cards = catalog.ToList();
        var unrated = cards
            .Where(c => c.Pool is not null && offerablePools.Contains(c.Pool))
            .Where(c => c.Rarity is null || !UnofferedRarities.Contains(c.Rarity))
            .Where(c => !isRated(c.Id))
            .Select(c => c.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Only meaningful when we have a catalog: with an empty one every tier entry would look "removed".
        var known = cards.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var removed = known.Count == 0
            ? []
            : tieredIds.Where(id => !known.Contains(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        return new VersionGuardResult(game, pack, cmp, unrated, removed);
    }

    /// <summary>Reads id/pool/rarity from a catalog JSON (local or committed). Missing/broken file → empty list.</summary>
    public static IReadOnlyList<GuardCard> LoadCatalog(string? path)
    {
        var list = new List<GuardCard>();
        if (path is null || !File.Exists(path)) return list;
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array) return list;
            foreach (var c in cards.EnumerateArray())
            {
                if (Str(c, "id") is { } id) list.Add(new GuardCard(id, Str(c, "pool"), Str(c, "rarity")));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            list.Clear();
        }
        return list;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
