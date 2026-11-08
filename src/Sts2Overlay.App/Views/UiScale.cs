namespace Sts2Overlay.App.Views;

/// <summary>
/// "UI 배율": how big the overlay looks, relative to its design size at a 1920x1080 game.
///
/// Two scales are involved and must not be applied twice:
/// - Windows display scaling (DPI): WPF ALREADY multiplies every DIP by it (125 % → 1 DIP = 1.25 physical px).
/// - The UI scale we want: a PHYSICAL size factor that follows the game's resolution (1.0 at 1080p, 1.5 at 2160p).
/// So the LayoutTransform we apply is <c>uiScale / dpiScale</c>: at 4K with 125 % Windows scaling, 1.5 / 1.25 = 1.2, and
/// 1.2 (ours) × 1.25 (WPF's) = 1.5 physical, exactly what we wanted. At 4K with 150 % scaling it becomes 1.0.
/// </summary>
public static class UiScale
{
    /// <summary>Choices offered in the tray menu (null = auto).</summary>
    public static readonly IReadOnlyList<double> Presets = [1.0, 1.25, 1.5, 1.75, 2.0];

    /// <summary>
    /// Auto scale from the game's PHYSICAL client height: 1080 → 1.0, 1440 → 1.25, 2160 → 1.5, linear in between,
    /// clamped to 0.8..1.75 outside that range. Based on game pixels (not DPI) so the overlay keeps its size relative to the game.
    /// </summary>
    public static double Auto(int gameHeightPx)
    {
        if (gameHeightPx <= 0) return 1.0;
        double h = gameHeightPx;
        var s = h <= 1440
            ? 1.0 + (h - 1080) / 360 * 0.25   // 1080..1440 → 1.00..1.25
            : 1.25 + (h - 1440) / 720 * 0.25; // 1440..2160 → 1.25..1.50
        return Math.Round(Math.Clamp(s, 0.8, 1.75), 3);
    }

    /// <summary>The user's override (settings.json <c>uiScale</c>), else <see cref="Auto"/>.</summary>
    public static double Effective(double? overrideScale, int gameHeightPx)
        => overrideScale is { } o && o > 0 ? Math.Clamp(o, 0.5, 3.0) : Auto(gameHeightPx);

    /// <summary>LayoutTransform factor for a WPF window on a monitor with <paramref name="dpiScale"/> (1.25 at 125 %).</summary>
    public static double LayoutFactor(double uiScale, double dpiScale) => uiScale / Math.Max(0.5, dpiScale);
}
