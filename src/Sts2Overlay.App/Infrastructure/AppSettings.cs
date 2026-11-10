using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Infrastructure;

/// <summary>User settings persisted to <c>%LOCALAPPDATA%\Sts2Overlay\settings.json</c>.</summary>
public sealed class AppSettings
{
    /// <summary>Panel top-left in PHYSICAL screen pixels (not WPF DIPs, which are ambiguous across monitors with different DPI).</summary>
    public int? PanelX { get; set; }

    public int? PanelY { get; set; }

    public bool PanelCollapsed { get; set; }

    /// <summary>Last selected panel tab (<see cref="ViewModels.PanelTab"/> name).</summary>
    public string? SelectedTab { get; set; }

    /// <summary>Data pack directory; relative paths are resolved against the repo root. Null = data/packs/v0.107.1.</summary>
    public string? PackDir { get; set; }

    /// <summary>Card catalog JSON with Korean names. Null = newest data/local/catalog.*.json.</summary>
    public string? CatalogPath { get; set; }

    /// <summary>Slay the Spire 2 install folder (has SlayTheSpire2.pck). Null = find it in the Steam libraries.</summary>
    public string? GameDir { get; set; }

    /// <summary>
    /// "UI 배율" override as a physical size factor relative to the 1080p look (1.5 = 150 %). Null = automatic from the
    /// game's resolution (1080p → 1.0, 1440p → 1.25, 2160p → 1.5). See <see cref="Views.UiScale"/>.
    /// </summary>
    public double? UiScale { get; set; }

    /// <summary>Capture/recognition rate while a reward or shop screen is likely.</summary>
    public double RecognizeHz { get; set; } = 3;

    /// <summary>Background watch rate when no reward/shop screen is hinted by the log (0 = only on log hints).</summary>
    public double IdleRecognizeHz { get; set; } = 1.5;

    /// <summary>
    /// Capture rate while the log says a reward or shop screen is coming but it is not on screen yet (the "hunt", see
    /// <see cref="Integration.OfferHunt"/>): from the hint change until badges show, the hint ends, or 8 s passed. At
    /// <see cref="RecognizeHz"/> the screen waits up to one interval (~350 ms at 3 Hz) before it is even captured; at
    /// 20 Hz about 50 ms. Cost: one look is ~13.5 ms of one core at 4K, so 20 Hz is ~27 % of a core for the few seconds
    /// of each hunt, then the normal rate again. Clamped to 30; 0 = off (no hunt). settings.json only.
    /// </summary>
    public double HuntRecognizeHz { get; set; } = 20;

    /// <summary>
    /// How the game window is captured: "auto" (Windows.Graphics.Capture, falling back to GDI when it is unsupported or
    /// fails), "wgc" (same as auto) or "gdi" (always the slower GDI BitBlt). The active backend is logged.
    /// </summary>
    public string CaptureBackend { get; set; } = "auto";

    /// <summary>
    /// "방송·캡처에 오버레이 표시": the overlay shows up in OBS, Discord screen share and screenshots. Off = our windows are
    /// hidden from every screen capture (SetWindowDisplayAffinity WDA_EXCLUDEFROMCAPTURE). Recognition is not affected
    /// either way: it captures the game window alone, and on the GDI fallback the overlay is hidden automatically
    /// (<see cref="Services.OverlayCaptureVisibility"/>). Replaces the old <c>hideOverlayFromCapture</c> (always true,
    /// which hid the overlay from OBS too); that key is ignored now.
    /// </summary>
    public bool ShowOverlayInCapture { get; set; } = true;

    /// <summary>"항상 맨 앞에 표시": the panel stays above other windows (and is shown only while the game is in front).
    /// Off: a normal window that stays open while the game runs and can be covered. Badges are always on top.</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>
    /// "맵·휴식처 위 추천": draws the recommended next node and path on the game's own map screen, and the advised
    /// button / card to upgrade at rest sites (tray / Ctrl+F5).
    /// </summary>
    public bool MapOverlay { get; set; } = true;

    /// <summary>Capture rate while the map screen is open, so the marks follow when the map is scrolled.</summary>
    public double MapHz { get; set; } = 6;

    /// <summary>
    /// "언어 / Language": "auto" (follow the game's own language setting, re-checked while it runs), "kor" or "eng".
    /// See <see cref="Services.GameLanguage"/>.
    /// </summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Developer switch (settings.json only, no menu item): Ctrl+F9 saves the game screen as a PNG in <c>captures\</c>
    /// (for the vision test set), and the folder is kept. Off (default): nothing is saved, and the folder is emptied at
    /// start (<see cref="Services.CaptureCleanup"/>). Recognition works in memory either way.
    /// </summary>
    public bool DebugCaptures { get; set; }

    /// <summary>
    /// Card reward ranking: "learned" (default) = the model trained on top players' picks
    /// (docs/research/LEARNED_CARD_POLICY_PLAN.md), "engine" = the hand-tuned rules only. settings.json only.
    /// </summary>
    public string CardModel { get; set; } = "learned";

    /// <summary>Panel background opacity 0..1.</summary>
    public double PanelOpacity { get; set; } = 0.86;

    /// <summary>
    /// Installed builds only: check for a new version ~15 s after start (then at most every 6 h) and download it in the
    /// background. It is applied when the overlay exits, or right away from the tray ("업데이트 적용"). Never restarts by itself.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// Where updates come from. Null = the GitHub releases of namsy8471/SlayTheSpire2TierOverlay. Also accepts another
    /// github.com repo URL, any http(s) folder with Velopack release files, or a local folder (for testing).
    /// </summary>
    public string? UpdateUrl { get; set; }

    /// <summary>티어표 tab: the last card pool picked (<c>CHARACTER.X</c> or <c>COLORLESS</c>), shown when there is no run.</summary>
    public string? TierCharacter { get; set; }

    /// <summary>
    /// settings.json exists but could not be read, so this run uses defaults. <see cref="SettingsStore.Save"/> then
    /// leaves the file alone until the user really changes a setting (the original is also copied to settings.json.bad).
    /// </summary>
    [JsonIgnore]
    public bool LoadedFromBrokenFile { get; internal set; }

    /// <summary><see cref="SettingsStore.UserChoices"/> right after a broken load: "nothing changed yet" means equal to this.</summary>
    internal string? BrokenLoadChoices;
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. A broken file falls back to defaults: it is copied to settings.json.bad, a
/// standing panel notice says so, and it is not overwritten with those defaults unless the user changes something.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Saved on every exit whether or not the user touched them (window position, last tab): not a user change.</summary>
    private static readonly string[] SessionKeys = ["panelX", "panelY", "selectedTab", "tierCharacter"];

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn($"settings: could not read {path}, using defaults ({ex.Message})");
            return BrokenFileDefaults(path);
        }
        return new AppSettings();
    }

    /// <summary>Defaults for this run, the unreadable file kept as settings.json.bad, and a standing panel notice.</summary>
    private static AppSettings BrokenFileDefaults(string path)
    {
        var kept = false;
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
            kept = true;
            AppLog.Warn("settings: the unreadable file was copied to settings.json.bad");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"settings: could not copy it to settings.json.bad ({ex.Message})");
        }
        var settings = new AppSettings { LoadedFromBrokenFile = true };
        settings.BrokenLoadChoices = UserChoices(settings);
        StandingNotices.Set(StandingNotices.Settings, kept
            ? () => Loc.T("settings.json을 읽지 못해 기본값으로 실행 중입니다 (원본은 settings.json.bad로 보관)",
                "settings.json could not be read, running with default settings (the original is kept as settings.json.bad)")
            : () => Loc.T("settings.json을 읽지 못해 기본값으로 실행 중입니다 (원본은 그대로 둡니다)",
                "settings.json could not be read, running with default settings (the original is left as it is)"));
        return settings;
    }

    /// <summary>The settings as JSON without <see cref="SessionKeys"/>: what counts as "the user changed a setting".</summary>
    internal static string UserChoices(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, Options)!.AsObject();
        foreach (var key in SessionKeys) node.Remove(key);
        return node.ToJsonString();
    }

    public static void Save(string path, AppSettings settings)
    {
        if (settings.LoadedFromBrokenFile && UserChoices(settings) == settings.BrokenLoadChoices)
        {
            // Writing now would replace the user's (broken but fixable) file with plain defaults.
            AppLog.Info("settings: not saved (settings.json could not be read at start and no setting was changed)");
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write to a temp file and swap it in, so a crash mid-write can't leave a half-written settings.json.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, path, overwrite: true);
            settings.LoadedFromBrokenFile = false; // the file holds the user's own choices now
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"settings: could not save {path} ({ex.Message})");
        }
    }
}
