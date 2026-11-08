using System.Globalization;
using System.Text;

namespace Sts2Overlay.Vision.Rest;

/// <summary>
/// Tells whether the game's "pick a card from your deck to upgrade" overlay (Smith) is open, including its
/// "before ▶▶▶ after" preview step.
///
/// WHY these probes: the upgrade screen (scenes deck_upgrade_select_screen.tscn) shares its card grid with the
/// removal, transform and pile-view screens, and the grid scrolls, so card positions say little. What it has and
/// the plain removal grid does not is the "View Upgrades" tickbox (강화 상태 보기) in the bottom-left corner
/// (offset 16 / -76, scale 0.75). Its rounded frame is a light grey-cream ring around a dark slate square, drawn
/// on the overlay's black backdrop. We check that ring and its contrast, plus two strips the grid always leaves
/// black (left of the first column, above the back button, and between the last column and the scrollbar).
/// The tickbox is also present on the transform screen and on the in-combat pile views, so this is "an upgrade-
/// style deck grid", not "Smith" by itself; at a rest site it can only be Smith (or the deck view the player may
/// open from the top bar, which also hides the campfire buttons).
/// Text-free on purpose: the label next to the box is localised, the box is not.
/// </summary>
public static class CardGridProbe
{
    // Tickbox ring (design px) measured on 4K captures of the upgrade grid: top/bottom/left/right edges.
    private static readonly RectF[] Ring =
    [
        new(33, 1020, 24, 4),   // top
        new(33, 1048, 24, 4),   // bottom
        new(29, 1026, 4, 20),   // left
        new(57, 1026, 4, 20),   // right
    ];

    /// <summary>Inside of the box: dark slate when unticked; a tick mark would brighten it but stay well below the ring.</summary>
    private static readonly RectF Inner = new(38, 1029, 14, 14);

    /// <summary>Black backdrop just left of the box (the grid's border gradient).</summary>
    private static readonly RectF LeftOfBox = new(4, 1022, 16, 26);

    /// <summary>Left margin of the grid (x 150 is where the scroll area starts), above the back button.</summary>
    private static readonly RectF LeftMargin = new(165, 150, 70, 550);

    /// <summary>Strip between the fifth card column and the scrollbar.</summary>
    private static readonly RectF RightMargin = new(1700, 150, 90, 800);

    /// <summary>Ring must be at least this bright (mean of R, G, B) and nearly grey (max channel spread).</summary>
    public const float RingMinBrightness = 120f, RingMaxSpread = 45f;

    /// <summary>Inner square must be this much darker than the ring.</summary>
    public const float MinRingContrast = 50f;

    /// <summary>Upper brightness limits for the black areas.</summary>
    public const float MaxLeftOfBox = 40f, MaxLeftMargin = 25f, MaxRightMargin = 35f;

    public static bool IsOpen(in ScreenFrame frame) => IsOpen(frame, out _);

    public static bool IsOpen(in ScreenFrame frame, out string detail)
    {
        RefTransform t = RefTransform.For(frame.Width, frame.Height);
        var sb = new StringBuilder("grid[");
        bool ok = true;
        float ringMin = float.MaxValue;
        foreach (RectF r in Ring)
        {
            (float cr, float cg, float cb) = ScreenClassifier.MeanRgb(frame, t.ToPixelRect(r));
            float v = (cr + cg + cb) / 3, spread = Math.Max(cr, Math.Max(cg, cb)) - Math.Min(cr, Math.Min(cg, cb));
            ringMin = Math.Min(ringMin, v);
            ok &= v >= RingMinBrightness && spread <= RingMaxSpread;
        }
        float inner = Brightness(frame, t, Inner);
        ok &= ringMin - inner >= MinRingContrast;
        sb.Append(CultureInfo.InvariantCulture, $"ring={ringMin:F0} in={inner:F0}");
        if (ok)
        {
            // The margins are large strips; only read them when the small tickbox already looks right
            // (on almost every other screen the ring test fails and this costs nothing).
            float leftBox = Brightness(frame, t, LeftOfBox);
            float leftMargin = Brightness(frame, t, LeftMargin);
            float rightMargin = Brightness(frame, t, RightMargin);
            ok &= leftBox <= MaxLeftOfBox && leftMargin <= MaxLeftMargin && rightMargin <= MaxRightMargin;
            sb.Append(CultureInfo.InvariantCulture, $" lb={leftBox:F0} lm={leftMargin:F0} rm={rightMargin:F0}");
        }
        sb.Append(ok ? " -> open]" : " -> no]");
        detail = sb.ToString();
        return ok;
    }

    private static float Brightness(in ScreenFrame frame, RefTransform t, RectF box)
    {
        (float r, float g, float b) = ScreenClassifier.MeanRgb(frame, t.ToPixelRect(box));
        return (r + g + b) / 3;
    }
}
