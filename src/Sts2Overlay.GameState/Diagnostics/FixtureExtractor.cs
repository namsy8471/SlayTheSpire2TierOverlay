using System.Globalization;
using System.Text.RegularExpressions;
using Sts2Overlay.GameState.Log;

namespace Sts2Overlay.GameState.Diagnostics;

/// <summary>
/// Cuts one co-op run out of the logs as a test fixture: only lines <see cref="LogLineParser"/> turns into an event
/// (ids and numbers, never game text), plus the matching <c>.run</c> file. Real Steam IDs become
/// 76561190000000001, ...002 in lobby order. Reads the game folders, writes only to the output folder.
/// </summary>
public static partial class FixtureExtractor
{
    [GeneratedRegex(@"7656119\d{10}")]
    private static partial Regex SteamId();

    public static bool Extract(string logsDir, string historyDir, string runFile, string outDir, TextWriter report)
    {
        string runPath = Path.Combine(historyDir, runFile);
        if (!File.Exists(runPath)) { report.WriteLine($"no {runPath}"); return false; }
        foreach (string log in Directory.GetFiles(logsDir, "godot*.log").Order(StringComparer.Ordinal))
        {
            List<string> lines = ReadAllLines(log);
            int end = lines.FindIndex(l => l.Contains("Saved run history: " + runFile, StringComparison.Ordinal));
            if (end < 0) continue;
            int start = lines.FindLastIndex(end, l => LogLineParser.Parse(l) is RunStartedEvent);
            if (start < 0) continue;

            // Keep the log owner's id line (the tracker needs it for "me"), then every parsed line of the run.
            var kept = new List<string>();
            string? owner = lines.Take(start).LastOrDefault(l => LogLineParser.Parse(l) is LocalPlayerIdEvent { FromProfilePath: false });
            if (owner != null) kept.Add(owner);
            kept.AddRange(lines.Skip(start).Take(end - start + 1).Where(l => LogLineParser.Parse(l) != null));

            // Steam IDs in lobby order (the Embarking line lists host first).
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in SteamId().Matches(lines[start]))
                map.TryAdd(m.Value, (76561190000000001UL + (ulong)map.Count).ToString(CultureInfo.InvariantCulture));
            string Anon(string s) => SteamId().Replace(s, m => map.TryGetValue(m.Value, out string? a) ? a : "76561190000000099");

            var output = kept.Select(Anon).ToList();
            // Game text is localized (non-ASCII here): such a line is never written, only its event type is reported.
            var withText = output.Where(l => l.Any(ch => ch > 127)).ToList();
            foreach (var g in withText.GroupBy(l => LogLineParser.Parse(l)?.GetType().Name ?? "?"))
                report.WriteLine($"  dropped {g.Count()} line(s) with game text: {g.Key}");
            output = output.Where(l => !l.Any(ch => ch > 127)).ToList(); // (not Except: that would also drop repeated lines)
            Directory.CreateDirectory(outDir);
            File.WriteAllLines(Path.Combine(outDir, Path.GetFileNameWithoutExtension(runFile) + ".log"), output);
            File.WriteAllText(Path.Combine(outDir, runFile), Anon(File.ReadAllText(runPath)));
            report.WriteLine($"{Path.GetFileName(log)}: kept {output.Count} of {end - start + 1} lines, {map.Count} players");
            return true;
        }
        report.WriteLine($"{runFile} not found in {logsDir}");
        return false;
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
