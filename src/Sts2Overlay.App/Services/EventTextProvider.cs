using System.Globalization;
using System.IO;
using System.Text.Json;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Event and option titles in the overlay language (<see cref="Loc.Current"/>), from the localization extracted out of the
/// game's pck (<c>data/local/pck/localization/{kor,eng}/{events,ancients,relics}.json</c>, gitignored). Game text never
/// enters the repo: the committed <c>events.json</c> only has keys, and this class turns keys into titles at runtime.
/// Both languages are loaded once; every lookup picks the current one, so a language switch needs no reload. A key the
/// current language lacks falls back to the other language, then to the prettified key
/// (<c>NEOW.pages.INITIAL.options.NEW_LEAF</c> → <c>New Leaf</c>).
/// </summary>
public sealed class EventTextProvider
{
    private sealed record Texts(Dictionary<string, string> Events, Dictionary<string, string> Relics)
    {
        public static Texts New() => new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
    }

    private readonly Texts _kor;
    private readonly Texts _eng;

    private EventTextProvider(Texts kor, Texts eng)
    {
        _kor = kor;
        _eng = eng;
    }

    public static EventTextProvider Empty { get; } = new(Texts.New(), Texts.New());

    /// <summary>Strings loaded for the current language.</summary>
    public int Count => Current.Events.Count;

    private Texts Current => Loc.IsEnglish ? _eng : _kor;

    private Texts Other => Loc.IsEnglish ? _kor : _eng;

    /// <summary>Reads <paramref name="localizationDir"/>/{kor,eng}/*.json; missing files are skipped.</summary>
    public static EventTextProvider Load(string localizationDir)
        => new(LoadLang(localizationDir, "kor"), LoadLang(localizationDir, "eng"));

    private static Texts LoadLang(string localizationDir, string lang)
    {
        var t = Texts.New();
        Read(Path.Combine(localizationDir, lang, "events.json"), t.Events);
        Read(Path.Combine(localizationDir, lang, "ancients.json"), t.Events);
        Read(Path.Combine(localizationDir, lang, "relics.json"), t.Relics);
        if (t.Events.Count > 0) AppLog.Info($"event text: {t.Events.Count} strings from {Path.Combine(localizationDir, lang)}");
        return t;
    }

    /// <summary>The default location next to the data packs (<see cref="AppPaths.LocalDataDir"/>).</summary>
    public static EventTextProvider ForLocalData() => Load(Path.Combine(AppPaths.LocalDataDir, "pck", "localization"));

    private bool TryEvent(string key, out string text) => TryGet(Current.Events, key, out text) || TryGet(Other.Events, key, out text);

    private bool TryRelic(string key, out string text) => TryGet(Current.Relics, key, out text) || TryGet(Other.Relics, key, out text);

    private static bool TryGet(Dictionary<string, string> d, string key, out string text)
    {
        if (d.TryGetValue(key, out var t) && t.Length > 0)
        {
            text = t;
            return true;
        }
        text = "";
        return false;
    }

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
        return TryEvent(entry + ".title", out var t) ? Clean(t) : Prettify(entry);
    }

    /// <summary>
    /// An option's button title. Ancient options are relics whose key ends with the relic id
    /// (<c>NEOW.pages.INITIAL.options.SCROLL_BOXES</c>), so the relic title is used when the event has none.
    /// </summary>
    public string OptionTitle(string optionKey)
    {
        var last = optionKey[(optionKey.LastIndexOf('.') + 1)..];
        foreach (var texts in new[] { Current, Other })
        {
            if (TryGet(texts.Events, optionKey + ".title", out var t)) return Clean(t);
            if (TryGet(texts.Relics, last + ".title", out var r)) return Clean(r);
        }
        return Prettify(last);
    }

    /// <summary>Relic title for <c>RELIC.X</c>, or null.</summary>
    public string? RelicTitle(string relicId)
    {
        var entry = relicId.StartsWith("RELIC.", StringComparison.Ordinal) ? relicId[6..] : relicId;
        return TryRelic(entry + ".title", out var r) ? Clean(r) : null;
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
