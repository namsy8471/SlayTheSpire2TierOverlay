using System.Globalization;
using System.Text.RegularExpressions;
using Sts2Overlay.Core.History;

namespace Sts2Overlay.Core.Replay.Validation;

/// <summary>
/// One person's decisions in one run: the unit that validation splits on.
/// A co-op run can yield two streams (one per player); both belong to the same <see cref="GameKey"/>
/// and are always put in the same fold so a test fold never shares a game with its training folds.
/// </summary>
/// <param name="Run">The parsed run.</param>
/// <param name="Player">The player whose picks are compared.</param>
/// <param name="PersonId">
/// Who made the picks: the Steam id in co-op, or the history folder's Steam id in single player
/// (single-player files store the player as id 1, which says nothing about who played).
/// </param>
/// <param name="SourcePath">The file the run came from (for the report only).</param>
public sealed record ReplayStream(RunHistory Run, RunPlayer Player, long PersonId, string SourcePath)
{
    /// <summary>Identifies the game: the same co-op game recorded in two players' folders has the same key.</summary>
    public string GameKey => Run.Seed.Length > 0 ? Run.Seed : Run.StartTime.ToString(CultureInfo.InvariantCulture);

    public string Character => Player.Character;

    /// <summary>Run start, unix seconds (also the file name of the run).</summary>
    public long StartTime => Run.StartTime;
}

/// <summary>Builds <see cref="ReplayStream"/>s from history files of one or more Steam profiles.</summary>
public static partial class ReplayStreams
{
    [GeneratedRegex(@"[\\/]steam[\\/](\d{5,})[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex SteamFolder();

    /// <summary>The Steam id in a path like <c>...\SlayTheSpire2\steam\&lt;id&gt;\profile1\saves\history\x.run</c>, or null.</summary>
    public static long? SteamIdFromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var m = SteamFolder().Match(path);
        return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>
    /// One stream per file for the file owner's player, plus (with <paramref name="allPlayers"/>) one per other co-op player.
    /// The file owner is the player whose id equals the folder's Steam id, else <paramref name="defaultOwner"/>, else
    /// <see cref="LocalPlayer.Resolve"/>. Duplicate streams (the same game and person seen from two folders) are dropped,
    /// keeping the first in input order.
    /// </summary>
    public static IReadOnlyList<ReplayStream> Build(IEnumerable<(string Path, RunHistory Run)> files, bool allPlayers, long? defaultOwner = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var seen = new HashSet<(string, long, string)>();
        var result = new List<ReplayStream>();

        foreach (var (path, run) in files)
        {
            if (run.Players.Count == 0) continue;
            var folderId = SteamIdFromPath(path);

            if (run.Players.Count == 1)
            {
                Add(new ReplayStream(run, run.Players[0], folderId ?? defaultOwner ?? run.Players[0].Id, path));
                continue;
            }

            var owner = (folderId is { } f ? run.FindPlayer(f) : null)
                        ?? (defaultOwner is { } d ? run.FindPlayer(d) : null)
                        ?? LocalPlayer.Resolve(run);
            Add(new ReplayStream(run, owner, owner.Id, path));
            if (!allPlayers) continue;
            foreach (var p in run.Players.Where(p => p.Id != owner.Id))
            {
                Add(new ReplayStream(run, p, p.Id, path));
            }
        }

        return result.OrderBy(s => s.StartTime).ThenBy(s => s.PersonId).ToArray();

        void Add(ReplayStream s)
        {
            if (seen.Add((s.GameKey, s.PersonId, s.Character))) result.Add(s);
        }
    }
}
