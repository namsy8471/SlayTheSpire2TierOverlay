using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using Sts2Overlay.Vision;
using VisionBench;

// VisionBench: art database build, diagnostics and the regression bench for Sts2Overlay.Vision.
//
//   VisionBench build-db  [--art dir] [--catalog file] [--db file]
//   VisionBench selftest  [--db file]                       each portrait shrunk/darkened → still found?
//   VisionBench recognize <png> [--db file] [--character CHARACTER.X] [--upscale 2]
//   VisionBench annotate  <png> [out.png]                   draws the found boxes (debugging)
//   VisionBench bench     [--captures dir] [--save file | --saves dir] [--labels file] [--out dir] [--upscale 2] [--partial off] [--new-screens off] [--prices file]
//   VisionBench stress    [--captures dir] [--out dir] [--every n] [--pool on] [--rows a,min,s,m]   covers clean cards artificially: recall + impostor accepts
//   VisionBench price-occlude [--prices file] [--upscale 2] [--dark x] [--no-rarity x]   paints a "hand" (or a dark
//                         panel) over parts of each price: unknown is fine, wrong is not
//   VisionBench price-glyphs [--prices file] [--upscale 2]     derive the price digit templates from labelled shops
//   (bench, price-occlude: --price-glyphs a.txt[,b.txt] reads prices with only these template files)
//   VisionBench preview   [png ...] [--captures dir] [--out dir] [--skip on]   badges + recommendation marks drawn where the overlay would put them
//                         (4K/150 % and FHD/100 %), with the debug card/price outlines
//   VisionBench removal   --labels file [--captures dir]   card-removal probe vs hand labels at FHD, 1440p and 2x (4K)
//
// Everything it reads or writes lives outside the repo or in data/local (gitignored).

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string repo = FindRepoRoot();
var opts = ParseOptions(args.Skip(1).ToArray(), out List<string> positional);
string artDir = opts.GetValueOrDefault("art") ?? Path.Combine(repo, "data", "local", "pck", "card_art");
string catalogPath = opts.GetValueOrDefault("catalog") ?? Path.Combine(repo, "data", "catalog", "cards.v0.107.1.json");
string dbPath = opts.GetValueOrDefault("db") ?? Path.Combine(repo, "data", "local", "vision", "card-art.bin");

switch (args.FirstOrDefault())
{
    case "build-db":
        BuildDb();
        break;
    case "selftest":
        SelfTest();
        break;
    case "recognize":
        RecognizeOne(positional[0]);
        break;
    case "annotate":
        Annotate(positional[0], positional.ElementAtOrDefault(1) ?? Path.Combine(repo, "data", "local", "bench", "annotated", Path.GetFileName(positional[0])));
        break;
    case "bench":
        Bench();
        break;
    case "profile":
        Profile(positional[0]);
        break;
    case "stress":
        StressTest();
        break;
    case "probe":
    {
        // probe <png> <CARD.ID> <cx> <cy> <scale> [radius]: best NCC of that card around a box (reference units)
        var rec = NewRecognizer(LoadDb());
        ScreenFrame f = ImageLoader.Load(positional[0]);
        float P(int i) => float.Parse(positional[i], CultureInfo.InvariantCulture);
        var b = rec.Probe(f, positional[1], P(2), P(3), P(4), positional.Count > 5 ? P(5) : 12);
        Console.WriteLine($"best {b.Score:F3} at ({b.X:F0}, {b.Y:F0}) scale {b.Scale:F2}");
        break;
    }
    case "price-glyphs":
        PriceGlyphsCommand();
        break;
    case "price-occlude":
        PriceOcclude();
        break;
    case "price-debug":
        PriceDebug(positional[0]);
        break;
    case "preview":
        PreviewCommand();
        break;
    case "removal":
    {
        // Labels live outside the repo (they name the user's captures); see RemovalBench for the format.
        string capturesDir = opts.GetValueOrDefault("captures") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay", "captures");
        int errors = RemovalBench.Run(capturesDir, opts["labels"],
            [
                new("native", f => f),
                new("1440p-linear", f => Resize(f, 2560, 1439, 1f)),
                new("2x-nearest", f => Upscale(f, 2)),
            ], Console.WriteLine);
        Environment.ExitCode = errors == 0 ? 0 : 1;
        break;
    }
    default:
        Console.WriteLine("commands: build-db | selftest | recognize <png> | annotate <png> [out] | bench | stress | probe | price-glyphs | price-occlude | price-debug | preview | removal");
        break;
}

// --partial off: disable the occlusion-robust second pass (before/after comparisons).
// --blocks / --rows agree,minTiles,score,margin[,wholeTopK]: override a partial stage (threshold tuning).
RecognizerOptions RecognizerOptionsFromArgs()
{
    var o = new RecognizerOptions
    {
        PartialMatching = opts.GetValueOrDefault("partial") != "off",
        // --new-screens off: only the reward row and the shop (the layouts before choose-a-card / grid support).
        GridScreens = opts.GetValueOrDefault("new-screens") != "off",
        ChoiceScreens = opts.GetValueOrDefault("new-screens") != "off",
    };
    static PartialStage Parse(PartialStage d, string? arg)
    {
        if (arg == null) return d;
        float[] v = arg.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return d with { Agree = v[0], MinTiles = (int)v[1], AcceptScore = v[2], AcceptMargin = v[3], WholeTopK = v.Length > 4 ? (int)v[4] : d.WholeTopK };
    }
    o = o with { BlockStage = Parse(o.BlockStage, opts.GetValueOrDefault("blocks")), RowStage = Parse(o.RowStage, opts.GetValueOrDefault("rows")) };
    // --price-glyphs a.txt[,b.txt]: read prices with these template files only (e.g. TitleBench digits output).
    if (opts.GetValueOrDefault("price-glyphs") is { } glyphFiles)
        o = o with
        {
            Price = new PriceReaderOptions
            {
                Glyphs = glyphFiles.Split(',').Select(f => PriceGlyphs.Parse(File.ReadAllLines(f).Select(l => l.Trim().Trim(',').Trim('"')).Where(l => l.Length > 0).ToList(), name: Path.GetFileNameWithoutExtension(f))).ToList(),
            },
        };
    return o;
}

CardArtDatabase LoadDb()
{
    var sw = Stopwatch.StartNew();
    CardArtDatabase db = CardArtDatabase.LoadOrBuild(artDir, catalogPath, dbPath, Console.WriteLine);
    Console.WriteLine($"art database: {db.Count} entries, loaded in {sw.ElapsedMilliseconds} ms ({dbPath})");
    return db;
}

void BuildDb()
{
    var sw = Stopwatch.StartNew();
    VisionCatalog? catalog = File.Exists(catalogPath) ? VisionCatalog.Load(catalogPath) : null;
    CardArtDatabase db = CardArtDatabase.Build(artDir, catalog, log: Console.WriteLine);
    db.Save(dbPath);
    Console.WriteLine($"built {db.Count} entries in {sw.ElapsedMilliseconds} ms -> {dbPath} ({new FileInfo(dbPath).Length / 1024} KiB)");
    foreach (ArtTwin t in db.Twins) Console.WriteLine($"  identical art: {t.CardIdA} = {t.CardIdB} (NCC {t.Score:F3})");

    // Also report twins among ALL portraits (no catalog filter) so stale look-alikes are visible.
    CardArtDatabase all = CardArtDatabase.Build(artDir, null);
    Console.WriteLine($"without catalog filter: {all.Count} entries, {all.Twins.Count} identical-art pairs");
    foreach (ArtTwin t in all.Twins) Console.WriteLine($"  {t.CardIdA} = {t.CardIdB} (NCC {t.Score:F3})");

    sw.Restart();
    CardArtDatabase? loaded = CardArtDatabase.TryLoad(dbPath);
    Console.WriteLine($"reload from cache: {sw.ElapsedMilliseconds} ms, {loaded?.Count} entries");
}

void SelfTest()
{
    // Simulate "seen on screen": render each portrait at the reward-screen size (200x152 at 1080p), darken it
    // to 60 % like the fade-in, then run the same extraction as for a capture.
    CardArtDatabase db = LoadDb();
    int ok = 0, total = 0;
    var worst = new List<(string Id, float Score, string Got)>();
    foreach (ArtEntry e in db.Entries)
    {
        ScreenFrame art = ImageLoader.Load(Path.Combine(artDir, e.ArtPath));
        ScreenFrame small = Resize(art, 200, 152, 0.6f);
        float[] full = new float[Ncc.FullLength], coarse = new float[Ncc.CoarseLength];
        CardArtDatabase.ComputeArtVectors(small, full, coarse);
        ArtMatch[] top = db.TopK(full, 2);
        total++;
        bool good = top[0].CardId == e.CardId || db.TwinsOf(e.CardId).Contains(top[0].CardId);
        if (good) ok++;
        else worst.Add((e.CardId, top[0].Score, top[0].CardId));
    }
    Console.WriteLine($"selftest: {ok}/{total} correct after resize to 200x152 + 60% brightness");
    foreach (var w in worst.Take(10)) Console.WriteLine($"  {w.Id} -> {w.Got} ({w.Score:F3})");
}

void RecognizeOne(string png)
{
    CardArtDatabase db = LoadDb();
    var rec = NewRecognizer(db);
    ScreenFrame frame = ImageLoader.Load(png);
    if (opts.TryGetValue("upscale", out string? up)) frame = Upscale(frame, int.Parse(up));
    var hints = new RecognitionHints(opts.GetValueOrDefault("character"));
    rec.Recognize(frame, hints); // warm-up (JIT)
    ScreenRecognition r = rec.Recognize(frame, hints);
    Console.WriteLine($"{Path.GetFileName(png)} {frame.Width}x{frame.Height}: {r.Kind} in {r.ElapsedMs:F1} ms ({r.Detail})");
    Console.WriteLine($"  gate: reward={r.Gate.RewardLikely} shop={r.Gate.ShopLikely} [{r.Gate.Detail}]");
    foreach (RecognizedCard c in r.Slots)
    {
        Console.WriteLine($"  {c} scale={c.NodeScale:F2} green={c.UpgradeEvidence:F3} card={c.CardBounds}{(c.PriceReading is { } pr ? $" price at {pr.Bounds}" : "")}");
        foreach (ArtMatch m in c.Candidates) Console.WriteLine($"      {m.Score:F3} {m.CardId}");
    }
}

void Profile(string png)
{
    // Where does the time go? Repeats each stage to get stable numbers (first call pays JIT).
    CardArtDatabase db = LoadDb();
    var rec = NewRecognizer(db);
    ScreenFrame frame = ImageLoader.Load(png);
    if (opts.TryGetValue("upscale", out string? up)) frame = Upscale(frame, int.Parse(up));
    var t = RefTransform.For(frame.Width, frame.Height);
    int step = Math.Max(1, (int)MathF.Floor(t.Scale + 1e-3f));
    const int n = 30;
    Time("gate (colour probes)", () => ScreenClassifier.Evaluate(frame));
    Time("integral image, reward band", () => IntegralImage.Build(frame, t.ToPixelRect(new RectF(80, 380, 1760, 285)), step).Dispose());
    float[] q = new float[Ncc.FullLength];
    Time("full-database scan, 48x36", () => db.TopK(q.AsSpan(), 8));
    float[] qc = new float[Ncc.CoarseLength];
    Time("full-database scan, 16x12", () => db.TopK(qc.AsSpan(), 16));
    Time("Recognize (total)", () => rec.Recognize(frame));

    void Time(string what, Action a)
    {
        a();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) a();
        Console.WriteLine($"{what,-32} {sw.Elapsed.TotalMilliseconds / n,7:F2} ms   ({frame.Width}x{frame.Height})");
    }
}

void StressTest()
{
    string capturesDir = opts.GetValueOrDefault("captures") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay", "captures");
    string outDir = opts.GetValueOrDefault("out") ?? Path.Combine(repo, "data", "local", "vision-robust");
    int every = opts.TryGetValue("every", out string? ev) ? int.Parse(ev) : 1;
    CardArtDatabase db = LoadDb();
    var rec = new CardRecognizer(db, RecognizerOptionsFromArgs());
    var files = Directory.GetFiles(capturesDir, "*.png")
        .Where(f => Path.GetFileName(f).StartsWith("reward_", StringComparison.Ordinal) || Path.GetFileName(f).StartsWith("shop_", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Where((_, i) => i % every == 0).ToList();
    bool poolOnly = opts.GetValueOrDefault("pool") == "on";
    string report = Stress.Run(db, rec, files, _ => { }, poolOnly);
    Directory.CreateDirectory(outDir);
    File.WriteAllText(Path.Combine(outDir, poolOnly ? "stress-pool.md" : "stress.md"), report);
    Console.WriteLine(report);
}

void Annotate(string png, string outPath)
{
    CardArtDatabase db = LoadDb();
    var rec = NewRecognizer(db);
    ScreenFrame frame = ImageLoader.Load(png);
    ScreenRecognition r = rec.Recognize(frame, new RecognitionHints(opts.GetValueOrDefault("character")));
    using SKBitmap bmp = SKBitmap.Decode(png);
    using var canvas = new SKCanvas(bmp);
    using var cardPaint = new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
    using var artPaint = new SKPaint { Color = SKColors.Magenta, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
    using var badPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
    using var font = new SKFont { Size = 18 };
    using var text = new SKPaint { Color = SKColors.Yellow };
    foreach (RecognizedCard c in r.Slots)
    {
        canvas.DrawRect(ToSk(c.CardBounds), c.Accepted ? cardPaint : badPaint);
        canvas.DrawRect(ToSk(c.ArtBounds), artPaint);
        canvas.DrawText($"{c.CardId}{(c.Upgraded ? "+" : "")} {c.Score:F2}", c.CardBounds.X, c.CardBounds.Y - 6, font, text);
    }
    canvas.DrawText($"{r.Kind} {r.Detail}", 20, 1060, font, text);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 90);
    using (FileStream fs = File.Create(outPath)) data.SaveTo(fs);
    Console.WriteLine($"{r.Kind}: {string.Join(", ", r.Cards)} -> {outPath}");

    static SKRect ToSk(PixelRect p) => new(p.X, p.Y, p.Right, p.Bottom);
}

void PreviewCommand()
{
    // Badge placement check without running the overlay: given captures, or 4 reward + 2 shop frames picked
    // evenly across the capture folder (a clean 3-card reward / 7-card shop recognition).
    var rec = NewRecognizer(LoadDb());
    string outDir = opts.GetValueOrDefault("out") ?? Path.Combine(repo, "data", "local", "vision-robust", "preview");
    List<string> files = positional.Count > 0 ? positional : PickPreviewCaptures(rec,
        opts.GetValueOrDefault("captures") ?? Path.Combine(repo, "data", "local", "vision-robust", "frozen2", "captures"));
    var tiers = Preview.LoadTiers(Path.Combine(repo, "data", "packs", "v0.107.1", "tiers"));
    Console.WriteLine(Preview.Run(files, rec, Upscale, tiers, outDir, Console.WriteLine, skip: opts.GetValueOrDefault("skip") == "on"));
}

static List<string> PickPreviewCaptures(CardRecognizer rec, string dir)
{
    var picked = new List<string>();
    foreach ((string prefix, ScreenKind kind, int cards, int want) in new[] { ("reward_", ScreenKind.RewardCardSelect, 3, 4), ("shop_", ScreenKind.Shop, 7, 2) })
    {
        string[] all = Directory.GetFiles(dir, prefix + "*.png").Order(StringComparer.Ordinal).ToArray();
        for (int q = 0; q < want; q++)
            for (int i = all.Length * q / want; i < all.Length * (q + 1) / want; i++)
            {
                ScreenRecognition r = rec.Recognize(ImageLoader.Load(all[i]));
                if (r.Kind != kind || r.Cards.Count != cards) continue;
                picked.Add(all[i]);
                break;
            }
    }
    return picked;
}

CardRecognizer NewRecognizer(CardArtDatabase db) =>
    new(db, RecognizerOptionsFromArgs() with { Catalog = File.Exists(catalogPath) ? VisionCatalog.Load(catalogPath) : null });

string PricesPath() => opts.GetValueOrDefault("prices") ?? Path.Combine(repo, "data", "local", "price", "labels.json");

void PriceOcclude()
{
    // Stress test for "unknown, never wrong": for every correctly read price, paint a hand-coloured block over
    // part of the label (the coin + first digit, the first digit only, the last digit, the right half of the
    // last digit, the middle) and read again. Any reading that is not null and not the true price is a failure.
    string capturesDir = opts.GetValueOrDefault("captures") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay", "captures");
    int upscale = opts.TryGetValue("upscale", out string? u) ? int.Parse(u) : 1;
    PriceLabels labels = PriceLabels.Load(PricesPath());
    CardArtDatabase db = LoadDb();
    var rec = NewRecognizer(db);
    var reader = new ShopPriceReader();
    var catalog = File.Exists(catalogPath) ? VisionCatalog.Load(catalogPath) : null;
    var tsRegex = new Regex(@"(\d{8}_\d{6}_\d{3})");
    int cases = 0, wrong = 0, unknown = 0, stillRight = 0;
    bool dark = opts.ContainsKey("dark");
    foreach (string path in Directory.GetFiles(capturesDir, "shop_*.png").OrderBy(f => f, StringComparer.Ordinal))
    {
        string ts = tsRegex.Match(Path.GetFileName(path)).Groups[1].Value;
        if (labels.At(ts) is not { } visit) continue;
        ScreenFrame frame = ImageLoader.Load(path);
        if (upscale > 1) frame = Upscale(frame, upscale);
        ScreenRecognition r = rec.Recognize(frame);
        if (r.Kind != ScreenKind.Shop) continue;
        if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame.Bgra, out ArraySegment<byte> seg)) continue;
        byte[] px = seg.Array!;
        var t = RefTransform.For(frame.Width, frame.Height);
        foreach (RecognizedCard c in r.Cards)
        {
            int truth = visit.Prices[c.Slot];
            if (c.PriceReading is not { Value: { } v } pr || v != truth) continue;
            RectF box = t.ToRef(new RectF(c.CardBounds.X, c.CardBounds.Y, c.CardBounds.Width, c.CardBounds.Height));
            string? rarity = opts.ContainsKey("no-rarity") ? null : catalog?.Find(c.CardId!)?.Rarity;
            PixelRect b = pr.Bounds;
            int n = truth.ToString(CultureInfo.InvariantCulture).Length;
            float dw = b.Width / (float)n;
            var blocks = new (string Name, int X0, int X1)[]
            {
                ("coin+first", (int)(b.X - 1.6f * b.Height), (int)(b.X + dw)),
                ("first", b.X - 1, (int)(b.X + dw)),
                ("last", (int)(b.Right - dw), b.Right + 2),
                ("last-half", (int)(b.Right - dw / 2), b.Right + 2),
                ("middle", (int)(b.X + dw * 0.5f), (int)(b.X + dw * 1.5f)),
            };
            foreach (var blk in blocks)
            {
                int y0 = Math.Max(0, b.Y - b.Height / 2), y1 = Math.Min(frame.Height, b.Bottom + b.Height / 2);
                int x0 = Math.Clamp(blk.X0, 0, frame.Width), x1 = Math.Clamp(blk.X1, 0, frame.Width);
                var saved = new byte[(y1 - y0) * (x1 - x0) * 4];
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int p = y * frame.Stride + x * 4, k = ((y - y0) * (x1 - x0) + (x - x0)) * 4;
                        Array.Copy(px, p, saved, k, 4);
                        if (dark) { px[p] = 40; px[p + 1] = 34; px[p + 2] = 30; } // a dark tooltip panel (BGR)
                        else { px[p] = 225; px[p + 1] = 200; px[p + 2] = 140; } // the merchant's light-blue hand (BGR)
                    }
                PriceReading? again = reader.Read(frame, box.CenterX, box.CenterY, c.NodeScale, c.SlotKind, rarity);
                cases++;
                if (again?.Value == null) unknown++;
                else if (again.Value == truth) stillRight++;
                else
                {
                    wrong++;
                    Console.WriteLine($"  WRONG {Path.GetFileName(path)} #{c.Slot} {blk.Name}: read {again} (truth {truth})");
                }
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        Array.Copy(saved, ((y - y0) * (x1 - x0) + (x - x0)) * 4, px, y * frame.Stride + x * 4, 4);
            }
        }
    }
    Console.WriteLine($"occlusion cases {cases}: wrong {wrong}, unknown {unknown}, still right {stillRight}");
}

void PriceDebug(string png)
{
    // Prints the connected components under every recognised shop card (why was a price missed or misread?).
    CardArtDatabase db = LoadDb();
    var rec = NewRecognizer(db);
    ScreenFrame frame = ImageLoader.Load(png);
    if (opts.TryGetValue("upscale", out string? up)) frame = Upscale(frame, int.Parse(up));
    ScreenRecognition r = rec.Recognize(frame);
    var t = RefTransform.For(frame.Width, frame.Height);
    foreach (RecognizedCard c in r.Slots.Where(c => c.CardId != null))
    {
        Console.WriteLine($"{c}");
        RectF box = t.ToRef(new RectF(c.CardBounds.X, c.CardBounds.Y, c.CardBounds.Width, c.CardBounds.Height));
        ShopPriceReader.Segment(frame, box.CenterX, box.CenterY, c.NodeScale, Console.WriteLine);
    }
}

void PriceGlyphsCommand()
{
    // Template derivation. Every recognised shop card of a labelled visit gives one digit row; when the number of
    // segmented digits matches the label, each glyph grid becomes a training sample for its digit. The template
    // is the per-cell mean. Leave-one-visit-out: to estimate accuracy honestly, each visit is read with
    // templates built from the OTHER visits only (a template always "recognises" the samples it was averaged from).
    string capturesDir = opts.GetValueOrDefault("captures") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay", "captures");
    int upscale = opts.TryGetValue("upscale", out string? u) ? int.Parse(u) : 1;
    PriceLabels labels = PriceLabels.Load(PricesPath());
    CardArtDatabase db = LoadDb();
    var rec = NewRecognizer(db);
    var samples = new List<(int Visit, string Capture, int Slot, string Truth, IReadOnlyList<float[]> Glyphs)>();
    var heights = new List<float>();
    var centers = new List<float>();
    var blockX = new List<(int Digits, float X)>();
    var coins = new List<float>();
    var widthsByDigit = Enumerable.Range(0, 10).Select(_ => new List<float>()).ToArray();
    var occl = new List<float>();
    int rows = 0, mismatched = 0;
    var tsRegex = new Regex(@"(\d{8}_\d{6}_\d{3})");
    foreach (string path in Directory.GetFiles(capturesDir, "*.png").OrderBy(f => f, StringComparer.Ordinal))
    {
        string ts = tsRegex.Match(Path.GetFileName(path)).Groups[1].Value;
        PriceLabels.Visit? visit = ts.Length > 0 ? labels.At(ts) : null;
        if (visit == null) continue;
        ScreenFrame frame = ImageLoader.Load(path);
        if (upscale > 1) frame = Upscale(frame, upscale);
        ScreenRecognition r = rec.Recognize(frame);
        if (r.Kind != ScreenKind.Shop) continue;
        var t = RefTransform.For(frame.Width, frame.Height);
        foreach (RecognizedCard c in r.Cards)
        {
            RectF box = t.ToRef(new RectF(c.CardBounds.X, c.CardBounds.Y, c.CardBounds.Width, c.CardBounds.Height));
            var row = ShopPriceReader.Segment(frame, box.CenterX, box.CenterY, c.NodeScale);
            if (row == null) continue;
            rows++;
            string truth = visit.Prices[c.Slot].ToString(CultureInfo.InvariantCulture);
            if (row.Glyphs.Count != truth.Length)
            {
                mismatched++;
                Console.WriteLine($"  {Path.GetFileName(path)} #{c.Slot}: {row.Glyphs.Count} digit(s) segmented, label {truth}");
                continue;
            }
            samples.Add((visit.Index, Path.GetFileName(path), c.Slot, truth, row.Glyphs));
            for (int i = 0; i < truth.Length; i++) widthsByDigit[truth[i] - '0'].Add(row.Widths[i]);
            heights.Add(row.Bounds.Height / (t.Scale * c.NodeScale));
            centers.Add(row.CenterY);
            blockX.Add((row.Glyphs.Count, row.CenterX));
            coins.Add(row.CoinFraction);
            occl.Add(row.OccluderFraction);
        }
    }
    Console.WriteLine($"{rows} digit rows, {mismatched} with a different digit count than the label, {samples.Count} usable ({samples.Sum(s => s.Truth.Length)} digits)");
    heights.Sort();
    if (heights.Count > 0)
        Console.WriteLine($"digit height in card units: min {heights[0]:F1} median {heights[heights.Count / 2]:F1} max {heights[^1]:F1}");
    centers.Sort();
    if (centers.Count > 0)
        Console.WriteLine($"digit centre y in card units: min {centers[0]:F1} median {centers[centers.Count / 2]:F1} max {centers[^1]:F1}");
    coins.Sort();
    if (coins.Count > 0)
        Console.WriteLine($"coin gold fraction left of the digits: min {coins[0]:F2} p5 {coins[coins.Count / 20]:F2} median {coins[coins.Count / 2]:F2}");
    for (int d = 0; d < 10; d++)
    {
        var w = widthsByDigit[d].OrderBy(x => x).ToList();
        if (w.Count > 0) Console.WriteLine($"digit {d} width/height: min {w[0]:F3} median {w[w.Count / 2]:F3} max {w[^1]:F3} (n={w.Count})");
    }
    occl.Sort();
    if (occl.Count > 0)
        Console.WriteLine($"bright ring around clean digits: median {occl[occl.Count / 2]:F3} p95 {occl[occl.Count * 19 / 20]:F3} max {occl[^1]:F3}");
    foreach (var g in blockX.GroupBy(b => b.Digits).OrderBy(g => g.Key))
    {
        var xs = g.Select(b => b.X).OrderBy(x => x).ToList();
        Console.WriteLine($"digit block centre x, {g.Key} digit(s): min {xs[0]:F1} median {xs[xs.Count / 2]:F1} max {xs[^1]:F1} (n={xs.Count})");
    }

    PriceGlyphs Build(Func<int, bool> useVisit)
    {
        var sum = Enumerable.Range(0, 10).Select(_ => new float[PriceGlyphs.Length]).ToArray();
        var count = new int[10];
        foreach (var s in samples.Where(s => useVisit(s.Visit)))
            for (int i = 0; i < s.Truth.Length; i++)
            {
                int d = s.Truth[i] - '0';
                count[d]++;
                for (int k = 0; k < PriceGlyphs.Length; k++) sum[d][k] += s.Glyphs[i][k];
            }
        for (int d = 0; d < 10; d++)
            if (count[d] > 0)
                for (int k = 0; k < PriceGlyphs.Length; k++) sum[d][k] /= count[d];
        return new PriceGlyphs(sum);
    }

    // Outlier rows (a hand or tooltip over the label, so the "digits" are something else) would blur the
    // templates: drop rows where a digit does not resemble the mean of its own class, then average again.
    {
        PriceGlyphs first = Build(_ => true);
        var outliers = samples.Where(s => s.Truth.Select((ch, i) => first.Score(s.Glyphs[i], ch - '0')).Min() < 0.5f).ToList();
        foreach (var o in outliers) Console.WriteLine($"  outlier (not used for templates): {o.Capture} #{o.Slot} label {o.Truth}");
        samples = samples.Except(outliers).ToList();
    }

    // Leave-one-visit-out accuracy (digits and whole prices).
    int digitOk = 0, digitAll = 0, priceOk = 0;
    float worstRight = 1, bestWrong = 0, worstMargin = 1;
    foreach (int v in samples.Select(s => s.Visit).Distinct())
    {
        PriceGlyphs g = Build(x => x != v);
        foreach (var s in samples.Where(s => s.Visit == v))
        {
            bool all = true;
            for (int i = 0; i < s.Truth.Length; i++)
            {
                (int d, float score, float margin) = g.Classify(s.Glyphs[i]);
                digitAll++;
                if (d == s.Truth[i] - '0')
                {
                    digitOk++;
                    worstRight = Math.Min(worstRight, score);
                    worstMargin = Math.Min(worstMargin, margin);
                }
                else
                {
                    all = false;
                    bestWrong = Math.Max(bestWrong, score);
                    Console.WriteLine($"  LOVO miss: {s.Capture} #{s.Slot} label {s.Truth} digit {i} read {d} ({score:F3})");
                }
            }
            if (all) priceOk++;
        }
    }
    Console.WriteLine($"leave-one-visit-out: digits {digitOk}/{digitAll}, prices {priceOk}/{samples.Count}; right digits: NCC min {worstRight:F3}, margin min {worstMargin:F3}; wrong digits: NCC max {bestWrong:F3}");

    PriceGlyphs final = Build(_ => true);
    var digitsSeen = samples.SelectMany(s => s.Truth).Distinct().OrderBy(c => c).ToArray();
    Console.WriteLine($"digits with samples: {new string(digitsSeen)}");
    string outFile = Path.Combine(repo, "data", "local", "price", upscale > 1 ? $"glyphs-x{upscale}.txt" : "glyphs.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
    File.WriteAllLines(outFile, final.Format().Select(l => $"        \"{l}\","));
    Console.WriteLine($"templates -> {outFile} (paste into src/Sts2Overlay.Vision/Price/PriceGlyphs.Data.cs)");
}

void Bench()
{
    string capturesDir = opts.GetValueOrDefault("captures") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay", "captures");
    // --saves <dir>: a (frozen) copy of the saves folder (current_run.save + history/*.run), so before/after runs
    // compare against the same ground truth while the game keeps writing the live one.
    string? savePath = opts.GetValueOrDefault("save")
        ?? (opts.GetValueOrDefault("saves") is { } savesDir ? Path.Combine(savesDir, "current_run.save") : FindCurrentRunSave());
    string labelsPath = opts.GetValueOrDefault("labels") ?? Path.Combine(repo, "data", "local", "bench", "labels.json");
    string outDir = opts.GetValueOrDefault("out") ?? Path.Combine(repo, "data", "local", "bench");
    int upscale = opts.TryGetValue("upscale", out string? u) ? int.Parse(u) : 1;

    CardArtDatabase db = LoadDb();
    var rec = NewRecognizer(db);
    Labels labels = Labels.Load(labelsPath);
    PriceLabels priceLabels = PriceLabels.Load(PricesPath());
    var priceScore = new PriceScore();

    var tsRegex = new Regex(@"(\d{8}_\d{6}_\d{3})");
    var files = Directory.GetFiles(capturesDir, "*.png")
        .Select(f => (Path: f, Ts: tsRegex.Match(Path.GetFileName(f)).Groups[1].Value))
        .Where(f => f.Ts.Length > 0).OrderBy(f => f.Ts, StringComparer.Ordinal).ToList();
    if (opts.TryGetValue("limit", out string? lim)) files = files.Take(int.Parse(lim)).ToList();
    static DateTime TimeOf(string ts) => DateTime.ParseExact(ts[..15], "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

    // Ground truth per capture: the run (current save, or a finished run from history/) played at that time.
    // --save pins one file (no history lookup).
    List<RunTruth> runs = savePath == null || (!File.Exists(savePath) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(savePath)!, "history"))) ? []
        : opts.ContainsKey("save") ? [RunTruth.Load(savePath)]
        : RunTruth.LoadAll(savePath, files.Count > 0 ? TimeOf(files[0].Ts) : DateTime.MinValue);
    foreach (RunTruth run in runs)
        Console.WriteLine($"{run.Tag}: {Path.GetFileName(run.Path)} {run.Character}, {run.Start:HH:mm}-{run.End:HH:mm}, {run.Floors.Count} floors with offers");
    string? characterOverride = opts.GetValueOrDefault("character");
    RecognitionHints HintsAt(DateTime time) => new(characterOverride ?? RunTruth.At(runs, time)?.Character);
    RecognitionHints hints = HintsAt(files.Count > 0 ? TimeOf(files[0].Ts) : DateTime.Now);

    var rows = new List<Row>();
    bool warmed = false;
    var warmedKinds = new HashSet<ScreenKind>();
    var coldNotes = new List<string>();
    // App-level view: merge consecutive frames of one screen visit like the overlay will (captures are ~2 s apart,
    // so the hold time is longer than the app's default).
    var tracker = new RecognitionTracker { HoldTime = TimeSpan.FromSeconds(6) };
    var visits = new List<(string First, string Last, TrackedScreen Screen, RunTruth? Run)>();
    var visitSlots = new Dictionary<int, int>(); // visit index → most slots seen (reward: 2-4 cards, shop: 7)
    int generation = -1;
    foreach ((string path, string ts) in files)
    {
        ScreenFrame frame = ImageLoader.Load(path);
        if (upscale > 1) frame = Upscale(frame, upscale);
        DateTime time = TimeOf(ts);
        RunTruth? truth = RunTruth.At(runs, time);
        hints = HintsAt(time);
        if (!warmed)
        {
            rec.Recognize(frame, hints);
            warmed = true;
        }
        ScreenRecognition r = rec.Recognize(frame, hints);
        string name = Path.GetFileName(path);
        if (r.Kind != ScreenKind.Other && warmedKinds.Add(r.Kind))
        {
            // The first reward/shop frame also pays for JIT-compiling the matching code (tiered compilation
            // starts with unoptimised code). Report it once, then time the steady state like the app sees it.
            coldNotes.Add($"first {r.Kind} frame ({name}): {r.ElapsedMs:F1} ms cold (JIT)");
            r = rec.Recognize(frame, hints);
        }
        string? expected = labels.ScreenOf(name, ts);
        Labels.Entry? label = labels.Captures.GetValueOrDefault(name);
        rows.Add(Evaluate(name, time, r, expected, label, truth));
        if (r.Kind == ScreenKind.Shop && priceLabels.At(ts) is { } pv)
            foreach (RecognizedCard c in r.Cards) priceScore.Add(name, c, pv.Prices[c.Slot]);
        TrackedScreen tracked = tracker.Update(r, time);
        if (tracked.Kind != ScreenKind.Other)
        {
            if (tracker.Generation != generation) visits.Add((name, name, tracked, truth));
            else visits[^1] = (visits[^1].First, name, tracked, visits[^1].Run);
            // Only frames of this screen count: an "Other" frame inside the hold time reports the slots it examined
            // (e.g. ten empty grid cells), not the screen's.
            if (r.Kind == tracked.Kind)
                visitSlots[visits.Count - 1] = Math.Max(visitSlots.GetValueOrDefault(visits.Count - 1), r.Slots.Count);
            generation = tracker.Generation;
        }
        Row row = rows[^1];
        Console.WriteLine($"{name,-34} {r.Kind,-16} {r.ElapsedMs,6:F1} ms  {row.Verdict,-12} {string.Join(" ", row.Recognized)}");
    }

    // Merged over each shop visit (what the overlay shows): every slot's last clear price vs the label.
    var visitPrices = new StringBuilder();
    visitPrices.AppendLine("Merged per screen visit (RecognitionTracker, like the app):");
    visitPrices.AppendLine();
    int vSlots = 0, vRight = 0, vWrong = 0, vUnknown = 0;
    foreach (var v in visits.Where(v => v.Screen.Kind == ScreenKind.Shop))
    {
        string ts = tsRegex.Match(v.First).Groups[1].Value;
        if (priceLabels.At(ts) is not { } pv) continue;
        var parts = new List<string>();
        foreach (RecognizedCard c in v.Screen.Cards)
        {
            vSlots++;
            int truthPrice = pv.Prices[c.Slot];
            if (c.Price == truthPrice) vRight++;
            else if (c.Price == null) vUnknown++;
            else vWrong++;
            parts.Add(c.Price == truthPrice ? $"{c.Price}" : c.Price == null ? $"?({truthPrice})" : $"**{c.Price}≠{truthPrice}**");
        }
        visitPrices.AppendLine($"- {v.First}: {v.Screen.Cards.Count} cards, prices {string.Join(" ", parts)}");
    }
    visitPrices.AppendLine();
    visitPrices.AppendLine($"Visit slots: {vSlots}, price right {vRight}, wrong {vWrong}, unknown {vUnknown}");
    visitPrices.AppendLine();
    Console.WriteLine($"visit-merged prices: {vRight}/{vSlots} right, {vWrong} wrong, {vUnknown} unknown");

    string report = Report(rows, runs, capturesDir, upscale, db.Count,
        VisitReport(visits, visitSlots) + priceScore.Report($"Shop prices (hand labels: {priceLabels.Visits.Count} visits, every recognised card of a labelled visit)") + visitPrices, coldNotes);
    Directory.CreateDirectory(outDir);
    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
    string file = Path.Combine(outDir, $"report-{stamp}{(upscale > 1 ? $"-x{upscale}" : "")}.md");
    File.WriteAllText(file, report);
    File.WriteAllText(Path.Combine(outDir, upscale > 1 ? $"latest-x{upscale}.md" : "latest.md"), report);
    // Every examined slot with its scores and whether its id belongs to the matched floor: threshold tuning data.
    var csv = new StringBuilder("capture,kind,slot,card,upgraded,upgradeKnown,score,margin,green,scale,accepted,truth,pCard,pScore,pMargin,pVisible,pAccepted,pTruth\n");
    foreach (Row row in rows)
        for (int i = 0; i < row.Result.Slots.Count; i++)
        {
            RecognizedCard c = row.Result.Slots[i];
            PartialMatch? p = c.Partial;
            PriceReading? pr = c.PriceReading;
            csv.AppendLine($"{row.Name},{row.Kind},{c.Slot},{c.CardId},{c.Upgraded},{c.UpgradeKnown},{c.Score:F4},{c.Margin:F4},{c.UpgradeEvidence:F4},{c.NodeScale:F2},{c.Accepted},{row.SlotTruth[i]}," +
                $"{p?.CardId},{p?.Score:F4},{p?.Margin:F4},{p?.Stage}:{p?.VisibleTiles},{p?.Accepted},{(p == null ? "" : row.FloorCards == null ? "?" : row.FloorCards.Contains(p.CardId) ? "in-floor" : "not-in-floor")}," +
                $"{c.Price},{pr?.Text},{pr?.Tint},{pr?.MinScore:F3},{pr?.MinMargin:F3}");
        }
    File.WriteAllText(Path.Combine(outDir, upscale > 1 ? $"slots-x{upscale}.csv" : "slots.csv"), csv.ToString());
    Console.WriteLine();
    Console.WriteLine(report[..report.IndexOf("## Captures", StringComparison.Ordinal)]);
    if (priceScore.Total > 0)
        Console.WriteLine($"prices: {priceScore.Correct}/{priceScore.Total} correct, {priceScore.Wrong} wrong, {priceScore.Rejected} rejected, {priceScore.Missing} not found");
    Console.WriteLine($"report: {file}");
}

Row Evaluate(string name, DateTime time, ScreenRecognition r, string? expected, Labels.Entry? label, RunTruth? truth)
{
    // Recognised card keys: "CARD.X", "CARD.X+" (upgraded) or "CARD.X?" (upgrade unknown: title hidden).
    var recognized = r.Cards.Select(Key).ToList();
    string kind = r.Kind.ToString();
    bool? kindOk = expected == null ? null : expected == kind;

    // Which floor offered these cards? Pick the floor with the largest id overlap (shops: offered + bought).
    var ids = (r.Cards.Count > 0 ? r.Cards : r.Slots.Where(c => c.CardId != null)).Select(c => c.CardId!).ToList();
    Floor? best = null;
    int bestOverlap = 0;
    foreach (Floor f in truth?.Floors ?? [])
    {
        HashSet<string> set = TruthSet(f).Select(StripUp).ToHashSet();
        int overlap = ids.Count(set.Contains);
        if (overlap > bestOverlap)
        {
            bestOverlap = overlap;
            best = f;
        }
    }
    IReadOnlyList<string>? truthCards = label?.Cards ?? (best != null ? TruthSet(best) : null);

    string verdict;
    if (r.Kind == ScreenKind.Other)
        verdict = expected == null ? "other?" : kindOk == true ? "ok" : "MISSED";
    // Card grids are mostly the player's own deck (remove/upgrade screens) and choose-a-card rows come from relics,
    // potions and events: the save's reward/shop floors are no ground truth for them (the app filters deck grids).
    else if (IsGridOrChoice(r.Kind) && expected is null or "Other")
        verdict = "grid/choice";
    else if (kindOk == false)
        verdict = "FALSE-POS";
    else if (truthCards == null)
        verdict = "unverified";
    else
    {
        var truthIds = truthCards.Select(StripUp).ToHashSet();
        int wrong = recognized.Count(c => !truthIds.Contains(StripUp(c)));
        int badUpgrade = recognized.Count(c => truthIds.Contains(StripUp(c)) && !truthCards.Any(t => KeyMatches(c, t)));
        bool after = truth?.SaveTime is { } st && time > st.LocalDateTime && label?.Cards == null;
        // Distinct ids: a floor can list a card both as offered and as gained upgraded (X and X+).
        int total = r.Kind == ScreenKind.Shop ? 7 : Math.Min(r.Slots.Count, truthIds.Count);
        if (wrong > 0) verdict = after ? "unverified" : $"WRONG ({wrong})";
        else if (badUpgrade > 0) verdict = "WRONG-UPG";
        else if (recognized.Count == Math.Min(total, truthIds.Count)) verdict = "CORRECT";
        else verdict = $"PARTIAL {recognized.Count}/{total}";
    }
    var slotTruth = r.Slots.Select(c => c.CardId == null || truthCards == null ? "?" :
        truthCards.Select(StripUp).Contains(c.CardId) ? "in-floor" : "not-in-floor").ToList();
    return new Row(name, expected, kind, recognized, r, verdict, best != null ? $"{truth!.Tag} {best.Name}" : null, slotTruth,
        truthCards?.Select(StripUp).ToHashSet());

    static IReadOnlyList<string> TruthSet(Floor f) => f.Offered.Concat(f.Gained).Distinct().ToList();
}

static bool IsGridOrChoice(ScreenKind k) => k is ScreenKind.CardGrid or ScreenKind.CardChoice;

static string Key(RecognizedCard c) => c.CardId + (c.Upgraded ? "+" : c.UpgradeKnown ? "" : "?");

static bool KeyMatches(string rec, string truth) =>
    rec.EndsWith('?') ? StripUp(rec) == StripUp(truth) : rec == truth;

string VisitReport(List<(string First, string Last, TrackedScreen Screen, RunTruth? Run)> visits, Dictionary<int, int> visitSlots)
{
    var sb = new StringBuilder();
    sb.AppendLine("## Screen visits (frames merged with RecognitionTracker)");
    sb.AppendLine();
    sb.AppendLine("| first capture | last capture | kind | merged cards | floor | verdict |");
    sb.AppendLine("|---|---|---|---|---|---|");
    int ok = 0;
    for (int vi = 0; vi < visits.Count; vi++)
    {
        (string first, string last, TrackedScreen s, RunTruth? truth) = visits[vi];
        var keys = s.Cards.Select(Key).ToList();
        int Overlap(Floor f) => keys.Count(k => f.Offered.Concat(f.Gained).Select(StripUp).Contains(StripUp(k)));
        Floor? best = (truth?.Floors ?? []).OrderByDescending(Overlap).FirstOrDefault();
        // No shared card at all: the floor is not in the save yet (captured after the last save), not a miss.
        if (best != null && Overlap(best) == 0) best = null;
        var truthCards = best?.Offered.Concat(best.Gained).Distinct().ToList();
        string verdict = "unverified";
        if (IsGridOrChoice(s.Kind)) verdict = "grid/choice";
        else if (truthCards != null)
        {
            int wrong = keys.Count(k => !truthCards.Select(StripUp).Contains(StripUp(k)));
            int badUp = keys.Count(k => truthCards.Select(StripUp).Contains(StripUp(k)) && !truthCards.Any(t => KeyMatches(k, t)));
            int distinct = truthCards.Select(StripUp).Distinct().Count();
            // A floor can list more ids than the screen had slots (a deck card swapped in shows up as gained).
            int expectedCount = Math.Min(visitSlots.GetValueOrDefault(vi, 7), distinct);
            verdict = wrong > 0 ? $"WRONG ({wrong})" : badUp > 0 ? "WRONG-UPG" : keys.Count == expectedCount ? "CORRECT" : $"PARTIAL {keys.Count}/{expectedCount}";
        }
        if (verdict == "CORRECT") ok++;
        sb.AppendLine($"| {first} | {last} | {s.Kind} | {string.Join(", ", keys)} | {(best != null ? $"{truth!.Tag} {best.Name}" : "")} | {verdict} |");
    }
    sb.AppendLine();
    sb.AppendLine($"Visits fully correct after merging: {ok} / {visits.Count}");
    sb.AppendLine();
    return sb.ToString();
}

string Report(List<Row> rows, IReadOnlyList<RunTruth> runs, string capturesDir, int upscale, int dbCount, string visitReport, IReadOnlyList<string> coldNotes)
{
    var sb = new StringBuilder();
    sb.AppendLine($"# VisionBench report {DateTime.Now:yyyy-MM-dd HH:mm}");
    sb.AppendLine();
    sb.AppendLine($"- captures: `{capturesDir}` ({rows.Count} files){(upscale > 1 ? $", upscaled x{upscale} to simulate 4K" : "")}");
    sb.AppendLine($"- ground truth: manual labels + card_choices of {runs.Count} run(s), chosen per capture by time:");
    foreach (RunTruth run in runs)
        sb.AppendLine($"  - {run.Tag} `{Path.GetFileName(run.Path)}` {run.Character} {run.Start:HH:mm}-{run.End:HH:mm}: {run.Floors.Count} floors with card offers");
    sb.AppendLine($"- art database: {dbCount} entries");
    sb.AppendLine();

    var reward = rows.Where(r => r.Expected == "RewardCardSelect").ToList();
    var shop = rows.Where(r => r.Expected == "Shop").ToList();
    var other = rows.Where(r => r.Expected == "Other").ToList();
    var unlabelled = rows.Where(r => r.Expected == null).ToList();
    sb.AppendLine("## Summary");
    sb.AppendLine();
    sb.AppendLine("| | count | classified right | cards exactly right | notes |");
    sb.AppendLine("|---|---|---|---|---|");
    sb.AppendLine($"| reward screens | {reward.Count} | {reward.Count(r => r.Kind == "RewardCardSelect")} | {reward.Count(r => r.Verdict == "CORRECT")} | {Verdicts(reward)} |");
    sb.AppendLine($"| shop screens | {shop.Count} | {shop.Count(r => r.Kind == "Shop")} | {shop.Count(r => r.Verdict.StartsWith("CORRECT", StringComparison.Ordinal))} | {Verdicts(shop)}; cards found {shop.Sum(r => r.Recognized.Count)} / {shop.Count * 7} slots |");
    sb.AppendLine($"| other screens | {other.Count} | {other.Count(r => r.Kind == "Other")} | - | false positives: {other.Count(r => r.Kind is not ("Other" or "CardGrid" or "CardChoice"))}; card grid / choose-a-card: {other.Count(r => r.Kind is "CardGrid" or "CardChoice")} |");
    sb.AppendLine($"| unlabelled | {unlabelled.Count} | - | - | recognised as reward {unlabelled.Count(r => r.Kind == "RewardCardSelect")}, shop {unlabelled.Count(r => r.Kind == "Shop")}, choose-a-card {unlabelled.Count(r => r.Kind == "CardChoice")}, card grid {unlabelled.Count(r => r.Kind == "CardGrid")}; reward/shop checked against the save: {Verdicts(unlabelled.Where(r => r.Kind is "RewardCardSelect" or "Shop"))} |");
    sb.AppendLine();
    sb.AppendLine("Verdicts: CORRECT = every card right (id + upgrade); PARTIAL k/n = no wrong card, some slots rejected (covered by a tooltip or the merchant's hand, still fading in, or sold); WRONG = a wrong card id was accepted; WRONG-UPG = right id, wrong upgrade flag. `X?` = upgrade unknown (title hidden).");
    sb.AppendLine();

    var cardRows = rows.Where(r => r.Kind != "Other").ToList();
    var cards = cardRows.SelectMany(r => r.Result.Cards).ToList();
    if (cards.Count > 0)
        sb.AppendLine($"Accepted cards: {cards.Count}, NCC min {cards.Min(c => c.Score):F3} / median {MedianF(cards.Select(c => c.Score)):F3}, margin min {cards.Min(c => c.Margin):F3}");
    var rejected = rows.SelectMany(r => r.Result.Slots).Where(c => !c.Accepted && c.CardId != null).ToList();
    if (rejected.Count > 0)
        sb.AppendLine($"Rejected slots: {rejected.Count}, best NCC max {rejected.Max(c => c.Score):F3}");
    var ms = rows.Select(r => r.Result.ElapsedMs).OrderBy(x => x).ToList();
    var msCards = cardRows.Select(r => r.Result.ElapsedMs).OrderBy(x => x).ToList();
    sb.AppendLine($"Time per frame: all median {Median(ms):F1} ms, max {ms.LastOrDefault():F1} ms; reward/shop frames median {Median(msCards):F1} ms, max {msCards.LastOrDefault():F1} ms");
    if (coldNotes.Count > 0) sb.AppendLine($"(steady state; {string.Join("; ", coldNotes)})");
    sb.AppendLine();

    sb.Append(visitReport);
    sb.AppendLine("## Captures");
    sb.AppendLine();
    sb.AppendLine("| capture | label | result | ms | verdict | floor | cards (NCC, margin) |");
    sb.AppendLine("|---|---|---|---|---|---|---|");
    foreach (Row r in rows)
    {
        string cardText = string.Join("<br>", r.Result.Slots.Select(c =>
            $"{(c.Accepted ? "" : "~~")}{Key(c)} {c.Score:F2}/{c.Margin:F2}{(c.Accepted ? "" : "~~")}"));
        string detail = r.Kind == "Other" && r.Result.Detail.Length > 0 ? $" ({r.Result.Detail})" : "";
        sb.AppendLine($"| {r.Name} | {r.Expected ?? "?"} | {r.Kind}{detail} | {r.Result.ElapsedMs:F1} | {r.Verdict} | {r.Floor ?? ""} | {cardText} |");
    }
    sb.AppendLine();
    sb.AppendLine("## Floors in the saves");
    sb.AppendLine();
    foreach (RunTruth run in runs)
        foreach (Floor f in run.Floors)
            sb.AppendLine($"- {run.Tag} {f.Name}: {string.Join(", ", f.Offered)}{(f.IsShop && f.Gained.Count > 0 ? $" (bought: {string.Join(", ", f.Gained)})" : "")}");
    return sb.ToString();
}

static string Verdicts(IEnumerable<Row> rows) =>
    string.Join(", ", rows.GroupBy(r => r.Verdict.Split(' ')[0]).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));

static double Median(IEnumerable<double> xs)
{
    var l = xs.OrderBy(x => x).ToList();
    return l.Count == 0 ? 0 : l[l.Count / 2];
}

static double MedianF(IEnumerable<float> xs) => Median(xs.Select(x => (double)x));

static string StripUp(string c) => c.TrimEnd('+', '?');

static ScreenFrame Upscale(ScreenFrame f, int k)
{
    // Nearest-neighbour: every source pixel becomes a k x k block. Simulates the user's 4K capture.
    int w = f.Width * k, h = f.Height * k;
    byte[] dst = new byte[w * h * 4];
    ReadOnlySpan<byte> src = f.Bgra.Span;
    for (int y = 0; y < h; y++)
    {
        int sy = y / k;
        for (int x = 0; x < w; x++)
        {
            int s = sy * f.Stride + (x / k) * 4, d = (y * w + x) * 4;
            dst[d] = src[s];
            dst[d + 1] = src[s + 1];
            dst[d + 2] = src[s + 2];
            dst[d + 3] = 255;
        }
    }
    return ScreenFrame.FromPacked(dst, w, h);
}

static ScreenFrame Resize(ScreenFrame f, int w, int h, float brightness)
{
    var info = new SKImageInfo(f.Width, f.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
    using var src = new SKBitmap(info);
    f.Bgra.Span.CopyTo(src.GetPixelSpan());
    using SKBitmap scaled = src.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    byte[] bytes = scaled.GetPixelSpan().ToArray();
    for (int i = 0; i < bytes.Length; i++)
        if (i % 4 != 3) bytes[i] = (byte)(bytes[i] * brightness);
    return new ScreenFrame(bytes, w, h, scaled.RowBytes);
}

static string? FindCurrentRunSave()
{
    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlayTheSpire2", "steam");
    if (!Directory.Exists(root)) return null;
    return Directory.EnumerateFiles(root, "current_run.save", SearchOption.AllDirectories)
        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
}

static Dictionary<string, string> ParseOptions(string[] a, out List<string> positional)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    positional = [];
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < a.Length) d[a[i][2..]] = a[++i];
        else positional.Add(a[i]);
    }
    return d;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

sealed record Row(string Name, string? Expected, string Kind, List<string> Recognized, ScreenRecognition Result, string Verdict, string? Floor, List<string> SlotTruth,
    HashSet<string>? FloorCards);
