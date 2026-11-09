namespace Sts2Overlay.Vision.Events;

/// <summary>How an event option button looks.</summary>
public enum EventButtonState
{
    /// <summary>Normal teal button.</summary>
    Idle,

    /// <summary>Under the mouse: brighter teal (and a glow). The button a click is about to choose.</summary>
    Hovered,

    /// <summary>Grey-brown "잠김" button: the option exists but can't be taken (the <c>_LOCKED</c> text keys).</summary>
    Locked,
}

/// <summary>One option button found on an event screen.</summary>
/// <param name="Index">0 = top button.</param>
/// <param name="Bounds">The button's visible texture, frame pixels.</param>
/// <param name="State">Idle, hovered or locked.</param>
/// <param name="TextSignature">
/// Where the button's light text pixels are (24x4 cells, fraction 0..1): a coarse summary, kept for tools and for
/// buttons without <paramref name="Ink"/>.
/// </param>
/// <param name="Ink">
/// The button's text pixel by pixel (null when not read). Two frames show the same page when the ink of every button
/// reads the same (<see cref="TextInk.Judge"/>); a new page of the same event has other text.
/// </param>
public sealed record EventButton(int Index, PixelRect Bounds, EventButtonState State, float[] TextSignature, TextInk? Ink = null);

/// <summary>The option buttons of an event screen, top to bottom.</summary>
public sealed record EventScreen(IReadOnlyList<EventButton> Buttons)
{
    /// <summary>Index of the hovered button, or -1.</summary>
    public int HoveredIndex => Buttons.FirstOrDefault(b => b.State == EventButtonState.Hovered)?.Index ?? -1;
}

/// <summary>
/// Finds the option buttons of the default event screen ("?" rooms).
/// <para>
/// Geometry from the game's scenes (<c>default_event_layout.tscn</c>, <c>event_option_button.tscn</c>), 1920x1080
/// reference units: the options are a column of 800x100 nodes at x 922..1722, node i at y = 596 + 104 i + d, whose
/// visible texture is x 930..1714, y 604 + 104 i + d .. 688 + 104 i + d. The count is "how many slots from the top
/// look like a button".
/// </para>
/// <para>
/// d is 0 while the event text fits its 280 px minimum box (most pages). The text box grows with its text
/// (<c>fit_content</c>) and the options follow it, so a long description, or the co-op shared label, pushes the whole
/// column down (Abyssal Baths in English: d = 18; Amalgamator in Korean: d = 63). <see cref="ColumnOffset"/> finds d from the
/// first button's run of fill rows: its centre, because a hovered button grows about its centre.
/// </para>
/// <para>
/// A slot is a button when two thin stripes inside the texture, one under the top border and one above the bottom
/// border (no text there), are at least 60 % button fill. The fill is a dark teal whose blue is a little above its
/// green (idle about 37,69,74; hovered 48,89,96). That small blue-over-green margin is what tells it apart from
/// the shop's teal rug (55,84,74). Locked buttons are a grey-brown (46,41,41). Each button must also end: the band
/// of the 20 px gap under it is not fill (real buttons: at most 27 %), which a field of teal, such as a waterfall
/// room's water behind the reward screen, is all over. On 1461 Korean captured frames and
/// 797 English ones this finds every event screen and nothing else (tests/Sts2Overlay.Vision.Tests/EventButtonTests.cs,
/// EnglishCaptureTests.cs).
/// </para>
/// </summary>
public static class EventButtonDetector
{
    public const float RefTop = 604, RefStep = 104, RefLeft = 930, RefWidth = 784, RefHeight = 84;

    /// <summary>The most options an event page shows (Colorful Philosophers: four other characters + leave).</summary>
    public const int MaxButtons = 6;

    // Stripes sampled for the fill test, relative to the texture top, and their x range (inside the end caps).
    private const float StripeX0 = 1000, StripeX1 = 1650;
    private const float TopStripe0 = 5, TopStripe1 = 11, BottomStripe0 = 72, BottomStripe1 = 80;
    private const float MinFill = 0.6f;
    private const int HoverGreen = 80; // mean green of the bottom stripe: idle 59..69, hovered 85..89
    private const int HoverGlow = 60;  // mean green just above the button: hovered 82..120, otherwise 1..34
    private const float Gap0 = 92, Gap1 = 100; // rows of the gap under a button (relative to its top), past a hovered one's growth

    // Text ink area (the label is node x 42..764, y 13..87, i.e. x 964..1686, texture y 5..79; with a margin for the
    // hover scale) and the node's scale pivot (400, 50): x 1322, texture y 42.
    private const float InkX0 = 954, InkX1 = 1692, InkY0 = 6, InkY1 = 80, PivotX = 1322, PivotDy = 42;

    // Text signature area and grid.
    private const float TextX0 = 950, TextX1 = 1690, TextY0 = 14, TextY1 = 70;
    private const int SigCols = 24, SigRows = 4, TextLight = 150;

    public static EventScreen? Detect(in ScreenFrame frame)
    {
        frame.Validate();
        var t = RefTransform.For(frame.Width, frame.Height);
        var px = frame.Bgra.Span;
        int d = ColumnOffset(px, frame, t);
        EventScreen? screen = DetectAt(px, frame, t, d);
        // A run that only looked like a pushed-down button: the plain column is still worth a look.
        return screen == null && d != 0 ? DetectAt(px, frame, t, 0) : screen;
    }

    // Column search: rows from just above the first slot (a hovered button reaches ~6 above it) down to the
    // lowest place a first button fits. A row is "fill" at MinFill; a run continues over text rows (>= RunKeep:
    // a dense Korean line still leaves 40 % fill, the gaps between buttons 0..3 %) and ends after RunGap thinner
    // rows. Idle runs are 84 rows high, hovered ones up to ~95.
    private const float SearchY0 = RefTop - 14, SearchY1 = 1080 - RefHeight;
    private const float RunKeep = 0.2f;
    private const int RunGap = 2, RunMin = 70, RunMax = 104;

    /// <summary>
    /// How far (reference px, >= 0) the option column sits below <see cref="RefTop"/>: from the centre of the first
    /// run of button-fill rows. 0 when that run is not button-sized or is within 4 px of the plain place (the stripes
    /// allow 5, and a hovered or fading top button moves the run's centre by up to 3).
    /// </summary>
    internal static int ColumnOffset(ReadOnlySpan<byte> px, in ScreenFrame f, RefTransform t)
    {
        int start = -1, last = -1;
        for (int y = (int)SearchY0; y < (int)SearchY1 + RunMax; y++)
        {
            float fill = RowFill(px, f, t, y);
            if (start < 0)
            {
                if (fill >= MinFill) { start = y; last = y; }
                else if (y >= SearchY1) break;
                continue;
            }
            if (fill >= RunKeep) { last = y; continue; }
            if (y - last <= RunGap) continue;
            int length = last + 1 - start;
            if (length is >= RunMin and <= RunMax)
            {
                // The coarse run samples one pixel row per reference row; on a 4K frame that leaves each edge a
                // row off, enough to put the hover glow test beside the glow. Find both edges on every pixel row.
                int p0 = (int)t.ToPixelY(start - 1), p1 = (int)t.ToPixelY(last + 2);
                int rise = p0, fall = p1;
                while (rise < p1 && RowFillAt(px, f, t, rise) < MinFill) rise++;
                while (fall > rise && RowFillAt(px, f, t, fall - 1) < RunKeep) fall--;
                float centre = ((rise + fall) / 2f - t.OffsetY) / t.Scale;
                int d = (int)MathF.Round(centre - RefHeight / 2 - RefTop);
                return d <= 4 ? 0 : d;
            }
            // The first run decides: one too thin or too tall is not a button, and looking further down could
            // only find a lower button and take it for the top one.
            return 0;
        }
        return 0;
    }

    /// <summary>Fraction of one reference row (stripe x range) that is button fill, teal or locked.</summary>
    private static float RowFill(ReadOnlySpan<byte> px, in ScreenFrame f, RefTransform t, int refY)
        => RowFillAt(px, f, t, (int)t.ToPixelY(refY + 0.5f));

    /// <summary>Mean <see cref="RowFill"/> over reference rows <paramref name="refY0"/>..<paramref name="refY1"/> (rows off the frame: none).</summary>
    private static float BandFill(ReadOnlySpan<byte> px, in ScreenFrame f, RefTransform t, float refY0, float refY1)
    {
        float sum = 0;
        int n = 0;
        for (int y = (int)refY0; y < (int)refY1; y++, n++) sum += RowFill(px, f, t, y);
        return n > 0 ? sum / n : 0;
    }

    /// <summary>Same, for one pixel row.</summary>
    private static float RowFillAt(ReadOnlySpan<byte> px, in ScreenFrame f, RefTransform t, int y)
    {
        if (y < 0 || y >= f.Height) return 0;
        int x0 = Math.Max(0, (int)t.ToPixelX(StripeX0)), x1 = Math.Min(f.Width, (int)t.ToPixelX(StripeX1));
        int step = Math.Max(1, (int)t.Scale), row = y * f.Stride, n = 0, fill = 0;
        for (int x = x0; x < x1; x += step, n++)
        {
            int p = row + x * 4;
            int bl = px[p], g = px[p + 1], rd = px[p + 2];
            if (IsTeal(rd, g, bl) || IsLocked(rd, g, bl)) fill++;
        }
        return n > 0 ? (float)fill / n : 0;
    }

    private static EventScreen? DetectAt(ReadOnlySpan<byte> px, in ScreenFrame frame, RefTransform t, int offset)
    {
        var buttons = new List<EventButton>();
        for (int i = 0; i < MaxButtons; i++)
        {
            float top = RefTop + offset + RefStep * i;
            if (t.ToPixelY(top + RefHeight) > frame.Height) break;
            var a = Stripe(px, frame, t, top + TopStripe0, top + TopStripe1);
            var b = Stripe(px, frame, t, top + BottomStripe0, top + BottomStripe1);
            EventButtonState state;
            if (a.Teal >= MinFill && b.Teal >= MinFill)
            {
                // Hovered: brighter fill AND the glow just above the top border. The fill alone fades for a few
                // frames after the mouse leaves; the glow moves with the mouse.
                state = b.TealGreen >= HoverGreen && MeanGreen(px, frame, t, top - 7, top - 2) >= HoverGlow
                    ? EventButtonState.Hovered
                    : EventButtonState.Idle;
            }
            else if (a.Locked >= MinFill && b.Locked >= MinFill) state = EventButtonState.Locked;
            else break;
            // A button ends in a gap; fill running on through it is a teal field, not an option column.
            if (BandFill(px, frame, t, top + Gap0, top + Gap1) >= MinFill) return null;

            var bounds = t.ToPixelRect(new RectF(RefLeft, top, RefWidth, RefHeight)).Intersect(frame.Width, frame.Height);
            buttons.Add(new EventButton(i, bounds, state, Signature(px, frame, t, new RectF(TextX0, top + TextY0, TextX1 - TextX0, TextY1 - TextY0)),
                TextInk.Read(px, frame, t, new RectF(InkX0, top + InkY0, InkX1 - InkX0, InkY1 - InkY0), PivotX, top + PivotDy)));
        }
        return buttons.Count > 0 ? new EventScreen(buttons) : null;
    }

    /// <summary>Mean absolute difference of two text signatures (0 = same text layout).</summary>
    public static float SignatureDistance(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 1;
        float sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    private readonly record struct StripeStats(float Teal, float Locked, int TealGreen);

    private static StripeStats Stripe(ReadOnlySpan<byte> px, in ScreenFrame f, RefTransform t, float refY0, float refY1)
    {
        var r = t.ToPixelRect(new RectF(StripeX0, refY0, StripeX1 - StripeX0, refY1 - refY0)).Intersect(f.Width, f.Height);
        int step = Math.Max(1, (int)t.Scale); // 4K: every other pixel is plenty
        int n = 0, teal = 0, locked = 0;
        long green = 0;
        for (int y = r.Y; y < r.Bottom; y += step)
        {
            int row = y * f.Stride;
            for (int x = r.X; x < r.Right; x += step)
            {
                int p = row + x * 4;
                int bl = px[p], g = px[p + 1], rd = px[p + 2];
                n++;
                if (IsTeal(rd, g, bl))
                {
                    teal++;
                    green += g;
                }
                else if (IsLocked(rd, g, bl)) locked++;
            }
        }
        if (n == 0) return default;
        return new StripeStats((float)teal / n, (float)locked / n, teal > 0 ? (int)(green / teal) : 0);
    }

    private static int MeanGreen(ReadOnlySpan<byte> px, in ScreenFrame f, RefTransform t, float refY0, float refY1)
    {
        var r = t.ToPixelRect(new RectF(StripeX0, refY0, StripeX1 - StripeX0, refY1 - refY0)).Intersect(f.Width, f.Height);
        int step = Math.Max(1, (int)t.Scale), n = 0;
        long sum = 0;
        for (int y = r.Y; y < r.Bottom; y += step)
            for (int x = r.X; x < r.Right; x += step, n++)
                sum += px[y * f.Stride + x * 4 + 1];
        return n > 0 ? (int)(sum / n) : 0;
    }

    /// <summary>Button fill, idle or hovered: teal with blue at or a little above green.</summary>
    internal static bool IsTeal(int r, int g, int b)
        => r is >= 22 and <= 64 && g - r is >= 18 and <= 58 && b - g is >= 1 and <= 22;

    /// <summary>Locked button fill: grey with a slight red cast.</summary>
    internal static bool IsLocked(int r, int g, int b)
        => r is >= 34 and <= 62 && r - g is >= 2 and <= 10 && Math.Abs(g - b) <= 4;

    /// <summary>Where the light text pixels of a button's text area are: <see cref="SigCols"/> x <see cref="SigRows"/> cells, fraction 0..1.</summary>
    internal static float[] Signature(ReadOnlySpan<byte> px, in ScreenFrame f, RefTransform t, RectF refTextArea)
    {
        var r = t.ToPixelRect(refTextArea).Intersect(f.Width, f.Height);
        var sig = new float[SigCols * SigRows];
        if (r.Width < SigCols || r.Height < SigRows) return sig;
        int step = Math.Max(1, (int)t.Scale);
        for (int cy = 0; cy < SigRows; cy++)
        {
            int y0 = r.Y + cy * r.Height / SigRows, y1 = r.Y + (cy + 1) * r.Height / SigRows;
            for (int cx = 0; cx < SigCols; cx++)
            {
                int x0 = r.X + cx * r.Width / SigCols, x1 = r.X + (cx + 1) * r.Width / SigCols;
                int n = 0, light = 0;
                for (int y = y0; y < y1; y += step)
                {
                    int row = y * f.Stride;
                    for (int x = x0; x < x1; x += step)
                    {
                        int p = row + x * 4;
                        n++;
                        if (Math.Max(px[p], Math.Max(px[p + 1], px[p + 2])) > TextLight) light++;
                    }
                }
                sig[cy * SigCols + cx] = n > 0 ? (float)light / n : 0;
            }
        }
        return sig;
    }
}
