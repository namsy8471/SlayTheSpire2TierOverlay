using System.Diagnostics;

namespace Sts2Overlay.Vision.Relics;

/// <summary>What a merchant relic slot shows.</summary>
public enum RelicSlotState
{
    /// <summary>A relic was identified (<see cref="RecognizedRelic.RelicId"/>).</summary>
    Identified,
    /// <summary>Nothing is drawn: the relic was bought (NMerchantRelic hides the slot) or the slot was never filled.</summary>
    Empty,
    /// <summary>Something is there but it could not be identified safely (covered by a tooltip or the hand, sliding in).</summary>
    Unknown,
}

/// <summary>One merchant relic slot after the search.</summary>
/// <param name="Slot">0..2, left to right (MerchantInventory: two rolled rarities, then the Shop-rarity relic).</param>
/// <param name="State">Identified / empty / unknown.</param>
/// <param name="RelicId">The identified relic (<see cref="RelicSlotState.Identified"/>), else the best guess or null.</param>
/// <param name="Rarity">Catalog rarity of <paramref name="RelicId"/>.</param>
/// <param name="Score">Masked NCC of the best relic at the best box (1 = identical).</param>
/// <param name="Margin">Gap to the second-best relic at the same box.</param>
/// <param name="NodeScale">Node scale found (0.65 resting, 0.8 hovered).</param>
/// <param name="IconBounds">Icon square in frame pixels.</param>
/// <param name="PriceBounds">Where the price label is, frame pixels.</param>
/// <param name="Price">Gold price when the label was read clearly and fits the relic price rules, else null.</param>
/// <param name="Candidates">Top relics at the decision box (id, score), for the bench and logs.</param>
public sealed record RecognizedRelic(
    int Slot,
    RelicSlotState State,
    string? RelicId,
    string? Rarity,
    float Score,
    float Margin,
    float NodeScale,
    PixelRect IconBounds,
    PixelRect PriceBounds,
    int? Price,
    IReadOnlyList<(string Id, float Score)> Candidates)
{
    public bool Accepted => State == RelicSlotState.Identified;

    /// <summary>The raw price reading (digits, colour), also when <see cref="Price"/> is null.</summary>
    public PriceReading? PriceReading { get; init; }

    /// <summary>The price is drawn red: the game says the player cannot afford it. Null when no label was read.</summary>
    public bool? Unaffordable => PriceReading is { Tint: not PriceTint.Unknown } r ? r.Unaffordable : null;

    /// <summary>
    /// Share of the icon square (at the resting layout box) in hover-tooltip slate, the merchant's light-blue hand,
    /// overlay colours (dark tooltips and card frames, a hovered card's olive-brown body) and bare rug: the evidence for Unknown / Empty.
    /// </summary>
    public float TooltipCover { get; init; }
    public float HandCover { get; init; }
    public float OverlayCover { get; init; }
    public float RugShare { get; init; }

    /// <summary>Whole-square (shape) NCC of the shape view's best relic, its lead, and whether it is the same relic.</summary>
    public float ShapeScore { get; init; }
    public float ShapeMargin { get; init; }
    public bool ShapeAgrees { get; init; }

    public override string ToString() =>
        $"relic#{Slot} {State} {RelicId ?? "-"} s={Score:F3} m={Margin:F3} shape={ShapeScore:F3}/{ShapeMargin:F3}{(ShapeAgrees ? "" : "(disagrees)")} scale={NodeScale:F2}{(PriceReading != null ? $" ${PriceReading}" : "")}" +
        (State == RelicSlotState.Unknown ? $" cover tip={TooltipCover:F2} hand={HandCover:F2} overlay={OverlayCover:F2} rug={RugShare:F2}" : "");
}

/// <summary>Thresholds of <see cref="RelicRecognizer"/>; defaults calibrated with <c>VisionBench relics</c> on real captures.</summary>
public sealed record RelicRecognizerOptions
{
    /// <summary>Fine-grid masked NCC needed to name a relic.</summary>
    public float AcceptScore { get; init; } = 0.80f;
    /// <summary>... and its lead over the second-best relic at the same box.</summary>
    public float AcceptMargin { get; init; } = 0.15f;
    /// <summary>The whole-square (shape) view must name the same relic with at least this NCC ...</summary>
    public float ShapeScore { get; init; } = 0.70f;
    /// <summary>... and a lead of this much is an alternative to <see cref="AcceptMargin"/> (same-colour look-alikes).</summary>
    public float ShapeMargin { get; init; } = 0.10f;
    /// <summary>
    /// A slot whose icon square is at least this share bare rug (teal or its olive chevrons) and that has no price
    /// label is empty: the relic was bought (NMerchantRelic hides the whole node).
    /// </summary>
    public float EmptyRugShare { get; init; } = 0.9f;
    /// <summary>
    /// Also require the slot's price label: a digit row under the found icon whose number fits the relic's rarity
    /// (read clearly or not). Every merchant relic has one; a relic-like picture elsewhere (the merchant's robe on the
    /// card-removal screen, a dimmed shop behind the deck view) has none.
    /// </summary>
    public bool RequirePriceLabel { get; init; } = true;
    /// <summary>Search radius around the layout position, reference pixels.</summary>
    public float SearchRadius { get; init; } = 8f;
    /// <summary>Candidates kept from the coarse search for the fine refinement.</summary>
    public int Candidates { get; init; } = 6;
}

/// <summary>
/// Finds and identifies the three relics on the merchant's rug, and reads their prices.
///
/// <para><b>Where they are</b> (decompiled v0.107.1, scenes/merchant/merchant_inventory.tscn + merchant_relic.tscn +
/// scenes/relics/relic.tscn): the rug (SlotsContainer) sits at x = 960 − 842 = 118 and is tweened to y 80; its
/// <c>Relics</c> control is at (871, 595), and the three MerchantRelic nodes at x 0 / 150 / 300 inside it, so node
/// origins are (989 + 150 i, 675). A node is drawn at scale 0.65 and jumps to 0.8 while hovered (NMerchantSlot
/// _smallScale / _hoverScale), scaling about its origin. Inside it the RelicHolder (−64, −80) at scale 2 holds an
/// NRelic whose 60x60 Icon starts at (4, 4): the icon square is (−56, −72) .. (64, 48) in node units. The Cost label
/// spans y 52..106. A bought relic hides the whole node (NMerchantRelic.UpdateVisual sets Visible = false).</para>
///
/// <para><b>How</b>: (1) coarse 12x12 masked NCC of every sellable relic over a small grid of origins and scales,
/// plus every relic on the 24x24 grid at the exact resting / hovered boxes (thin relics drop out of the coarse list),
/// (2) the best few of both lists are refined on the 24x24 grid, (3) at the winner's box every relic is scored again:
/// the masked view and the whole-square (shape) view must name the same relic, beat the runner-up by
/// <see cref="RelicRecognizerOptions.AcceptMargin"/> (or the shape lead), and (4) a price label that fits the relic's
/// rarity must sit under it (<see cref="RelicRecognizerOptions.RequirePriceLabel"/>).</para>
/// <para>Calibrated with <c>VisionBench relics</c> on 2538 real captures against the run history: every identified slot
/// right, no relic reported off the shop; unidentified slots were all covered (tooltip, hand, hovered card).</para>
/// Thread-safe: the database is read-only and scratch memory is per call.
/// </summary>
public sealed class RelicRecognizer
{
    public const float SlotX0 = 118f + 871f, SlotStepX = 150f, SlotY = 80f + 595f;
    public const float SmallScale = 0.65f, HoverScale = 0.8f;
    /// <summary>The icon square in node units (see the class remarks).</summary>
    public static readonly RectF IconBox = new(-56, -72, 120, 120);
    /// <summary>The price label box in node units (merchant_relic.tscn Cost).</summary>
    public static readonly RectF LabelBox = new(-148, 52, 296, 54);
    public const int SlotCount = 3;

    private readonly RelicIconDatabase _db;
    private readonly ShopPriceReader? _prices;

    public RelicRecognizerOptions Options { get; }
    public RelicIconDatabase Database => _db;

    public RelicRecognizer(RelicIconDatabase db, RelicRecognizerOptions? options = null, PriceReaderOptions? priceOptions = null)
    {
        _db = db;
        Options = options ?? new RelicRecognizerOptions();
        _prices = new ShopPriceReader(options: priceOptions ?? new PriceReaderOptions());
    }

    /// <summary>Node origin of slot <paramref name="i"/> in reference units.</summary>
    public static (float X, float Y) Origin(int i) => (SlotX0 + SlotStepX * i, SlotY);

    /// <summary>Icon square of a node at origin (ox, oy) and scale s, reference units.</summary>
    public static RectF IconAt(float ox, float oy, float s) => new(ox + IconBox.X * s, oy + IconBox.Y * s, IconBox.Width * s, IconBox.Height * s);

    /// <summary>Reads all three slots: ~8 ms per frame at 1080p and ~9 ms at 4K (median, 3 slots in parallel).</summary>
    public IReadOnlyList<RecognizedRelic> Recognize(in ScreenFrame frame) => Recognize(frame, out _);

    public IReadOnlyList<RecognizedRelic> Recognize(in ScreenFrame frame, out double elapsedMs)
    {
        var sw = Stopwatch.StartNew();
        RefTransform t = RefTransform.For(frame.Width, frame.Height);
        int step = Math.Max(1, (int)MathF.Floor(t.Scale + 1e-3f));
        float r = Options.SearchRadius + 2;
        // Band: every icon square any slot can take within the search (scale up to 0.82).
        var band = new RectF(SlotX0 - 56 * 0.82f - r, SlotY - 72 * 0.82f - r, SlotStepX * (SlotCount - 1) + 120 * 0.82f + 2 * r, 120 * 0.82f + 2 * r);
        using IntegralImage img = IntegralImage.Build(frame, t.ToPixelRect(band), step);
        var result = new RecognizedRelic[SlotCount];
        ScreenFrame f = frame;
        Parallel.For(0, SlotCount, i => result[i] = MatchSlot(img, t, f, i));
        elapsedMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    private readonly record struct Box(float X, float Y, float S);

    private RecognizedRelic MatchSlot(IntegralImage img, RefTransform t, in ScreenFrame frame, int slot)
    {
        (float ox0, float oy0) = Origin(slot);
        RelicTemplates coarse = _db.Coarse, fine = _db.Fine;
        int n = _db.Count;
        float[] q = new float[coarse.Length], q2 = new float[coarse.Length];
        var best = new float[n];
        var bestBox = new Box[n];
        Array.Fill(best, float.NegativeInfinity);

        // (1) coarse search over origins and both resting/hovered scales (the hover tween passes in between)
        float rad = Options.SearchRadius;
        foreach (float s in (float[])[0.65f, 0.70f, 0.75f, 0.80f])
            for (float dy = -rad; dy <= rad + 1e-3f; dy += 4)
                for (float dx = -rad; dx <= rad + 1e-3f; dx += 4)
                {
                    var b = new Box(ox0 + dx, oy0 + dy, s);
                    Sample(img, t, b, coarse.Grid, q, q2);
                    for (int k = 0; k < n; k++)
                    {
                        float v = coarse.Score(k, q, q2);
                        if (v > best[k])
                        {
                            best[k] = v;
                            bestBox[k] = b;
                        }
                    }
                }

        // (1b) the exact resting / hovered boxes on the fine grid: a thin relic (a chain, a triangle of wire) has few
        // fully covered cells on the coarse grid and can miss the coarse top list, but at rest it sits exactly here.
        float[] fq = new float[fine.Length], fq2 = new float[fine.Length];
        var layoutBest = new float[n];
        var layoutBox = new Box[n];
        Array.Fill(layoutBest, float.NegativeInfinity);
        foreach (float s in (float[])[SmallScale, HoverScale])
        {
            var b = new Box(ox0, oy0, s);
            Sample(img, t, b, fine.Grid, fq, fq2);
            for (int k = 0; k < n; k++)
            {
                float v = fine.Score(k, fq, fq2);
                if (v > layoutBest[k])
                {
                    layoutBest[k] = v;
                    layoutBox[k] = b;
                }
            }
        }

        // (2) fine refinement of the best few relics (of either list) around their own box
        var seeds = Enumerable.Range(0, n).OrderByDescending(k => best[k]).Take(Options.Candidates).Select(k => (k, bestBox[k]))
            .Concat(Enumerable.Range(0, n).OrderByDescending(k => layoutBest[k]).Take(Options.Candidates).Select(k => (k, layoutBox[k])))
            .Distinct().ToArray();
        float winScore = float.NegativeInfinity;
        Box win = new(ox0, oy0, SmallScale);
        foreach ((int k, Box c) in seeds)
        {
            float kBest = float.NegativeInfinity;
            Box kBox = c;
            for (int pass = 0; pass < 2; pass++)
            {
                Box centre = kBox;
                float d = pass == 0 ? 2 : 1;
                foreach (float ds in pass == 0 ? (float[])[-0.025f, 0, 0.025f] : [-0.01f, 0, 0.01f])
                    for (float dy = -d; dy <= d + 1e-3f; dy += d)
                        for (float dx = -d; dx <= d + 1e-3f; dx += d)
                        {
                            var b = new Box(centre.X + dx, centre.Y + dy, Math.Clamp(centre.S + ds, 0.6f, 0.85f));
                            Sample(img, t, b, fine.Grid, fq, fq2);
                            float v = fine.Score(k, fq, fq2);
                            if (v > kBest)
                            {
                                kBest = v;
                                kBox = b;
                            }
                        }
            }
            if (kBest > winScore)
            {
                winScore = kBest;
                win = kBox;
            }
        }

        // (3) decision at the winner's box: every relic scored on the fine grid
        Sample(img, t, win, fine.Grid, fq, fq2);
        var ranked = Enumerable.Range(0, n).Select(k => (k, s: fine.Score(k, fq, fq2))).OrderByDescending(x => x.s).Take(4).ToArray();
        float score = ranked.Length > 0 ? ranked[0].s : 0;
        float margin = ranked.Length > 1 ? score - ranked[1].s : score;
        // Second view: the whole square (shape + colour) against every relic composited over the rug.
        float[] norm = (float[])fq.Clone();
        bool contrast = Ncc.Normalise(norm);
        var shape = Enumerable.Range(0, n).Select(k => (k, s: contrast ? fine.ScoreFull(k, norm) : 0)).OrderByDescending(x => x.s).Take(2).ToArray();
        float shapeMargin = shape.Length > 1 ? shape[0].s - shape[1].s : shape.Length > 0 ? shape[0].s : 0;
        bool agree = ranked.Length > 0 && shape.Length > 0 && shape[0].k == ranked[0].k;
        bool looksRight = agree && score >= Options.AcceptScore && shape[0].s >= Options.ShapeScore
                          && (margin >= Options.AcceptMargin || shapeMargin >= Options.ShapeMargin);
        RelicEntry? pick = ranked.Length > 0 ? _db.Entries[ranked[0].k] : null;

        // Price: at the found node when the picture matched, else at the resting layout node.
        Box priceAt = looksRight ? win : new Box(ox0, oy0, SmallScale);
        PriceReading? reading = _prices?.ReadRelic(frame, priceAt.X, priceAt.Y, priceAt.S, looksRight ? pick!.Rarity : null);
        bool accepted = looksRight && (!Options.RequirePriceLabel || reading is { Plausible: true });
        if (!accepted) priceAt = new Box(ox0, oy0, SmallScale);
        if (reading == null && !accepted)
            foreach (float s in (float[])[HoverScale])
                reading ??= _prices?.ReadRelic(frame, ox0, oy0, s);

        CoverShares cover = Cover(img, t, new Box(ox0, oy0, SmallScale));
        RelicSlotState state = accepted ? RelicSlotState.Identified
            : cover.Rug >= Options.EmptyRugShare && reading is not { Plausible: true } ? RelicSlotState.Empty
            : RelicSlotState.Unknown;
        RectF priceBox = new(priceAt.X + LabelBox.X * priceAt.S, priceAt.Y + LabelBox.Y * priceAt.S, LabelBox.Width * priceAt.S, LabelBox.Height * priceAt.S);
        return new RecognizedRelic(slot, state, accepted ? pick!.RelicId : pick?.RelicId, pick?.Rarity, score, margin, win.S,
            t.ToPixelRect(IconAt(win.X, win.Y, win.S)), t.ToPixelRect(priceBox), accepted ? reading?.Value : null,
            ranked.Select(x => (_db.Entries[x.k].RelicId, x.s)).ToArray())
        {
            PriceReading = reading,
            TooltipCover = cover.Tooltip,
            HandCover = cover.Hand,
            OverlayCover = cover.Overlay,
            RugShare = cover.Rug,
            ShapeScore = shape.Length > 0 ? shape[0].s : 0,
            ShapeMargin = shapeMargin,
            ShapeAgrees = agree,
        };
    }

    private static void Sample(IntegralImage img, RefTransform t, Box b, int g, float[] q, float[] q2)
    {
        img.SampleGrid(t.ToPixels(IconAt(b.X, b.Y, b.S)), g, g, q);
        for (int i = 0; i < q.Length; i++) q2[i] = q[i] * q[i];
    }

    private readonly record struct CoverShares(float Tooltip, float Hand, float Overlay, float Rug);

    /// <summary>Share of the icon square's cells in tooltip slate, the merchant's hand, dark overlay colours and bare rug.</summary>
    private static CoverShares Cover(IntegralImage img, RefTransform t, Box b)
    {
        const int g = 12;
        float[] c = new float[g * g * 3];
        img.SampleGrid(t.ToPixels(IconAt(b.X, b.Y, b.S)), g, g, c);
        int tip = 0, hand = 0, dark = 0, rug = 0;
        for (int i = 0; i < c.Length; i += 3)
        {
            if (UpgradeDetector.IsTooltipSlate(c[i], c[i + 1], c[i + 2])) tip++;
            if (IsHand(c[i], c[i + 1], c[i + 2])) hand++;
            if (IsOverlay(c[i], c[i + 1], c[i + 2])) dark++;
            if (IsRug(c[i], c[i + 1], c[i + 2])) rug++;
        }
        float k = g * g;
        return new CoverShares(tip / k, hand / k, dark / k, rug / k);
    }

    /// <summary>
    /// The merchant's rug: teal (~ 60-100, 100-130, 80-110) or its olive chevrons (~ 100-140, 120-150, 50-90).
    /// Green dominates both; nothing grey, dark, warm or blue passes.
    /// </summary>
    /// <summary>Dark (tooltip slate, card frames) or a card body's olive brown (~ 82, 73, 44: red ≥ green ≥ blue + 15).</summary>
    internal static bool IsOverlay(float r, float g, float b) => Math.Max(r, Math.Max(g, b)) < 80 || (r >= g && g >= b + 15 && r <= 140 && !IsRug(r, g, b));

    internal static bool IsRug(float r, float g, float b) => g >= 90 && g <= 160 && g >= r + 8 && g >= b + 5 && r >= 45 && b >= 40 && b <= 120;

    /// <summary>The merchant's hand: light, clearly blue (see ShopPriceReader.ClassOf "Bright").</summary>
    internal static bool IsHand(float r, float g, float b) => b >= 150 && b >= r + 25 && g >= r && b >= g - 10;
}
