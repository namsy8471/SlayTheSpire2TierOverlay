using System.Diagnostics;
using System.Globalization;

namespace Sts2Overlay.Vision;

/// <summary>Thresholds and search ranges. Defaults are calibrated with tools/VisionBench on real captures.</summary>
public sealed record RecognizerOptions
{
    /// <summary>Minimum full-resolution NCC to accept a card (clean matches score 0.9-1.0).</summary>
    public float AcceptScore { get; init; } = 0.70f;
    /// <summary>Minimum gap to the best different card.</summary>
    public float AcceptMargin { get; init; } = 0.10f;
    /// <summary>
    /// Second acceptance path for partly covered cards (merchant hand, tooltip edge): a lower score is enough
    /// when the winner stands far above every other card. On the bench, wrong winners never had a margin
    /// above 0.17 while their score was below 0.70.
    /// </summary>
    public float CoveredScore { get; init; } = 0.55f;
    public float CoveredMargin { get; init; } = 0.25f;
    /// <summary>
    /// Tile-fit check for whole-art accepts below <see cref="UnoccludedScore"/>: the chosen card must also agree
    /// on at least this many of the 24 blocks, with an NCC over those blocks of at least <see cref="TileFitScore"/>.
    /// A big smooth occluder (the merchant's hand) can lift a card with a big smooth sky in the same place to a
    /// winning whole-art score, but that card does not fit the rest of the art block by block. VisionBench stress:
    /// wrong picks fitted at most 9 blocks / 0.789, right picks of 2,023 kept 2,001.
    /// </summary>
    public int TileFitMinTiles { get; init; } = 8;
    public float TileFitScore { get; init; } = 0.82f;
    /// <summary>Coarse NCC above which a slot counts as "a card is here" while choosing the layout.</summary>
    public float SlotPresentScore { get; init; } = 0.60f;
    /// <summary>Coarse NCC of a slot that clearly holds a card; ranks layout hypotheses (empty slots stay below).</summary>
    public float SlotStrongScore { get; init; } = 0.75f;
    /// <summary>Fraction of upgrade-green title pixels that marks an upgraded card (upgraded titles: 5-10 %).</summary>
    public float UpgradeFraction { get; init; } = 0.02f;
    /// <summary>Art NCC above which the card is considered fully visible (title not hidden by a tooltip).</summary>
    public float UnoccludedScore { get; init; } = 0.85f;
    /// <summary>
    /// Share of the title box in the hover tooltip's slate colour above which the title counts as covered: the art
    /// can still match cleanly while a neighbour's tooltip hides just the name (bench: clean titles ≤ 0.4 %,
    /// covered ones ≥ 52 %).
    /// </summary>
    public float TitleCoveredFraction { get; init; } = 0.15f;
    /// <summary>A hinted-pool candidate wins if it is within this NCC of the overall best.</summary>
    public float HintTolerance { get; init; } = 0.03f;
    /// <summary>Local search radius around the layout position, in reference pixels.</summary>
    public float SearchRadius { get; init; } = 12f;
    /// <summary>Candidates kept from the coarse full-database scan for the local box search.</summary>
    public int SearchCandidates { get; init; } = 16;
    /// <summary>Pools that can be offered (others are never considered). Null = all database entries.</summary>
    public IReadOnlySet<string>? CandidatePools { get; init; } = VisionCatalog.OfferablePools;
    /// <summary>Card catalog (rarity): lets the shop price check use the card's own price range. Optional.</summary>
    public VisionCatalog? Catalog { get; init; }
    /// <summary>
    /// Look for a card grid (event "pick n of N" and the deck/pile selection screens) when neither the reward banner
    /// nor the shop rug is seen.
    /// </summary>
    public bool GridScreens { get; init; } = true;
    /// <summary>Also try the choose-a-card row (relics, potions, Discovery) on banner screens.</summary>
    public bool ChoiceScreens { get; init; } = true;
    /// <summary>Shop price reading; null disables it.</summary>
    public PriceReaderOptions? Price { get; init; } = new();
    /// <summary>
    /// Shop: an unidentified card slot whose resting art box is at least this share bare rug, with no price label
    /// under it, is empty (the card was bought; NMerchantCard hides the card and its label). Calibrated on the real
    /// shop captures, see <c>ShopEmptySlotTests</c>.
    /// </summary>
    public float EmptySlotRugShare { get; init; } = 0.9f;

    // ---- occlusion-robust second pass (see TileMatcher); runs only on rejected slots of a confirmed screen ----

    /// <summary>Try the tile-based partial match on slots the whole-art NCC rejected.</summary>
    public bool PartialMatching { get; init; } = true;
    /// <summary>
    /// Stage 1, 6x4 blocks: covers of any shape. At least 5 textured blocks (a fifth of the art) must agree; the
    /// winner must also be in the whole-art top 8 unless its margin is 0.40 or more. VisionBench stress (9,800
    /// covered thumbnails): 0 wrong accepts, 0 impostor accepts, highest impostor margin 0.13.
    /// </summary>
    public PartialStage BlockStage { get; init; } = new(Agree: 0.7f, FlatEnergy: 0.1f, MinTiles: 5, AcceptScore: 0.85f, AcceptMargin: 0.30f, WholeTopK: 8, FreeMargin: 0.40f);
    /// <summary>
    /// Stage 2, 36 one-cell rows: stacked hover tooltips can leave only a thin strip of a card visible. A strip
    /// carries less evidence than a third of the art, so the bar is higher and does not depend on the whole-art
    /// ranking (which a 90 % covered card cannot win).
    /// </summary>
    public PartialStage RowStage { get; init; } = new(Agree: 0.85f, FlatEnergy: 0.1f, MinTiles: 2, AcceptScore: 0.90f, AcceptMargin: 0.30f, WholeTopK: 0);
    /// <summary>Reward slots whose best coarse NCC stays below this after the local search get a taller search.</summary>
    public float WideSearchBelow { get; init; } = 0.70f;
    /// <summary>How far above the layout position the taller search looks, in reference pixels.</summary>
    public float WideSearchUp { get; init; } = 44f;
    /// <summary>Half-size of the wider search square for a shop slot that fits nothing near its layout position.</summary>
    public float WideSearchShop { get; init; } = 24f;
}

/// <summary>
/// Per-frame screen recognition: classify the frame, find the offered cards, identify them.
/// Thread-safe (the database is read-only and all scratch memory is per call).
///
/// <para>Typical app loop (2-4 Hz while the log says a reward or shop is open):</para>
/// <code>
/// var r = recognizer.Recognize(frame, new RecognitionHints(characterId));
/// if (r.Kind != ScreenKind.Other)
///     foreach (var c in r.Cards) DrawTierBadge(c.CardId!, c.Upgraded, c.CardBounds);
/// </code>
/// </summary>
public sealed class CardRecognizer
{
    private readonly CardArtDatabase _db;
    private readonly bool[] _offerable;
    private readonly Lazy<TileMatcher> _blocks;
    private readonly Lazy<TileMatcher> _rows;
    private readonly ShopPriceReader? _priceReader;

    public RecognizerOptions Options { get; }
    public CardArtDatabase Database => _db;

    /// <summary>
    /// Optional merchant relic reader, run on screens with the shop rug (<see cref="ScreenGate.ShopLikely"/>). Its
    /// identified relics also confirm a shop on their own (a frame where every card is covered).
    /// </summary>
    public global::Sts2Overlay.Vision.Relics.RelicRecognizer? Relics { get; init; }

    public CardRecognizer(CardArtDatabase db, RecognizerOptions? options = null)
    {
        _db = db;
        Options = options ?? new RecognizerOptions();
        _offerable = db.Entries.Select(e => Options.CandidatePools?.Contains(e.Pool) ?? true).ToArray();
        _blocks = new Lazy<TileMatcher>(() => new TileMatcher(db, TileGrid.Blocks));
        _rows = new Lazy<TileMatcher>(() => new TileMatcher(db, TileGrid.Rows));
        _priceReader = Options.Price is { } po ? new ShopPriceReader(options: po) : null;
    }

    public ScreenRecognition Recognize(in ScreenFrame frame, RecognitionHints? hints = null)
    {
        var sw = Stopwatch.StartNew();
        frame.Validate();
        ScreenGate gate = ScreenClassifier.Evaluate(frame);
        RefTransform t = RefTransform.For(frame.Width, frame.Height);
        // One table cell ≈ one reference pixel: average 2x2 source pixels on a 4K frame, 1x1 at 1080p.
        int step = Math.Max(1, (int)MathF.Floor(t.Scale + 1e-3f));
        var notes = new List<string>();
        IReadOnlyList<RecognizedCard> examined = [];

        // The inspect view dims the screen it opened from; its cards still match, but nothing should be marked.
        if (gate.InspectLikely)
            return new ScreenRecognition(ScreenKind.Other, [], [], gate, sw.Elapsed.TotalMilliseconds, "inspect view");

        if (gate.RewardLikely)
        {
            (List<RecognizedCard> slots, string detail, ScreenKind kind) = RecognizeReward(frame, t, step, hints);
            List<RecognizedCard> accepted = slots.Where(s => s.Accepted).ToList();
            // Two whole-art matches are required (checked before any partial rescue, see RescueCovered): one lucky
            // match on a banner screen (loot list) is not enough.
            if (accepted.Count(c => c.Partial == null) >= 2)
                return new ScreenRecognition(kind, accepted, slots, gate, sw.Elapsed.TotalMilliseconds, detail);
            notes.Add($"reward banner but {accepted.Count} card(s): {detail}");
            examined = slots;
        }

        if (gate.ShopLikely)
        {
            List<RecognizedCard> slots = RecognizeShop(frame, t, step, hints);
            List<RecognizedCard> accepted = slots.Where(s => s.Accepted).ToList();
            // A relic is accepted only with its price label under it (RelicRecognizerOptions.RequirePriceLabel), so
            // one identified relic is as strong a shop signal as one whole-art card match.
            IReadOnlyList<global::Sts2Overlay.Vision.Relics.RecognizedRelic> relics = Relics?.Recognize(frame) ?? [];
            if (accepted.Count(c => c.Partial == null) >= 1 || relics.Any(r => r.Accepted))
                return new ScreenRecognition(ScreenKind.Shop, accepted, slots, gate, sw.Elapsed.TotalMilliseconds, "shop") { Relics = relics };
            notes.Add("shop rug but no cards");
            examined = slots;
        }

        if (Options.GridScreens)
        {
            // No probe announces a card grid (it has no banner and covers whatever was behind it), so the cheap
            // presence test of the grid slots is the gate: a few ms on a screen without cards.
            (List<RecognizedCard> slots, string detail) = RecognizeGrid(frame, t, step, hints);
            List<RecognizedCard> accepted = slots.Where(s => s.Accepted).ToList();
            if (accepted.Count(c => c.Partial == null) >= 2)
                return new ScreenRecognition(ScreenKind.CardGrid, accepted, slots, gate, sw.Elapsed.TotalMilliseconds, detail);
            if (slots.Count > 0)
            {
                notes.Add($"grid-like but {accepted.Count} card(s): {detail}");
                if (examined.Count == 0) examined = slots;
            }
        }

        return new ScreenRecognition(ScreenKind.Other, [], examined, gate, sw.Elapsed.TotalMilliseconds, string.Join("; ", notes));
    }

    // ------------------------------------------------------------------ reward screen

    private (List<RecognizedCard>, string, ScreenKind) RecognizeReward(in ScreenFrame frame, RefTransform t, int step, RecognitionHints? hints)
    {
        // Band covering portrait + title of every possible slot (cards at scale <= 1.04 plus the search radius).
        // The choose-a-card row (centre y 600) fits in the same band as the reward row (616).
        using IntegralImage img = IntegralImage.Build(frame, t.ToPixelRect(new RectF(80, 380, 1760, 285)), step);

        // Both screens share the banner, so every row hypothesis is scored and the best one wins (see Better): most
        // slots with a clear card, then the fewest empty slots (a 3-card row also fills 3 of the 5 slots of a 5-card
        // row because the middle positions coincide), then the higher summed NCC (the reward and choose-a-card rows
        // are only 10-20 px apart, so the exact one scores higher).
        SlotLayout[]? chosen = null;
        string detail = "";
        Presence best = default;
        foreach (int n in (int[])[3, 4, 2, 5])
            foreach (bool choice in Options.ChoiceScreens ? (bool[])[false, true] : [false])
            {
                SlotLayout[] layout = choice ? ScreenLayouts.Choice(n) : ScreenLayouts.Reward(n);
                Presence p = QuickPresence(img, t, layout, 0.76f, 1.04f);
                if (p.Present < (n >= 3 ? n - 1 : n)) continue;
                if (chosen == null || Better(p, best))
                {
                    best = p;
                    chosen = layout;
                    detail = $"{n} cards{(choice ? ", choose-a-card" : "")}";
                }
            }

        if (chosen == null)
        {
            // Cards still sliding apart (opening tween, 0.5 s): search the spread of a 3-card row.
            float bestSum = 0;
            for (float spread = 0.2f; spread < 0.99f; spread += 0.05f)
                foreach (bool choice in Options.ChoiceScreens ? (bool[])[false, true] : [false])
                {
                    SlotLayout[] layout = choice ? ScreenLayouts.Choice(3, spread) : ScreenLayouts.Reward(3, spread);
                    Presence p = QuickPresence(img, t, layout, 0.76f, 0.84f);
                    if (p.Present >= 2 && p.Sum > bestSum)
                    {
                        bestSum = p.Sum;
                        chosen = layout;
                        string what = choice ? ", choose-a-card" : "";
                        detail = string.Create(CultureInfo.InvariantCulture, $"3 cards{what}, opening (spread {spread:F2})");
                    }
                }
        }

        if (chosen == null) return ([], "no card row found", ScreenKind.Other);
        List<RecognizedCard> cards = MatchAll(img, t, chosen, hints, trimRow: true);
        if (cards.Count < chosen.Length) detail += $" → {cards.Count} (outer slots empty)";
        ScreenKind kind = RowKind(cards, t) ?? (chosen[0].Kind == CardSlotKind.Choice ? ScreenKind.CardChoice : ScreenKind.RewardCardSelect);
        return (cards, detail, kind);
    }

    /// <summary>
    /// Reward row or choose-a-card row, from where the cards were actually found. The rows differ by 16 px in y
    /// (616 vs 600), which never changes: the opening tween only moves the cards along x, and while it runs a reward
    /// row can sit exactly where a settled choose-a-card row would (bench: a fading-in reward frame fitted the
    /// choose-a-card x positions better). The card box is symmetric about the node centre, so its centre is the
    /// node position at any scale. Null when no card was accepted.
    /// </summary>
    private static ScreenKind? RowKind(List<RecognizedCard> cards, RefTransform t)
    {
        float[] ys = cards.Where(c => c.Accepted && c.Partial == null)
            .Select(c => (c.CardBounds.Y + c.CardBounds.Height / 2f - t.OffsetY) / t.Scale)
            .Order().ToArray();
        if (ys.Length == 0) return null;
        float y = ys[ys.Length / 2];
        float mid = (ScreenLayouts.RewardRowY + ScreenLayouts.ChoiceRowY) / 2;
        return y >= mid ? ScreenKind.RewardCardSelect : ScreenKind.CardChoice;
    }

    /// <summary>
    /// Layout ranking: more slots with a clear card (<see cref="RecognizerOptions.SlotStrongScore"/>), then fewer empty
    /// slots, then the higher NCC sum. Clear cards come first because an empty outer slot of a too-wide hypothesis
    /// can reach the weaker "present" bar on background texture (bench: 0.66 next to a 3-card reward row).
    /// </summary>
    private static bool Better(Presence a, Presence b)
        => a.Strong != b.Strong ? a.Strong > b.Strong : a.Empty != b.Empty ? a.Empty < b.Empty : a.Sum > b.Sum;

    /// <summary>Matches every slot of a layout, then gives covered cards a second chance.</summary>
    /// <param name="img">Integral table of the band the layout lies in.</param>
    /// <param name="t">Reference-to-frame transform.</param>
    /// <param name="chosen">The slots to match.</param>
    /// <param name="hints">Character hint for near-ties.</param>
    /// <param name="trimRow">
    /// A centred row: the n-2 card row sits exactly on the inner slots of the n card row (x = 960 + (i - (n-1)/2) *
    /// spacing), so when neither outer slot holds a card the row is really two cards narrower. Trimming happens before
    /// the covered-card rescue, which must not run on empty background (bench: a 3-card row taken as 5 wide let the
    /// rescue accept a phantom card on the dark left edge).
    /// </param>
    private List<RecognizedCard> MatchAll(IntegralImage img, RefTransform t, SlotLayout[] chosen, RecognitionHints? hints, bool trimRow = false)
    {
        var cards = new RecognizedCard[chosen.Length];
        var boxes = new SlotBox[chosen.Length];
        // Slots are independent and only READ the shared table, so they can be matched on several cores.
        Parallel.For(0, chosen.Length, i => cards[i] = MatchSlot(img, t, chosen[i], 0.76f, 1.04f, hints, out boxes[i]));
        while (trimRow && cards.Length >= 4 && !cards[0].Accepted && !cards[^1].Accepted)
        {
            chosen = chosen[1..^1].Select(s => s with { Index = s.Index - 1 }).ToArray();
            cards = cards[1..^1].Select(c => c with { Slot = c.Slot - 1 }).ToArray();
            boxes = boxes[1..^1];
        }
        // Covered cards get a second chance only on a screen already confirmed by two whole-art matches.
        if (cards.Count(c => c.Accepted) >= 2)
            Parallel.For(0, chosen.Length, i => cards[i] = RescueCovered(img, t, chosen[i], 0.76f, 1.04f, cards[i], boxes[i], hints));
        return [.. cards];
    }

    // ------------------------------------------------------------------ card grid

    private (List<RecognizedCard>, string) RecognizeGrid(in ScreenFrame frame, RefTransform t, int step, RecognitionHints? hints)
    {
        // Band: titles of the top row of a tall grid (y 328.8 - 204 = 125) down to the art bottom of its second row
        // (706.4 + 22 = 728), plus the search radius and the hover lift; x covers the five columns (375..1495 +- 150).
        using IntegralImage img = IntegralImage.Build(frame, t.ToPixelRect(new RectF(215, 70, 1440, 680)), step);
        SlotLayout[]? chosen = null;
        Presence best = default;
        foreach (bool twoRows in (bool[])[false, true])
        {
            SlotLayout[] layout = ScreenLayouts.Grid(twoRows);
            Presence p = QuickPresence(img, t, layout, 0.76f, 1.04f);
            if (p.Present < 2) continue;
            if (chosen == null || Better(p, best))
            {
                best = p;
                chosen = layout;
            }
        }
        if (chosen == null) return ([], "no grid");
        string detail = chosen.Length > ScreenLayouts.GridColumns ? "grid, 2+ rows" : "grid, 1 row";
        return (MatchAll(img, t, chosen, hints), detail);
    }

    // ------------------------------------------------------------------ shop

    private List<RecognizedCard> RecognizeShop(in ScreenFrame frame, RefTransform t, int step, RecognitionHints? hints)
    {
        // Two bands: the character-card row (centre y 383) and the colourless row (centre y 756).
        using IntegralImage top = IntegralImage.Build(frame, t.ToPixelRect(new RectF(285, 190, 1355, 235)), step);
        using IntegralImage bottom = IntegralImage.Build(frame, t.ToPixelRect(new RectF(375, 563, 535, 235)), step);
        SlotLayout[] slots = ScreenLayouts.Shop();
        var cards = new RecognizedCard[slots.Length];
        var boxes = new SlotBox[slots.Length];
        IntegralImage ImageOf(int i) => slots[i].Kind == CardSlotKind.ShopColorless ? bottom : top;
        ScreenFrame f = frame; // an `in` parameter cannot be captured by the lambda; the struct copy is cheap
        // 7 independent slots reading two immutable tables: one task per slot (~2-3 ms each at 4K).
        Parallel.For(0, slots.Length, i => cards[i] = MatchSlot(ImageOf(i), t, slots[i], 0.61f, 0.84f, hints, out boxes[i], f));
        if (cards.Any(c => c.Accepted))
            Parallel.For(0, slots.Length, i => cards[i] = RescueCovered(ImageOf(i), t, slots[i], 0.61f, 0.84f, cards[i], boxes[i], hints, f));
        // A bought card leaves its slot empty: bare rug where the art was and no price label under it.
        for (int i = 0; i < slots.Length; i++)
        {
            if (cards[i].Accepted) continue;
            float rug = RugShare(ImageOf(i), t, slots[i]);
            cards[i] = cards[i] with { RugShare = rug, SlotEmpty = rug >= Options.EmptySlotRugShare && cards[i].PriceReading == null };
        }
        return [.. cards];
    }

    /// <summary>Share of the slot's resting art box in bare rug colours (the merchant relic reader's rug test), 16x12 cells.</summary>
    private static float RugShare(IntegralImage img, RefTransform t, SlotLayout slot)
    {
        const int gw = 16, gh = 12;
        float[] rgb = new float[gw * gh * 3];
        img.SampleGrid(t.ToPixels(CardGeometry.Place(CardGeometry.Art, slot.CenterX, slot.CenterY, slot.SmallScale)), gw, gh, rgb);
        int rug = 0;
        for (int i = 0; i < rgb.Length; i += 3)
            if (global::Sts2Overlay.Vision.Relics.RelicRecognizer.IsRug(rgb[i], rgb[i + 1], rgb[i + 2])) rug++;
        return rug / (float)(gw * gh);
    }

    // ------------------------------------------------------------------ per-slot matching

    /// <summary>
    /// How many slots of a layout look like a card: best coarse NCC over the whole database at the small and
    /// the hovered scale. ~0.3 ms per slot; used only to choose between layout hypotheses.
    /// </summary>
    private readonly record struct Presence(int Present, int Strong, int Empty, float Sum);

    private Presence QuickPresence(IntegralImage img, RefTransform t, SlotLayout[] layout, float minScale, float maxScale)
    {
        Span<float> v = stackalloc float[Ncc.CoarseLength];
        int present = 0, strong = 0;
        float sum = 0;
        foreach (SlotLayout slot in layout)
        {
            float best = 0;
            foreach (float s in (float[])[slot.SmallScale, slot.HoverScale])
            {
                if (s < minScale || s > maxScale) continue;
                if (!ExtractAt(img, t, slot.CenterX, slot.CenterY, s, v)) continue;
                ArtMatch[] top = _db.TopK(v, 1, Allowed);
                if (top.Length > 0) best = Math.Max(best, top[0].Score);
            }
            sum += best;
            if (best >= Options.SlotPresentScore) present++;
            if (best >= Options.SlotStrongScore) strong++;
        }
        return new Presence(present, strong, layout.Length - present, sum);
    }

    private bool Allowed(ArtEntry e) => _offerable[e.Index];

    /// <summary>
    /// Diagnostics (VisionBench probe): the best whole-art NCC of one card over a grid of boxes around (cx, cy, scale),
    /// in reference units. Shows whether a miss is a box-search problem or the card really is not visible.
    /// </summary>
    internal (float Score, float X, float Y, float Scale) Probe(in ScreenFrame frame, string cardId, float cx, float cy, float scale, float radius)
    {
        RefTransform t = RefTransform.For(frame.Width, frame.Height);
        using IntegralImage img = IntegralImage.Build(frame, new PixelRect(0, 0, frame.Width, frame.Height), 1);
        int e = _db.Entries.First(x => x.CardId == cardId).Index;
        float[] buf = new float[Ncc.FullLength];
        (float Score, float X, float Y, float Scale) best = (float.NegativeInfinity, cx, cy, scale);
        for (float s = scale - 0.06f; s <= scale + 0.06f + 1e-4f; s += 0.01f)
            for (float dy = -radius; dy <= radius; dy += 1)
                for (float dx = -radius; dx <= radius; dx += 1)
                    if (ExtractAt(img, t, cx + dx, cy + dy, s, buf))
                    {
                        float v = Ncc.Dot(buf, _db.FullVector(e));
                        if (v > best.Score) best = (v, cx + dx, cy + dy, s);
                    }
        return best;
    }

    private static bool ExtractAt(IntegralImage img, RefTransform t, float cx, float cy, float scale, Span<float> dst)
    {
        RectF art = t.ToPixels(CardGeometry.Place(CardGeometry.Art, cx, cy, scale));
        int w = dst.Length == Ncc.FullLength ? Ncc.FullW : Ncc.CoarseW;
        int h = dst.Length == Ncc.FullLength ? Ncc.FullH : Ncc.CoarseH;
        return Ncc.Extract(img, art, w, h, dst);
    }

    /// <summary>
    /// Finds the exact card box around a layout slot and identifies the card.
    /// Coarse-to-fine: (1) whole-database coarse scan at the nominal box keeps the best few candidates;
    /// (2) sweep the node scale (hover/unhover tween) at the nominal centre; (3) 4-px grid of offsets;
    /// (4) 1-px refinement. Steps 2-4 only score the kept candidates, so each probe costs ~16 short dot
    /// products instead of a full scan. (5) One full-resolution scan at the best box decides the card.
    /// </summary>
    private RecognizedCard MatchSlot(IntegralImage img, RefTransform t, SlotLayout slot, float minScale, float maxScale, RecognitionHints? hints,
        out SlotBox box, ScreenFrame? priceFrame = null)
    {
        box = default;
        Span<float> coarse = stackalloc float[Ncc.CoarseLength];

        // (1) candidates
        var candidates = new HashSet<int>();
        foreach (float s in (float[])[slot.SmallScale, slot.HoverScale])
        {
            if (!ExtractAt(img, t, slot.CenterX, slot.CenterY, s, coarse)) continue;
            foreach (ArtMatch m in _db.TopK(coarse, Options.SearchCandidates, Allowed)) candidates.Add(m.EntryIndex);
        }
        if (candidates.Count == 0) return Empty(t, slot);
        int[] cand = candidates.ToArray();

        float bestScore = float.NegativeInfinity, bx = slot.CenterX, by = slot.CenterY, bs = slot.SmallScale;
        void Try(float cx, float cy, float s, Span<float> buf)
        {
            if (!ExtractAt(img, t, cx, cy, s, buf)) return;
            float score = _db.BestOf(buf, cand, out _);
            if (score > bestScore)
            {
                bestScore = score;
                bx = cx;
                by = cy;
                bs = s;
            }
        }

        // (2) scale sweep at the layout centre
        for (float s = minScale; s <= maxScale + 1e-4f; s += 0.02f) Try(slot.CenterX, slot.CenterY, s, coarse);
        // (3) coarse offsets
        float r = Options.SearchRadius;
        float cx0 = bx, cy0 = by, s0 = bs;
        for (float dy = -r; dy <= r + 1e-3f; dy += 4)
            for (float dx = -r; dx <= r + 1e-3f; dx += 4)
                Try(cx0 + dx, cy0 + dy, s0, coarse);
        // (3b) Nothing fits well near the layout position: a reward card can sit higher than the layout says
        // (hover lift / click animation, seen 26 px up at scale 1.0). Search a taller window, only then.
        if (!slot.Kind.IsShop() && bestScore < Options.WideSearchBelow)
        {
            // The candidates came from the layout position; a lifted card may not be among them. Add the best
            // matches seen from a few lifted positions first.
            foreach (float s in (float[])[slot.SmallScale, slot.HoverScale])
                for (float dy = -Options.WideSearchUp * 0.75f; dy < -1; dy += Options.WideSearchUp / 4)
                    if (ExtractAt(img, t, slot.CenterX, slot.CenterY + dy, s, coarse))
                        foreach (ArtMatch m in _db.TopK(coarse, Options.SearchCandidates / 2, Allowed)) candidates.Add(m.EntryIndex);
            cand = candidates.ToArray();
            foreach (float s in (float[])[slot.SmallScale, slot.HoverScale])
                for (float dy = -Options.WideSearchUp; dy <= r + 1e-3f; dy += 4)
                    for (float dx = -r; dx <= r + 1e-3f; dx += 4)
                        Try(slot.CenterX + dx, slot.CenterY + dy, s, coarse);
        }
        // (3c) Same for a shop card: the hovered card (scale 0.8, the merchant's hand pointing at it) was seen
        // 15 px right of the layout position. Search a wider square, only when nothing fits near the layout.
        else if (slot.Kind.IsShop() && bestScore < Options.WideSearchBelow)
        {
            float w = Options.WideSearchShop;
            foreach (float s in (float[])[slot.SmallScale, slot.HoverScale])
                for (float dy = -w / 2; dy <= w / 2 + 1e-3f; dy += w / 2)
                    for (float dx = -w / 2; dx <= w / 2 + 1e-3f; dx += w / 2)
                        if ((dx != 0 || dy != 0) && ExtractAt(img, t, slot.CenterX + dx, slot.CenterY + dy, s, coarse))
                            foreach (ArtMatch m in _db.TopK(coarse, Options.SearchCandidates / 2, Allowed)) candidates.Add(m.EntryIndex);
            cand = candidates.ToArray();
            foreach (float s in (float[])[slot.SmallScale, slot.HoverScale])
                for (float dy = -w; dy <= w + 1e-3f; dy += 4)
                    for (float dx = -w; dx <= w + 1e-3f; dx += 4)
                        Try(slot.CenterX + dx, slot.CenterY + dy, s, coarse);
        }
        // (4) fine offsets, then a final scale nudge
        cx0 = bx;
        cy0 = by;
        for (float dy = -2; dy <= 2; dy++)
            for (float dx = -2; dx <= 2; dx++)
                Try(cx0 + dx, cy0 + dy, s0, coarse);
        Try(bx, by, bs - 0.01f, coarse);
        Try(bx, by, bs + 0.01f, coarse);

        // (5) full-resolution identification over the whole (allowed) database
        float[] full = new float[Ncc.FullLength];
        if (!ExtractAt(img, t, bx, by, bs, full)) return Empty(t, slot);
        box = new SlotBox(full, bx, by, bs);
        ArtMatch[] top = _db.TopK(full, 8, Allowed);
        if (top.Length == 0) return Empty(t, slot);

        (ArtMatch pick, string? ambiguous) = Decide(top, hints);
        float runnerUp = top.Where(m => m.CardId != pick.CardId && !_db.TwinsOf(pick.CardId).Contains(m.CardId))
            .Select(m => m.Score).DefaultIfEmpty(-1).Max();
        float margin = pick.Score - runnerUp;
        bool accepted = (pick.Score >= Options.AcceptScore && margin >= Options.AcceptMargin)
                        || (pick.Score >= Options.CoveredScore && margin >= Options.CoveredMargin);
        if (accepted && Options.PartialMatching)
        {
            // Confirm it tile by tile (a rejected slot still gets the partial-match rescue). One per-tile pass
            // over a single entry: ~2k multiply-adds, negligible next to the search.
            (int fitTiles, float fitScore) = _blocks.Value.Fit(full, pick.EntryIndex, Options.BlockStage);
            accepted = fitTiles >= Options.TileFitMinTiles && fitScore >= Options.TileFitScore;
        }

        (float green, float covered) = UpgradeDetector.TitleEvidence(img, t, bx, by, bs);
        bool upgraded = green >= Options.UpgradeFraction;
        // A clean art match and no tooltip panel over the title: a title without green really is not upgraded.
        bool upgradeKnown = upgraded || (pick.Score >= Options.UnoccludedScore && covered < Options.TitleCoveredFraction);
        PixelRect? price = !slot.Kind.IsShop() ? null : t.ToPixelRect(ScreenLayouts.ShopPrice(bx, by, bs));
        PriceReading? reading = ReadPrice(priceFrame, slot, pick.CardId, bx, by, bs);
        return new RecognizedCard(slot.Index, slot.Kind, pick.CardId, upgraded, pick.Score, margin, accepted,
            ambiguous, green, upgradeKnown, bs,
            t.ToPixelRect(CardGeometry.Place(CardGeometry.Art, bx, by, bs)),
            t.ToPixelRect(CardGeometry.Place(CardGeometry.Card, bx, by, bs)),
            price, reading?.Value, top.Take(3).ToArray())
        {
            PriceReading = reading,
        };
    }

    /// <summary>
    /// Shop: read the price label under the card box (it moves and scales with the card). A card mostly under the
    /// merchant's hand gets an unreliable box, but its label usually still sits at the slot's resting place: retry
    /// there (resting, then hovered size).
    /// </summary>
    private PriceReading? ReadPrice(ScreenFrame? priceFrame, SlotLayout slot, string cardId, float bx, float by, float bs)
    {
        if (priceFrame is not { } pf || _priceReader == null || !slot.Kind.IsShop()) return null;
        string? rarity = Options.Catalog?.Find(cardId)?.Rarity;
        PriceReading? reading = _priceReader.Read(pf, bx, by, bs, slot.Kind, rarity);
        foreach (float s in (float[])[slot.SmallScale, slot.HoverScale])
        {
            if (reading?.Value != null) break;
            PriceReading? retry = _priceReader.Read(pf, slot.CenterX, slot.CenterY, s, slot.Kind, rarity);
            if (retry?.Value != null || reading == null) reading = retry;
        }
        return reading;
    }

    /// <summary>
    /// Picks the card from the ranked candidates: a candidate from the hinted character's pool (or colourless)
    /// wins near-ties, and identical-art twins are resolved by pool when possible.
    /// </summary>
    internal (ArtMatch Pick, string? Ambiguous) Decide(ArtMatch[] top, RecognitionHints? hints)
    {
        string? charPool = hints?.CharacterId is { } ch ? VisionCatalog.PoolOfCharacter(ch) : null;
        bool Preferred(ArtMatch m) => charPool != null &&
            (_db.Entries[m.EntryIndex].Pool == charPool || _db.Entries[m.EntryIndex].Pool == VisionCatalog.ColorlessPool);

        ArtMatch pick = top[0];
        if (charPool != null && !Preferred(pick))
        {
            foreach (ArtMatch m in top)
            {
                if (m.Score < pick.Score - Options.HintTolerance) break;
                if (Preferred(m))
                {
                    pick = m;
                    break;
                }
            }
        }

        string? ambiguous = null;
        IReadOnlySet<string> twins = _db.TwinsOf(pick.CardId);
        foreach (ArtMatch m in top)
        {
            if (m.CardId == pick.CardId || !twins.Contains(m.CardId) || m.Score < pick.Score - 0.03f) continue;
            if (charPool != null && Preferred(pick) && !Preferred(m)) continue; // resolved by the hint
            if (charPool != null && Preferred(m) && !Preferred(pick))
            {
                pick = m;
                continue;
            }
            ambiguous = m.CardId;
        }
        return (pick, ambiguous);
    }

    /// <summary>
    /// Second chance for a slot the whole-art NCC rejected (partly covered, cross-fading): the tile-based
    /// partial match (<see cref="TileMatcher"/>), first on 6x4 blocks, then on single rows. The box found by the
    /// whole-art search may be off when a big part of the card is covered, so it is first refined on the visible
    /// tiles of the likely candidates. Accepted slots and slots without a usable thumbnail are returned unchanged;
    /// a rejected partial match is still attached for the bench and the debug view.
    /// </summary>
    private RecognizedCard RescueCovered(IntegralImage img, RefTransform t, SlotLayout slot, float minScale, float maxScale,
        RecognizedCard c, SlotBox box, RecognitionHints? hints, ScreenFrame? priceFrame = null)
    {
        if (!Options.PartialMatching || c.Accepted || box.Full == null) return c;

        // Candidates for the refinement: what the whole-art scan and both partial scans at the first box suggest.
        string[] firstTop = c.Candidates.Select(m => m.CardId).ToArray();
        PartialMatch? firstBlocks = EvaluatePartial(box.Full, firstTop, _blocks.Value, Options.BlockStage, Allowed, hints, out ArtMatch[] r1, out _);
        PartialMatch? firstRows = EvaluatePartial(box.Full, firstTop, _rows.Value, Options.RowStage, Allowed, hints, out ArtMatch[] r2, out _);
        PartialMatch? firstBest = firstBlocks ?? firstRows;
        int[] cand = c.Candidates.Take(3).Concat(r1.Take(3)).Concat(r2.Take(3)).Select(m => m.EntryIndex).Distinct().ToArray();
        if (cand.Length == 0) return firstBest == null ? c : c with { Partial = firstBest };

        float[] buf = new float[Ncc.FullLength];
        float bestObj = float.NegativeInfinity, bx = box.X, by = box.Y, bs = box.Scale;
        void Try(float x, float y, float s)
        {
            if (s < minScale - 1e-4f || s > maxScale + 1e-4f || !ExtractAt(img, t, x, y, s, buf)) return;
            // Sum of agreeing tile NCCs on both grids: more visible area and better fit both count.
            float obj = _blocks.Value.Agreement(buf, cand, Options.BlockStage) + _rows.Value.Agreement(buf, cand, Options.RowStage);
            if (obj > bestObj)
            {
                bestObj = obj;
                bx = x;
                by = y;
                bs = s;
            }
        }
        Try(box.X, box.Y, box.Scale);
        for (float s = minScale; s <= maxScale + 1e-4f; s += 0.02f) Try(box.X, box.Y, s);
        float cx0 = bx, cy0 = by;
        for (float dy = -6; dy <= 6; dy += 2)
            for (float dx = -6; dx <= 6; dx += 2)
                Try(cx0 + dx, cy0 + dy, bs);
        cx0 = bx;
        cy0 = by;
        for (float dy = -1; dy <= 1; dy++)
            for (float dx = -1; dx <= 1; dx++)
                Try(cx0 + dx, cy0 + dy, bs);
        Try(bx, by, bs - 0.01f);
        Try(bx, by, bs + 0.01f);

        // Boxes to decide at: the refined box, then the resting layout box (a card covered by its neighbour's
        // tooltip is itself not hovered, so it sits exactly where the layout says), with +-1 px vertical nudges
        // there because a thin visible strip is only a few thumbnail rows tall and must not straddle them.
        var boxes = new List<(float X, float Y, float S)> { (bx, by, bs) };
        foreach (float dy in (float[])[0, -1, 1])
            if (MathF.Abs(slot.CenterX - bx) > 0.5f || MathF.Abs(slot.CenterY + dy - by) > 0.5f || MathF.Abs(slot.SmallScale - bs) > 0.005f)
                boxes.Add((slot.CenterX, slot.CenterY + dy, slot.SmallScale));

        float[] full = new float[Ncc.FullLength];
        PartialMatch? partial = null;
        ArtMatch[] ranking = [];
        string? ambiguous = null;
        foreach ((float x, float y, float sc) in boxes)
        {
            if (!ExtractAt(img, t, x, y, sc, full)) continue;
            (PartialMatch? p, ArtMatch[] rk, string? amb) = DecideCovered(full, hints);
            if (p == null) continue;
            if (partial == null || p.Accepted || (!partial.Accepted && p.Margin > partial.Margin))
            {
                partial = p;
                ranking = rk;
                ambiguous = amb;
                (bx, by, bs) = (x, y, sc);
            }
            if (p.Accepted) break;
        }
        if (partial == null) return firstBest == null ? c : c with { Partial = firstBest };
        if (!partial.Accepted) return c with { Partial = partial };
        ExtractAt(img, t, bx, by, bs, full);

        ArtMatch pick = ranking.First(m => m.CardId == partial.CardId);
        // Whole-art NCC of the chosen card: what it scores with the covered part included. It stays low, so
        // UpgradeKnown stays false unless green was actually seen (the title may be the covered part).
        float whole = Ncc.Dot(full, _db.FullVector(pick.EntryIndex));
        (float green, float covered) = UpgradeDetector.TitleEvidence(img, t, bx, by, bs);
        bool upgraded = green >= Options.UpgradeFraction;
        // The price was read for the whole-art pick at the first box; read it again for this card at its box.
        PriceReading? reading = ReadPrice(priceFrame, slot, partial.CardId, bx, by, bs);
        return c with
        {
            CardId = partial.CardId,
            Upgraded = upgraded,
            Score = whole,
            Margin = partial.Margin,
            Accepted = true,
            AmbiguousWith = ambiguous,
            UpgradeEvidence = green,
            UpgradeKnown = upgraded || (whole >= Options.UnoccludedScore && covered < Options.TitleCoveredFraction),
            NodeScale = bs,
            ArtBounds = t.ToPixelRect(CardGeometry.Place(CardGeometry.Art, bx, by, bs)),
            CardBounds = t.ToPixelRect(CardGeometry.Place(CardGeometry.Card, bx, by, bs)),
            PriceBounds = !slot.Kind.IsShop() ? null : t.ToPixelRect(ScreenLayouts.ShopPrice(bx, by, bs)),
            Candidates = ranking.Take(3).ToArray(),
            Partial = partial,
            Price = reading?.Value,
            PriceReading = reading,
        };
    }

    /// <summary>Candidate filter of a partial stage: offerable cards, narrowed to the hinted character's pool + colourless.</summary>
    private Func<ArtEntry, bool> StageFilter(PartialStage stage, RecognitionHints? hints)
    {
        string? pool = stage.HintedPoolOnly && hints?.CharacterId is { } ch ? VisionCatalog.PoolOfCharacter(ch) : null;
        if (pool == null) return Allowed;
        return e => _offerable[e.Index] && (e.Pool == pool || e.Pool == VisionCatalog.ColorlessPool);
    }

    /// <summary>Blocks first, then rows; returns the accepted match, or the rejected one that came closest.</summary>
    private (PartialMatch?, ArtMatch[], string?) DecideCovered(float[] full, RecognitionHints? hints)
    {
        // The whole-art ranking at this box (for the agreement condition of the block stage).
        string[] wholeTop = _db.TopK(full, 8, Allowed).Select(m => m.CardId).ToArray();
        PartialMatch? blocks = EvaluatePartial(full, wholeTop, _blocks.Value, Options.BlockStage, StageFilter(Options.BlockStage, hints), hints,
            out ArtMatch[] ranking, out string? ambiguous);
        if (blocks?.Accepted == true) return (blocks, ranking, ambiguous);
        PartialMatch? rows = EvaluatePartial(full, wholeTop, _rows.Value, Options.RowStage, StageFilter(Options.RowStage, hints), hints,
            out ArtMatch[] rowRanking, out string? rowAmbiguous);
        return rows != null && (rows.Accepted || blocks == null || rows.Margin > blocks.Margin)
            ? (rows, rowRanking, rowAmbiguous)
            : (blocks, ranking, ambiguous);
    }

    /// <summary>
    /// The partial-match decision for one thumbnail on one grid (also used by VisionBench's stress test).
    /// Choosing "the tiles where a card fits" flatters every card, so acceptance needs all of:
    /// <list type="number">
    /// <item>its NCC over its agreeing tiles is high (<see cref="PartialStage.AcceptScore"/>);</item>
    /// <item>it beats every other card scored on exactly the same tiles by <see cref="PartialStage.AcceptMargin"/>;</item>
    /// <item>for stages with <see cref="PartialStage.WholeTopK"/> &gt; 0: the whole-art ranking
    /// (<paramref name="wholeTop"/>, best first) also has it among its first k - two views of the evidence agree.</item>
    /// </list>
    /// Returns null when no card fits on enough tiles.
    /// </summary>
    internal PartialMatch? EvaluatePartial(ReadOnlySpan<float> full, IReadOnlyList<string> wholeTop, TileMatcher matcher, PartialStage stage,
        Func<ArtEntry, bool> allowed, RecognitionHints? hints, out ArtMatch[] ranking, out string? ambiguous)
    {
        ambiguous = null;
        if (matcher.Best(full, allowed, stage, out ranking) is not { } best || ranking.Length == 0) return null;
        (ArtMatch pick, ambiguous) = Decide(ranking, hints);
        string pickId = pick.CardId;
        IReadOnlySet<string> twins = _db.TwinsOf(pickId);
        float runnerUp = ranking.Where(m => m.CardId != pickId && !twins.Contains(m.CardId))
            .Select(m => m.Score).DefaultIfEmpty(-1).Max();
        float margin = pick.Score - runnerUp;
        bool agrees = stage.WholeTopK <= 0 || margin >= stage.FreeMargin || wholeTop.Take(stage.WholeTopK).Any(id => id == pickId || twins.Contains(id));
        bool accepted = agrees && pick.Score >= stage.AcceptScore && margin >= stage.AcceptMargin;
        return new PartialMatch(pickId, pick.Score, margin, best.Visible, matcher.Grid.Count, accepted, matcher.Grid == TileGrid.Rows ? "rows" : "blocks");
    }

    /// <summary>The two partial-match grids (VisionBench stress test).</summary>
    internal TileMatcher BlockMatcher => _blocks.Value;
    internal TileMatcher RowMatcher => _rows.Value;

    private static RecognizedCard Empty(RefTransform t, SlotLayout slot) =>
        new(slot.Index, slot.Kind, null, false, 0, 0, false, null, 0, false, slot.SmallScale,
            t.ToPixelRect(CardGeometry.Place(CardGeometry.Art, slot.CenterX, slot.CenterY, slot.SmallScale)),
            t.ToPixelRect(CardGeometry.Place(CardGeometry.Card, slot.CenterX, slot.CenterY, slot.SmallScale)),
            null, null, []);
}

/// <summary>
/// Upgraded cards draw their title in StsColors.green = #7FFF00 (NCard.UpdateTitleLabel), normal cards in
/// cream #FFF6E2. We count title-area pixels that are clearly chartreuse: green dominant, red at most ~60 %
/// of green, almost no blue. Ratios rather than absolute values keep it working while the card fades in
/// from black. The tightest look-alike is the rare (gold) banner, whose shading reaches (120,165,28):
/// red/green 0.73, so the red limit sits at 0.62 (measured: rare banner 0 %, upgraded titles 5-10 %).
/// </summary>
public static class UpgradeDetector
{
    public static bool IsUpgradeGreen(float r, float g, float b) =>
        g >= 50 && r <= 0.62f * g && b <= 0.35f * g;

    /// <summary>The dark slate body of a hover tooltip panel (about RGB 35, 45, 52): bluish, unlike a card fading in from black.</summary>
    public static bool IsTooltipSlate(float r, float g, float b) =>
        r is >= 18 and <= 56 && g - r is >= 3 and <= 20 && b - r is >= 8 and <= 28;

    internal static float GreenFraction(IntegralImage img, RefTransform t, float cx, float cy, float scale) =>
        TitleEvidence(img, t, cx, cy, scale).Green;

    /// <summary>
    /// Share of the title box in upgrade green, and share in tooltip slate (a neighbour's hover tooltip lying over
    /// the name, so "no green" proves nothing).
    /// </summary>
    internal static (float Green, float Covered) TitleEvidence(IntegralImage img, RefTransform t, float cx, float cy, float scale)
    {
        RectF title = t.ToPixels(CardGeometry.Place(CardGeometry.Title, cx, cy, scale));
        // About one sample per table cell: thin glyph strokes must not be averaged away.
        int gw = Math.Clamp((int)(title.Width / img.Step), 8, 400);
        int gh = Math.Clamp((int)(title.Height / img.Step), 4, 120);
        float[] rgb = new float[gw * gh * 3];
        img.SampleGrid(title, gw, gh, rgb);
        int green = 0, slate = 0;
        for (int i = 0; i < rgb.Length; i += 3)
        {
            if (IsUpgradeGreen(rgb[i], rgb[i + 1], rgb[i + 2])) green++;
            else if (IsTooltipSlate(rgb[i], rgb[i + 1], rgb[i + 2])) slate++;
        }
        return (green / (float)(gw * gh), slate / (float)(gw * gh));
    }
}

/// <summary>Where the whole-art search put a slot's card (reference units) and the thumbnail it extracted there.</summary>
internal readonly record struct SlotBox(float[]? Full, float X, float Y, float Scale);
