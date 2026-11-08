namespace Sts2Overlay.Vision.Events;

/// <summary>
/// Finds the relic option buttons of an ancient's screen (Neow and the act-start ancients), which use their own layout.
/// <para>
/// Geometry from the game's scenes (<c>ancient_event_layout.tscn</c>, <c>ancient_event_option_button.tscn</c>,
/// NAncientEventLayout.SetDialogueLineAndAnimate), 1920x1080 reference units: the options are a column of 1000x100
/// buttons at x 460..1460 stacked with a −4 gap (step 96) whose bottom is pinned at y 1042, so with n options button i
/// spans y <c>1038 − 96n + 96i</c> .. +100 — the lowest button always starts at y 942 and the column grows upward.
/// Each button holds the relic icon (x 482..542) and, from x 552, a gold title line over a cream description line.
/// </para>
/// <para>
/// The button itself is a see-through dark panel tinted per ancient (<c>ButtonColor</c>, alpha 0.35..0.8), so over a
/// dark painting it barely shows; the text is what always shows. A slot is an option when its title band has gold text
/// that starts at the text column (x 546..557: nothing gold just left of it, not a line that starts further in like
/// the dialogue bubble above the column) and the rest of the band is quiet: at most 12 % bright and 7 % strongly
/// coloured pixels. Card hands, card rewards and tooltips that happen to have gold at that x are busy (13.5 %+ saturated
/// on every capture), which is what the second test is for. The column is counted from the bottom up until a slot fails.
/// The single proceed button after the choice has no gold title and is not reported (nothing to rank).
/// </para>
/// <para>
/// Hovered: the cyan outline ring (StsColors.blueGlow, additive) along the top edge. A ring fades for a few frames after
/// the mouse leaves, so the strongest ring is the hovered one.
/// </para>
/// <para>
/// Checked on 2,538 real captures (2026-11-08/09) holding 24 ancient option frames of Orobas, Nonupeipe, Tanx, Darv and
/// Vakuu: every one gives its 3 buttons and hovered button, and nothing else gives any (dialogue, proceed button, the
/// column sliding in, card overlays, combat, rewards, shops: none) — tests/Sts2Overlay.Vision.Tests/AncientButtonTests.cs.
/// Neow, Pael and Tezcatara use the same scene and text colours but have no capture yet.
/// </para>
/// </summary>
public static class AncientButtonDetector
{
    public const float RefLeft = 460, RefWidth = 1000, RefHeight = 100, RefStep = 96, RefLowestTop = 942;

    /// <summary>The most options an ancient page shows (three relics; a fourth would still fit).</summary>
    public const int MaxButtons = 4;

    // Title band (relative to the button top) scanned for gold, from just right of the relic icon.
    private const float TitleX0 = 544, TitleX1 = 1440, TitleY0 = 18, TitleY1 = 46;
    // The title's first gold column (measured 549..554, hovered buttons are scaled 1.01 about the centre).
    private const float TitleStart0 = 546, TitleStart1 = 557;
    private const float MinTitleWidth = 20; // reference px of columns holding gold

    // Clutter band: the whole text area. Measured on the real option slots: bright ≤ 7.6 %, saturated ≤ 2.6 %;
    // the busy frames that pass the title test: saturated ≥ 13.5 %.
    private const float BandX0 = 552, BandX1 = 1420, BandY0 = 10, BandY1 = 90;
    private const int Bright = 150, Saturated = 90;
    private const float MaxBright = 0.12f, MaxSaturated = 0.07f;

    // Text signature area (HBox x 22..964 minus the 60px relic icon and spacer; y 13..87).
    private const float TextX0 = 552, TextX1 = 1420, TextY0 = 13, TextY1 = 87;

    // Hover: an additive cyan outline ring runs along the top edge, about 5 px down, against the panel 17 px down.
    private const float RingY0 = 3, RingY1 = 10, RingFillY = 17, RingX0 = 560, RingX1 = 1360;
    private const int RingCyan = 40;
    private const float RingShare = 0.8f;

    /// <summary>The top of button <paramref name="index"/> of <paramref name="count"/> (reference y).</summary>
    public static float SlotTop(int count, int index) => RefLowestTop - RefStep * (count - 1 - index);

    public static EventScreen? Detect(in ScreenFrame frame)
    {
        frame.Validate();
        var t = RefTransform.For(frame.Width, frame.Height);
        var px = frame.Bgra.Span;

        // Count from the lowest button up: the column is pinned at the bottom.
        int count = 0;
        while (count < MaxButtons && IsOption(px, frame, t, RefLowestTop - RefStep * count)) count++;
        if (count == 0) return null;

        int hovered = -1;
        float best = float.MinValue;
        for (int i = 0; i < count; i++)
            if (Ring(px, frame, t, SlotTop(count, i)) is { } strength && strength > best)
            {
                best = strength;
                hovered = i;
            }

        var buttons = new List<EventButton>(count);
        for (int i = 0; i < count; i++)
        {
            float top = SlotTop(count, i);
            var bounds = t.ToPixelRect(new RectF(RefLeft, top, RefWidth, RefHeight)).Intersect(frame.Width, frame.Height);
            var sig = EventButtonDetector.Signature(px, frame, t, new RectF(TextX0, top + TextY0, TextX1 - TextX0, TextY1 - TextY0));
            buttons.Add(new EventButton(i, bounds, i == hovered ? EventButtonState.Hovered : EventButtonState.Idle, sig));
        }
        return new EventScreen(buttons);
    }

    private static bool IsOption(ReadOnlySpan<byte> px, in ScreenFrame f, RefTransform t, float top)
    {
        if (top < 0 || t.ToPixelY(top + RefHeight) > f.Height + 1) return false;
        int step = Math.Max(1, (int)t.Scale);

        // Gold title starting at the text column.
        var title = t.ToPixelRect(new RectF(TitleX0, top + TitleY0, TitleX1 - TitleX0, TitleY1 - TitleY0)).Intersect(f.Width, f.Height);
        int first = -1, goldCols = 0;
        for (int x = title.X; x < title.Right; x += step)
        {
            for (int y = title.Y; y < title.Bottom; y += step)
            {
                int p = y * f.Stride + x * 4;
                if (!IsGold(px[p + 2], px[p + 1], px[p])) continue;
                if (first < 0) first = x;
                goldCols++;
                break;
            }
        }
        if (first < 0) return false;
        float firstRef = (first - t.OffsetX) / t.Scale;
        if (firstRef < TitleStart0 || firstRef > TitleStart1 || goldCols * step / t.Scale < MinTitleWidth) return false;

        // A quiet text area: dark panel and text, not a card or a picture.
        var band = t.ToPixelRect(new RectF(BandX0, top + BandY0, BandX1 - BandX0, BandY1 - BandY0)).Intersect(f.Width, f.Height);
        int n = 0, bright = 0, saturated = 0;
        for (int y = band.Y; y < band.Bottom; y += step)
        {
            int row = y * f.Stride;
            for (int x = band.X; x < band.Right; x += step, n++)
            {
                int p = row + x * 4;
                int b = px[p], g = px[p + 1], r = px[p + 2];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max >= Bright) bright++;
                if (max - min >= Saturated) saturated++;
            }
        }
        return n > 0 && bright <= MaxBright * n && saturated <= MaxSaturated * n;
    }

    /// <summary>Title gold (StsColors.gold and its anti-aliased edge).</summary>
    internal static bool IsGold(int r, int g, int b) => r > 190 && g is > 140 and < 225 && b < 150 && r - b > 80;

    /// <summary>
    /// The hover outline: a cyan line along the top edge of the button across its width (null: none). The value is
    /// the line's mean cyan over the panel below it.
    /// </summary>
    private static float? Ring(ReadOnlySpan<byte> px, in ScreenFrame f, RefTransform t, float top)
    {
        var ring = t.ToPixelRect(new RectF(RingX0, top + RingY0, RingX1 - RingX0, RingY1 - RingY0)).Intersect(f.Width, f.Height);
        int fillY = Math.Min(f.Height - 1, (int)t.ToPixelY(top + RingFillY));
        int step = Math.Max(1, (int)t.Scale), cols = 0, cyan = 0;
        long sum = 0;
        static int Cyan(ReadOnlySpan<byte> px, int p) => Math.Min(px[p], px[p + 1]) - px[p + 2]; // min(blue, green) over red
        for (int x = ring.X; x < ring.Right; x += step)
        {
            cols++;
            int best = int.MinValue;
            for (int y = ring.Y; y < ring.Bottom; y++) best = Math.Max(best, Cyan(px, y * f.Stride + x * 4));
            // Against the panel just below the line: a cyan background alone is not a ring.
            int d = best - Cyan(px, fillY * f.Stride + x * 4);
            sum += d;
            if (d >= RingCyan) cyan++;
        }
        return cols > 0 && cyan >= RingShare * cols ? (float)sum / cols : null;
    }
}
