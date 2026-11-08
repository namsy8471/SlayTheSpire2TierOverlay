using System.Diagnostics;
using System.Globalization;
using SkiaSharp;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Tools.MapBench;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Map;

// MapBench fit <frame files or folders...> --saves <dir with current_run*.save copies> [--render <dir>] [--truth <file>]
//
// For every frame, every distinct act map found in the saves is fitted; the best fit decides "map or not".
// --truth: see below. A map frame passes when it is detected and every measurable node icon is within 6 design px of
// the fitted layout; any other frame passes when it is not detected as a map.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 0 || args[0] != "fit")
{
    Console.WriteLine("usage: MapBench fit <frames|dirs...> --saves <dir> [--render <dir>] [--truth <file>]");
    return 2;
}

var inputs = new List<string>();
string? savesDir = null, renderDir = null, truthFile = null, previewDir = null, packDir = null;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--saves": savesDir = args[++i]; break;
        case "--render": renderDir = args[++i]; break;
        case "--truth": truthFile = args[++i]; break;
        case "--preview": previewDir = args[++i]; break;
        case "--pack": packDir = args[++i]; break;
        default: inputs.Add(args[i]); break;
    }
}

var layouts = new List<(string Name, MapScreenLayout Layout, MapGraph Map)>();
// Every save snapshot with its time (from the file name), for --preview's run state at the frame's time.
var saves = new List<(DateTime At, ParsedSave Save)>();
if (savesDir != null)
{
    foreach (string f in Directory.GetFiles(savesDir, "*.save").Order(StringComparer.Ordinal))
    {
        ParsedSave? s = RunSaveParser.ParseFile(f, null);
        if (s != null && MarkPreview.TimeOf(Path.GetFileName(f)) is { } savedAt) saves.Add((savedAt, s));
        if (s?.Map is not { } map || MapScreenGeometry.Compute(map, s.Seed) is not { } layout) continue;
        if (layouts.Any(l => l.Layout.Seed == layout.Seed && l.Layout.ActIndex == layout.ActIndex)) continue;
        layouts.Add(($"{s.Seed}/act{layout.ActIndex + 1}", layout, map));
    }
}
Console.WriteLine($"{layouts.Count} act map(s): {string.Join(", ", layouts.Select(l => l.Name))}");

var files = inputs.SelectMany(p => Directory.Exists(p)
        ? Directory.GetFiles(p).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        : [p])
    .Order(StringComparer.Ordinal).ToList();

// Truth lines: "<frame name> map" (the act map of a known save is open, unobstructed) or "<frame name> -" (anything
// else, including a map of an act we have no save for, which must NOT be fitted), or "<frame name> skip <reason>"
// (a transition frame where either answer is fine, e.g. the act title banner over a scrolling map).
Dictionary<string, string>? truth = truthFile is null ? null : File.ReadAllLines(truthFile)
    .Where(l => l.Trim().Length > 0 && !l.StartsWith('#'))
    .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    .ToDictionary(p => p[0], p => p[1]);
const float MaxNodeError = 6f;
int nodesTotal = 0, nodesGood = 0, nodesSkewed = 0;

// Width/height of each icon's ink box (median over 1,720 measured icons). A box whose shape differs from this by more
// than 5 % is not the icon alone: a path dash, the "visited" brush circle or its drawing streaks touch it and stretch
// the box on one side, which moves its centre by half the stretch. Those boxes can't measure the node centre and are
// counted apart (a hovered icon grows evenly and keeps its shape, so it stays in).
var iconAspect = new Dictionary<MapNodeType, float>
{
    [MapNodeType.Monster] = 1.0f, [MapNodeType.Unknown] = 1.0f, [MapNodeType.Shop] = 1.0f,
    [MapNodeType.RestSite] = 0.75f, [MapNodeType.Elite] = 1.304f, [MapNodeType.Treasure] = 1.36f,
};
const float MaxAspectDeviation = 0.05f;

var est = new MapScrollEstimator();
DataPack? pack = previewDir is null ? null : DataPackLoader.LoadDirectory(packDir ?? Path.Combine("data", "packs", "v0.107.1"));
int previews = 0;
int maps = 0, checkedN = 0, pass = 0;
var times = new List<double>();
foreach (string file in files)
{
    ScreenFrame frame;
    try { frame = ImageLoader.Load(file); }
    catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(file)}: load failed {ex.Message}"); continue; }

    (string Name, MapScreenLayout Layout, MapGraph Map)? bestL = null;
    MapFit? best = null;
    foreach (var l in layouts)
    {
        var marks = Marks(l.Layout, l.Map);
        var sw = Stopwatch.StartNew();
        MapFit fit = Environment.GetEnvironmentVariable("MAPBENCH_RANGE") is { } rg && rg.Split(',') is [var lo, var hi] ? est.Estimate(frame, marks, float.Parse(lo), float.Parse(hi)) : est.Estimate(frame, marks);
        times.Add(sw.Elapsed.TotalMilliseconds);
        if (best is null || (fit.IsMap && !best.IsMap) || (fit.IsMap == best.IsMap && fit.Score > best.Score)) { best = fit; bestL = l; }
    }
    best ??= MapFit.None("no layouts");
    string name = Path.GetFileName(file);
    if (best.IsMap) maps++;
    // Node check: every visible node whose icon box can be measured must sit within MaxNodeError design px of the
    // fitted layout (icon textures are centred on the node, so the box centre is the node centre).
    int nodesHere = 0, nodesOk = 0;
    var bad = new List<string>();
    // MAPBENCH_FORCE=1: check/render the best fit even when it was rejected (to see why).
    bool force = Environment.GetEnvironmentVariable("MAPBENCH_FORCE") == "1";
    if ((best.IsMap || force) && bestL is { } rl)
    {
        foreach (var (coord, p) in rl.Layout.Centers.OrderBy(kv => kv.Key.Row).ThenBy(kv => kv.Key.Col))
        {
            if (coord == rl.Map.Start || coord == rl.Map.Boss || coord == rl.Map.SecondBoss) continue;
            float y = p.Y + best.Scroll;
            if (y < 50 || y > 1030 || MapScrollEstimator.IsMasked(p.X, y)) continue;
            if (est.IconBox(p.X, y) is not { } b) continue;
            if (Environment.GetEnvironmentVariable("MAPBENCH_SIZES") is { } sizesFile)
                File.AppendAllText(sizesFile, $"{name} {coord} {rl.Map.Nodes[coord].Type} {b.W:F0} {b.H:F0} {b.X - p.X:F1} {b.Y - y:F1}\n");
            if (iconAspect.TryGetValue(rl.Map.Nodes[coord].Type, out float a0) && Math.Abs(b.W / b.H / a0 - 1) > MaxAspectDeviation)
            {
                nodesSkewed++;
                continue;
            }
            nodesHere++;
            float ex = b.X - p.X, ey = b.Y - y;
            if (Math.Abs(ex) <= MaxNodeError && Math.Abs(ey) <= MaxNodeError) nodesOk++;
            else bad.Add($"{coord} {rl.Map.Nodes[coord].Type} off ({ex:F0},{ey:F0})");
            if (Environment.GetEnvironmentVariable("MAPBENCH_RESIDUALS") == "1")
                Console.WriteLine($"   {coord} {rl.Map.Nodes[coord].Type,-9} box=({ex,4:F0},{ey,4:F0})");
        }
        nodesTotal += nodesHere;
        nodesGood += nodesOk;
    }
    string verdict = "";
    if (truth != null && truth.TryGetValue(name, out string? want) && want != "skip")
    {
        checkedN++;
        bool ok = want == "map" ? best.IsMap && bad.Count == 0 : !best.IsMap;
        if (ok) pass++;
        verdict = ok ? " OK" : $" FAIL (truth {want})";
    }
    if (best.IsMap || (truth != null && truth.ContainsKey(name)) || (Environment.GetEnvironmentVariable("MAPBENCH_ALL") == "1" && best.Paper > 0))
        Console.WriteLine($"{name}: {(best.IsMap ? bestL?.Name : "-")} {best} nodes {nodesOk}/{nodesHere}{verdict}" + (bad.Count > 0 ? " BAD " + string.Join("; ", bad) : ""));
    if (previewDir != null && pack != null && best.IsMap && bestL is { } pl && MarkPreview.TimeOf(name) is { } frameAt
        && MarkPreview.SaveAt(saves, frameAt, pl.Layout.Seed, pl.Map.Act) is { Map: { } runMap } runSave)
    {
        var rec = new PathAdvisor(pack).Recommend(runSave.State, runMap);
        var plan = MapMarkLayout.Build(runMap, pl.Layout, best.Scroll, rec, [], UnknownRoomOdds.From(runSave.UnknownMapPointOdds));
        string what = rec.NextStep is { } ns ? $"next {ns} {runMap.Nodes[ns].Type}" : "no path";
        MarkPreview.Render(file, frame, plan, $"at {runMap.Current?.ToString() ?? "start"} -> {what}  ({plan.Lines.Count} lines, {plan.Nodes.Count} marks)",
            Path.Combine(previewDir, Path.ChangeExtension(name, ".jpg")));
        previews++;
    }
    if (renderDir != null && (best.IsMap || force) && bestL is { } bl) Render(file, frame, bl.Layout, best, Path.Combine(renderDir, Path.ChangeExtension(name, ".png")));
}
Console.WriteLine($"frames {files.Count}, detected as map {maps}, nodes within {MaxNodeError} px {nodesGood}/{nodesTotal} ({nodesSkewed} stretched icon boxes not measured)" + (truth != null ? $", truth checks {pass}/{checkedN}" : "")
    + (times.Count > 0 ? $", fit median {times.Order().ElementAt(times.Count / 2):F1} ms" : "")
    + (previewDir != null ? $", {previews} preview(s)" : ""));
return (truth != null && pass != checkedN) || nodesGood != nodesTotal ? 1 : 0;

static List<MapMark> Marks(MapScreenLayout layout, MapGraph map) => layout.Centers
    .Where(kv => kv.Key != map.Start && kv.Key != map.Boss && kv.Key != map.SecondBoss)
    .Select(kv => new MapMark(kv.Value.X, kv.Value.Y))
    .ToList();

static void Render(string file, ScreenFrame frame, MapScreenLayout layout, MapFit fit, string outPath)
{
    using var bmp = SKBitmap.Decode(file);
    using var canvas = new SKCanvas(bmp);
    RefTransform t = RefTransform.For(frame.Width, frame.Height);
    using var pen = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * t.Scale, IsAntialias = true };
    using var dot = new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Fill, IsAntialias = true };
    foreach (var (coord, p) in layout.Centers)
    {
        float x = t.ToPixelX(p.X), y = t.ToPixelY(p.Y + fit.Scroll);
        canvas.DrawCircle(x, y, 30 * t.Scale, pen);
        canvas.DrawCircle(x, y, 3 * t.Scale, dot);
    }
    using var text = new SKPaint { Color = SKColors.Yellow, IsAntialias = true };
    using var font = new SKFont { Size = 28 * t.Scale };
    canvas.DrawText($"scroll {fit.Scroll:F1} score {fit.Score:F1} runner {fit.Runner:F1} {fit.Matched}/{fit.Visible}", 380 * t.Scale, 130 * t.Scale, font, text);
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 90);
    using var fs = File.Create(outPath);
    data.SaveTo(fs);
}
