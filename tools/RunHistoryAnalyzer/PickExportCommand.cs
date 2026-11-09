using System.Text;
using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Learning;

namespace Sts2Overlay.Tools.RunHistoryAnalyzer;

/// <summary>
/// <c>pick-export</c>: training rows for the learned card policy (docs/research/LEARNED_CARD_POLICY_PLAN.md step 1).
/// Writes <c>screens.jsonl</c> (one card reward screen per line) and <c>fights.jsonl</c> (one fight per line) into the
/// output folder. The run id is the run file's name without extension; rows hold ids and numbers only (no Steam id).
/// </summary>
internal static class PickExportCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static int Run(IReadOnlyList<string> files, DataPack pack, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var export = new PickExport(pack);
        var unreadable = 0;
        using var screens = new StreamWriter(Path.Combine(outDir, "screens.jsonl"), false, new UTF8Encoding(false));
        using var fights = new StreamWriter(Path.Combine(outDir, "fights.jsonl"), false, new UTF8Encoding(false));
        foreach (var file in files)
        {
            RunHistory run;
            try
            {
                run = RunHistoryReader.ReadFile(file);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException)
            {
                unreadable++;
                Console.Error.WriteLine($"skip {Path.GetFileName(file)}: {e.Message}");
                continue;
            }

            var (s, f) = export.Export(Path.GetFileNameWithoutExtension(file), run);
            foreach (var row in s)
            {
                screens.WriteLine(JsonSerializer.Serialize(row, Json));
            }

            foreach (var row in f)
            {
                fights.WriteLine(JsonSerializer.Serialize(row, Json));
            }
        }

        var st = export.Stats;
        Console.Error.WriteLine(
            $"runs {st.Runs} (unreadable {unreadable}, multiplayer {st.RunsSkippedMultiplayer}, abandoned {st.RunsSkippedAbandoned}); " +
            $"screens {st.Screens} (left out: shop {st.ScreensSkippedShop}, single card {st.ScreensSkippedSingleCard}, merged groups {st.ScreensSkippedMultiPick}); " +
            $"fights {st.Fights}; deck replay clean in {st.RunsCleanReplay} runs ({st.ReplayWarnings} warnings) → {outDir}");
        foreach (var (kind, count) in st.ReplayWarningKinds.OrderByDescending(kv => kv.Value))
        {
            Console.Error.WriteLine($"  replay warning ×{count}: {kind}");
        }

        return 0;
    }
}
