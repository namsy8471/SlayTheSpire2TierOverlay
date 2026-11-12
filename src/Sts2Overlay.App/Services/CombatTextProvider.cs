using System.IO;
using System.Text.Json;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Monster and potion names in the overlay language, for the in-fight play order. Read from the localization extracted out
/// of the game's pck (<c>data/local/pck/localization/{kor,eng}/{monsters,potions}.json</c>, gitignored): monsters by
/// <c>&lt;ID&gt;.name</c>, potions by <c>&lt;ID&gt;.title</c>. Both languages are loaded once; a lookup picks the current one
/// and falls back to the other. Null when neither has the id (the caller decides the fallback text).
/// </summary>
public sealed class CombatTextProvider
{
    private readonly Dictionary<string, string> _monstersKor, _monstersEng, _potionsKor, _potionsEng;

    private CombatTextProvider(Dictionary<string, string> monstersKor, Dictionary<string, string> monstersEng,
        Dictionary<string, string> potionsKor, Dictionary<string, string> potionsEng)
    {
        _monstersKor = monstersKor;
        _monstersEng = monstersEng;
        _potionsKor = potionsKor;
        _potionsEng = potionsEng;
    }

    public static CombatTextProvider Empty { get; } = new([], [], [], []);

    /// <summary>Monster names loaded (both languages).</summary>
    public int MonsterCount => _monstersKor.Count + _monstersEng.Count;

    /// <summary>Reads <paramref name="localizationDir"/>/{kor,eng}/{monsters,potions}.json; missing files are skipped.</summary>
    public static CombatTextProvider Load(string localizationDir)
    {
        var p = new CombatTextProvider(
            Read(Path.Combine(localizationDir, "kor", "monsters.json"), ".name"),
            Read(Path.Combine(localizationDir, "eng", "monsters.json"), ".name"),
            Read(Path.Combine(localizationDir, "kor", "potions.json"), ".title"),
            Read(Path.Combine(localizationDir, "eng", "potions.json"), ".title"));
        if (p.MonsterCount > 0) AppLog.Info($"combat text: {p._monstersKor.Count}/{p._monstersEng.Count} monster names, {p._potionsKor.Count}/{p._potionsEng.Count} potion names (kor/eng)");
        return p;
    }

    /// <summary>The default location next to the data packs (<see cref="AppPaths.LocalDataDir"/>).</summary>
    public static CombatTextProvider ForLocalData() => Load(Path.Combine(AppPaths.LocalDataDir, "pck", "localization"));

    /// <summary><c>JAW_WORM</c> (or <c>MONSTER.JAW_WORM</c>) → its name, or null.</summary>
    public string? MonsterName(string id) => Find(Entry(id, "MONSTER."), _monstersKor, _monstersEng);

    /// <summary><c>FIRE_POTION</c> (or <c>POTION.FIRE_POTION</c>) → its name, or null.</summary>
    public string? PotionName(string id) => Find(Entry(id, "POTION."), _potionsKor, _potionsEng);

    private static string Entry(string id, string prefix) => id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id;

    private static string? Find(string entry, Dictionary<string, string> kor, Dictionary<string, string> eng)
    {
        var (current, other) = Loc.IsEnglish ? (eng, kor) : (kor, eng);
        return current.TryGetValue(entry, out var n) ? n : other.TryGetValue(entry, out n) ? n : null;
    }

    /// <summary>Keeps only the keys ending in <paramref name="suffix"/>, stored by the id in front of it, BBCode removed.</summary>
    private static Dictionary<string, string> Read(string path, string suffix)
    {
        var into = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return into;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String || !prop.Name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                var id = prop.Name[..^suffix.Length];
                if (id.Contains('.', StringComparison.Ordinal)) continue; // a move title (X.moves.Y.title), not the name
                var text = EventTextProvider.Clean(prop.Value.GetString() ?? "");
                if (text.Length > 0) into[id] = text;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn($"combat text: could not read {path} ({ex.Message})");
        }
        return into;
    }
}
