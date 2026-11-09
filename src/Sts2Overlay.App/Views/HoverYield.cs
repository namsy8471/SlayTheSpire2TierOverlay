using RectF = Sts2Overlay.Vision.RectF;
using RefTransform = Sts2Overlay.Vision.RefTransform;

namespace Sts2Overlay.App.Views;

// Why the overlay has to step aside for the game's hover tips
// -------------------------------------------------------------
// The game draws its hover tips (keyword boxes next to a card, a relic's description, an event option's card preview)
// INSIDE its own frame: NHoverTipSet is a Godot Control added to the game's HoverTipsContainer, not a window of its own.
// Our layers are separate windows stacked ABOVE the game window, so wherever a badge and a hover tip overlap, the
// badge always wins, whatever the z-order. Raising or lowering our windows cannot fix that.
//
// So instead we watch the mouse: while it rests on something the game shows a hover tip for (a recognised card or its
// price, the removal coin, an event option, a rest-site button or upgrade-grid card, the top bar and relic row), every
// overlay layer fades out. When the mouse has been off those spots for a short moment, they fade back in.

/// <summary>A layer that steps aside while the mouse is on something the game shows a hover tip for.</summary>
internal interface IHoverYield
{
    /// <summary>Shown and has something drawn (an empty layer has nothing to hide and no zones).</summary>
    bool IsShowingMarks { get; }

    /// <summary>Where the game shows a hover tip under this layer's marks, physical screen pixels.</summary>
    IEnumerable<PxBox> HoverZones();

    /// <summary>Fades the layer out (true) or back in (false). Never changes click-through or z-order.</summary>
    void SetHoverHidden(bool hidden);
}

/// <summary>Hover-zone geometry (pure, unit-tested).</summary>
public static class HoverGeometry
{
    /// <summary>
    /// The game's top bar (HP, gold, potions, map/deck buttons; design y 0..90) and the relic row under it
    /// (y 90..160, see MapScrollEstimator's masks), plus a little room for a second relic row. Relic and potion tips
    /// drop down from here over the screen below, where the shop's top-row badges sit.
    /// </summary>
    public const float TopStripRefHeight = 170;

    /// <summary>
    /// How much a zone grows on each side, as a fraction of its size. Cards and buttons scale up ~10 % when hovered,
    /// so the game still shows the tip when the mouse is just past the resting outline.
    /// </summary>
    public const double Grow = 0.06;

    /// <summary>
    /// How much bigger a hovered card is than the resting one the recogniser usually saw: the reward screen and card
    /// grids snap from 0.8 to 1.0 (+25 %), the shop from 0.65 to 0.8 (+23 %) the moment the mouse touches the card
    /// (NCardHolder / NMerchantSlot), and the game shows the card's tips for the whole enlarged card. A zone of the
    /// resting size missed the outer ring, so pointing near a card's edge showed the tip under our badges.
    /// </summary>
    public const double CardHoverZoom = 1.25;

    /// <summary><paramref name="b"/> scaled by <paramref name="factor"/> about its centre.</summary>
    public static PxBox Zoom(PxBox b, double factor)
    {
        if (b.Width <= 0 || b.Height <= 0) return b;
        double w = b.Width * factor, h = b.Height * factor;
        double cx = b.X + b.Width / 2.0, cy = b.Y + b.Height / 2.0;
        return new PxBox((int)Math.Floor(cx - w / 2), (int)Math.Floor(cy - h / 2), (int)Math.Ceiling(w), (int)Math.Ceiling(h));
    }

    /// <summary>The top strip of the game's 1920x1080 design space, in physical screen pixels over <paramref name="layer"/>.</summary>
    public static PxBox TopStrip(PxBox layer) => FromRef(new RectF(0, 0, RefTransform.RefWidth, TopStripRefHeight), layer);

    /// <summary>A 1920x1080 design-px rectangle (map/rest marks) in physical screen pixels over <paramref name="layer"/>.</summary>
    public static PxBox FromRef(RectF r, PxBox layer)
    {
        var t = RefTransform.For(layer.Width, layer.Height);
        var px = t.ToPixels(r);
        return new PxBox(layer.X + (int)Math.Floor(px.X), layer.Y + (int)Math.Floor(px.Y),
            (int)Math.Ceiling(px.Width), (int)Math.Ceiling(px.Height));
    }

    /// <summary>True when (<paramref name="x"/>, <paramref name="y"/>) is inside any zone grown by <paramref name="grow"/>.</summary>
    public static bool Contains(IEnumerable<PxBox> zones, int x, int y, double grow = Grow)
    {
        foreach (var z in zones)
        {
            if (z.Width <= 0 || z.Height <= 0) continue;
            double gx = z.Width * grow, gy = z.Height * grow;
            if (x >= z.X - gx && x < z.Right + gx && y >= z.Y - gy && y < z.Bottom + gy) return true;
        }
        return false;
    }
}

/// <summary>
/// When the layers hide and come back (pure, unit-tested).
/// <list type="bullet">
/// <item>Mouse enters a zone: hide at once. The game shows its tip right away, so waiting would leave it covered.</item>
/// <item>Mouse leaves: come back only after <see cref="ShowDelayMs"/> outside every zone. Moving from one card to the
/// next crosses a short gap, and the badges should not flash in between.</item>
/// </list>
/// </summary>
public sealed class HoverYieldState
{
    public const int DefaultShowDelayMs = 300;

    private long? _leftAt;

    public HoverYieldState(int showDelayMs = DefaultShowDelayMs) => ShowDelayMs = showDelayMs;

    public int ShowDelayMs { get; }

    public bool Hidden { get; private set; }

    /// <summary>Feeds one mouse sample. Returns true when <see cref="Hidden"/> changed.</summary>
    /// <param name="overZone">The mouse is on a hover-tip zone of a visible layer.</param>
    /// <param name="nowMs">A monotonic clock in milliseconds (Environment.TickCount64).</param>
    public bool Update(bool overZone, long nowMs)
    {
        if (overZone)
        {
            _leftAt = null;
            if (Hidden) return false;
            Hidden = true;
            return true;
        }
        if (!Hidden) return false;
        _leftAt ??= nowMs;
        if (nowMs - _leftAt.Value < ShowDelayMs) return false;
        _leftAt = null;
        Hidden = false;
        return true;
    }

    /// <summary>Back to shown (the overlay was hidden or the game left the foreground).</summary>
    public bool Reset()
    {
        _leftAt = null;
        if (!Hidden) return false;
        Hidden = false;
        return true;
    }
}

/// <summary>
/// The last hover zones seen, kept for a moment after the marks vanish (pure, unit-tested).
/// <para>Hovering a card changes the frame the recogniser reads (the card grows, its tips cover the neighbours), so a
/// recognition can come back empty for a beat and the badges vanish with their zones. Without a memory the overlay
/// then counted the mouse as "off every zone", came back 300 ms later, and the next good recognition drew the badges
/// at full strength over the tip again: the overlay kept popping back in front while the player read the card.</para>
/// </summary>
public sealed class HoverZoneMemory
{
    public const int DefaultHoldMs = 1500;

    private PxBox[] _zones = [];
    private long _seenAt;

    public HoverZoneMemory(int holdMs = DefaultHoldMs) => HoldMs = holdMs;

    public int HoldMs { get; }

    /// <summary>
    /// The zones to test the mouse against: <paramref name="current"/> when there are any (and remembered), else the
    /// last non-empty set while it is younger than <see cref="HoldMs"/>.
    /// </summary>
    public IReadOnlyList<PxBox> Zones(IReadOnlyList<PxBox> current, long nowMs)
    {
        if (current.Count > 0)
        {
            _zones = current as PxBox[] ?? current.ToArray();
            _seenAt = nowMs;
            return current;
        }
        return nowMs - _seenAt <= HoldMs ? _zones : [];
    }

    /// <summary>Forget (the overlay was hidden or the game left the foreground).</summary>
    public void Clear() => _zones = [];
}
