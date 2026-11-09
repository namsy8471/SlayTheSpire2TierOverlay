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
