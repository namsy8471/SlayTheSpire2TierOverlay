using System.Text.Json;
using Sts2Overlay.Core.History;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Diagnostics;

/// <summary>
/// One player's visit to an act-start ancient in a run-history file: the inputs the game had (deck and relics right
/// before the node, rebuilt by <see cref="DeckTimeline"/>), what the game offered (<c>ancient_choice</c>, button order)
/// and what <see cref="AncientOptions"/> replays.
/// </summary>
/// <param name="Truth">The game's options as history lists them (option title keys: relic entry, or the character for Sea Glass).</param>
/// <param name="Replay">The replay in the same notation.</param>
public sealed record AncientVisit(
    string RunFile, string EventId, int Slot, int Players, AncientInputs Inputs,
    IReadOnlyList<string> Truth, IReadOnlyList<string> Replay)
{
    public bool Exact => Truth.SequenceEqual(Replay);

    /// <summary>The run file's <c>build_id</c> (decides the seed arithmetic, <see cref="RngSeeds.ForGameVersion"/>).</summary>
    public string BuildId { get; init; } = "";

    /// <summary>
    /// The game showed options this release's ancient can't offer (a mod added or replaced options): not a replay
    /// target. Decided from the truth alone (option count, ids outside <see cref="AncientOptions.AllOptions"/>).
    /// </summary>
    public bool Modded { get; init; }

    public override string ToString() =>
        $"{RunFile} {EventId} act {Inputs.ActIndex + 1} slot {Slot}/{Players}: game [{string.Join(", ", Truth)}] replay [{string.Join(", ", Replay)}]";
}

/// <summary>
/// Replays every act-start ancient (not Neow: <see cref="NeowOptions"/>) of run-history files and compares with the
/// options the game recorded. Only runs of the given game version (default v0.107.1, the first port) without modifiers
/// count; the run's <c>build_id</c> picks the seed arithmetic.
/// </summary>
public static class AncientReplayEvaluation
{
    public const string Version = "v0.107.1";

    /// <summary>
    /// Every ancient visit in <paramref name="runFiles"/>. <paramref name="epochsOf"/> gives a player's unlocked epochs
    /// (from their progress.save; null = all unlocked) — history files don't keep them. <paramref name="anyVersion"/> also
    /// reads runs of other game versions (to measure how far the replay carries; not a target). Runs whose seed a save
    /// migration prefixed (<c>old...</c>: started before v0.109, finished after) are left out: which of their ancients
    /// the old game drew can't be told from the file.
    /// </summary>
    public static IReadOnlyList<AncientVisit> Run(
        IEnumerable<string> runFiles, Func<long, IReadOnlyCollection<string>?>? epochsOf = null, bool anyVersion = false,
        string version = Version)
    {
        var visits = new List<AncientVisit>();
        foreach (string file in runFiles)
        {
            string json = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string build = root.GetProperty("build_id").GetString() ?? "";
            if ((!anyVersion && build != version) || root.GetProperty("modifiers").GetArrayLength() > 0) continue;
            RunHistory run = RunHistoryReader.Parse(json);
            if (RngSeeds.IsMigrated(run.Seed)) continue;
            RngScheme scheme = RngSeeds.ForGameVersion(build);
            var players = run.Players.Select(p => p.Id).ToList();
            var timelines = players.ToDictionary(id => id, id => DeckTimeline.Build(run, id));

            int nodeIndex = 0;
            foreach (var act in root.GetProperty("map_point_history").EnumerateArray())
            {
                foreach (var point in act.EnumerateArray())
                {
                    int index = nodeIndex++;
                    var node = run.Nodes[index];
                    string? eventId = node.Rooms.FirstOrDefault()?.ModelId;
                    if (eventId is null || !AncientOptions.Handles(eventId) || !point.TryGetProperty("player_stats", out var stats)) continue;
                    foreach (var s in stats.EnumerateArray())
                    {
                        if (!s.TryGetProperty("ancient_choice", out var choices)) continue;
                        long id = s.GetProperty("player_id").GetInt64();
                        var player = run.FindPlayer(id)!;
                        var inputs = new AncientInputs
                        {
                            RunSeed = run.Seed,
                            Slot = players.IndexOf(id),
                            ActIndex = node.ActIndex,
                            Character = player.Character,
                            Deck = timelines[id].DeckBefore(index).Select(c => new AncientDeckCard(c.Id, c.EnchantmentId)).ToArray(),
                            Relics = RelicsBefore(run, player, index),
                            UnlockedEpochs = epochsOf?.Invoke(id),
                            Scheme = scheme,
                        };
                        var draw = AncientOptions.Draw(eventId, inputs)!;
                        var replay = draw.Options.Select(o => o == AncientOptions.SeaGlass ? Entry(draw.SeaGlassCharacter!) : o).ToArray();
                        var truth = choices.EnumerateArray().Select(c => c.GetProperty("TextKey").GetString()!).ToArray();
                        visits.Add(new AncientVisit(Path.GetFileName(file), eventId, inputs.Slot, players.Count, inputs, truth, replay)
                        {
                            BuildId = build,
                            Modded = truth.Length != replay.Length || !truth.All(t => Known(eventId, t, scheme)),
                        });
                    }
                }
            }
        }
        return visits;
    }

    /// <summary>
    /// Relics held right before node <paramref name="index"/>: the final relics added on an earlier floor, plus relics
    /// removed at this node or later that were not gained in between (Touch of Orobas replaces the starter relic, so the
    /// final list no longer shows it, but <c>relics_removed</c> does).
    /// </summary>
    private static string[] RelicsBefore(RunHistory run, RunPlayer player, int index)
    {
        var held = player.Relics.Where(r => r.FloorAdded < run.Nodes[index].Floor).Select(r => r.Id).ToList();
        var gainedSince = new HashSet<string>(StringComparer.Ordinal);
        for (int j = index; j < run.Nodes.Count; j++)
        {
            if (run.Nodes[j].StatsFor(player.Id) is not { } s) continue;
            foreach (var c in s.RelicChoices.Where(c => c.WasPicked)) gainedSince.Add(c.Id);
            foreach (string b in s.BoughtRelics) gainedSince.Add(b);
            foreach (string removed in s.RelicsRemoved)
                if (!gainedSince.Contains(removed)) held.Add(removed);
        }
        return [.. held];
    }

    private static string Entry(string id) => id[(id.IndexOf('.') + 1)..];

    private static readonly HashSet<string> CharacterEntries = new(StringComparer.Ordinal) { "IRONCLAD", "SILENT", "REGENT", "NECROBINDER", "DEFECT" };

    /// <summary>An option history can list for this ancient: one of its options, Orobas's locked slot, or Sea Glass's character.</summary>
    private static bool Known(string eventId, string option, RngScheme scheme) =>
        AncientOptions.AllOptions(eventId, scheme).Contains(option) || option == AncientOptions.OrobasLocked || CharacterEntries.Contains(option);
}
