using System.Globalization;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Resources;
using Sts2Overlay.Resources.Pck;
using Sts2Overlay.Tools.TitleBench;
using Sts2Overlay.Vision;

// TitleBench: how much upgrade green (UpgradeDetector.TitleEvidence) a card title carries, per language.
//
//   TitleBench validate [--slots data/local/bench/slots.csv] [--captures dir] [--out dir]
//       Renders the Korean title of every upgraded card the bench saw on a real capture (reward row and card
//       grids) at the position and scale found there, and compares the detector's green fraction on the
//       rendering with the one on the capture. Writes side-by-side crops to --out.
//   TitleBench sweep [--out dir]
//       Every card of the catalog, Korean and English, upgraded ("Name+", green) and not (cream), at every node
//       scale the recogniser reads (shop 0.65/0.8, reward/grid 0.8/1.0) on FHD and 4K frames: distribution of the
//       green fraction, lowest upgraded titles, highest non-upgraded ones.
//   TitleBench banner | events | events-validate <capture> <EVENT.ID> [page]
//       Reward banner text extents vs the screen probes; event option text vs EventButtonDetector's stripes and
//       text signatures (page-to-page changes), in both languages.
//   TitleBench digits [--price-out dir]
//       Shop price labels rendered in GyeonggiBatang (Korean game) and Kreon Bold (English), segmented by the real
//       ShopPriceReader: template sets + width ranges (PriceGlyphs.Data.cs KreonData) and read accuracy per set.
// Options: --pck <SlayTheSpire2.pck>, --catalog data/local/catalog.<game version>.json, --data-version v0.111.0 (pack + committed
// catalog; default: the one the app picks for the game data/local was prepared from)
//
// Fonts and names are read from the game's pack / data/local at run time; nothing is written into the repo.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string repo = FindRepoRoot();
var opts = new Dictionary<string, string>();
var positional = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length) opts[args[i][2..]] = args[++i];
    else positional.Add(args[i]);
}
string pckPath = opts.GetValueOrDefault("pck") ?? GameInstall.Locate()?.PckPath
    ?? @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.pck";
string localDir = Path.Combine(repo, "data", "local");
string dataVersion = opts.GetValueOrDefault("data-version") ?? DataVersionSelector.ForLocalResources(repo, localDir).Version
    ?? throw new InvalidOperationException("no data pack under data/packs: pass --data-version");
Console.WriteLine($"data version {dataVersion}");
string catalogPath = opts.GetValueOrDefault("catalog") ?? new LocalLayout(localDir).ManifestCatalog()
    ?? Path.Combine(localDir, $"catalog.{dataVersion}.json");
string outDir = opts.GetValueOrDefault("out") ?? Path.Combine(repo, "data", "local", "bench", "titles");

using var pck = new PckReader(pckPath);
using var kor = new TitleFont("kor", GameFonts.Load(pck, "GyeonggiCheonnyeonBatangBold.ttf"), transform: float.Parse(opts.GetValueOrDefault("kor-transform") ?? "1", CultureInfo.InvariantCulture), glyphSpacing: 0, spacingTop: 1, spacingBottom: 3);
using var eng = new TitleFont("eng", GameFonts.Load(pck, "kreon_regular.ttf"), transform: 1f, glyphSpacing: 1);
List<CatalogEntry> catalog = LoadCatalog(catalogPath);
TitleCanvas.FillGrowPx = float.Parse(opts.GetValueOrDefault("grow") ?? "0.1", CultureInfo.InvariantCulture);
Console.WriteLine($"fonts: kor {kor.Name}, eng {eng.Name}; catalog {catalog.Count} cards");

switch (args.FirstOrDefault())
{
    case "validate":
        Validate();
        break;
    case "sweep":
        Sweep();
        break;
    case "dump-resource":
        // dump-resource <imported file name> <out>: decompressed .fontdata (debugging the import settings)
        File.WriteAllBytes(positional[1], GameFonts.Decompress(pck.ReadAllBytes(pck.Entries.First(e => e.Path.Contains(positional[0], StringComparison.Ordinal) && e.Path.EndsWith(".fontdata", StringComparison.Ordinal)))));
        break;
    case "events":
        Events();
        break;
    case "events-validate":
        // events-validate <capture.png> <EVENT.ID> [page]: real vs rendered Korean signature per button
        EventsValidate(positional[0], positional[1], positional.Count > 2 ? positional[2] : "INITIAL");
        break;
    case "digits":
        Digits();
        break;
    case "banner":
        Banner();
        break;
    case "render":
        // render <lang> <text> [scale]: one title onto a 1920x1080 canvas → out/render.png (debugging)
        RenderOne(positional[0] == "eng" ? eng : kor, positional[1], positional.Count > 2 ? float.Parse(positional[2]) : 0.8f);
        break;
    default:
        Console.Error.WriteLine("usage: TitleBench validate|sweep|render ...");
        return 1;
}
return 0;

void RenderOne(TitleFont font, string text, float scale)
{
    var canvas = new TitleCanvas(1920, 1080);
    canvas.Fill(new PixelRect(0, 0, 1920, 1080), new SKColor(190, 190, 185));
    var tex = TitleLayout.Layout(font, text);
    canvas.DrawTitle(tex.Path, 960, 616, scale, TitleLayout.Green, TitleLayout.OutlineSpecial);
    var ev = canvas.Evidence(960, 616, scale);
    Console.WriteLine($"{text}: font size {tex.Size}, green {ev.Green:F4}");
    SaveCrop(canvas.Frame, canvas.Region(960, 616, scale), Path.Combine(outDir, "render.png"), 4);
}

void Validate()
{
    string slotsPath = opts.GetValueOrDefault("slots") ?? Path.Combine(repo, "data", "local", "bench", "slots.csv");
    string captures = opts.GetValueOrDefault("captures") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay", "captures");
    var db = CardArtDatabase.LoadOrBuild(Path.Combine(repo, "data", "local", "pck", "card_art"),
        Path.Combine(repo, DataPackLoader.CatalogRelativePath(dataVersion)), Path.Combine(repo, "data", "local", "vision", "card-art.bin"));
    var rec = new CardRecognizer(db);
    var byId = catalog.ToDictionary(c => c.Id);

    // Rows: capture,kind,slot,card,upgraded,upgradeKnown,score,margin,green,scale,accepted,...
    var rows = File.ReadLines(slotsPath).Skip(1).Select(l => l.Split(','))
        .Where(f => f[10] == "True" && f[4] == "True" && f[1] is "RewardCardSelect" or "CardGrid" or "CardChoice" or "Shop")
        .ToList();
    Console.WriteLine($"{rows.Count} upgraded slots in {slotsPath}");
    var results = new List<(string Capture, string Card, float Scale, float Real, float Rendered, int Size)>();
    var sheet = new List<(ScreenFrame Real, PixelRect RealBox, ScreenFrame Rend, PixelRect RendBox)>();
    foreach (var group in rows.GroupBy(f => f[0]))
    {
        string path = Path.Combine(captures, group.Key);
        if (!File.Exists(path)) continue;
        ScreenFrame frame = ImageLoader.Load(path);
        ScreenRecognition r = rec.Recognize(frame);
        var t = RefTransform.For(frame.Width, frame.Height);
        foreach (string[] f in group)
        {
            int slot = int.Parse(f[2]);
            RecognizedCard? c = r.Slots.FirstOrDefault(s => s.Slot == slot && s.CardId == f[3]);
            if (c == null) { Console.WriteLine($"  {group.Key} #{slot}: not found again"); continue; }
            RectF cb = t.ToRef(new RectF(c.CardBounds.X, c.CardBounds.Y, c.CardBounds.Width, c.CardBounds.Height));
            var p = rec.Probe(frame, c.CardId!, cb.CenterX, cb.CenterY, c.NodeScale, 3);
            PixelRect tr = t.ToPixelRect(CardGeometry.Place(CardGeometry.Title, p.X, p.Y, p.Scale));
            using IntegralImage img = IntegralImage.Build(frame, new PixelRect(tr.X - 16, tr.Y - 16, tr.Width + 32, tr.Height + 32), Math.Max(1, (int)MathF.Floor(t.Scale + 1e-3f)));
            float real = UpgradeDetector.TitleEvidence(img, t, p.X, p.Y, p.Scale).Green;

            var canvas = new TitleCanvas(frame.Width, frame.Height);
            canvas.Fill(canvas.Region(p.X, p.Y, p.Scale, 24), new SKColor(190, 190, 185));
            string text = byId[c.CardId!].Kor + "+";
            var tex = TitleLayout.Layout(kor, text);
            canvas.DrawTitle(tex.Path, p.X, p.Y, p.Scale, TitleLayout.Green, TitleLayout.OutlineSpecial);
            float rendered = canvas.Evidence(p.X, p.Y, p.Scale).Green;
            results.Add((group.Key, c.CardId!, p.Scale, real, rendered, tex.Size));
            if (opts.ContainsKey("bbox"))
            {
                var rb = GreenBox(frame, canvas.Region(p.X, p.Y, p.Scale));
                var cb2 = GreenBox(canvas.Frame, canvas.Region(p.X, p.Y, p.Scale));
                Console.WriteLine($"    green bbox real {rb}  rendered {cb2}");
            }
            Console.WriteLine($"  {group.Key} #{slot} {c.CardId,-26} s={p.Scale:F2} size {tex.Size,2}  real {real:F4}  rendered {rendered:F4}  ratio {rendered / Math.Max(real, 1e-4f):F2}");
            if (sheet.Count < 40) sheet.Add((frame, canvas.Region(p.X, p.Y, p.Scale), canvas.Frame, canvas.Region(p.X, p.Y, p.Scale)));
        }
    }
    if (results.Count == 0) return;
    var ratios = results.Select(x => x.Rendered / Math.Max(x.Real, 1e-4f)).OrderBy(x => x).ToList();
    var diffs = results.Select(x => x.Rendered - x.Real).OrderBy(x => x).ToList();
    Console.WriteLine();
    Console.WriteLine($"validated {results.Count} slots: rendered/real ratio min {ratios[0]:F2} p10 {ratios[ratios.Count / 10]:F2} median {ratios[ratios.Count / 2]:F2} p90 {ratios[ratios.Count * 9 / 10]:F2} max {ratios[^1]:F2}; " +
                      $"diff min {diffs[0]:+0.0000;-0.0000} median {diffs[diffs.Count / 2]:+0.0000;-0.0000} max {diffs[^1]:+0.0000;-0.0000}");
    // Distinct (card, scale) pairs: the same title is captured many times.
    foreach (var g in results.GroupBy(x => (x.Card, MathF.Round(x.Scale, 1))).OrderBy(g => g.Key.Card))
        Console.WriteLine($"  {g.Key.Card,-26} scale {g.Key.Item2:F1} n={g.Count(),2}  real {g.Average(x => x.Real):F4} (min {g.Min(x => x.Real):F4})  rendered {g.Average(x => x.Rendered):F4}");
    SaveSheet(sheet, Path.Combine(outDir, "validate.png"));
    Console.WriteLine($"side-by-side crops (left real, right rendered): {Path.Combine(outDir, "validate.png")}");
}

void Sweep()
{
    float[] scales = [0.65f, 0.8f, 1.0f];
    (int W, int H)[] screens = [(1920, 1080), (3840, 2160)];
    var lines = new StringBuilder();
    var all = new List<(string Lang, string Id, string Text, int W, float Scale, bool Up, float Green, int Size)>();
    foreach ((int sw, int sh) in screens)
    {
        var canvas = new TitleCanvas(sw, sh);
        foreach (TitleFont font in new[] { kor, eng })
        {
            foreach (CatalogEntry card in catalog)
            {
                string name = font == kor ? card.Kor : card.Eng;
                if (string.IsNullOrEmpty(name)) continue;
                foreach (bool up in card.MaxUpgrade > 0 ? new[] { true, false } : new[] { false })
                {
                    string text = up ? name + "+" : name;
                    var tex = TitleLayout.Layout(font, text);
                    foreach (float scale in scales)
                    {
                        // Two sub-pixel phases (the card centre is not pixel-aligned in general); keep the worse one.
                        float g = 0;
                        foreach (float phase in (float[])[0f, 0.37f])
                        {
                            float cx = 960 + phase, cy = 616 + phase;
                            canvas.Fill(canvas.Region(cx, cy, scale, 24), new SKColor(190, 190, 185));
                            canvas.DrawTitle(tex.Path, cx, cy, scale, up ? TitleLayout.Green : TitleLayout.Cream,
                                up ? TitleLayout.OutlineSpecial : OutlineFor(card.Rarity));
                            float e = canvas.Evidence(cx, cy, scale).Green;
                            g = phase == 0 ? e : up ? Math.Min(g, e) : Math.Max(g, e);
                        }
                        all.Add((font.Name, card.Id, text, sw, scale, up, g, tex.Size));
                    }
                }
            }
        }
    }

    foreach (var grp in all.GroupBy(x => (x.Lang, x.W, x.Scale, x.Up)).OrderBy(g => g.Key.Up).ThenBy(g => g.Key.W).ThenBy(g => g.Key.Scale).ThenBy(g => g.Key.Lang))
    {
        var v = grp.Select(x => x.Green).OrderBy(x => x).ToList();
        string Q(double q) => v[(int)Math.Clamp(Math.Round(q * (v.Count - 1)), 0, v.Count - 1)].ToString("F4");
        lines.AppendLine($"{(grp.Key.Up ? "upgraded" : "normal  ")} {grp.Key.W}p scale {grp.Key.Scale:F2} {grp.Key.Lang}: n={v.Count} min {Q(0)} p1 {Q(0.01)} p5 {Q(0.05)} median {Q(0.5)} max {Q(1)}");
    }
    lines.AppendLine();
    foreach (string lang in new[] { "kor", "eng" })
    {
        lines.AppendLine($"lowest upgraded titles ({lang}, FHD, scale 0.80 / 0.65):");
        foreach (var x in all.Where(x => x.Lang == lang && x.Up && x.W == 1920 && Math.Abs(x.Scale - 0.8f) < 1e-3).OrderBy(x => x.Green).Take(15))
        {
            float shop = all.First(y => y.Lang == lang && y.Id == x.Id && y.Up && y.W == 1920 && Math.Abs(y.Scale - 0.65f) < 1e-3).Green;
            lines.AppendLine($"  {x.Id,-28} size {x.Size,2}  {x.Green:F4} / {shop:F4}  len {x.Text.Length}");
        }
    }
    var engSizes = all.Where(x => x.Lang == "eng" && x.Up && x.W == 1920 && x.Scale == 0.8f).GroupBy(x => x.Size).OrderBy(g => g.Key);
    lines.AppendLine("English upgraded font sizes (auto-shrink): " + string.Join(", ", engSizes.Select(g => $"{g.Key}px x{g.Count()}")));
    var korSizes = all.Where(x => x.Lang == "kor" && x.Up && x.W == 1920 && x.Scale == 0.8f).GroupBy(x => x.Size).OrderBy(g => g.Key);
    lines.AppendLine("Korean upgraded font sizes (auto-shrink): " + string.Join(", ", korSizes.Select(g => $"{g.Key}px x{g.Count()}")));
    var missing = catalog.Where(c => !eng.HasAllGlyphs(c.Eng + "+")).Select(c => c.Id).ToList();
    lines.AppendLine($"English names with glyphs missing from Kreon: {missing.Count} {string.Join(" ", missing)}");
    Console.Write(lines);
    Directory.CreateDirectory(outDir);
    File.WriteAllText(Path.Combine(outDir, "sweep.txt"), lines.ToString());
    var csv = new StringBuilder("lang,card,width,scale,upgraded,green,size\n");
    foreach (var x in all) csv.AppendLine($"{x.Lang},{x.Id},{x.W},{x.Scale:F2},{x.Up},{x.Green:F5},{x.Size}");
    File.WriteAllText(Path.Combine(outDir, "sweep.csv"), csv.ToString());
}

// Reward-screen banner text (scenes/ui/common_banner.tscn %Label: Kreon Bold + spacing_glyph 3, size 20..32, label
// x 733..1187, y 227..312 in reference pixels; Korean swaps in GyeonggiBatang Bold) against the classifier's
// banner probes, which must see plain ribbon.
void Banner()
{
    using var engBold = new TitleFont("eng-bold", GameFonts.Load(pck, "kreon_bold.ttf"), transform: 1f, glyphSpacing: 3);
    string loc = Path.Combine(repo, "data", "local", "pck", "localization");
    (string Name, float X, float Y, float W, float H)[] probes =
        [("banL", 715, 250, 75, 50), ("banR", 1135, 250, 70, 50), ("banTop", 820, 240, 280, 10)];
    foreach (string key in new[] { "CHOOSE_CARD_HEADER", "COMBAT_REWARD_HEADER_LOOT", "COMBAT_REWARD_HEADER_MUGGED", "CHOOSE_A_PACK", "CHOOSE_RELIC_HEADER", "CHOOSE_CARD_UPGRADE_HEADER" })
        foreach ((string lang, TitleFont font) in new[] { ("kor", kor), ("eng", engBold) })
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(loc, lang, "gameplay_ui.json")));
            if (!doc.RootElement.TryGetProperty(key, out JsonElement v)) continue;
            string text = v.GetString()!;
            var (path, size) = TitleLayout.Layout(font, text, 454, 85, 20, 32);
            path.Offset(733, 227);
            // Ink plus the outline (outline_size 16 → 4 px), in reference pixels.
            using var grown = new SKPaint { Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 8, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true, Color = SKColors.White };
            using SKPath outline = grown.GetFillPath(path);
            SKRect b = outline.Bounds;
            var parts = new List<string>();
            foreach (var pr in probes)
            {
                using var bmp = new SKBitmap(new SKImageInfo((int)pr.W, (int)pr.H, SKColorType.Alpha8, SKAlphaType.Premul));
                using (var c = new SKCanvas(bmp)) { c.Clear(SKColors.Transparent); c.Translate(-pr.X, -pr.Y); c.DrawPath(outline, grown); }
                double cov = 0;
                foreach (byte a in bmp.GetPixelSpan()) cov += a / 255.0;
                parts.Add($"{pr.Name} {cov / (pr.W * pr.H) * 100:F1}%");
            }
            Console.WriteLine($"{key,-28} {lang} size {size}  ink+outline x {b.Left:F0}..{b.Right:F0} y {b.Top:F0}..{b.Bottom:F0}  covers {string.Join(", ", parts)}  (text length {text.Length})");
        }
}

// Event option buttons: how much of EventButtonDetector's fill stripes the option text covers, and whether two
// different pages of one event could leave every button's text signature within EventScreenTracker.TextChanged.
void Events()
{
    using var engBold = new TitleFont("eng-bold", GameFonts.Load(pck, "kreon_bold.ttf"), transform: 1f, glyphSpacing: 1);
    string loc = Path.Combine(repo, "data", "local", "pck", "localization");
    using JsonDocument pack = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, DataPackLoader.PackRelativePath(dataVersion), "events.json")));
    var top = new SKRect(78, 13, 728, 19);    // texture y 5..11, x 1000..1650
    var bottom = new SKRect(78, 80, 728, 88); // texture y 72..80
    foreach (string lang in new[] { "kor", "eng" })
    {
        using JsonDocument text = JsonDocument.Parse(File.ReadAllText(Path.Combine(loc, lang, "events.json")));
        string? Get(string k) => text.RootElement.TryGetProperty(k, out JsonElement v) ? v.GetString() : null;
        TitleFont b = lang == "kor" ? kor : engBold, r = lang == "kor" ? kor : eng;
        var rows = new List<(string Key, int Size, int Lines, float Top, float Bottom)>();
        var collisions = new List<string>();
        int pagePairs = 0, transitions = 0;
        var transitionMisses = new List<string>();
        float minPairDistance = float.MaxValue;
        string minPair = "";
        // The same with EventScreenTracker's ink comparison (TextInk.Distance, every button, hovered or not).
        var inkPairs = new List<(float D, string Name)>();
        var inkTransitions = new List<(float D, string Name)>();
        foreach (JsonElement ev in pack.RootElement.GetProperty("events").EnumerateArray())
        {
            var vars = new Dictionary<string, double>();
            if (ev.TryGetProperty("vars", out JsonElement vs) && vs.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty vp in vs.EnumerateObject())
                    if (vp.Value.ValueKind == JsonValueKind.Number) vars[vp.Name] = vp.Value.GetDouble();
            var pages = new List<(string Page, List<float[]> Sigs, List<string> Texts, List<string?> Next, List<Sts2Overlay.Vision.Events.TextInk> Inks)>();
            foreach (JsonElement page in ev.GetProperty("pages").EnumerateArray())
            {
                var sigs = new List<float[]>();
                var inks = new List<Sts2Overlay.Vision.Events.TextInk>();
                var texts = new List<string>();
                var next = new List<string?>();
                foreach (JsonElement opt in page.GetProperty("options").EnumerateArray())
                {
                    string key = opt.GetProperty("key").GetString()!;
                    string title = EventText.Plain(Get(key + ".title") ?? "", vars);
                    string desc = EventText.Plain(Get(key + ".description") ?? "", vars);
                    var (path, size, lines) = EventText.Layout(b, r, title, desc);
                    rows.Add((key, size, lines, EventText.InkShare(path, top), EventText.InkShare(path, bottom)));
                    sigs.Add(EventText.Signature(path));
                    inks.Add(EventText.Ink(path));
                    texts.Add(title + "|" + desc);
                    next.Add(opt.TryGetProperty("next", out JsonElement nx) && nx.ValueKind == JsonValueKind.String ? nx.GetString() : null);
                }
                pages.Add((page.GetProperty("page").GetString()!, sigs, texts, next, inks));
            }
            // Real transitions: option k of page P leads to page Q. The mouse still rests on button k when Q comes
            // up, so EventScreenTracker.SameButtons compares only the other (idle) buttons.
            foreach (var from in pages)
                for (int k = 0; k < from.Next.Count; k++)
                {
                    var to = pages.FirstOrDefault(p => p.Page == from.Next[k]);
                    if (to.Sigs is null || to.Sigs.Count != from.Sigs.Count || from.Sigs.Count < 2 || to.Texts.SequenceEqual(from.Texts)) continue;
                    transitions++;
                    float d = Enumerable.Range(0, from.Sigs.Count).Where(i => i != k)
                        .Max(i => Sts2Overlay.Vision.Events.EventButtonDetector.SignatureDistance(from.Sigs[i], to.Sigs[i]));
                    if (d <= 0.02f) transitionMisses.Add($"{ev.GetProperty("id").GetString()} {from.Page} --#{k}--> {to.Page} ({d:F4})");
                    inkTransitions.Add((Enumerable.Range(0, from.Inks.Count).Max(i => Sts2Overlay.Vision.Events.TextInk.Distance(from.Inks[i], to.Inks[i])),
                        $"{ev.GetProperty("id").GetString()} {from.Page} --#{k}--> {to.Page} {Verdict(from.Inks, to.Inks)}"));
                }
            for (int i = 0; i < pages.Count; i++)
                for (int j = i + 1; j < pages.Count; j++)
                {
                    if (pages[i].Sigs.Count != pages[j].Sigs.Count || pages[i].Sigs.Count == 0) continue;
                    // Pages showing the very same options (loops) cannot be told apart by any text signature.
                    if (pages[i].Texts.SequenceEqual(pages[j].Texts)) continue;
                    pagePairs++;
                    float d = pages[i].Sigs.Zip(pages[j].Sigs, (x, y) => Sts2Overlay.Vision.Events.EventButtonDetector.SignatureDistance(x, y)).Max();
                    string name = $"{ev.GetProperty("id").GetString()} {pages[i].Page}/{pages[j].Page}";
                    if (d < minPairDistance) { minPairDistance = d; minPair = name; }
                    if (d <= 0.02f) collisions.Add($"{name} ({d:F4})");
                    inkPairs.Add((pages[i].Inks.Zip(pages[j].Inks, Sts2Overlay.Vision.Events.TextInk.Distance).Max(), $"{name} {Verdict(pages[i].Inks, pages[j].Inks, ordered: false)}"));
                }
        }
        var tops = rows.Select(x => x.Top).OrderBy(x => x).ToList();
        var bots = rows.Select(x => x.Bottom).OrderBy(x => x).ToList();
        Console.WriteLine($"{lang}: {rows.Count} options; font size min {rows.Min(x => x.Size)} median {rows.Select(x => x.Size).OrderBy(x => x).ElementAt(rows.Count / 2)}; " +
                          $"lines max {rows.Max(x => x.Lines)} (3+ lines: {rows.Count(x => x.Lines >= 3)})");
        Console.WriteLine($"  text ink in top stripe: max {tops[^1] * 100:F1}% (options > 0: {tops.Count(x => x > 0)}); bottom stripe: max {bots[^1] * 100:F1}% (options > 0: {bots.Count(x => x > 0)})");
        foreach (var x in rows.OrderByDescending(x => Math.Max(x.Top, x.Bottom)).Take(5))
            Console.WriteLine($"    {x.Key,-60} size {x.Size} lines {x.Lines} top {x.Top * 100:F1}% bottom {x.Bottom * 100:F1}%");
        Console.WriteLine($"  page pairs with equal button counts and different text: {pagePairs}; closest pair {minPair} max-button distance {minPairDistance:F4}; at or below TextChanged 0.02: {collisions.Count}");
        foreach (string c in collisions) Console.WriteLine($"    {c}");
        Console.WriteLine($"  page transitions to a page with the same button count and other text: {transitions}; other buttons' signatures within 0.02: {transitionMisses.Count}");
        foreach (string c in transitionMisses) Console.WriteLine($"    {c}");
        Console.WriteLine($"  ink verdicts: page pairs not Changed {inkPairs.Count(x => !x.Name.Contains("Changed"))}, transitions not Changed {inkTransitions.Count(x => !x.Name.Contains("Changed"))}");
        foreach (var x in inkPairs.Concat(inkTransitions).Where(x => !x.Name.Contains("Changed"))) Console.WriteLine($"    {x.Name} {x.D:F4}");
        Console.WriteLine($"  ink (TextInk.Distance, all buttons): page pairs {inkPairs.Count}, lowest " +
                          string.Join(", ", inkPairs.OrderBy(x => x.D).Take(5).Select(x => $"{x.Name} {x.D:F4}")));
        Console.WriteLine($"  ink transitions {inkTransitions.Count}, lowest " +
                          string.Join(", ", inkTransitions.OrderBy(x => x.D).Take(5).Select(x => $"{x.Name} {x.D:F4}")));
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), $"titlebench-ink-{lang}.tsv"),
            inkPairs.Select(x => $"pair	{x.D:F4}	{x.Name}").Concat(inkTransitions.Select(x => $"transition	{x.D:F4}	{x.Name}")));
    }
}

// Page verdict as EventScreenTracker reads it with page a as the reference and b on screen: Changed when any button
// is, else Unsure when any is, else Same. For an unordered pair, the weaker of the two orders.
static Sts2Overlay.Vision.Events.TextVerdict Judge(List<Sts2Overlay.Vision.Events.TextInk> a, List<Sts2Overlay.Vision.Events.TextInk> b) =>
    a.Zip(b, Sts2Overlay.Vision.Events.TextInk.Judge).Max();

static string Verdict(List<Sts2Overlay.Vision.Events.TextInk> a, List<Sts2Overlay.Vision.Events.TextInk> b, bool ordered = true)
{
    var v = ordered ? Judge(a, b) : (Sts2Overlay.Vision.Events.TextVerdict)Math.Min((int)Judge(a, b), (int)Judge(b, a));
    float inside = a.Zip(b, (x, y) => Sts2Overlay.Vision.Events.TextInk.Match(x, y))
        .Where(m => m.Distance >= Sts2Overlay.Vision.Events.TextInk.ChangedMin).Select(m => Math.Min(m.Lost, m.Gained)).DefaultIfEmpty(-1).Max();
    return $"{v} (inside {inside:F3})";
}

void EventsValidate(string capture, string eventId, string pageName)
{
    string captures = opts.GetValueOrDefault("captures") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay", "captures");
    var screen = Sts2Overlay.Vision.Events.EventButtonDetector.Detect(ImageLoader.Load(Path.Combine(captures, capture)));
    using JsonDocument pack = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, DataPackLoader.PackRelativePath(dataVersion), "events.json")));
    using JsonDocument text = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "data", "local", "pck", "localization", "kor", "events.json")));
    string? Get(string k) => text.RootElement.TryGetProperty(k, out JsonElement v) ? v.GetString() : null;
    JsonElement ev = pack.RootElement.GetProperty("events").EnumerateArray().First(e => e.GetProperty("id").GetString() == eventId);
    var vars = new Dictionary<string, double>();
    foreach (JsonProperty vp in ev.GetProperty("vars").EnumerateObject())
        if (vp.Value.ValueKind == JsonValueKind.Number) vars[vp.Name] = vp.Value.GetDouble();
    JsonElement page = ev.GetProperty("pages").EnumerateArray().First(p => p.GetProperty("page").GetString() == pageName);
    var opts2 = page.GetProperty("options").EnumerateArray().ToList();
    Console.WriteLine($"{capture}: {screen?.Buttons.Count ?? 0} buttons, page {pageName} has {opts2.Count} options");
    if (screen == null) return;
    for (int i = 0; i < Math.Min(opts2.Count, screen.Buttons.Count); i++)
    {
        string key = opts2[i].GetProperty("key").GetString()!;
        var (path, size, lines) = EventText.Layout(kor, kor, EventText.Plain(Get(key + ".title") ?? "", vars), EventText.Plain(Get(key + ".description") ?? "", vars));
        float[] sig = EventText.Signature(path);
        float d = Sts2Overlay.Vision.Events.EventButtonDetector.SignatureDistance(sig, screen.Buttons[i].TextSignature);
        Console.WriteLine($"  #{i} {screen.Buttons[i].State,-7} {key,-55} size {size} lines {lines}  rendered-vs-real {d:F4}  ink real {screen.Buttons[i].TextSignature.Average():F3} rendered {sig.Average():F3}");
    }
}

// Shop price digits (merchant_card.tscn %CostLabel: a MegaLabel, Kreon Bold + spacing_glyph 2, size 39). A MegaLabel
// gets the locale font substitution, so on a Korean game the digits are GyeonggiBatang Bold's. Renders whole price
// labels (coin, outline, rug) in both fonts at FHD and 4K, node scales 0.65..0.8 and sub-pixel offsets, runs the real
// ShopPriceReader.Segment on them and averages the digit grids into templates exactly as VisionBench price-glyphs
// does with real captures. Then reads every rendering with the committed templates and with the rendered ones.
void Digits()
{
    using var korPrice = new TitleFont("gyeonggi", GameFonts.Load(pck, "GyeonggiCheonnyeonBatangBold.ttf"), transform: 1f, glyphSpacing: 0);
    using var kreonBold = new TitleFont("kreon-bold", GameFonts.Load(pck, "kreon_bold.ttf"), transform: 1f, glyphSpacing: 2);
    string dir = opts.GetValueOrDefault("price-out") ?? Path.Combine(repo, "data", "local", "price");
    var numbers = Enumerable.Range(10, 190).Concat(Enumerable.Range(200, 100).Where(n => n % 7 == 0))
        .Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList();
    var configs = new List<(int W, int H, float Scale, float Dx)>();
    foreach (var (w, h) in new[] { (1920, 1080), (3840, 2160) })
        foreach (float s in new[] { 0.65f, 0.72f, 0.8f })
            foreach (float dx in new[] { 0f, 0.25f, 0.5f, 0.75f })
                configs.Add((w, h, s, dx));
    const float Cx = 437f, Cy = 383f; // shop slot 0
    var canvases = new Dictionary<int, TitleCanvas>();
    IEnumerable<(string Truth, (int W, int H, float Scale, float Dx) Config, TitleCanvas Canvas)> Renders(TitleFont font)
    {
        foreach (var cfg in configs)
        {
            if (!canvases.TryGetValue(cfg.W, out TitleCanvas? canvas)) canvases[cfg.W] = canvas = new TitleCanvas(cfg.W, cfg.H);
            foreach (string truth in numbers)
            {
                PriceLabel.Draw(canvas, font, truth, Cx + cfg.Dx, Cy, cfg.Scale);
                yield return (truth, cfg, canvas);
            }
        }
    }
    Directory.CreateDirectory(dir);
    foreach (TitleFont font in new[] { korPrice, kreonBold })
    {
        var sum = Enumerable.Range(0, 10).Select(_ => new float[PriceGlyphs.Length]).ToArray();
        var count = new int[10];
        var widths = Enumerable.Range(0, 10).Select(_ => new List<float>()).ToArray();
        var heights = new List<float>();
        var blockX = new List<float>();
        int rows = 0, segmented = 0;
        foreach (var (truth, cfg, canvas) in Renders(font))
        {
            rows++;
            var row = ShopPriceReader.Segment(canvas.Frame, Cx + cfg.Dx, Cy, cfg.Scale);
            if (row == null || row.Glyphs.Count != truth.Length) continue;
            segmented++;
            heights.Add(row.Bounds.Height / (canvas.T.Scale * cfg.Scale));
            blockX.Add(row.CenterX);
            for (int i = 0; i < truth.Length; i++)
            {
                int d = truth[i] - '0';
                count[d]++;
                widths[d].Add(row.Widths[i]);
                for (int k = 0; k < PriceGlyphs.Length; k++) sum[d][k] += row.Glyphs[i][k];
            }
        }
        for (int d = 0; d < 10; d++)
            for (int k = 0; k < PriceGlyphs.Length; k++) sum[d][k] /= Math.Max(1, count[d]);
        var rendered = new PriceGlyphs(sum);
        heights.Sort();
        blockX.Sort();
        string pitch = string.Join(" ", Enumerable.Range(0, 10).Select(d => (TitleLayout.LineWidth(font, d.ToString(CultureInfo.InvariantCulture), (int)PriceLabel.FontSize, out _, out _) / ShopPriceReader.DigitHeight).ToString("F2", CultureInfo.InvariantCulture)));
        Console.WriteLine($"{font.Name}: digit pitch / digit height {pitch}");
        Console.WriteLine($"{font.Name}: {segmented}/{rows} labels segmented into the right number of digits; digit height {heights[0]:F1}..{heights[^1]:F1} card units; block centre x {blockX[0]:F1}..{blockX[^1]:F1}");
        for (int d = 0; d < 10; d++)
        {
            var w = widths[d].OrderBy(x => x).ToList();
            var (lo, hi) = PriceGlyphs.WidthRange[d];
            Console.WriteLine($"  {d}: n={count[d]} width/height {w[0]:F3}..{w[^1]:F3} (committed {lo:F3}..{hi:F3}); NCC vs committed template {PriceGlyphs.Default.Score(rendered.Raw[d], d):F3}");
        }
        string file = Path.Combine(dir, $"rendered-{font.Name}.txt");
        File.WriteAllLines(file, rendered.Format().Select(l => $"        \"{l}\","));
        Console.WriteLine($"  templates -> {file}");
        Console.WriteLine($"  width ranges: {string.Join(", ", widths.Select(w => $"({w.Min():F3}f, {w.Max():F3}f)"))}");

        foreach (var (name, reader) in new[] { ("committed Korean set only", new ShopPriceReader(PriceGlyphs.Default)), ("committed sets (Korean + Kreon)", new ShopPriceReader()), ("these rendered templates only", new ShopPriceReader(rendered)) })
        {
            int textOk = 0, value = 0, wrong = 0, total = 0;
            float minScore = 1, minMargin = 1, minGap = 1;
            var fonts = new Dictionary<string, int>();
            foreach (var (truth, cfg, canvas) in Renders(font))
            {
                total++;
                PriceReading? r = reader.Read(canvas.Frame, Cx + cfg.Dx, Cy, cfg.Scale, CardSlotKind.ShopCharacter);
                if (r != null)
                {
                    fonts[r.Font] = fonts.GetValueOrDefault(r.Font) + 1;
                    minGap = Math.Min(minGap, r.FontGap);
                }
                if (r?.Text == truth)
                {
                    textOk++;
                    minScore = Math.Min(minScore, r.MinScore);
                    minMargin = Math.Min(minMargin, r.MinMargin);
                }
                if (r?.Value is { } v)
                {
                    if (v.ToString(CultureInfo.InvariantCulture) == truth) value++;
                    else
                    {
                        wrong++;
                        if (wrong <= 5) Console.WriteLine($"    WRONG {truth} read {r} at {cfg}");
                    }
                }
            }
            Console.WriteLine($"  {name}: digits right {textOk}/{total} (NCC min {minScore:F3}, margin min {minMargin:F3}); accepted prices {value} right, {wrong} wrong; font chosen {string.Join(" ", fonts.Select(f => $"{f.Key}:{f.Value}"))}, font gap min {minGap:F3}");
        }
    }
}

static string GreenBox(ScreenFrame f, PixelRect r)
{
    ReadOnlySpan<byte> px = f.Bgra.Span;
    int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, n = 0;
    for (int y = r.Y; y < r.Bottom; y++)
        for (int x = r.X; x < r.Right; x++)
        {
            int p = y * f.Stride + x * 4;
            if (!UpgradeDetector.IsUpgradeGreen(px[p + 2], px[p + 1], px[p])) continue;
            n++;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
    return n == 0 ? "none" : $"x {x0}-{x1} ({x1 - x0 + 1}) y {y0}-{y1} ({y1 - y0 + 1}) n {n}";
}

static SKColor OutlineFor(string? rarity) => rarity switch
{
    "Uncommon" => TitleLayout.OutlineUncommon,
    "Rare" => TitleLayout.OutlineRare,
    "Event" => TitleLayout.OutlineSpecial,
    _ => TitleLayout.OutlineCommon,
};

static void SaveCrop(ScreenFrame frame, PixelRect r, string path, int zoom)
{
    var info = new SKImageInfo(r.Width * zoom, r.Height * zoom, SKColorType.Bgra8888, SKAlphaType.Unpremul);
    using var bmp = new SKBitmap(info);
    Blit(bmp, frame, r, 0, 0, zoom);
    Save(bmp, path);
}

static void SaveSheet(List<(ScreenFrame Real, PixelRect RealBox, ScreenFrame Rend, PixelRect RendBox)> sheet, string path)
{
    if (sheet.Count == 0) return;
    const int zoom = 2;
    int w = sheet.Max(s => s.RealBox.Width + s.RendBox.Width) * zoom + 8, h = sheet.Sum(s => Math.Max(s.RealBox.Height, s.RendBox.Height) * zoom + 4);
    using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
    bmp.Erase(SKColors.Black);
    int y = 0;
    foreach (var s in sheet)
    {
        Blit(bmp, s.Real, s.RealBox, 0, y, zoom);
        Blit(bmp, s.Rend, s.RendBox, s.RealBox.Width * zoom + 8, y, zoom);
        y += Math.Max(s.RealBox.Height, s.RendBox.Height) * zoom + 4;
    }
    Save(bmp, path);
}

static void Blit(SKBitmap dst, ScreenFrame f, PixelRect r, int ox, int oy, int zoom)
{
    ReadOnlySpan<byte> src = f.Bgra.Span;
    for (int y = 0; y < r.Height * zoom; y++)
        for (int x = 0; x < r.Width * zoom; x++)
        {
            int sx = r.X + x / zoom, sy = r.Y + y / zoom;
            if (sx < 0 || sy < 0 || sx >= f.Width || sy >= f.Height) continue;
            int p = sy * f.Stride + sx * 4;
            dst.SetPixel(ox + x, oy + y, new SKColor(src[p + 2], src[p + 1], src[p]));
        }
}

static void Save(SKBitmap bmp, string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream fs = File.Create(path);
    data.SaveTo(fs);
}

static List<CatalogEntry> LoadCatalog(string path)
{
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
    var list = new List<CatalogEntry>();
    foreach (JsonElement c in doc.RootElement.GetProperty("cards").EnumerateArray())
    {
        JsonElement names = c.GetProperty("names");
        list.Add(new CatalogEntry(c.GetProperty("id").GetString()!,
            names.TryGetProperty("kor", out var k) ? k.GetString() ?? "" : "",
            names.TryGetProperty("eng", out var e) ? e.GetString() ?? "" : "",
            c.TryGetProperty("maxUpgrade", out var m) ? m.GetInt32() : 0,
            c.TryGetProperty("rarity", out var r) ? r.GetString() : null));
    }
    return list;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SlayTheSpire2TierOverlay.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

internal sealed record CatalogEntry(string Id, string Kor, string Eng, int MaxUpgrade, string? Rarity);
