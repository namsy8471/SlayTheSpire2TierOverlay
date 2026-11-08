using System.Globalization;
using System.IO;
using System.Text.Json;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Services;

/// <summary>
/// The game's own language setting and the overlay's "언어 / Language" choice.
/// <para>
/// The game keeps its options in <c>%APPDATA%\SlayTheSpire2\steam\&lt;SteamID&gt;\settings.save</c> (plain JSON with
/// <c>"language": "kor"</c>, <c>"eng"</c>, <c>"jpn"</c> …). A PC can have several Steam accounts, so the newest file wins,
/// the same rule <see cref="Sts2Overlay.GameState.GamePaths.FindSavesDirectory"/> uses for the run saves.
/// </para>
/// Everything here is pure (strings and paths in, values out) so it is unit-tested without the game; the polling lives in
/// <see cref="GameLanguageWatcher"/>.
/// </summary>
public static class GameLanguage
{
    /// <summary>File name of the game's options file in each Steam account folder.</summary>
    public const string SettingsFileName = "settings.save";

    /// <summary><see cref="Infrastructure.AppSettings.Language"/> values.</summary>
    public const string Auto = "auto";
    public const string Korean = "kor";
    public const string English = "eng";

    /// <summary>The <c>language</c> value of a settings.save text, or null when it is missing, empty or not JSON.</summary>
    public static string? ReadLanguageCode(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("language", out var value) || value.ValueKind != JsonValueKind.String) return null;
            var code = value.GetString()?.Trim();
            return string.IsNullOrEmpty(code) ? null : code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The most recently written <c>steam/&lt;digits&gt;/settings.save</c> under <paramref name="gameDataRoot"/>
    /// (<c>%APPDATA%\SlayTheSpire2</c>), or null when there is none.
    /// </summary>
    public static string? FindSettingsFile(string gameDataRoot)
    {
        try
        {
            var steamDir = Path.Combine(gameDataRoot, "steam");
            if (!Directory.Exists(steamDir)) return null;
            return Directory.EnumerateDirectories(steamDir)
                .Where(d => ulong.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .Select(d => Path.Combine(d, SettingsFileName))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Normalizes a settings.json value: <c>kor</c>, <c>eng</c>, else <c>auto</c> (missing, empty, unknown).</summary>
    public static string NormalizeSetting(string? setting)
    {
        var s = setting?.Trim().ToLowerInvariant();
        return s is Korean or English ? s : Auto;
    }

    /// <summary>
    /// The language to show: a fixed choice (<c>kor</c> / <c>eng</c>) wins; on <c>auto</c> the game's language
    /// (<see cref="Loc.FromGameCode"/>: Korean for <c>kor</c>, English for every other game language).
    /// Null = unknown (auto and the game's language could not be read): keep the current language.
    /// </summary>
    public static Lang? Resolve(string? setting, string? gameCode) => NormalizeSetting(setting) switch
    {
        Korean => Lang.Kor,
        English => Lang.Eng,
        _ => Loc.FromGameCode(gameCode),
    };
}

/// <summary>
/// Watches the game's settings.save for the "auto" language choice: <see cref="Poll"/> is cheap (a directory listing and a
/// write time) and only re-reads the file when it changed, so it can run every few seconds on the UI thread. The player
/// can change the language from the game's options menu mid-session; the game rewrites settings.save then.
/// </summary>
public sealed class GameLanguageWatcher(string gameDataRoot)
{
    private string? _path;
    private DateTime _written;
    private string? _code;

    /// <summary>The game's language code from the last read (null = unknown).</summary>
    public string? Code => _code;

    /// <summary>Re-reads settings.save if a different file is now the newest or it was rewritten. True when the code changed.</summary>
    public bool Poll()
    {
        var path = GameLanguage.FindSettingsFile(gameDataRoot);
        if (path is null) return false; // keep the last known code
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (path == _path && written == _written) return false;

        string text;
        try
        {
            // The game may be writing it right now: share read/write so we never block it, and retry on the next poll.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var code = GameLanguage.ReadLanguageCode(text);
        if (code is null) return false; // half-written or garbage: read it again on the next poll
        _path = path;
        _written = written;
        if (string.Equals(code, _code, StringComparison.OrdinalIgnoreCase)) return false;
        _code = code;
        return true;
    }
}
