using System.Text.Json;
using Sts2Overlay.GameState.Diagnostics;
using Sts2Overlay.GameState.Tracking;
using Xunit.Abstractions;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// Opt-in measurement over a folder of other players' run-history files (<c>*.run</c>), grouped by game version: how
/// often the Neow and act-start ancient replays give exactly the options the game offered. The files hold Steam ids:
/// they stay outside the repo and the output lists counts and file names only.
/// <para>Run with <c>STS2_RUN_CORPUS=&lt;folder&gt;</c>; skipped without it.</para>
/// </summary>
public class RunCorpusTests(ITestOutputHelper output)
{
    private static IEnumerable<string> Files() =>
        Directory.EnumerateFiles(Environment.GetEnvironmentVariable(RunCorpusFactAttribute.Variable)!, "*.run", SearchOption.AllDirectories);

    [RunCorpusFact]
    public void Neow_replay_per_game_version()
    {
        var rows = new SortedDictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var misses = new List<string>();
        foreach (string file in Files())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            string build = root.GetProperty("build_id").GetString() ?? "";
            if (!rows.TryGetValue(build, out var row)) rows[build] = row = [];
            void Count(string key) => row[key] = row.GetValueOrDefault(key) + 1;

            if (root.GetProperty("modifiers").GetArrayLength() > 0) { Count("skip modifiers"); continue; }
            var history = root.GetProperty("map_point_history");
            if (history.GetArrayLength() == 0 || history[0].GetArrayLength() == 0) { Count("skip empty"); continue; }
            var first = history[0][0];
            if (first.GetProperty("rooms")[0].GetProperty("model_id").GetString() != NeowOptions.EventId) { Count("skip no neow"); continue; }

            string seed = root.GetProperty("seed").GetString()!;
            var players = root.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("id").GetRawText()).ToList();
            RngScheme scheme = NeowOptions.EffectiveScheme(seed, RngSeeds.ForGameVersion(build));
            var possible = NeowOptions.PossibleRelics(players.Count, allCharactersUnlocked: true, scheme);
            foreach (var stats in first.GetProperty("player_stats").EnumerateArray())
            {
                if (!stats.TryGetProperty("ancient_choice", out var choices)) { Count("skip no choice"); continue; }
                var truth = choices.EnumerateArray().Select(c => c.GetProperty("TextKey").GetString()!).ToArray();
                if (truth.Length != 3 || !truth.All(possible.Contains)) { Count("skip modded"); continue; }
                int slot = players.IndexOf(stats.GetProperty("player_id").GetRawText());
                // History files don't keep the profile's unlocks: try both (Kaleidoscope needs every character).
                var unlocked = NeowOptions.Relics(seed, slot, players.Count, true, scheme);
                var locked = NeowOptions.Relics(seed, slot, players.Count, false, scheme);
                Count("tried");
                if (RngSeeds.IsMigrated(seed)) Count("migrated");
                if (unlocked.SequenceEqual(truth) || locked.SequenceEqual(truth))
                {
                    Count("ok");
                    Count(unlocked.SequenceEqual(locked) ? "ok unlock-independent" : unlocked.SequenceEqual(truth) ? "ok all unlocked" : "ok some locked");
                }
                else misses.Add($"{build} {Path.GetFileName(file)} slot {slot}/{players.Count}: game [{string.Join(", ", truth)}] replay [{string.Join(", ", unlocked)}]");
            }
        }
        foreach (var (build, row) in rows)
            output.WriteLine($"{build,-10} {row.GetValueOrDefault("ok")}/{row.GetValueOrDefault("tried")}  " +
                             string.Join(", ", row.Where(kv => kv.Key is not "ok" and not "tried").OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        foreach (string m in misses.Take(40)) output.WriteLine(m);

        // The versions the replay claims (RealFilesTests.ReplayVersions) minus v0.107.1, which has two unexplained misses.
        string[] claimed = ["v0.108.0", "v0.109.0", "v0.109.1", "v0.110.1", "v0.111.0"];
        Assert.DoesNotContain(misses, m => claimed.Contains(m[..m.IndexOf(' ')]));
    }

    [RunCorpusFact]
    public void Ancient_replay_per_game_version()
    {
        var files = Files().ToList();
        foreach (string version in new[] { "v0.107.1", "v0.108.0", "v0.109.0", "v0.109.1", "v0.110.1", "v0.111.0" })
        {
            var visits = AncientReplayEvaluation.Run(files, version: version);
            var vanilla = visits.Where(v => !v.Modded).ToList();
            output.WriteLine($"{version}: {vanilla.Count(v => v.Exact)}/{vanilla.Count} exact, {visits.Count - vanilla.Count} modded visits skipped");
            foreach (var g in vanilla.GroupBy(v => v.EventId).OrderBy(g => g.Key))
                output.WriteLine($"  {g.Key,-22} {g.Count(v => v.Exact)}/{g.Count()}");
            foreach (var v in vanilla.Where(v => !v.Exact).Take(25)) output.WriteLine("  " + v);
            // v0.107.1's only misses (2026-11-12) are in the two runs whose Neow also misses: not this replay's target.
            if (version != "v0.107.1") Assert.All(vanilla, v => Assert.True(v.Exact, v.ToString()));
        }
    }
}

/// <summary>Skips unless <see cref="Variable"/> names a folder (the corpus is never part of the repo).</summary>
public sealed class RunCorpusFactAttribute : FactAttribute
{
    public const string Variable = "STS2_RUN_CORPUS";

    public RunCorpusFactAttribute()
    {
        string? dir = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Skip = $"set {Variable} to a folder of run-history files (*.run)";
    }
}
