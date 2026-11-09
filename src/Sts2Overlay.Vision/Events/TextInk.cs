namespace Sts2Overlay.Vision.Events;

/// <summary>
/// Where a button's text is: one bit per reference pixel of its text area ("ink" = a light text pixel), plus the point
/// the button scales about. Two buttons show the same text when almost every ink pixel of each has ink within one
/// pixel at the same place on the other.
/// <para>
/// Why not the 24x4 <see cref="EventButton.TextSignature"/>: those cells average the text away, so a changed word or a
/// number moves a cell by a percent or two, which is also what a hover does to it. Here a changed glyph is a set of
/// pixels with nothing under them, while the same text lines up to the pixel once the hover scale is undone.
/// </para>
/// <para>
/// Hover: the game scales a focused option button by 1.01 about its centre (NEventOptionButton._hoverScale, 0.05 s in,
/// 0.5 s out) and a pressed one by 0.99, so two frames of the same text can differ by a scale of 0.98..1.02 about the
/// centre, up to 4 px at the ends of a line. The column's place is read a few pixels off too (a hovered or fading
/// top button moves the fill run it is found by). <see cref="Distance"/> tries those scales and offsets and keeps the
/// best.
/// </para>
/// </summary>
public sealed class TextInk
{
    /// <summary>Max channel above which a pixel is text (cream, gold and the keyword colours; the fill is below 100).</summary>
    public const int Light = 150;

    private readonly bool[] _ink;
    private readonly int[] _xs, _ys;
    private bool[]? _grown;

    /// <param name="ink">Row-major, <paramref name="width"/> x <paramref name="height"/>.</param>
    /// <param name="width">Mask width (reference px).</param>
    /// <param name="height">Mask height (reference px).</param>
    /// <param name="pivotX">The button's scale pivot in mask coordinates.</param>
    /// <param name="pivotY">Same, y.</param>
    public TextInk(bool[] ink, int width, int height, float pivotX, float pivotY)
    {
        if (ink.Length != width * height) throw new ArgumentException("ink size", nameof(ink));
        _ink = ink;
        Width = width;
        Height = height;
        PivotX = pivotX;
        PivotY = pivotY;
        var xs = new List<int>();
        var ys = new List<int>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (ink[y * width + x]) { xs.Add(x); ys.Add(y); }
        _xs = xs.ToArray();
        _ys = ys.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public float PivotX { get; }
    public float PivotY { get; }

    /// <summary>Ink pixels.</summary>
    public int Count => _xs.Length;

    /// <summary>
    /// Reads the text area <paramref name="refArea"/> of a frame: a reference pixel is ink when any frame pixel under
    /// it is brighter than <see cref="Light"/> (at 4K, any of its 2x2 pixels).
    /// </summary>
    public static TextInk Read(ReadOnlySpan<byte> px, in ScreenFrame f, RefTransform t, RectF refArea, float refPivotX, float refPivotY)
    {
        int w = (int)refArea.Width, h = (int)refArea.Height;
        var ink = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int py0 = (int)t.ToPixelY(refArea.Y + y), py1 = Math.Max(py0 + 1, (int)t.ToPixelY(refArea.Y + y + 1));
            if (py0 < 0 || py1 > f.Height) continue;
            for (int x = 0; x < w; x++)
            {
                int px0 = (int)t.ToPixelX(refArea.X + x), px1 = Math.Max(px0 + 1, (int)t.ToPixelX(refArea.X + x + 1));
                if (px0 < 0 || px1 > f.Width) continue;
                bool any = false;
                for (int yy = py0; yy < py1 && !any; yy++)
                {
                    int row = yy * f.Stride;
                    for (int xx = px0; xx < px1; xx++)
                    {
                        int p = row + xx * 4;
                        if (Math.Max(px[p], Math.Max(px[p + 1], px[p + 2])) > Light) { any = true; break; }
                    }
                }
                ink[y * w + x] = any;
            }
        }
        return new TextInk(ink, w, h, refPivotX - refArea.X, refPivotY - refArea.Y);
    }

    /// <summary>Relative scales tried between two frames: idle 1.0, hovered 1.01, pressed 0.99, and the tweens between.</summary>
    private static readonly float[] Scales = Enumerable.Range(-8, 17).Select(i => 1 + i * 0.0025f).ToArray();

    /// <summary>Offsets tried (reference px): the button's place is read to about 3 px up or down.</summary>
    private const int MaxShiftX = 2, MaxShiftY = 4;

    /// <summary>Every (scale, offset), nearest to (1, 0, 0) first.</summary>
    private static readonly (float S, int Dx, int Dy)[] Tries =
        (from s in Scales
         from dy in Enumerable.Range(-MaxShiftY, 2 * MaxShiftY + 1)
         from dx in Enumerable.Range(-MaxShiftX, 2 * MaxShiftX + 1)
         orderby MathF.Abs(s - 1) / 0.0025f + Math.Abs(dx) + Math.Abs(dy)
         select (s, dx, dy)).ToArray();

    /// <summary>
    /// At or below: the same text. Real frames of one page, hover, glow and the column's place included: Korean events
    /// at most 0.0002 (37 pairs), ancients 0.0066 (21), English 0 (4); a button whose number alone changed 0.0031.
    /// </summary>
    public const float SameMax = 0.02f;

    /// <summary>
    /// At or above: other text. Real page changes: Korean 0.25 and up, English 0.74 and up; every pair of pages of
    /// one event with as many options, rendered from the game's text: Korean 0.074 and up, English 0.13 and up.
    /// </summary>
    public const float ChangedMin = 0.04f;

    /// <summary>
    /// At or below (share of one text's ink missing on the other): that text lies inside the other, as a fading button
    /// or a dark tooltip over part of it draws it. Never a page change: in the nearest pair of the game's pages
    /// (rendered) 6.9 % (Korean) and 10.5 % (English) of the smaller text is missing on the other.
    /// </summary>
    public const float InsideMax = 0.01f;

    /// <summary>A text fading out (inside the reference) must keep at least this share of the reference's ink to say "same".</summary>
    public const float InsideMinShare = 0.25f;

    /// <summary>
    /// How much of the two texts does not line up (0 = same text, 1 = nothing in common): the ink pixels of each
    /// with no ink within one pixel on the other, over all ink pixels, at the best relative scale and offset. Both
    /// empty: 0.
    /// </summary>
    public static float Distance(TextInk a, TextInk b) => Match(a, b).Distance;

    /// <summary>
    /// What the button's text on a later frame (<paramref name="current"/>) says against the text it was matched on
    /// (<paramref name="reference"/>): the same text, other text, or too little to tell.
    /// <list type="bullet">
    /// <item><see cref="Distance"/> at most <see cref="SameMax"/>: the same text.</item>
    /// <item>The reference lies inside the current text (fading in, or was partly covered): the same text.</item>
    /// <item>The current text lies inside the reference and keeps at least <see cref="InsideMinShare"/> of its ink
    /// (fading out, or a dark tooltip over part of it): the same text. Keeping less (a blank or nearly faded button):
    /// <see cref="TextVerdict.Unsure"/>, as there is nothing left to compare.</item>
    /// <item>Otherwise <see cref="Distance"/> at least <see cref="ChangedMin"/>: other text; between: unsure.</item>
    /// </list>
    /// </summary>
    public static TextVerdict Judge(TextInk reference, TextInk current)
        => ProvablyChanged(reference, current) ? TextVerdict.Changed : JudgeExact(reference, current);

    /// <summary><see cref="Judge"/> from the full <see cref="Match"/>, without the quick "changed" proof (tests compare the two).</summary>
    internal static TextVerdict JudgeExact(TextInk reference, TextInk current)
    {
        InkMatch m = Match(reference, current);
        if (m.Distance <= SameMax) return TextVerdict.Same;
        if (m.Lost <= InsideMax && reference.Count <= current.Count) return TextVerdict.Same;
        if (m.Gained <= InsideMax)
            return current.Count > 0 && current.Count >= InsideMinShare * reference.Count ? TextVerdict.Same : TextVerdict.Unsure;
        return m.Distance >= ChangedMin ? TextVerdict.Changed : TextVerdict.Unsure;
    }

    /// <summary>
    /// True only when <see cref="JudgeExact"/> would say <see cref="TextVerdict.Changed"/>, found without counting every
    /// pixel of every try.
    /// <para>
    /// Why it is needed: <see cref="Match"/> keeps the best of 765 (scale, offset) tries, and a try stops counting
    /// once it can't beat the best so far. For the same text the first try is nearly perfect, so the others stop
    /// after a few pixels (~0.1 ms). For other text even the best try misses a quarter of the ink or more, so every
    /// try counts almost every pixel: 765 × a few thousand pixels, 13–44 ms on the UI thread per page change.
    /// </para>
    /// <para>
    /// Why it gives the same answer: <see cref="JudgeExact"/> says "changed" exactly when the best try misses at least
    /// <see cref="ChangedMin"/> of all ink AND more than <see cref="InsideMax"/> of each text's ink (else one of the
    /// "inside" rules or "unsure" applies). If EVERY try misses that much, the best one does too, whichever it is.
    /// Proving that for one try only needs to count misses up to those few-percent bounds, not every pixel, so a
    /// page change costs about 1/20 of the full search. When any try comes in under a bound, nothing is proven and
    /// the full search decides, exactly as before.
    /// </para>
    /// </summary>
    private static bool ProvablyChanged(TextInk a, TextInk b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Count == 0 || b.Count == 0) return false;
        int n = a.Count + b.Count;
        // The smallest miss counts at which Match's own float shares cross the thresholds that Judge tests.
        int lostOver = FirstCount(a.Count, k => (float)k / a.Count > InsideMax);
        int gainedOver = FirstCount(b.Count, k => (float)k / b.Count > InsideMax);
        int changedAt = FirstCount(n, k => (float)k / n >= ChangedMin);
        bool[] ga = a.Grown(), gb = b.Grown();
        foreach (var (s, dx, dy) in Tries)
        {
            int ma = Misses(a, gb, b, s, dx, dy, Math.Max(lostOver, changedAt));
            if (ma < lostOver) return false;
            int mb = Misses(b, ga, a, 1 / s, -dx, -dy, Math.Max(gainedOver, changedAt - ma));
            if (mb < gainedOver || ma + mb < changedAt) return false;
        }
        return true;
    }

    /// <summary>The smallest k in 0..<paramref name="max"/> with <paramref name="over"/>(k) (monotone), or max + 1.</summary>
    private static int FirstCount(int max, Func<int, bool> over)
    {
        for (int k = 0; k <= max; k++)
            if (over(k)) return k;
        return max + 1;
    }

    /// <summary>
    /// <see cref="Distance"/> and, at the same best scale and offset, the share of <paramref name="a"/>'s ink missing on
    /// <paramref name="b"/> (<see cref="InkMatch.Lost"/>) and of <paramref name="b"/>'s missing on <paramref name="a"/>
    /// (<see cref="InkMatch.Gained"/>).
    /// </summary>
    public static InkMatch Match(TextInk a, TextInk b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return new InkMatch(1, 1, 1);
        if (a.Count + b.Count == 0) return new InkMatch(0, 0, 0);
        if (a.Count == 0) return new InkMatch(1, 0, 1);
        if (b.Count == 0) return new InkMatch(1, 1, 0);
        bool[] ga = a.Grown(), gb = b.Grown();
        // Every scale at every offset (the column's place is read to a few pixels: a hovered or fading top button
        // moves it), the likeliest first. Counting stops once a try can no longer win, so after a good first match
        // most tries end within a few hundred pixels.
        int best = int.MaxValue, lost = 0, gained = 0;
        foreach (var (s, dx, dy) in Tries)
        {
            int ma = Misses(a, gb, b, s, dx, dy, best);
            if (ma >= best) continue;
            int mb = Misses(b, ga, a, 1 / s, -dx, -dy, best - ma);
            if (ma + mb >= best) continue;
            best = ma + mb;
            lost = ma;
            gained = mb;
        }
        return new InkMatch((float)best / (a.Count + b.Count), (float)lost / a.Count, (float)gained / b.Count);
    }

    /// <summary>
    /// Ink pixels of <paramref name="from"/> that land on no (grown) ink of <paramref name="to"/> when scaled by
    /// <paramref name="s"/> about the pivots and moved by (<paramref name="dx"/>, <paramref name="dy"/>); stops counting
    /// at <paramref name="limit"/>.
    /// </summary>
    private static int Misses(TextInk from, bool[] grownTo, TextInk to, float s, int dx, int dy, int limit)
    {
        int miss = 0;
        for (int i = 0; i < from._xs.Length && miss < limit; i++)
        {
            int x = (int)MathF.Round(to.PivotX + (from._xs[i] - from.PivotX) * s) + dx;
            int y = (int)MathF.Round(to.PivotY + (from._ys[i] - from.PivotY) * s) + dy;
            if ((uint)x >= (uint)to.Width || (uint)y >= (uint)to.Height || !grownTo[y * to.Width + x]) miss++;
        }
        return miss;
    }

    /// <summary>The ink grown by one pixel in every direction (3x3), so one pixel of misalignment still matches.</summary>
    private bool[] Grown()
    {
        if (_grown is not null) return _grown;
        var g = new bool[_ink.Length];
        for (int i = 0; i < _xs.Length; i++)
            for (int dy = -1; dy <= 1; dy++)
            {
                int y = _ys[i] + dy;
                if ((uint)y >= (uint)Height) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = _xs[i] + dx;
                    if ((uint)x < (uint)Width) g[y * Width + x] = true;
                }
            }
        return _grown = g;
    }
}

/// <summary>How two inks compare: <see cref="TextInk.Distance"/>, and the share of each text's ink missing on the other.</summary>
public readonly record struct InkMatch(float Distance, float Lost, float Gained);

/// <summary>What <see cref="TextInk.Judge"/> says about two frames of one button.</summary>
public enum TextVerdict
{
    /// <summary>The same text (or one inside the other: fading, partly covered).</summary>
    Same,

    /// <summary>Neither clearly the same nor clearly other text.</summary>
    Unsure,

    /// <summary>Other text: another page.</summary>
    Changed,
}
