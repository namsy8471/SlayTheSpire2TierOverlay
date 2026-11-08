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
//   VisionBench bench     [--captures dir] [--save file | --saves dir] [--labels file] [--out dir] [--upscale 2] [--partial off]
//   VisionBench stress    [--captures dir] [--out dir] [--every n] [--pool on] [--rows a,min,s,m]   covers clean cards artificially: recall + impostor accepts
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
        var rec = new CardRecognizer(LoadDb());
        ScreenFrame f = ImageLoader.Load(positional[0]);
        float P(int i) => float.Parse(positional[i], CultureInfo.InvariantCulture);
        var b = rec.Probe(f, positional[1], P(2), P(3), P(4), positional.Count > 5 ? P(5) : 12);
        Console.WriteLine($"best {b.Score:F3} at ({b.X:F0}, {b.Y:F0}) scale {b.Scale:F2}");
        break;
    }
    default:
        Console.WriteLine("commands: build-db | selftest | recognize <png> | annotate <png> [out] | bench");
        break;
}

// --partial off: disable the occlusion-robust second pass (before/after comparisons).
// --blocks / --rows agree,minTiles,score,margin[,wholeTopK]: override a partial stage (threshold tuning).
RecognizerOptions RecognizerOptionsFromArgs()
{
    var o = new RecognizerOptions { PartialMatching = opts.GetValueOrDefault("partial") != "off" };
    static PartialStage Parse(PartialStage d, string? arg)
    {
        if (arg == null) return d;
        float[] v = arg.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return d with { Agree = v[0], MinTiles = (int)v[1], AcceptScore = v[2], AcceptMargin = v[3], WholeTopK = v.Length > 4 ? (int)v[4] : d.WholeTopK };
    }
    return o with { BlockStage = Parse(o.BlockStage, opts.GetValueOrDefault("blocks")), RowStage = Parse(o.RowStage, opts.GetValueOrDefault("rows")) };
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
    var rec = new CardRecognizer(db, RecognizerOptionsFromArgs());
    ScreenFrame frame = ImageLoader.Load(png);
    if (opts.TryGetValue("upscale", out string? up)) frame = Upscale(frame, int.Parse(up));
    var hints = new RecognitionHints(opts.GetValueOrDefault("character"));
    rec.Recognize(frame, hints); // warm-up (JIT)
    ScreenRecognition r = rec.Recognize(frame, hints);
    Console.WriteLine($"{Path.GetFileName(png)} {frame.Width}x{frame.Height}: {r.Kind} in {r.ElapsedMs:F1} ms ({r.Detail})");
    Console.WriteLine($"  gate: reward={r.Gate.RewardLikely} shop={r.Gate.ShopLikely} [{r.Gate.Detail}]");
    foreach (RecognizedCard c in r.Slots)
    {
        Console.WriteLine($"  {c} scale={c.NodeScale:F2} green={c.UpgradeEvidence:F3} card={c.CardBounds}");
        foreach (ArtMatch m in c.Candidates) Console.WriteLine($"      {m.Score:F3} {m.CardId}");
    }
}

void Profile(string png)
{
    // Where does the time go? Repeats each stage to get stable numbers (first call pays JIT).
    CardArtDatabase db = LoadDb();
    var rec = new CardRecognizer(db);
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
    var rec = new CardRecognizer(db);
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
    var rec = new CardRecognizer(db, RecognizerOptionsFromArgs());
    Labels labels = Labels.Load(labelsPath);

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
        TrackedScreen tracked = tracker.Update(r, time);
        if (tracked.Kind != ScreenKind.Other)
        {
            if (tracker.Generation != generation) visits.Add((name, name, tracked, truth));
            else visits[^1] = (visits[^1].First, name, tracked, visits[^1].Run);
            visitSlots[visits.Count - 1] = Math.Max(visitSlots.GetValueOrDefault(visits.Count - 1), r.Slots.Count);
            generation = tracker.Generation;
        }
        Row row = rows[^1];
        Console.WriteLine($"{name,-34} {r.Kind,-16} {r.ElapsedMs,6:F1} ms  {row.Verdict,-12} {string.Join(" ", row.Recognized)}");
    }

    string report = Report(rows, runs, capturesDir, upscale, db.Count, VisitReport(visits, visitSlots), coldNotes);
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
            csv.AppendLine($"{row.Name},{row.Kind},{c.Slot},{c.CardId},{c.Upgraded},{c.UpgradeKnown},{c.Score:F4},{c.Margin:F4},{c.UpgradeEvidence:F4},{c.NodeScale:F2},{c.Accepted},{row.SlotTruth[i]}," +
                $"{p?.CardId},{p?.Score:F4},{p?.Margin:F4},{p?.Stage}:{p?.VisibleTiles},{p?.Accepted},{(p == null ? "" : row.FloorCards == null ? "?" : row.FloorCards.Contains(p.CardId) ? "in-floor" : "not-in-floor")}");
        }
    File.WriteAllText(Path.Combine(outDir, upscale > 1 ? $"slots-x{upscale}.csv" : "slots.csv"), csv.ToString());
    Console.WriteLine();
    Console.WriteLine(report[..report.IndexOf("## Captures", StringComparison.Ordinal)]);
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
        if (truthCards != null)
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
    sb.AppendLine($"| other screens | {other.Count} | {other.Count(r => r.Kind == "Other")} | - | false positives: {other.Count(r => r.Kind != "Other")} |");
    sb.AppendLine($"| unlabelled | {unlabelled.Count} | - | - | recognised as reward {unlabelled.Count(r => r.Kind == "RewardCardSelect")}, shop {unlabelled.Count(r => r.Kind == "Shop")}; checked against the save: {Verdicts(unlabelled.Where(r => r.Kind != "Other"))} |");
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
