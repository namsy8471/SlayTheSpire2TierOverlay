namespace Sts2Overlay.Vision;

/// <summary>
/// Colour of the price label. NMerchantCard.UpdateVisual: red (#FF5555) when the player cannot afford the card,
/// otherwise green (#7FFF00) for the sale card and cream (#FFF6E2) for the rest.
/// </summary>
public enum PriceTint
{
    Unknown,
    Cream,
    Red,
    Green,
}

/// <summary>What was read under one shop card.</summary>
/// <param name="Value">The price, or null when the digits were unclear or the number is impossible for this slot.</param>
/// <param name="Text">The digits as read (even when rejected), e.g. "174".</param>
/// <param name="Tint">Label colour: <see cref="PriceTint.Red"/> = the game says "not enough gold".</param>
/// <param name="MinScore">Worst digit NCC against its template (1 = identical).</param>
/// <param name="MinMargin">Smallest gap between a digit's best and second-best template.</param>
/// <param name="Plausible">The number fits the game's price rules (<see cref="ShopPriceRules"/>).</param>
/// <param name="Bounds">The digits' bounding box in frame pixels.</param>
public sealed record PriceReading(int? Value, string Text, PriceTint Tint, float MinScore, float MinMargin, bool Plausible, PixelRect Bounds)
{
    /// <summary>The template set (<see cref="PriceGlyphs.Name"/>) that matched the digits best.</summary>
    public string Font { get; init; } = "";

    /// <summary>How much better the chosen set's worst digit matched than that of a set reading other digits (1 = none did).</summary>
    public float FontGap { get; init; } = 1;

    /// <summary>The label is red: the game itself says the player cannot pay for it right now.</summary>
    public bool Unaffordable => Tint == PriceTint.Red;

    /// <summary>The label is green: the merchant's sale card (half price). Only visible while it is affordable.</summary>
    public bool OnSale => Tint == PriceTint.Green;

    public override string ToString() => $"{Text}{(Value == null ? "(rejected)" : "")} {Tint} s={MinScore:F2} m={MinMargin:F2}";
}

/// <summary>Thresholds of <see cref="ShopPriceReader"/>; defaults calibrated with <c>VisionBench price</c>.</summary>
public sealed record PriceReaderOptions
{
    /// <summary>Every digit must match its template at least this well.</summary>
    public float MinDigitScore { get; init; } = 0.60f;
    /// <summary>... and beat the second-best digit by this much.</summary>
    public float MinDigitMargin { get; init; } = 0.04f;
    /// <summary>
    /// When two template sets (fonts) read DIFFERENT digits, the better one's worst digit must beat the other's by
    /// this much; otherwise the label is unclear (calibrated with <c>TitleBench digits</c> and the price bench).
    /// </summary>
    public float MinFontGap { get; init; } = 0.10f;
    /// <summary>Template sets to try (one per label font); null = every committed one (<see cref="PriceGlyphs.All"/>).</summary>
    public IReadOnlyList<PriceGlyphs>? Glyphs { get; init; }
    /// <summary>Relic price factors allowed by the plausibility check (null = all known ones).</summary>
    public IReadOnlyList<double>? PriceFactors { get; init; }
}

/// <summary>
/// Reads the gold price under a shop card: a tiny OCR for one known font.
///
/// <para>Pipeline (all in a grid of ~1 reference pixel per cell, so 1080p and 4K look the same):</para>
/// <list type="number">
/// <item><b>Colour mask.</b> Each cell gets the fraction of its pixels that have one of the three label colours
/// (cream / red / green). The coin is gold and the rug teal, so neither passes; the dark outline around each
/// digit separates neighbouring digits.</item>
/// <item><b>Connected components</b> of the mask; pieces of one glyph that overlap horizontally are merged.</item>
/// <item><b>Digit row.</b> Components about one digit high, on a common baseline and close together.</item>
/// <item><b>Classify</b> each digit by NCC against 10 templates (<see cref="PriceGlyphs"/>), once per font (the label
/// font depends on the game language); the set whose worst digit matches best wins.</item>
/// <item><b>Check</b> the number against the game's price rules (<see cref="ShopPriceRules"/>).</item>
/// </list>
/// Thread-safe: all scratch memory is per call.
/// </summary>
public sealed class ShopPriceReader
{
    /// <summary>The label box under the card (merchant_card.tscn "Cost": y 220..278 in card units), padded a bit.</summary>
    public static readonly RectF LabelBox = new(-150, 212, 300, 76);

    /// <summary>Height of a digit (font size 39; GyeonggiBatang and Kreon Bold figures are equally tall) in card units; measured on the captures.</summary>
    public const float DigitHeight = 26.6f;

    /// <summary>Vertical centre of the digits in card units (the label is vertically centred in its 220..278 box).</summary>
    public const float DigitCenterY = 249f;

    /// <summary>
    /// Horizontal centre of the digit block in card units. The coin and the digits are centred together
    /// (HBoxContainer alignment = centre), so the digit block's centre is half the coin's width right of the card
    /// centre whatever the number of digits (measured: 31-32 for 2 and 3 digits). A digit hidden under the
    /// merchant's hand shifts the visible block by half a digit (~10 units), which this catches.
    /// </summary>
    public const float DigitBlockCenterX = 31.5f;

    /// <summary>Allowed deviation from <see cref="DigitBlockCenterX"/>, card units.</summary>
    public const float DigitBlockTolerance = 6f;

    /// <summary>Coin-gold fraction just left of the first digit that counts as "the coin is there" (measured: min 0.20, median 0.38).</summary>
    public const float MinCoinFraction = 0.10f;

    /// <summary>Most bright non-label coverage allowed around the digits before the label counts as partly covered.</summary>
    public const float MaxOccluderFraction = 0.05f;

    /// <summary>Slack around <see cref="PriceGlyphs.Widths"/>: one grid cell at the smallest label size (1/17).</summary>
    public const float WidthTolerance = 0.06f;

    private readonly IReadOnlyList<PriceGlyphs> _sets;

    public PriceReaderOptions Options { get; }

    /// <param name="glyphs">One template set to use; null = <see cref="PriceReaderOptions.Glyphs"/>.</param>
    /// <param name="options">Thresholds; null = the calibrated defaults.</param>
    public ShopPriceReader(PriceGlyphs? glyphs = null, PriceReaderOptions? options = null)
    {
        Options = options ?? new PriceReaderOptions();
        _sets = glyphs != null ? [glyphs] : Options.Glyphs ?? PriceGlyphs.All;
    }

    private sealed record Digits(PriceGlyphs Set, string Text, float MinScore, float MinMargin, bool WidthsOk);

    /// <summary>The row's digits read with one template set; null when a glyph is blank.</summary>
    private static Digits? Classify(PriceGlyphs set, DigitRow row)
    {
        var text = new char[row.Glyphs.Count];
        float minScore = 1, minMargin = 1;
        bool widthsOk = true;
        for (int i = 0; i < row.Glyphs.Count; i++)
        {
            (int d, float s, float m) = set.Classify(row.Glyphs[i]);
            if (d < 0) return null;
            text[i] = (char)('0' + d);
            minScore = Math.Min(minScore, s);
            minMargin = Math.Min(minMargin, m);
            (float lo, float hi) = set.Widths[d];
            if (i < row.Widths.Count && (row.Widths[i] < lo - WidthTolerance || row.Widths[i] > hi + WidthTolerance)) widthsOk = false;
        }
        return new Digits(set, new string(text), minScore, minMargin, widthsOk);
    }

    /// <summary>
    /// Reads the price of the shop card centred at (<paramref name="cx"/>, <paramref name="cy"/>) (reference units)
    /// drawn at node <paramref name="scale"/>. Null when no digit row was found (sold slot, covered label).
    /// The catalog <paramref name="rarity"/> of the recognised card, if known, tightens the plausibility check.
    /// </summary>
    public PriceReading? Read(in ScreenFrame frame, float cx, float cy, float scale, CardSlotKind kind, string? rarity = null)
    {
        IReadOnlyList<double>? factors = Options.PriceFactors;
        return ReadAt(frame, cx, cy, scale, v => ShopPriceRules.IsPlausible(v, kind, rarity, factors));
    }

    /// <summary>
    /// Card-unit y of the relic label's centre relative to a card-sized frame: merchant_relic.tscn "Cost" spans
    /// y 52..106 below the relic node's origin (centre 79), a card's label 220..278 below the card centre (249).
    /// The label itself (font, coin, HBox centring, colours) is the same scene, so the same reader works once the
    /// box is moved up by 249 − 79 = 170 node units.
    /// </summary>
    public const float RelicLabelShift = 249f - 79f;

    /// <summary>
    /// Reads the price under a merchant relic whose node origin is (<paramref name="ox"/>, <paramref name="oy"/>)
    /// (reference units) at node <paramref name="scale"/>. <paramref name="rarity"/> (catalog: Common, Uncommon, Rare,
    /// Shop), when known, tightens the plausibility check (<see cref="ShopPriceRules.IsPlausibleRelic"/>).
    /// </summary>
    public PriceReading? ReadRelic(in ScreenFrame frame, float ox, float oy, float scale, string? rarity = null)
    {
        IReadOnlyList<double>? factors = Options.PriceFactors;
        return ReadAt(frame, ox, oy - RelicLabelShift * scale, scale, v => ShopPriceRules.IsPlausibleRelic(v, rarity, factors));
    }

    private PriceReading? ReadAt(in ScreenFrame frame, float cx, float cy, float scale, Func<int, bool> isPlausible)
    {
        DigitRow? row = Segment(frame, cx, cy, scale);
        if (row == null) return null;
        // Every font's reading. A font whose digit widths do not fit is the wrong font; among the rest, the one whose
        // WORST digit fits best wins (a wrong font has at least one poor digit).
        var readings = new List<Digits>(_sets.Count);
        foreach (PriceGlyphs set in _sets)
            if (Classify(set, row) is { } r) readings.Add(r);
        if (readings.Count == 0) return null;
        Digits best = readings.OrderByDescending(r => r.WidthsOk).ThenByDescending(r => r.MinScore).First();
        // Another font that fits as well and reads a DIFFERENT number about equally well: do not guess.
        Digits? rival = readings.Where(r => r != best && r.Text != best.Text && r.WidthsOk == best.WidthsOk)
            .OrderByDescending(r => r.MinScore).FirstOrDefault();
        float fontGap = rival == null ? 1 : best.MinScore - rival.MinScore;
        bool fontClear = fontGap >= Options.MinFontGap;
        (string digits, float minScore, float minMargin, bool widthsOk) = (best.Text, best.MinScore, best.MinMargin, best.WidthsOk);
        int value = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        bool plausible = digits[0] != '0' && isPlausible(value);
        // Is this the WHOLE number? A digit hidden under the merchant's hand would leave a shorter, still
        // plausible number ("146" -> "14"). Two independent checks: the coin is right before the first digit,
        // and the block is centred where complete numbers are (a missing digit shifts it by half a digit).
        bool complete = row.CoinFraction >= MinCoinFraction && Math.Abs(row.CenterX - DigitBlockCenterX) <= DigitBlockTolerance
                        && row.OccluderFraction <= MaxOccluderFraction;
        bool clear = minScore >= Options.MinDigitScore && minMargin >= Options.MinDigitMargin && complete && widthsOk && fontClear;
        return new PriceReading(plausible && clear ? value : null, digits, row.Tint, minScore, minMargin, plausible, row.Bounds)
        {
            Font = best.Set.Name,
            FontGap = fontGap,
        };
    }

    // ------------------------------------------------------------------ segmentation

    /// <summary>The digits found under a card.</summary>
    /// <param name="Glyphs">Raw coverage grid per digit, left to right.</param>
    /// <param name="Tint">Majority label colour.</param>
    /// <param name="Bounds">Digits in frame pixels.</param>
    /// <param name="CenterX">Centre of the digit block relative to the card centre, card units.</param>
    /// <param name="CenterY">Same, vertically.</param>
    /// <param name="CoinFraction">Fraction of coin-gold cells just left of the first digit.</param>
    /// <param name="OccluderFraction">Fraction of bright non-label cells in a thin ring around the digits.</param>
    internal sealed record DigitRow(IReadOnlyList<float[]> Glyphs, PriceTint Tint, PixelRect Bounds, float CenterX, float CenterY,
        float CoinFraction, float OccluderFraction)
    {
        /// <summary>Width / row height of each digit's own pixels (a "1" is narrow, a "0" wide).</summary>
        public IReadOnlyList<float> Widths { get; init; } = [];
    }

    private const int Cream = 0, Red = 1, Green = 2, Gold = 3, Bright = 4, Channels = 5;

    /// <summary>Which label colour a pixel has, or -1. Ratios keep it working on anti-aliased edges.</summary>
    internal static int ClassOf(int r, int g, int b)
    {
        // cream #FFF6E2: bright and nearly neutral (the gold coin fails b >= 0.75 r, its highlights too)
        if (b >= 150 && g >= 0.85f * r && b >= 0.75f * r && r >= 160) return Cream;
        // red #FF5555: g == b. The coin's brown glyph has g >> b, the coin itself g >> b as well.
        if (r >= 150 && g <= 0.6f * r && b <= 0.6f * r && Math.Abs(g - b) <= 0.12f * r) return Red;
        // green #7FFF00 (same test as the upgrade-green title)
        if (g >= 150 && r <= 0.7f * g && b <= 0.4f * g) return Green;
        // the coin left of the digits (not text; used to check that the first digit really is the first)
        if (r >= 170 && g >= 0.55f * r && g <= 0.88f * r && b <= 0.45f * r) return Gold;
        // bright and bluish: the merchant's light-blue hand. (Anti-aliased label edges mix cream/red/green with the
        // dark outline, which keeps b <= r or g; the rug is dark teal.) A white nail already counts as cream text.
        if (Math.Max(r, Math.Max(g, b)) >= 150 && b >= r + 20 && b >= g - 10) return Bright;
        return -1;
    }

    internal static DigitRow? Segment(in ScreenFrame frame, float cx, float cy, float scale, Action<string>? trace = null)
    {
        RefTransform t = RefTransform.For(frame.Width, frame.Height);
        PixelRect pr = t.ToPixelRect(CardGeometry.Place(LabelBox, cx, cy, scale)).Intersect(frame.Width, frame.Height);
        int step = Math.Max(1, (int)MathF.Floor(t.Scale + 1e-3f));
        int gw = pr.Width / step, gh = pr.Height / step;
        if (gw < 8 || gh < 8) return null;

        // Scratch grids from the pool: at 4K the coverage grid alone is ~50k floats (a large-object-heap array), and a
        // shop frame reads up to ~20 labels. Exact-length spans keep the bounds checks of the old per-call arrays.
        int n = gw * gh;
        float[] coverArray = System.Buffers.ArrayPool<float>.Shared.Rent(n * Channels), textArray = System.Buffers.ArrayPool<float>.Shared.Rent(n);
        int[] labelArray = System.Buffers.ArrayPool<int>.Shared.Rent(n);
        try
        {
            return Segment(frame, cx, cy, scale, trace, t, pr, step, gw, gh,
                coverArray.AsSpan(0, n * Channels), textArray.AsSpan(0, n), labelArray.AsSpan(0, n));
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(coverArray);
            System.Buffers.ArrayPool<float>.Shared.Return(textArray);
            System.Buffers.ArrayPool<int>.Shared.Return(labelArray);
        }
    }

    private static DigitRow? Segment(in ScreenFrame frame, float cx, float cy, float scale, Action<string>? trace, RefTransform t, PixelRect pr,
        int step, int gw, int gh, Span<float> cover, Span<float> text, Span<int> label)
    {
        // (1) colour coverage per cell, per class
        int n = gw * gh;
        cover.Clear(); // rented: start from zero like a new array (the loop below adds up)
        ReadOnlySpan<byte> px = frame.Bgra.Span;
        float w1 = 1f / (step * step);
        for (int gy = 0; gy < gh; gy++)
            for (int sy = 0; sy < step; sy++)
            {
                int rowStart = (pr.Y + gy * step + sy) * frame.Stride + pr.X * 4;
                for (int gx = 0; gx < gw; gx++)
                    for (int sx = 0; sx < step; sx++)
                    {
                        int p = rowStart + (gx * step + sx) * 4;
                        int c = ClassOf(px[p + 2], px[p + 1], px[p]);
                        if (c >= 0) cover[(gy * gw + gx) * Channels + c] += w1;
                    }
            }
        for (int i = 0; i < n; i++) text[i] = cover[i * Channels + Cream] + cover[i * Channels + Red] + cover[i * Channels + Green];

        // (2) 8-connected components of text >= 0.5
        label.Clear();
        var comps = new List<Comp>();
        var stack = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            if (label[i] != 0 || text[i] < 0.5f) continue;
            var comp = new Comp(comps.Count + 1);
            comps.Add(comp);
            label[i] = comp.Root;
            stack.Push(i);
            while (stack.Count > 0)
            {
                int j = stack.Pop();
                int x = j % gw, y = j / gw;
                comp.Add(x, y, cover.Slice(j * Channels, 3));
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                        int k = ny * gw + nx;
                        if (label[k] != 0 || text[k] < 0.5f) continue;
                        label[k] = comp.Root;
                        stack.Push(k);
                    }
            }
        }
        if (comps.Count == 0) return null;

        float hExp = DigitHeight * scale * t.Scale / step;
        // Pieces of one glyph (a thin joint lost to anti-aliasing) overlap horizontally: merge them.
        int[] root = Enumerable.Range(0, comps.Count + 1).ToArray();
        int Find(int a) => root[a] == a ? a : root[a] = Find(root[a]);
        var live = comps.ToList();
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int a = 0; a < live.Count && !merged; a++)
                for (int b = a + 1; b < live.Count && !merged; b++)
                {
                    Comp p = live[a], q = live[b];
                    int overlap = Math.Min(p.MaxX, q.MaxX) - Math.Max(p.MinX, q.MinX) + 1;
                    int union = Math.Max(p.MaxY, q.MaxY) - Math.Min(p.MinY, q.MinY) + 1;
                    if (overlap < 0.5f * Math.Min(p.W, q.W) || union > 1.3f * hExp) continue;
                    p.Absorb(q);
                    root[q.Root] = p.Root;
                    live.RemoveAt(b);
                    merged = true;
                }
        }

        // Two digits whose fills touch (e.g. "44": the outline between them is thinner than one cell) form one
        // wide component: cut it at the emptiest column near where the digit boundary should be.
        var pieces = new List<Comp>();
        foreach (Comp c in live)
        {
            int k = (int)MathF.Round(c.W / (0.65f * hExp));
            if (c.H < 0.7f * hExp || c.H > 1.3f * hExp || c.W <= 1.0f * hExp || k < 2 || k > 3)
            {
                pieces.Add(c);
                continue;
            }
            var cuts = new List<int> { c.MinX };
            for (int j = 1; j < k; j++)
            {
                int expected = c.MinX + (int)MathF.Round(c.W * j / (float)k);
                int radius = Math.Max(1, (int)(c.W / (4f * k)));
                int bestX = expected;
                float bestSum = float.MaxValue;
                for (int x = expected - radius; x <= expected + radius; x++)
                {
                    float colSum = 0;
                    for (int y = c.MinY; y <= c.MaxY; y++)
                    {
                        int idx = y * gw + x;
                        if (label[idx] != 0 && Find(label[idx]) == c.Root) colSum += text[idx];
                    }
                    if (colSum < bestSum)
                    {
                        bestSum = colSum;
                        bestX = x;
                    }
                }
                cuts.Add(bestX);
            }
            cuts.Add(c.MaxX + 1);
            for (int j = 0; j < k; j++)
            {
                var part = new Comp(c.Root);
                for (int y = c.MinY; y <= c.MaxY; y++)
                    for (int x = cuts[j]; x < cuts[j + 1]; x++)
                    {
                        int idx = y * gw + x;
                        if (label[idx] != 0 && Find(label[idx]) == c.Root) part.Add(x, y, cover.Slice(idx * Channels, 3));
                    }
                if (part.Area > 0) pieces.Add(part);
            }
        }

        // (3) digit-sized components, grouped into rows (close together, common vertical centre)
        if (trace != null)
        {
            trace($"grid {gw}x{gh} step {step} hExp {hExp:F1} yExp {(DigitCenterY - LabelBox.Y) * scale * t.Scale / step:F1}");
            foreach (Comp c in pieces)
                trace($"  comp x {c.MinX}-{c.MaxX} y {c.MinY}-{c.MaxY} ({c.W}x{c.H}) area {c.Area} cover c/r/g {c.Cover[0]:F0}/{c.Cover[1]:F0}/{c.Cover[2]:F0}");
        }
        // The digits sit near a known line. The card's node scale is only estimated (a covered card, the hover
        // tween), so the line is a loose prior; rows of glyphs of EQUAL height (lining figures) are what count.
        float yExp = (DigitCenterY - LabelBox.Y) * scale * t.Scale / step;
        var digits = pieces.Where(c => c.H >= 0.7f * hExp && c.H <= 1.3f * hExp && c.W >= 0.12f * hExp && c.W <= 1.0f * hExp
                                       && Math.Abs(c.CenterY - yExp) <= 0.9f * hExp)
            .OrderBy(c => c.MinX).ToList();
        List<Comp>? bestRow = null;
        var current = new List<Comp>();
        foreach (Comp c in digits)
        {
            if (current.Count > 0)
            {
                Comp prev = current[^1];
                int gap = c.MinX - prev.MaxX - 1;
                // Lining figures (both fonts) are all the same height, so a height jump means "not the same row" (e.g. a fingernail).
                bool sameHeight = Math.Max(c.H, prev.H) <= 1.2f * Math.Min(c.H, prev.H);
                if (gap > 0.6f * hExp || gap < -0.2f * hExp || Math.Abs(c.CenterY - prev.CenterY) > 0.2f * hExp || !sameHeight)
                {
                    bestRow = Better(bestRow, current, yExp);
                    current = [];
                }
            }
            current.Add(c);
        }
        bestRow = Better(bestRow, current, yExp);
        if (bestRow == null || bestRow.Count == 0 || bestRow.Count > 4) return null;

        // (4) one coverage grid per digit, all in a window of the row's height
        int top = bestRow.Min(c => c.MinY), bottom = bestRow.Max(c => c.MaxY) + 1;
        float hh = bottom - top, ww = hh * PriceGlyphs.Aspect;
        var glyphs = new List<float[]>();
        var widths = new List<float>();
        const int Sub = 3;
        foreach (Comp c in bestRow)
        {
            float left = (c.MinX + c.MaxX + 1) / 2f - ww / 2;
            float[] g = new float[PriceGlyphs.Length];
            for (int oy = 0; oy < PriceGlyphs.Height; oy++)
                for (int ox = 0; ox < PriceGlyphs.Width; ox++)
                {
                    float sum = 0;
                    for (int sy = 0; sy < Sub; sy++)
                        for (int sx = 0; sx < Sub; sx++)
                        {
                            int x = (int)MathF.Floor(left + (ox + (sx + 0.5f) / Sub) / PriceGlyphs.Width * ww);
                            int y = (int)MathF.Floor(top + (oy + (sy + 0.5f) / Sub) / PriceGlyphs.Height * hh);
                            if (x < 0 || y < 0 || x >= gw || y >= gh) continue;
                            int k = y * gw + x;
                            // only this digit's own cells: a close neighbour must not leak into the window
                            if (label[k] != 0 && x >= c.MinX && x <= c.MaxX && Find(label[k]) == c.Root) sum += text[k];
                        }
                    g[oy * PriceGlyphs.Width + ox] = sum / (Sub * Sub);
                }
            glyphs.Add(g);
            widths.Add(c.W / hh);
        }

        float[] tint = new float[3];
        foreach (Comp c in bestRow)
            for (int k = 0; k < 3; k++) tint[k] += c.Cover[k];
        int major = tint[Red] > tint[Cream] && tint[Red] > tint[Green] ? Red : tint[Green] > tint[Cream] ? Green : Cream;
        int x0 = bestRow[0].MinX, x1 = bestRow[^1].MaxX + 1;
        var bounds = new PixelRect(pr.X + x0 * step, pr.Y + top * step, (x1 - x0) * step, (bottom - top) * step);
        // Coin check: the gold coin sits right before the first digit. If the merchant's hand hides the first
        // digit, the cells left of the visible block are hand, not coin.
        int cx0 = Math.Max(0, (int)(x0 - 0.9f * hExp)), cx1 = Math.Max(0, (int)(x0 - 0.15f * hExp));
        float gold = 0;
        int goldCells = 0;
        for (int y = top; y < bottom; y++)
            for (int x = cx0; x < cx1; x++)
            {
                gold += cover[(y * gw + x) * Channels + Gold];
                goldCells++;
            }
        float coin = goldCells == 0 ? 0 : gold / goldCells;

        // Occluder check: a ring around the digit block, from 1 to 4 cells out (the cell right next to the glyphs
        // is anti-aliasing). Clean labels have only outline and rug there; a hand that clips a digit touches the cut.
        float bright = 0;
        int ringCells = 0;
        for (int y = top - 4; y < bottom + 4; y++)
            for (int x = x0 - 4; x < x1 + 4; x++)
            {
                if (x < 0 || y < 0 || x >= gw || y >= gh) continue;
                if (y >= top - 1 && y < bottom + 1 && x >= x0 - 1 && x < x1 + 1) continue;
                bright += cover[(y * gw + x) * Channels + Bright];
                ringCells++;
            }
        float occluder = ringCells == 0 ? 0 : bright / ringCells;
        // Where the digit block sits relative to the card centre, in card units (see DigitBlockCenterX).
        float blockX = ((pr.X + (x0 + x1) * step / 2f - t.OffsetX) / t.Scale - cx) / scale;
        float blockY = ((pr.Y + (top + bottom) * step / 2f - t.OffsetY) / t.Scale - cy) / scale;
        return new DigitRow(glyphs, major switch { Red => PriceTint.Red, Green => PriceTint.Green, _ => PriceTint.Cream }, bounds, blockX, blockY, coin, occluder)
        {
            Widths = widths,
        };

        // More digits first; equal counts: the row closer to the expected line.
        static List<Comp>? Better(List<Comp>? a, List<Comp> b, float yExp) =>
            b.Count == 0 ? a : a == null || b.Count > a.Count || (b.Count == a.Count && Dist(b, yExp) < Dist(a, yExp)) ? b : a;

        static float Dist(List<Comp> row, float yExp) => Math.Abs(row.Average(c => c.CenterY) - yExp);
    }

    /// <summary>A connected component: bounding box, cell count and summed colour coverage.</summary>
    private sealed class Comp(int root)
    {
        /// <summary>Component label (after merging) whose cells belong to this glyph, within [MinX, MaxX].</summary>
        public int Root { get; } = root;
        public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue, Area;
        public readonly float[] Cover = new float[3];
        public int W => MaxX - MinX + 1;
        public int H => MaxY - MinY + 1;
        public float CenterY => (MinY + MaxY) / 2f;

        public void Add(int x, int y, ReadOnlySpan<float> cover)
        {
            MinX = Math.Min(MinX, x);
            MaxX = Math.Max(MaxX, x);
            MinY = Math.Min(MinY, y);
            MaxY = Math.Max(MaxY, y);
            Area++;
            for (int k = 0; k < 3; k++) Cover[k] += cover[k];
        }

        public void Absorb(Comp o)
        {
            MinX = Math.Min(MinX, o.MinX);
            MaxX = Math.Max(MaxX, o.MaxX);
            MinY = Math.Min(MinY, o.MinY);
            MaxY = Math.Max(MaxY, o.MaxY);
            Area += o.Area;
            for (int k = 0; k < 3; k++) Cover[k] += o.Cover[k];
        }
    }
}
