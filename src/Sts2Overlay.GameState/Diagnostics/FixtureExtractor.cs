using System.Globalization;
using System.Text.RegularExpressions;
using Sts2Overlay.GameState.Log;

namespace Sts2Overlay.GameState.Diagnostics;

/// <summary>
/// Cuts one co-op run out of the logs as a test fixture: only lines <see cref="LogLineParser"/> turns into an event
/// (ids and numbers, never game text), plus the matching <c>.run</c> file. Real Steam IDs become
/// 76561190000000001, ...002 in lobby order. Reads the game folders, writes only to the output folder.
/// <para>Localized game text is never written, in any language: lines that carry it next to ids (the event-began
/// line's option titles) are rewritten to their ids (<see cref="Redact"/>), and any line still holding non-ASCII
/// text is dropped. A non-ASCII check alone would let English titles through.</para>
/// </summary>
public static partial class FixtureExtractor
{
    [GeneratedRegex(@"7656119\d{10}")]
    private static partial Regex SteamId();

    private const string OptionsMarker = " with options: ";

    /// <summary>
    /// The line without its localized text, or null when it cannot be cleaned. "Event X began for player P with
    /// options: EventOption title: &lt;title&gt; textKey: K1,EventOption title: ... textKey: K2" becomes
    /// "... with options: EventOption textKey: K1,EventOption textKey: K2", which parses to the same event.
    /// Other parsed lines hold only ids and numbers and are returned unchanged.
    /// </summary>
    public static string? Redact(string line)
    {
        if (LogLineParser.Parse(line) is not EventStartedEvent ev) return line;
        int at = line.IndexOf(OptionsMarker, StringComparison.Ordinal);
        if (at < 0) return null;
        string clean = line[..(at + OptionsMarker.Length)] + string.Join(",", ev.OptionKeys.Select(k => "EventOption textKey: " + k));
        return LogLineParser.Parse(clean) is EventStartedEvent again && again.EventId == ev.EventId && again.PlayerId == ev.PlayerId
               && again.OptionKeys.SequenceEqual(ev.OptionKeys) ? clean : null;
    }

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

            var redacted = kept.Select(Redact).ToList();
            if (redacted.Any(l => l == null)) report.WriteLine($"  dropped {redacted.Count(l => l == null)} line(s) whose game text could not be removed");
            var output = redacted.OfType<string>().Select(Anon).ToList();
            // Backstop: any line still holding non-ASCII text is never written, only its event type is reported.
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
