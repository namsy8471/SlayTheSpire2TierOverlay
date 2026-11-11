using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Log;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Diagnostics;

/// <summary>One player of one co-op run: log-only deck vs the final deck in the run's <c>.run</c> file.</summary>
/// <param name="LogFile">Log file name the run was cut from.</param>
/// <param name="RunFile">History file name (from <c>Saved run history: N.run</c>).</param>
/// <param name="PlayerId">The player we pretended to be (log rebuild with no save).</param>
/// <param name="IsLogOwner">True when that player wrote the log (their local-only Debug lines are present).</param>
/// <param name="TrueDeck">Final deck from the <c>.run</c> file.</param>
/// <param name="RebuiltDeck">Deck the tracker rebuilt from the log alone (before any run-end reconciliation).</param>
/// <param name="Run">The tracker snapshot just before the run ended (log only).</param>
/// <param name="ReconciledExact">After "Saved run history" with the history folder known: deck equals the truth exactly.</param>
public sealed record GuestDeckResult(
    string LogFile, string RunFile, ulong PlayerId, string Character, bool IsLogOwner,
    IReadOnlyList<DeckCard> TrueDeck, IReadOnlyList<DeckCard> RebuiltDeck, TrackedRun? Run, bool ReconciledExact)
{
    public int UncertainCards => Run?.UncertainCards.Count ?? 0;

    /// <summary>Cards matched by id (multiset intersection).</summary>
    public int IdMatches => Intersect(TrueDeck.Select(c => c.Id), RebuiltDeck.Select(c => c.Id));

    /// <summary>Cards matched by id AND upgrade level.</summary>
    public int ExactMatches => Intersect(TrueDeck.Select(c => c.Card.ToString()), RebuiltDeck.Select(c => c.Card.ToString()));

    /// <summary>In the true deck but not rebuilt (by id+upgrade).</summary>
    public IReadOnlyList<string> Missing => Minus(TrueDeck.Select(c => c.Card.ToString()), RebuiltDeck.Select(c => c.Card.ToString()));

    /// <summary>Rebuilt but not in the true deck (by id+upgrade).</summary>
    public IReadOnlyList<string> Extra => Minus(RebuiltDeck.Select(c => c.Card.ToString()), TrueDeck.Select(c => c.Card.ToString()));

    public bool Perfect => ExactMatches == TrueDeck.Count && RebuiltDeck.Count == TrueDeck.Count;

    /// <summary>
    /// Differences the tracker did NOT warn about. Each difference must be covered by a flag: a wrong upgrade by an
    /// uncertain card of that id or an unplaced upgrade, a missing card by an unidentified card, an extra card by an
    /// uncertain card or an unplaced removal. 0 means the overlay never shows a wrong card as certain.
    /// </summary>
    public int SilentErrors
    {
        get
        {
            var missing = Missing.ToList();
            var extra = Extra.ToList();
            var uncertain = (Run?.UncertainCards ?? []).GroupBy(u => u.CardId).ToDictionary(g => g.Key, g => g.Count());
            int upgrades = Run?.UnknownUpgrades ?? 0, unknown = Run?.UnidentifiedCards ?? 0, removals = Run?.UnknownRemovals ?? 0;
            int silent = 0;
            bool Use(Dictionary<string, int> d, string id) { if (d.GetValueOrDefault(id) <= 0) return false; d[id]--; return true; }
            foreach (string m in missing.ToList())
            {
                string id = m.Split('+')[0];
                int e = extra.FindIndex(x => x.Split('+')[0] == id);
                if (e < 0) continue;                         // id missing entirely: handled below
                extra.RemoveAt(e); missing.Remove(m);
                if (!Use(uncertain, id) && upgrades-- <= 0) silent++;
            }
            foreach (string m in missing) if (unknown-- <= 0) silent++;
            foreach (string x in extra) if (!Use(uncertain, x.Split('+')[0]) && removals-- <= 0) silent++;
            return silent;
        }
    }

    public override string ToString() =>
        FormattableString.Invariant($"{RunFile} {Character,-22} {(IsLogOwner ? "owner " : "remote")} id {IdMatches,2}/{TrueDeck.Count,-2} exact {ExactMatches,2}/{TrueDeck.Count,-2} ")
        + FormattableString.Invariant($"size {RebuiltDeck.Count,2} flags u{UncertainCards}/?{Run?.UnidentifiedCards ?? 0}/up{Run?.UnknownUpgrades ?? 0}/rm{Run?.UnknownRemovals ?? 0} end:{(ReconciledExact ? "exact" : "NOT EXACT")} silent {SilentErrors}")
        + (Missing.Count > 0 ? $"  missing [{string.Join(", ", Missing)}]" : "")
        + (Extra.Count > 0 ? $"  extra [{string.Join(", ", Extra)}]" : "");

    private static int Intersect(IEnumerable<string> a, IEnumerable<string> b)
    {
        var counts = b.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        int n = 0;
        foreach (string x in a)
            if (counts.TryGetValue(x, out int c) && c > 0) { counts[x] = c - 1; n++; }
        return n;
    }

    private static List<string> Minus(IEnumerable<string> a, IEnumerable<string> b)
    {
        var counts = b.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var left = new List<string>();
        foreach (string x in a)
        {
            if (counts.TryGetValue(x, out int c) && c > 0) counts[x] = c - 1;
            else left.Add(x);
        }
        return left;
    }
}

/// <summary>
/// Reproducible check of the co-op guest rebuild (a guest has no save, so the deck comes from godot.log alone).
/// Every co-op run found in the logs is replayed once per player, pretending to be that player with no save, and
/// the rebuilt deck is compared with the <c>.run</c> history file the game wrote at the end (ground truth).
/// Read-only: logs are opened with FileShare.ReadWrite, nothing is written.
/// </summary>
public static class GuestDeckEvaluation
{
    /// <summary>Evaluates every <c>godot*.log</c> in <paramref name="logsDir"/> against <c>.run</c> files in <paramref name="historyDir"/>.</summary>
    public static IReadOnlyList<GuestDeckResult> Run(string logsDir, string historyDir)
    {
        var results = new List<GuestDeckResult>();
        foreach (string log in Directory.GetFiles(logsDir, "godot*.log").Order(StringComparer.Ordinal))
            results.AddRange(EvaluateLog(Path.GetFileName(log), ReadAllLines(log), historyDir));
        return results;
    }

    /// <summary>Cuts <paramref name="lines"/> into co-op runs (Embarking … Saved run history) and evaluates each.</summary>
    public static IEnumerable<GuestDeckResult> EvaluateLog(string logName, IReadOnlyList<string> lines, string historyDir)
    {
        ulong? owner = null;
        string? versionLine = null; // the startup line naming the game version (seed arithmetic), above the run
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            LogEvent? e = LogLineParser.Parse(lines[i]);
            switch (e)
            {
                case GameVersionEvent:
                    versionLine = lines[i];
                    break;
                case LocalPlayerIdEvent { FromProfilePath: false, NetId: > 1 } id:
                    owner = id.NetId;
                    break;
                case RunStartedEvent s:
                    start = s.IsMultiplayer ? i : -1;
                    break;
                case RunContinuedEvent:
                    start = -1; // a continued co-op run's start is in an older log: can't rebuild it
                    break;
                case RunHistorySavedEvent h when start >= 0:
                    string runPath = Path.Combine(historyDir, h.FileName);
                    if (File.Exists(runPath))
                    {
                        RunHistory history = RunHistoryReader.ReadFile(runPath);
                        // The live tracker reads the log from its start, so it has seen the version line; a trimmed log
                        // (the test fixtures) stands in the history file's build_id for it.
                        var segment = lines.Skip(start).Take(i - start + 1)
                            .Prepend(versionLine ?? $"Release Version: {history.BuildId}").ToList();
                        foreach (RunPlayer p in history.Players)
                            yield return EvaluatePlayer(logName, h.FileName, segment, history, p, owner, historyDir);
                    }
                    start = -1;
                    break;
            }
        }
    }

    /// <summary>Replays one run's lines as <paramref name="player"/> with no save and compares the decks.</summary>
    public static GuestDeckResult EvaluatePlayer(string logName, string runFile, IReadOnlyList<string> segment,
                                                 RunHistory history, RunPlayer player, ulong? logOwner, string historyDir)
    {
        ulong id = (ulong)player.Id;
        // 1) Stop before "Saved run history": the pure log rebuild, i.e. what the overlay shows during the run.
        var tracker = new GameStateTracker(new TrackerOptions { LocalNetIdOverride = id });
        tracker.ProcessLines(segment.Take(segment.Count - 1));
        TrackedRun? run = tracker.Current;

        // 2) The same run through the end with the saves folder known: the tracker reads history/<n>.run itself.
        var saves = Path.GetDirectoryName(Path.GetFullPath(historyDir))!;
        var reconciling = new GameStateTracker(new TrackerOptions { LocalNetIdOverride = id, Paths = new GamePaths(saves, null, null) });
        reconciling.ProcessLines(segment);
        TrackedRun? end = reconciling.Current;
        bool exact = end is { DeckFromRunHistory: true }
                     && end.State.Deck.Select(c => c.Card.ToString()).Order().SequenceEqual(player.Deck.Select(c => c.Card.ToString()).Order());

        return new GuestDeckResult(logName, runFile, id, player.Character, logOwner == id,
            player.Deck, run?.State.Deck ?? [], run, exact);
    }

    private static List<string> ReadAllLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        var lines = new List<string>();
        for (string? l; (l = reader.ReadLine()) != null;) lines.Add(l);
        return lines;
    }
}
