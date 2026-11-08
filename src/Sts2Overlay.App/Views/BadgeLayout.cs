using System.Windows;
using Sts2Overlay.App.Integration;

namespace Sts2Overlay.App.Views;

/// <summary>
/// Pure geometry for badge placement (no WPF objects needed besides Rect/Size/Point), so it is easy to reason about
/// and to test.
/// </summary>
public static class BadgeLayout
{
    /// <summary>Badge design size in DIPs before scaling.</summary>
    public const double BaseWidth = 170;

    public const double BaseHeight = 40;

    /// <summary>
    /// Converts a physical screen rectangle to DIPs relative to the badge layer.
    /// Step 1: make it relative to the layer's top-left (both in physical px).
    /// Step 2: divide by the DPI scale (1.5 at 150 %) to get WPF device-independent pixels.
    /// </summary>
    public static Rect ToLayerDips(PixelRect screen, PixelRect layer, double dpiScale)
        => new((screen.X - layer.X) / dpiScale, (screen.Y - layer.Y) / dpiScale, screen.Width / dpiScale, screen.Height / dpiScale);

    /// <summary>Badge scale for a card: the badge is about 72 % of the card's width, so it grows with the game's card size.</summary>
    public static double ScaleFor(double cardWidthDip) => Math.Clamp(cardWidthDip * 0.72 / BaseWidth, 0.5, 3.0);

    /// <summary>
    /// Top-left of a badge for a card. Preference order:
    /// 1. centred ABOVE the card (never covers the art or the title banner);
    /// 2. below the card if there is no room above (card touching the top edge);
    /// 3. inside the card's top edge as a last resort.
    /// Always clamped horizontally to stay on screen.
    /// </summary>
    public static Point Place(Rect card, Size badge, Size area, double gap)
    {
        const double margin = 2;
        var left = card.Left + (card.Width - badge.Width) / 2;
        left = Math.Clamp(left, margin, Math.Max(margin, area.Width - badge.Width - margin));

        var top = card.Top - badge.Height - gap;
        if (top < margin)
        {
            top = card.Bottom + gap;
            if (top + badge.Height > area.Height - margin) top = Math.Max(margin, card.Top + margin);
        }
        return new Point(left, top);
    }
}
