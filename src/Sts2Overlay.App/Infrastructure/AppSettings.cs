using System.IO;
using System.Text.Json;

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
}

/// <summary>Loads and saves <see cref="AppSettings"/>. A broken file falls back to defaults (and is logged).</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

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
        }
        return new AppSettings();
    }

    public static void Save(string path, AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write to a temp file and swap it in, so a crash mid-write can't leave a half-written settings.json.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"settings: could not save {path} ({ex.Message})");
        }
    }
}
