using System.Globalization;
using System.IO;
using System.Text.Json;
using Sts2Overlay.App.Infrastructure;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Korean event and option titles from the localization extracted out of the game's pck
/// (<c>data/local/pck/localization/kor/{events,ancients,relics}.json</c>, gitignored). Game text never enters the repo:
/// the committed <c>events.json</c> only has keys, and this class turns keys into titles at runtime.
/// Without the extracted files, keys are prettified (<c>NEOW.pages.INITIAL.options.NEW_LEAF</c> → <c>New Leaf</c>).
/// </summary>
public sealed class EventTextProvider
{
    private readonly Dictionary<string, string> _events;
    private readonly Dictionary<string, string> _relics;

    private EventTextProvider(Dictionary<string, string> events, Dictionary<string, string> relics)
    {
        _events = events;
        _relics = relics;
    }

    public static EventTextProvider Empty { get; } = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    public int Count => _events.Count;

    /// <summary>Reads <paramref name="localizationDir"/>/<paramref name="lang"/>/*.json; missing files are skipped.</summary>
    public static EventTextProvider Load(string localizationDir, string lang = "kor")
    {
        var events = new Dictionary<string, string>(StringComparer.Ordinal);
        var relics = new Dictionary<string, string>(StringComparer.Ordinal);
        Read(Path.Combine(localizationDir, lang, "events.json"), events);
        Read(Path.Combine(localizationDir, lang, "ancients.json"), events);
        Read(Path.Combine(localizationDir, lang, "relics.json"), relics);
        if (events.Count > 0) AppLog.Info($"event text: {events.Count} strings from {Path.Combine(localizationDir, lang)}");
        return new EventTextProvider(events, relics);
    }

    /// <summary>The default location next to the data packs (<see cref="AppPaths.LocalDataDir"/>).</summary>
    public static EventTextProvider ForLocalData() => Load(Path.Combine(AppPaths.LocalDataDir, "pck", "localization"));

    private static void Read(string path, Dictionary<string, string> into)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String) into[p.Name] = p.Value.GetString() ?? "";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn($"event text: could not read {path} ({ex.Message})");
        }
    }

    /// <summary><c>EVENT.ABYSSAL_BATHS</c> → its title.</summary>
    public string EventTitle(string eventId)
    {
        var entry = eventId.StartsWith("EVENT.", StringComparison.Ordinal) ? eventId[6..] : eventId;
        return _events.TryGetValue(entry + ".title", out var t) && t.Length > 0 ? Clean(t) : Prettify(entry);
    }

    /// <summary>
    /// An option's button title. Ancient options are relics whose key ends with the relic id
    /// (<c>NEOW.pages.INITIAL.options.SCROLL_BOXES</c>), so the relic title is used when the event has none.
    /// </summary>
    public string OptionTitle(string optionKey)
    {
        if (_events.TryGetValue(optionKey + ".title", out var t) && t.Length > 0) return Clean(t);
        var last = optionKey[(optionKey.LastIndexOf('.') + 1)..];
        if (_relics.TryGetValue(last + ".title", out var r) && r.Length > 0) return Clean(r);
        return Prettify(last);
    }

    /// <summary>Relic title for <c>RELIC.X</c>, or null.</summary>
    public string? RelicTitle(string relicId)
    {
        var entry = relicId.StartsWith("RELIC.", StringComparison.Ordinal) ? relicId[6..] : relicId;
        return _relics.TryGetValue(entry + ".title", out var r) && r.Length > 0 ? Clean(r) : null;
    }

    /// <summary>Strips the game's BBCode tags (<c>[gold]</c>, <c>[/red]</c>).</summary>
    internal static string Clean(string s)
    {
        if (s.IndexOf('[') < 0) return s.Trim();
        var sb = new System.Text.StringBuilder(s.Length);
        var depth = 0;
        foreach (var c in s)
        {
            if (c == '[') depth++;
            else if (c == ']' && depth > 0) depth--;
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string Prettify(string raw)
        => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Replace('_', ' ').ToLowerInvariant());
}
