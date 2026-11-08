namespace Sts2Overlay.Core.Replay.Validation;

/// <summary>One train/test split. Train and test never share a game (<see cref="ReplayStream.GameKey"/>).</summary>
public sealed record Fold(string Name, IReadOnlyList<ReplayStream> Train, IReadOnlyList<ReplayStream> Test);

/// <summary>Ways to split streams into folds. All splits group by game and are deterministic.</summary>
public static class Folds
{
    /// <summary>
    /// Train = older games, test = the newest <paramref name="testGames"/> games (by run start time).
    /// This is the most honest split for "will the data work on my next runs".
    /// </summary>
    public static Fold Chronological(IReadOnlyList<ReplayStream> streams, int testGames)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var games = GamesByTime(streams);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(testGames);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(testGames, games.Count);

        var split = games.Count - testGames;
        return new Fold(
            $"시간순 (학습 {split}판 / 시험 최신 {testGames}판)",
            games.Take(split).SelectMany(g => g).ToArray(),
            games.Skip(split).SelectMany(g => g).ToArray());
    }

    /// <summary>One fold per game: test = that game, train = every other game.</summary>
    public static IReadOnlyList<Fold> LeaveOneGameOut(IReadOnlyList<ReplayStream> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var games = GamesByTime(streams);
        return games.Select((g, i) => new Fold(
                $"LOGO {g[0].StartTime}",
                games.Where((_, j) => j != i).SelectMany(x => x).ToArray(),
                g))
            .ToArray();
    }

    /// <summary>
    /// <paramref name="k"/> folds; games sorted by time are dealt round-robin (game i goes to fold i mod k),
    /// so every fold mixes old and new games.
    /// </summary>
    public static IReadOnlyList<Fold> KFold(IReadOnlyList<ReplayStream> streams, int k)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var games = GamesByTime(streams);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(k, games.Count);

        return Enumerable.Range(0, k)
            .Select(f => new Fold(
                $"{k}-fold #{f + 1}",
                games.Where((_, i) => i % k != f).SelectMany(g => g).ToArray(),
                games.Where((_, i) => i % k == f).SelectMany(g => g).ToArray()))
            .ToArray();
    }

    /// <summary>Streams grouped by game, oldest game first.</summary>
    public static IReadOnlyList<IReadOnlyList<ReplayStream>> GamesByTime(IReadOnlyList<ReplayStream> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        return streams
            .GroupBy(s => s.GameKey, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<ReplayStream>)g.OrderBy(s => s.PersonId).ToArray())
            .OrderBy(g => g.Min(s => s.StartTime))
            .ToArray();
    }
}
