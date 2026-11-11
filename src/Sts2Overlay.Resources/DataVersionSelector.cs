namespace Sts2Overlay.Resources;

/// <summary>Why <see cref="DataVersionSelector.Select"/> picked its version.</summary>
public enum DataVersionReason
{
    /// <summary>The newest data version that is not newer than the game (exact match or the closest older one).</summary>
    NewestNotNewerThanGame,

    /// <summary>The game is older than every data version: the oldest one.</summary>
    GameOlderThanAll,

    /// <summary>The game version is unknown (no install found, release_info.json unreadable): the newest one.</summary>
    GameUnknown,
}

/// <summary>Outcome of <see cref="DataVersionSelector.Select"/>.</summary>
/// <param name="Version">The chosen data version as it is named on disk (e.g. <c>v0.111.0</c>); null when none was offered.</param>
/// <param name="Game">The parsed game version; null when unknown.</param>
public sealed record DataVersionChoice(string? Version, GameVersion? Game, DataVersionReason Reason);

/// <summary>
/// The one rule that matches the shipped data (a data pack <c>data/packs/&lt;version&gt;</c> and its catalogs
/// <c>data/catalog/*.&lt;version&gt;.json</c>) to the installed game: the newest data version whose version is &lt;= the game
/// version; if the game is older than every data version, the oldest; if the game version is unknown, the newest. A game
/// newer than the newest data still gets the newest data (and the version guard says so). Pure, so it is unit-tested.
/// </summary>
public static class DataVersionSelector
{
    /// <param name="available">Version names on disk (pack folder names); names that are not versions (<c>sample</c>) are ignored.</param>
    /// <param name="gameVersion">The installed game's version (release_info.json), or null when unknown.</param>
    public static DataVersionChoice Select(IEnumerable<string> available, string? gameVersion)
    {
        var versions = available
            .Select(name => GameVersion.TryParse(name, out var v) ? (Name: name, Version: v) : default)
            .Where(x => x.Version is not null)
            .OrderBy(x => x.Version)
            .ToList();
        GameVersion.TryParse(gameVersion, out var game);
        if (versions.Count == 0) return new DataVersionChoice(null, game, game is null ? DataVersionReason.GameUnknown : DataVersionReason.GameOlderThanAll);
        if (game is null) return new DataVersionChoice(versions[^1].Name, null, DataVersionReason.GameUnknown);

        var notNewer = versions.Where(x => x.Version! <= game).ToList();
        return notNewer.Count > 0
            ? new DataVersionChoice(notNewer[^1].Name, game, DataVersionReason.NewestNotNewerThanGame)
            : new DataVersionChoice(versions[0].Name, game, DataVersionReason.GameOlderThanAll);
    }
}
