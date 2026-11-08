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

    /// <summary>Fully inside a <paramref name="width"/> x <paramref name="height"/> area (with a small margin).</summary>
    public bool InsideArea(double width, double height, double margin = 0)
        => X >= margin && Y >= margin && Right <= width - margin && Bottom <= height - margin;
}

/// <summary>One placed badge: its box and the scale of its design (e.g. the 170x40 badge).</summary>
public readonly record struct PlacedBadge(DipRect Box, double Scale);

/// <summary>A stack of reason chips under a card: the box the stack may use, its scale and how many rows.</summary>
public readonly record struct PlacedChips(DipRect Box, double Scale, int Count);

/// <summary>What to draw for one card besides its badge.</summary>
/// <param name="ChipCount">Reason chips (0..2) under the card.</param>
/// <param name="Ribbon">A gold "추천!"/"구매!" ribbon on the badge.</param>
public readonly record struct CardMarks(int ChipCount = 0, bool Ribbon = false);

/// <summary>Everything the badge layer draws for one frame, in layer DIPs.</summary>
/// <param name="Badges">One per input card, same order.</param>
/// <param name="Skip">The "스킵 추천!" pill under the card row, or null.</param>
/// <param name="CardOutlines">Debug outlines of the recognised cards (empty unless asked for).</param>
/// <param name="PriceOutlines">Debug outlines of the shop price labels (empty unless asked for).</param>
public sealed record BadgeFrame(
    IReadOnlyList<PlacedBadge> Badges,
    PlacedBadge? Skip,
    IReadOnlyList<DipRect> CardOutlines,
    IReadOnlyList<DipRect> PriceOutlines)
{
    /// <summary>One per input card: its reason chips, or null when it has none.</summary>
    public IReadOnlyList<PlacedChips?> Chips { get; init; } = [];

    /// <summary>One per input card: its ribbon, or null.</summary>
    public IReadOnlyList<PlacedBadge?> Ribbons { get; init; } = [];

    /// <summary>The "제거 추천: ..." marker over the shop's card-removal service, or null.</summary>
    public PlacedBadge? Removal { get; init; }
}

/// <summary>
/// Badge placement math (pure functions, unit-tested and shared with VisionBench's preview renderer).
///
/// <para>Every mark is sized from something the GAME draws (a card's width, the removal coin's width), not from the
/// Windows DPI, so it keeps the same size relative to the game at FHD and 4K. The DPI only converts physical pixels
/// to WPF's DIPs at the end (<see cref="ToLayerDips"/>).</para>
/// </summary>
public static class BadgeLayout
{
    /// <summary>Badge design size in DIPs before scaling.</summary>
    public const double BaseWidth = 170;

    public const double BaseHeight = 40;

    /// <summary>"추천!" / "구매!" ribbon design size (scaled like the badge it sits on).</summary>
    public const double RibbonWidth = 84, RibbonHeight = 30;

    /// <summary>One reason chip row ("광역 부족 +24") and the gap between rows, design units.</summary>
    public const double ChipHeight = 22, ChipGap = 3;

    /// <summary>Chips may use this fraction of the card width: never wider than the card, so the stacks of two
    /// neighbouring cards cannot touch.</summary>
    public const double ChipWidthOfCard = 0.96;

    /// <summary>The skip pill: wider than a badge because it also carries the top reason.</summary>
    public const double SkipWidth = 300, SkipHeight = 40;

    /// <summary>The card-removal marker design size; scale 1 when the coin is 170 DIPs wide (FHD at 100 %).</summary>
    public const double RemovalWidth = 230, RemovalHeight = 34, RemovalCoinDesignWidth = 170;

    /// <summary>
    /// Bottom of the reward screen's "넘기기" (skip) button in the game's 1920x1080 reference space
    /// (card_reward_selection_screen.tscn: RewardAlternatives at screen centre + 344..417 → y 884..957).
    /// The skip pill goes just below it, so it never covers the button or the cards' reason chips.
    /// </summary>
    public const double RewardSkipButtonBottomRef = 957;

    private const double Margin = 2;

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
    /// A y coordinate of the game's 1920x1080 reference space in layer DIPs. The game scales its UI uniformly by
    /// <c>s = min(W/1920, H/1080)</c> and centres it (Godot "canvas_items" + "expand"), the same rule as Vision's
    /// RefTransform; repeated here because this file must not depend on the Vision project.
    /// </summary>
    public static double RefYToLayerDips(double refY, PxBox layer, double dpiScale)
    {
        double s = Math.Min(layer.Width / 1920.0, layer.Height / 1080.0);
        double offsetY = (layer.Height - 1080 * s) / 2;
        return (offsetY + refY * s) / dpiScale;
    }

    /// <summary>
    /// Top-left of a badge for a card. Preference order:
    /// 1. centred ABOVE the card (never covers the art or the title banner);
    /// 2. below the card if there is no room above (card touching the top edge);
    /// 3. inside the card's top edge as a last resort.
    /// Always clamped horizontally to stay on screen.
    /// </summary>
    public static (double Left, double Top) Place(DipRect card, double width, double height, double areaWidth, double areaHeight, double gap)
    {
        var left = ClampX(card.X + (card.Width - width) / 2, width, areaWidth);

        var top = card.Y - height - gap;
        if (top < Margin)
        {
            top = card.Bottom + gap;
            if (top + height > areaHeight - Margin) top = Math.Max(Margin, card.Y + Margin);
        }
        return (left, top);
    }

    /// <summary>
    /// Where the ribbon goes on a badge. First choice: on the badge's right end, sticking out ABOVE it by half its
    /// height, like a tag pinned to the badge (it replaces the badge's small crown, and leaves the tier letter and
    /// score readable). If that would leave the screen or touch something we must not hide (another card, its badge,
    /// a price label), it shrinks back fully INSIDE the badge's right end instead.
    /// </summary>
    public static DipRect PlaceRibbon(DipRect badge, double scale, IEnumerable<DipRect> avoid, double areaWidth, double areaHeight)
    {
        double w = RibbonWidth * scale, h = RibbonHeight * scale;
        var tag = new DipRect(badge.Right - w + 4 * scale, badge.Y - h / 2, w, h);
        if (tag.InsideArea(areaWidth, areaHeight, Margin) && !avoid.Any(tag.Intersects)) return tag;
        return new DipRect(badge.Right - w - 3 * scale, badge.Y + (badge.Height - h) / 2, w, h);
    }

    /// <summary>
    /// The box for a card's reason chips: centred just BELOW the card (the bottom of the card is its description,
    /// the least important part; the name and art at the top stay clear). Falls back to the inside of the card's
    /// bottom edge when below would leave the screen or run into another card, badge or price label (a two-row event
    /// grid, a card hovered near the bottom).
    /// </summary>
    public static DipRect PlaceChips(DipRect card, int count, double scale, IEnumerable<DipRect> avoid, double areaWidth, double areaHeight)
    {
        double w = card.Width * ChipWidthOfCard;
        double h = count * ChipHeight * scale + Math.Max(0, count - 1) * ChipGap * scale;
        double left = ClampX(card.X + (card.Width - w) / 2, w, areaWidth);
        var below = new DipRect(left, card.Bottom + 7 * scale, w, h);
        if (below.InsideArea(areaWidth, areaHeight, Margin) && !avoid.Any(below.Intersects)) return below;
        return new DipRect(left, Math.Max(Margin, card.Bottom - h - 6 * scale), w, h);
    }

    /// <summary>
    /// The "제거 추천" marker for the shop's card-removal coin: centred above the coin with a gap that leaves room for
    /// the coin growing ~20 % while hovered; below the coin's price label if there is no room above.
    /// </summary>
    public static PlacedBadge PlaceRemoval(DipRect coin, double multiplier, double areaWidth, double areaHeight)
    {
        double scale = Math.Clamp(coin.Width / RemovalCoinDesignWidth, 0.5, 3.0) * multiplier;
        double w = RemovalWidth * scale, h = RemovalHeight * scale;
        double left = ClampX(coin.X + (coin.Width - w) / 2, w, areaWidth);
        double top = coin.Y - h - (0.15 * coin.Height + 4 * scale);
        if (top < Margin) top = Math.Min(coin.Bottom + 0.55 * coin.Height, areaHeight - h - Margin);
        return new PlacedBadge(new DipRect(left, top, w, h), scale);
    }

    private static double ClampX(double left, double width, double areaWidth)
        => Math.Clamp(left, Margin, Math.Max(Margin, areaWidth - width - Margin));

    /// <summary>
    /// Lays out one frame: a badge per card (scaled from the card width, times the user's UI-scale multiplier), the
    /// optional skip pill under the row, and, for the debug view, the card and price outlines.
    /// </summary>
    /// <param name="cards">Full card bounds, physical screen pixels (same space as <paramref name="layer"/>).</param>
    /// <param name="prices">Price-label bounds per card (null entries: none / not a shop); may be shorter than cards.</param>
    /// <param name="layer">The badge layer = the game's client area, physical screen pixels.</param>
    /// <param name="dpiScale">Monitor DPI / 96 (1.5 at 150 %).</param>
    /// <param name="multiplier">User UI scale relative to auto (1 = auto).</param>
    /// <param name="skip">Add the "스킵 추천!" pill.</param>
    /// <param name="outlines">Return the card/price outlines (debug view).</param>
    /// <param name="marks">Per card: reason chips and ribbon (null or shorter: nothing extra).</param>
    /// <param name="removal">The shop's card-removal coin, physical screen pixels, when its marker should be shown.</param>
    public static BadgeFrame Compute(IReadOnlyList<PxBox> cards, IReadOnlyList<PxBox?> prices, PxBox layer, double dpiScale,
        double multiplier, bool skip, bool outlines, IReadOnlyList<CardMarks>? marks = null, PxBox? removal = null)
    {
        var areaWidth = layer.Width / dpiScale;
        var areaHeight = layer.Height / dpiScale;
        var cardRects = cards.Select(c => ToLayerDips(c, layer, dpiScale)).ToArray();
        var priceRects = cards.Select((_, i) => i < prices.Count && prices[i] is { } p ? ToLayerDips(p, layer, dpiScale) : (DipRect?)null).ToArray();
        CardMarks MarksOf(int i) => marks is not null && i < marks.Count ? marks[i] : default;

        var badges = new List<PlacedBadge>(cards.Count);
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cardRects[i];
            var scale = ScaleFor(card.Width) * multiplier;
            double w = BaseWidth * scale, h = BaseHeight * scale;
            var (left, top) = Place(card, w, h, areaWidth, areaHeight, gap: 6 * scale);
            badges.Add(new PlacedBadge(new DipRect(left, top, w, h), scale));
        }

        // Things a card's extra marks must never cover: every OTHER card, badge and price label.
        IEnumerable<DipRect> OthersOf(int i) => Enumerable.Range(0, cards.Count).Where(j => j != i)
            .SelectMany(j => priceRects[j] is { } p ? [cardRects[j], badges[j].Box, p] : new[] { cardRects[j], badges[j].Box });

        var ribbons = new PlacedBadge?[cards.Count];
        for (var i = 0; i < cards.Count; i++)
        {
            if (!MarksOf(i).Ribbon) continue;
            // Its own price label too: the ribbon is about the card, the price must stay readable.
            var avoid = priceRects[i] is { } own ? OthersOf(i).Append(own) : OthersOf(i);
            ribbons[i] = new PlacedBadge(PlaceRibbon(badges[i].Box, badges[i].Scale, avoid, areaWidth, areaHeight), badges[i].Scale);
        }

        // Chips are placed card by card; each stack also avoids the stacks already placed (two-row grids).
        // All stacks use the SMALLEST badge scale of the row: a hovered card is 25 % bigger and sits lower, and chips
        // sized from it would reach the game's skip button; one size for all also reads more calmly.
        var chips = new PlacedChips?[cards.Count];
        var placedStacks = new List<DipRect>();
        var withChips = Enumerable.Range(0, cards.Count).Where(i => MarksOf(i).ChipCount > 0).ToArray();
        var chipScale = withChips.Length > 0 ? withChips.Min(i => badges[i].Scale) : 1;
        foreach (var i in withChips)
        {
            var count = Math.Min(MarksOf(i).ChipCount, 2);
            var avoid = OthersOf(i).Concat(ribbons.Where(r => r is not null).Select(r => r!.Value.Box)).Concat(placedStacks).ToArray();
            var box = PlaceChips(cardRects[i], count, chipScale, avoid, areaWidth, areaHeight);
            chips[i] = new PlacedChips(box, chipScale, count);
            placedStacks.Add(box);
        }

        PlacedBadge? skipPill = null;
        if (skip && cards.Count > 0)
        {
            double l = cardRects.Min(c => c.X), r = cardRects.Max(c => c.Right), b = cardRects.Max(c => c.Bottom);
            var scale = badges.Min(x => x.Scale);
            double w = SkipWidth * scale, h = SkipHeight * scale;
            var left = ClampX((l + r - w) / 2, w, areaWidth);
            // Under the cards, under their chips, and under the game's own skip button: whichever is lowest.
            var top = Math.Max(b + 12 * scale, RefYToLayerDips(RewardSkipButtonBottomRef, layer, dpiScale) + 8 * scale);
            foreach (var c in chips)
                if (c is { } st) top = Math.Max(top, st.Box.Bottom + 6 * scale);
            top = Math.Min(top, areaHeight - h - Margin);
            skipPill = new PlacedBadge(new DipRect(left, top, w, h), scale);
        }

        PlacedBadge? removalMarker = removal is { } coin
            ? PlaceRemoval(ToLayerDips(coin, layer, dpiScale), multiplier, areaWidth, areaHeight)
            : null;

        return new BadgeFrame(badges, skipPill,
            outlines ? cardRects : [],
            outlines ? priceRects.Where(p => p is not null).Select(p => p!.Value).ToArray() : [])
        {
            Chips = chips,
            Ribbons = ribbons,
            Removal = removalMarker,
        };
    }
}
