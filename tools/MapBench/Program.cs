using System.Diagnostics;
using System.Globalization;
using SkiaSharp;
using Sts2Overlay.Core.Map;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Map;

// MapBench fit <frame files or folders...> --saves <dir with current_run*.save copies> [--render <dir>] [--truth <file>]
//
// For every frame, every distinct act map found in the saves is fitted; the best fit decides "map or not".
// --truth: a text file "frameName scroll" (or "frameName -" for not-a-map) checked line by line: a map frame passes
// when detected and the scroll is within 4 design px; any other frame passes when it is not detected as a map.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 0 || args[0] != "fit")
{
    Console.WriteLine("usage: MapBench fit <frames|dirs...> --saves <dir> [--render <dir>] [--truth <file>]");
    return 2;
}

var inputs = new List<string>();
string? savesDir = null, renderDir = null, truthFile = null;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--saves": savesDir = args[++i]; break;
        case "--render": renderDir = args[++i]; break;
        case "--truth": truthFile = args[++i]; break;
        default: inputs.Add(args[i]); break;
    }
}

var layouts = new List<(string Name, MapScreenLayout Layout, MapGraph Map)>();
if (savesDir != null)
{
    foreach (string f in Directory.GetFiles(savesDir, "*.save").Order(StringComparer.Ordinal))
    {
        ParsedSave? s = RunSaveParser.ParseFile(f, null);
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

Dictionary<string, float?>? truth = truthFile is null ? null : File.ReadAllLines(truthFile)
    .Where(l => l.Trim().Length > 0 && !l.StartsWith('#'))
    .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    .ToDictionary(p => p[0], p => p[1] == "-" ? (float?)null : float.Parse(p[1], CultureInfo.InvariantCulture));

var est = new MapScrollEstimator();
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
        MapFit fit = est.Estimate(frame, marks);
        times.Add(sw.Elapsed.TotalMilliseconds);
        if (best is null || (fit.IsMap && !best.IsMap) || (fit.IsMap == best.IsMap && fit.Score > best.Score)) { best = fit; bestL = l; }
    }
    best ??= MapFit.None("no layouts");
    string name = Path.GetFileName(file);
    if (best.IsMap) maps++;
    string verdict = "";
    if (truth != null && truth.TryGetValue(name, out float? want))
    {
        checkedN++;
        bool ok = want is null ? !best.IsMap : best.IsMap && Math.Abs(best.Scroll - want.Value) <= 4f;
        if (ok) pass++;
        verdict = ok ? " OK" : $" FAIL (truth {(want is null ? "-" : want.Value.ToString("F1"))})";
    }
    if (best.IsMap || truth != null) Console.WriteLine($"{name}: {(best.IsMap ? bestL?.Name : "-")} {best}{verdict}");
    if (Environment.GetEnvironmentVariable("MAPBENCH_RESIDUALS") == "1" && best.IsMap && bestL is { } rl)
    {
        foreach (var (coord, p) in rl.Layout.Centers.OrderBy(kv => kv.Key.Row).ThenBy(kv => kv.Key.Col))
        {
            if (coord == rl.Map.Start || coord == rl.Map.Boss) continue;
            float y = p.Y + best.Scroll;
            if (y < 40 || y > 1040 || MapScrollEstimator.IsMasked(p.X, y)) continue;
            var (dx, dy, ink) = est.LocalPeak(new MapMark(p.X, p.Y), best.Scroll);
            Console.WriteLine($"   {coord} {rl.Map.Nodes[coord].Type,-9} dx={dx,4:F0} dy={dy,4:F0} ink={ink:F0}");
        }
    }
    if (renderDir != null && best.IsMap && bestL is { } bl) Render(file, frame, bl.Layout, best, Path.Combine(renderDir, Path.ChangeExtension(name, ".png")));
}
Console.WriteLine($"frames {files.Count}, detected as map {maps}" + (truth != null ? $", truth checks {pass}/{checkedN}" : "")
    + (times.Count > 0 ? $", fit median {times.Order().ElementAt(times.Count / 2):F1} ms" : ""));
return truth != null && pass != checkedN ? 1 : 0;

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
