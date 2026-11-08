using System.Globalization;
using System.Text;
using Sts2Overlay.Vision;

namespace VisionBench;

/// <summary>
/// Stress test for the occlusion-robust (partial) match: take cards the recogniser identified cleanly on real
/// captures, cover part of their art artificially, and check two things per covered thumbnail:
/// <list type="bullet">
/// <item><b>recall</b>: is the right card still accepted?</item>
/// <item><b>impostor</b>: remove the right card (and its identical-art twins) from the candidates, as if it were
/// missing from the database. Anything accepted now is by construction a WRONG card. Must stay at 0.</item>
/// </list>
/// The real bench has only a handful of covered cards; this turns every clean card into dozens of covered ones,
/// so a threshold that would let wrong cards through shows up here first. Several threshold variants are scored
/// on the same coverings so they can be compared directly.
/// </summary>
static class Stress
{
    private static readonly string[] Sides = ["top", "bottom", "left", "right", "top-right", "bottom-left"];
    private static readonly string[] Occluders = ["tooltip", "hand", "other-art"];
    private static readonly float[] Fractions = [0.3f, 0.45f, 0.6f];
    /// <summary>Tooltip stacks that leave only a band of this many thumbnail rows (of 36) visible.</summary>
    private static readonly int[] StripRows = [3, 4, 6, 9];

    /// <param name="FreeMargin">At this margin the whole-art top-k condition is waived.</param>
    private sealed record Rule(string Name, bool Rows, int TopK, float S, float M, float FreeMargin = float.PositiveInfinity);

    private static readonly Rule[] Rules =
    [
        new("blocks top8 .85/.30", false, 8, 0.85f, 0.30f),
        new("blocks top8 .85/.25", false, 8, 0.85f, 0.25f),
        new("blocks top8 .80/.25", false, 8, 0.80f, 0.25f),
        new("blocks none .85/.30", false, 0, 0.85f, 0.30f),
        new("blocks top8 .85/.30 or .40", false, 8, 0.85f, 0.30f, 0.40f),
        new("rows none .90/.30", true, 0, 0.90f, 0.30f),
        new("rows none .90/.20", true, 0, 0.90f, 0.20f),
        new("rows none .90/.15", true, 0, 0.90f, 0.15f),
        new("rows none .93/.15", true, 0, 0.93f, 0.15f),
        new("rows none .93/.18", true, 0, 0.93f, 0.18f),
        new("rows none .92/.20", true, 0, 0.92f, 0.20f),
        new("rows none .85/.20", true, 0, 0.85f, 0.20f),
    ];

    /// <param name="poolOnly">Candidates limited to the true card's pool + colourless, as the recogniser does with a
    /// character hint (<see cref="PartialStage.HintedPoolOnly"/>).</param>
    public static string Run(CardArtDatabase db, CardRecognizer rec, IReadOnlyList<string> captures, Action<string> log, bool poolOnly = false)
    {
        var rng = new Random(12345);
        bool[] offerable = db.Entries.Select(e => VisionCatalog.OfferablePools.Contains(e.Pool)).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Lenient stages: every decision is re-made below with each rule's thresholds.
        PartialStage blocks = rec.Options.BlockStage with { AcceptScore = 0, AcceptMargin = -1, WholeTopK = 0 };
        PartialStage rows = rec.Options.RowStage with { AcceptScore = 0, AcceptMargin = -1, WholeTopK = 0 };

        var kinds = Occluders.SelectMany(o => Fractions.Select(f => $"{o} {f:P0}")).Concat(StripRows.Select(k => $"strip {k} rows")).ToList();
        var recall = new Dictionary<(string Kind, int Rule), int>();
        var impostor = new Dictionary<(string Kind, int Rule), int>();
        var wrongPresent = new int[Rules.Length]; // a different card accepted while the right one WAS a candidate
        var total = new Dictionary<string, int>();
        var wholeRecall = new Dictionary<string, int>();
        var wholeImpostor = new Dictionary<string, int>();
        var gatedRecall = new Dictionary<string, int>();
        var gatedImpostor = new Dictionary<string, int>();
        int wholeWrongPresent = 0, gatedWrongPresent = 0;
        var wholeWrongs = new List<string>();
        var closest = new List<(int Rule, float S, float M, string Text)>();
        int cards = 0;

        foreach (string path in captures)
        {
            ScreenFrame frame = ImageLoader.Load(path);
            ScreenRecognition r = rec.Recognize(frame);
            if (r.Kind == ScreenKind.Other) continue;
            string name = Path.GetFileName(path);
            foreach (RecognizedCard c in r.Cards)
            {
                // Clean, unambiguous cards only; each card id once per capture minute (frames of one visit are alike).
                if (c.Score < 0.9f || c.Partial != null || c.CardId == null) continue;
                if (!seen.Add($"{c.CardId}|{name[..Math.Min(20, name.Length)]}")) continue;
                float[] raw = RawThumbnail(frame, c.ArtBounds);
                cards++;
                string truth = c.CardId;
                IReadOnlySet<string> twins = db.TwinsOf(truth);
                string truthPool = db.Entries.First(e => e.CardId == truth).Pool;
                bool InPool(ArtEntry e) => !poolOnly || e.Pool == truthPool || e.Pool == VisionCatalog.ColorlessPool;
                bool AllowedAll(ArtEntry e) => offerable[e.Index] && InPool(e);
                bool AllowedImpostor(ArtEntry e) => offerable[e.Index] && InPool(e) && e.CardId != truth && !twins.Contains(e.CardId);

                foreach ((string kind, Action<float[]> cover) in Coverings(db, rng))
                {
                    float[] v = (float[])raw.Clone();
                    cover(v);
                    if (!Ncc.Normalise(v)) continue;
                    total[kind] = total.GetValueOrDefault(kind) + 1;

                    for (int pass = 0; pass < 2; pass++)
                    {
                        Func<ArtEntry, bool> allowed = pass == 0 ? AllowedAll : AllowedImpostor;
                        (string? whole, bool wholeAcc, string[] wholeTop, float wholeScore) = Whole(db, rec, v, allowed);
                        PartialMatch? pb = rec.EvaluatePartial(v, wholeTop, rec.BlockMatcher, blocks, allowed, null, out _, out _);
                        PartialMatch? pr = rec.EvaluatePartial(v, wholeTop, rec.RowMatcher, rows, allowed, null, out _, out _);
                        if (pass == 0 && wholeAcc && whole == truth) wholeRecall[kind] = wholeRecall.GetValueOrDefault(kind) + 1;
                        if (pass == 1 && wholeAcc) wholeImpostor[kind] = wholeImpostor.GetValueOrDefault(kind) + 1;
                        if (pass == 0 && wholeAcc && whole != truth && !twins.Contains(whole!))
                        {
                            wholeWrongPresent++;
                            wholeWrongs.Add($"{name} {truth} {kind} -> {whole} s={wholeScore:F3} pb={pb}");
                        }
                        // Gated: the low-score "covered" path also needs the block partial winner to be the same card.
                        // Gated: below the "clean" score the pick must also fit tile by tile (the recogniser's rule).
                        bool gated = wholeAcc;
                        if (gated && wholeScore < rec.Options.UnoccludedScore)
                        {
                            (int fv, float fs) = rec.BlockMatcher.Fit(v, db.Entries.First(e => e.CardId == whole).Index, rec.Options.BlockStage);
                            gated = fv >= rec.Options.TileFitMinTiles && fs >= rec.Options.TileFitScore;
                        }
                        if (pass == 0 && gated && whole == truth) gatedRecall[kind] = gatedRecall.GetValueOrDefault(kind) + 1;
                        if (pass == 0 && gated && whole != truth && !twins.Contains(whole!)) gatedWrongPresent++;
                        if (pass == 1 && gated) gatedImpostor[kind] = gatedImpostor.GetValueOrDefault(kind) + 1;
                        for (int k = 0; k < Rules.Length; k++)
                        {
                            Rule rule = Rules[k];
                            PartialMatch? p = rule.Rows ? pr : pb;
                            bool acc = p != null && p.Score >= rule.S && p.Margin >= rule.M &&
                                       (rule.TopK == 0 || p.Margin >= rule.FreeMargin || wholeTop.Take(rule.TopK).Contains(p.CardId));
                            if (pass == 0 && ((wholeAcc && whole == truth) || (acc && p!.CardId == truth)))
                                recall[(kind, k)] = recall.GetValueOrDefault((kind, k)) + 1;
                            if (pass == 0 && acc && p!.CardId != truth && !twins.Contains(p.CardId)) wrongPresent[k]++;
                            if (pass == 1 && acc)
                                impostor[(kind, k)] = impostor.GetValueOrDefault((kind, k)) + 1;
                            if (pass == 1 && p != null && p.Score >= rule.S - 0.05f && (rule.TopK == 0 || p.Margin >= rule.FreeMargin || wholeTop.Take(rule.TopK).Contains(p.CardId)))
                                closest.Add((k, p.Score, p.Margin, $"{name} {truth} {kind} → {p}"));
                        }
                    }
                }
            }
            log($"{name}: {cards} clean cards so far");
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Partial-match stress test ({cards} clean cards, {total.Values.Sum()} covered thumbnails{(poolOnly ? ", candidates: own pool + colourless" : ", candidates: all offerable")})");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Stages: blocks agree {rec.Options.BlockStage.Agree} min {rec.Options.BlockStage.MinTiles}; rows agree {rec.Options.RowStage.Agree} min {rec.Options.RowStage.MinTiles}");
        sb.AppendLine();
        sb.AppendLine("Impostor = the right card removed from the candidates, so ANY accept is a wrong card. Recall counts the whole-art rule OR the partial rule.");
        sb.AppendLine();
        sb.AppendLine("## Totals per rule");
        sb.AppendLine();
        sb.AppendLine("| rule | recall | wrong (right card present) | impostor accepts (wrong) | highest impostor margin above the score bar |");
        sb.AppendLine("|---|---|---|---|---|");
        int all = total.Values.Sum();
        for (int k = 0; k < Rules.Length; k++)
        {
            int rc = kinds.Sum(kd => recall.GetValueOrDefault((kd, k)));
            int im = kinds.Sum(kd => impostor.GetValueOrDefault((kd, k)));
            float maxM = closest.Where(x => x.Rule == k && x.S >= Rules[k].S).Select(x => x.M).DefaultIfEmpty(float.NaN).Max();
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {Rules[k].Name} | {rc}/{all} ({Pct(rc, all)}) | {wrongPresent[k]} | {im} | {maxM:F3} |");
        }
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Whole-art rule: wrong while the right card is present {wholeWrongPresent}, impostors {wholeImpostor.Values.Sum()}; gated by the tile fit (recogniser rule): wrong {gatedWrongPresent}, impostors {gatedImpostor.Values.Sum()}, recall {gatedRecall.Values.Sum()} vs {wholeRecall.Values.Sum()}.");
        sb.AppendLine();
        foreach (string w in wholeWrongs.Take(60)) sb.AppendLine(CultureInfo.InvariantCulture, $"- whole wrong: {w}");
        sb.AppendLine();
        sb.AppendLine("## Per covering (recall %; impostors in brackets)");
        sb.AppendLine();
        sb.AppendLine($"| covering | n | whole-art only | whole gated | {string.Join(" | ", Rules.Select(r => r.Name))} |");
        sb.AppendLine($"|---|---|---|---|{string.Concat(Rules.Select(_ => "---|"))}");
        foreach (string kd in kinds)
        {
            int n = total.GetValueOrDefault(kd);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {kd} | {n} | {Pct(wholeRecall.GetValueOrDefault(kd), n)} [{wholeImpostor.GetValueOrDefault(kd)}] | {Pct(gatedRecall.GetValueOrDefault(kd), n)} [{gatedImpostor.GetValueOrDefault(kd)}] | {string.Join(" | ", Rules.Select((_, k) => $"{Pct(recall.GetValueOrDefault((kd, k)), n)} [{impostor.GetValueOrDefault((kd, k))}]"))} |");
        }
        sb.AppendLine();
        for (int k = 0; k < Rules.Length; k++)
        {
            var near = closest.Where(x => x.Rule == k).OrderByDescending(x => Math.Min(x.S - Rules[k].S, x.M - Rules[k].M)).Take(5).ToList();
            if (near.Count == 0) continue;
            sb.AppendLine(CultureInfo.InvariantCulture, $"### Closest impostors: {Rules[k].Name}");
            foreach (var x in near) sb.AppendLine(CultureInfo.InvariantCulture, $"- {x.S:F3} / {x.M:F3}: {x.Text}");
            sb.AppendLine();
        }
        return sb.ToString();

        static string Pct(int a, int b) => b == 0 ? "-" : (100.0 * a / b).ToString("F0", CultureInfo.InvariantCulture) + "%";
    }

    private static IEnumerable<(string Kind, Action<float[]> Cover)> Coverings(CardArtDatabase db, Random rng)
    {
        foreach (string occ in Occluders)
            foreach (float f in Fractions)
                foreach (string side in Sides)
                {
                    int other = rng.Next(db.Count);
                    int seed = rng.Next();
                    yield return ($"{occ} {f:P0}", v => Occlude(v, occ, side, f, db, other, new Random(seed)));
                }
        foreach (int k in StripRows)
            for (int i = 0; i < 4; i++)
            {
                // Visible band somewhere in the art (the stacked tooltips leave gaps at different heights).
                int y0 = rng.Next(0, Ncc.FullH - k);
                int seed = rng.Next();
                yield return ($"strip {k} rows", v => StripCover(v, y0, k, new Random(seed)));
            }
    }

    /// <summary>The whole-art decision exactly as the recogniser makes it (top-8, hint-free, score and margin rules).</summary>
    private static (string? Pick, bool Accepted, string[] Top, float Score) Whole(CardArtDatabase db, CardRecognizer rec, float[] v, Func<ArtEntry, bool> allowed)
    {
        ArtMatch[] top = db.TopK(v, 8, allowed);
        if (top.Length == 0) return (null, false, [], 0);
        (ArtMatch pick, _) = rec.Decide(top, null);
        float runnerUp = top.Where(m => m.CardId != pick.CardId && !db.TwinsOf(pick.CardId).Contains(m.CardId)).Select(m => m.Score).DefaultIfEmpty(-1).Max();
        float margin = pick.Score - runnerUp;
        RecognizerOptions o = rec.Options;
        bool acc = (pick.Score >= o.AcceptScore && margin >= o.AcceptMargin) || (pick.Score >= o.CoveredScore && margin >= o.CoveredMargin);
        return (pick.CardId, acc, top.Select(m => m.CardId).ToArray(), pick.Score);
    }

    /// <summary>Raw (not normalised) 48x36 RGB thumbnail of the trusted inner part of an art box.</summary>
    private static float[] RawThumbnail(in ScreenFrame frame, PixelRect art)
    {
        using IntegralImage img = IntegralImage.Build(frame, new PixelRect(art.X - 4, art.Y - 4, art.Width + 8, art.Height + 8), 1);
        var box = new RectF(art.X, art.Y, art.Width, art.Height);
        RectF inner = box.Fraction(Ncc.Inner.X, Ncc.Inner.Y, Ncc.Inner.Width, Ncc.Inner.Height);
        float[] v = new float[Ncc.FullLength];
        img.SampleGrid(inner, Ncc.FullW, Ncc.FullH, v);
        return v;
    }

    private static (float R, float G, float B) TooltipPixel(Random rng) =>
        rng.NextDouble() < 0.18 ? (215f, 205f, 185f) : (38f + rng.Next(6), 30f + rng.Next(6), 26f + rng.Next(6));

    /// <summary>Everything but rows [y0, y0+k) covered by tooltip panels.</summary>
    private static void StripCover(float[] v, int y0, int k, Random rng)
    {
        for (int y = 0; y < Ncc.FullH; y++)
        {
            if (y >= y0 && y < y0 + k) continue;
            for (int x = 0; x < Ncc.FullW; x++)
            {
                int o = (y * Ncc.FullW + x) * 3;
                (v[o], v[o + 1], v[o + 2]) = TooltipPixel(rng);
            }
        }
    }

    /// <summary>Paints an occluder over a fraction of the thumbnail (cell grid 48x36, interleaved RGB 0..255).</summary>
    private static void Occlude(float[] v, string kind, string side, float f, CardArtDatabase db, int other, Random rng)
    {
        int W = Ncc.FullW, H = Ncc.FullH;
        // Corner blocks cover f of the area with a square-ish block.
        float c = MathF.Sqrt(f);
        (int x0, int y0, int x1, int y1) = side switch
        {
            "top" => (0, 0, W, (int)(H * f)),
            "bottom" => (0, H - (int)(H * f), W, H),
            "left" => (0, 0, (int)(W * f), H),
            "right" => (W - (int)(W * f), 0, W, H),
            "top-right" => (W - (int)(W * c), 0, W, (int)(H * c)),
            _ => (0, H - (int)(H * c), (int)(W * c), H),
        };
        ReadOnlySpan<float> otherArt = db.FullVector(other);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int o = (y * W + x) * 3;
                // Rotated 180 degrees: real art texture, but never aligned with any card's own position.
                int m = ((H - 1 - y) * W + (W - 1 - x)) * 3;
                (float r, float g, float b) = kind switch
                {
                    // Dark keyword panel with light glyph cells.
                    "tooltip" => TooltipPixel(rng),
                    // The merchant's pale blue hand: smooth vertical shading.
                    "hand" => (110f + 40f * y / H, 175f + 30f * y / H, 210f + 20f * y / H),
                    // Another card's art, rotated (worst case: the occluder itself looks like card art).
                    _ => (110 + 3500 * otherArt[m], 110 + 3500 * otherArt[m + 1], 110 + 3500 * otherArt[m + 2]),
                };
                v[o] = Math.Clamp(r, 0, 255);
                v[o + 1] = Math.Clamp(g, 0, 255);
                v[o + 2] = Math.Clamp(b, 0, 255);
            }
    }
}
