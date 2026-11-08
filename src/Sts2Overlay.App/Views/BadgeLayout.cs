namespace Sts2Overlay.App.Views;

// Pure badge geometry: no WPF types, so the same file is compiled into tools/VisionBench (linked) and the offline
// preview renderer draws badges exactly where the overlay would.

/// <summary>A rectangle in PHYSICAL pixels (screen or captured frame).</summary>
public readonly record struct PxBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>A rectangle in DIPs relative to the badge layer's top-left (what WPF's Canvas uses).</summary>
public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Intersects(DipRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
}

/// <summary>One placed badge: its box and the scale of the 170x40 design.</summary>
public readonly record struct PlacedBadge(DipRect Box, double Scale);

/// <summary>Everything the badge layer draws for one frame, in layer DIPs.</summary>
/// <param name="Badges">One per input card, same order.</param>
/// <param name="Skip">The "스킵 추천" pill under the card row, or null.</param>
/// <param name="CardOutlines">Debug outlines of the recognised cards (empty unless asked for).</param>
/// <param name="PriceOutlines">Debug outlines of the shop price labels (empty unless asked for).</param>
public sealed record BadgeFrame(
    IReadOnlyList<PlacedBadge> Badges,
    PlacedBadge? Skip,
    IReadOnlyList<DipRect> CardOutlines,
    IReadOnlyList<DipRect> PriceOutlines);

/// <summary>
/// Badge placement math (pure functions, unit-tested and shared with VisionBench's preview renderer).
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
    public static DipRect ToLayerDips(PxBox screen, PxBox layer, double dpiScale)
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
    public static (double Left, double Top) Place(DipRect card, double width, double height, double areaWidth, double areaHeight, double gap)
    {
        const double margin = 2;
        var left = card.X + (card.Width - width) / 2;
        left = Math.Clamp(left, margin, Math.Max(margin, areaWidth - width - margin));

        var top = card.Y - height - gap;
        if (top < margin)
        {
            top = card.Bottom + gap;
            if (top + height > areaHeight - margin) top = Math.Max(margin, card.Y + margin);
        }
        return (left, top);
    }

    /// <summary>
    /// Lays out one frame: a badge per card (scaled from the card width, times the user's UI-scale multiplier), the
    /// optional skip pill under the row, and, for the debug view, the card and price outlines.
    /// </summary>
    /// <param name="cards">Full card bounds, physical screen pixels (same space as <paramref name="layer"/>).</param>
    /// <param name="prices">Price-label bounds per card (null entries: none / not a shop); may be shorter than cards.</param>
    /// <param name="layer">The badge layer = the game's client area, physical screen pixels.</param>
    /// <param name="dpiScale">Monitor DPI / 96 (1.5 at 150 %).</param>
    /// <param name="multiplier">User UI scale relative to auto (1 = auto).</param>
    /// <param name="skip">Add the "스킵 추천" pill.</param>
    /// <param name="outlines">Return the card/price outlines (debug view).</param>
    public static BadgeFrame Compute(IReadOnlyList<PxBox> cards, IReadOnlyList<PxBox?> prices, PxBox layer, double dpiScale,
        double multiplier, bool skip, bool outlines)
    {
        var areaWidth = layer.Width / dpiScale;
        var areaHeight = layer.Height / dpiScale;
        var badges = new List<PlacedBadge>(cards.Count);
        var cardOutlines = new List<DipRect>();
        var priceOutlines = new List<DipRect>();
        for (var i = 0; i < cards.Count; i++)
        {
            var card = ToLayerDips(cards[i], layer, dpiScale);
            var scale = ScaleFor(card.Width) * multiplier;
            double w = BaseWidth * scale, h = BaseHeight * scale;
            var (left, top) = Place(card, w, h, areaWidth, areaHeight, gap: 6 * scale);
            badges.Add(new PlacedBadge(new DipRect(left, top, w, h), scale));
            if (!outlines) continue;
            cardOutlines.Add(card);
            if (i < prices.Count && prices[i] is { } p) priceOutlines.Add(ToLayerDips(p, layer, dpiScale));
        }

        PlacedBadge? skipPill = null;
        if (skip && cards.Count > 0)
        {
            int l = cards.Min(c => c.X), r = cards.Max(c => c.Right), b = cards.Max(c => c.Bottom);
            var row = ToLayerDips(new PxBox(l, b, r - l, 1), layer, dpiScale);
            var scale = badges.Min(x => x.Scale);
            double w = BaseWidth * scale, h = BaseHeight * scale;
            var left = Math.Clamp(row.X + (row.Width - w) / 2, 2, Math.Max(2, areaWidth - w - 2));
            var top = Math.Min(row.Y + 12 * scale, areaHeight - h - 2);
            skipPill = new PlacedBadge(new DipRect(left, top, w, h), scale);
        }
        return new BadgeFrame(badges, skipPill, cardOutlines, priceOutlines);
    }
}
