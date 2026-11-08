using System.Diagnostics;
using System.Globalization;
using System.Text;
using SkiaSharp;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Rest;

// RestBench bench --truth <file> <frame dirs/files...> [--render <dir>] [--failures-only]
//     Runs RestSiteDetector on every labelled frame found in the inputs and prints an accuracy table:
//     choice screens (detected + every button's option and rectangle within 10 design px), grid frames, negatives,
//     transitions (excluded, listed with their reason). Frames in the inputs without a label are listed as unlabelled.
//     Also times the detector on each frame and on a 2x upscaled copy (stand-in for the user's 4K window).
// RestBench scan <frame dirs/files...>
//     One line per frame: name, choice/-, grid/-, probe detail (for labelling new frames).
// RestBench render <frame dirs/files...> --out <dir>
//     Draws the detected button rectangles onto copies of the frames (local only, never commit them).
// RestBench signatures <dir with option_*.webp|png> [--out <file.cs>]
//     Regenerates src/Sts2Overlay.Vision/Rest/RestIconSignatures.cs from the game's rest-site icons
//     (extract them first: PckTool extract-images "images/ui/rest_site/*" <dir>).

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 0)
{
    Console.WriteLine("usage: RestBench bench|scan|render|signatures ... (see Program.cs)");
    return 2;
}

var inputs = new List<string>();
string? truthFile = null, outPath = null, renderDir = null;
bool failuresOnly = false;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--truth": truthFile = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--render": renderDir = args[++i]; break;
        case "--failures-only": failuresOnly = true; break;
        default: inputs.Add(args[i]); break;
    }
}

return args[0] switch
{
    "bench" when truthFile is not null => Bench(truthFile, inputs, renderDir, failuresOnly),
    "scan" => Scan(inputs),
    "render" when outPath is not null => Render(inputs, outPath),
    "signatures" when inputs.Count == 1 => Signatures(inputs[0], outPath),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("usage: RestBench bench --truth <file> <inputs...> | scan <inputs...> | render <inputs...> --out <dir> | signatures <iconDir> [--out <file>]");
    return 2;
}

static List<string> Files(IEnumerable<string> inputs) => inputs
    .SelectMany(p => Directory.Exists(p)
        ? Directory.GetFiles(p).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        : [p])
    // The frame collector writes a 1-byte placeholder before the JPEG; skip files still being written.
    .Where(f => new FileInfo(f).Length > 1000)
    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
    .ToList();

static int Bench(string truthFile, List<string> inputs, string? renderDir, bool failuresOnly)
{
    IReadOnlyList<RestTruthEntry> truth = RestSiteTruth.Parse(File.ReadAllLines(truthFile));
    var byName = truth.ToDictionary(t => t.File, StringComparer.OrdinalIgnoreCase);
    List<string> files = Files(inputs);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    var total = new Dictionary<RestTruthLabel, int>();
    var pass = new Dictionary<RestTruthLabel, int>();
    var failures = new List<string>();
    var transitions = new List<string>();
    var unlabelled = new List<string>();
    var times = new List<double>();
    var times4K = new List<double>();
    int buttons = 0;
    float worstEdge = 0, minMargin = float.MaxValue;
    var sw = new Stopwatch();
    if (files.Count > 0) RestSiteDetector.Detect(ImageLoader.Load(files[0])); // JIT warm-up, not timed

    foreach (string f in files)
    {
        string name = Path.GetFileName(f);
        if (!byName.TryGetValue(name, out RestTruthEntry? t))
        {
            unlabelled.Add(name);
            continue;
        }
        seen.Add(name);
        ScreenFrame frame = ImageLoader.Load(f);
        sw.Restart();
        RestSiteResult r = RestSiteDetector.Detect(frame);
        times.Add(sw.Elapsed.TotalMilliseconds);

        // Native 4K check on a subset (every choice frame and every 10th other frame): same verdict, same rects.
        if (t.Label == RestTruthLabel.Choice || times.Count % 10 == 0)
        {
            ScreenFrame big = Upscale2(frame);
            sw.Restart();
            RestSiteResult r4 = RestSiteDetector.Detect(big);
            times4K.Add(sw.Elapsed.TotalMilliseconds);
            string? why4 = RestSiteTruth.Check(t, r4);
            if (why4 is not null && t.Label != RestTruthLabel.Transition) failures.Add($"{name} [x2] {t.Label}: {why4} | {r4.Detail}");
        }

        total[t.Label] = total.GetValueOrDefault(t.Label) + 1;
        string? why = RestSiteTruth.Check(t, r);
        if (t.Label == RestTruthLabel.Transition)
        {
            transitions.Add($"{name}: {t.Note} -> detector says choice={r.ChoiceScreen} grid={r.CardGridOpen}");
            continue;
        }
        if (why is null)
        {
            pass[t.Label] = pass.GetValueOrDefault(t.Label) + 1;
            if (t.Label == RestTruthLabel.Choice)
            {
                for (int i = 0; i < t.Buttons.Count; i++)
                {
                    buttons++;
                    worstEdge = Math.Max(worstEdge, RestSiteTruth.EdgeError(r.Buttons[i].Rect, t.Buttons[i].Rect));
                    minMargin = Math.Min(minMargin, r.Buttons[i].Margin);
                }
            }
            if (!failuresOnly && t.Label != RestTruthLabel.None) Console.WriteLine($"ok   {name} {t.Label} | {r.Detail}");
        }
        else
        {
            failures.Add($"{name} {t.Label}: {why} | {r.Detail}");
        }
        if (renderDir is not null && (why is not null || r.ChoiceScreen)) RenderOne(f, frame, r, renderDir);
    }

    var missing = truth.Where(t => !seen.Contains(t.File)).ToList();

    Console.WriteLine();
    Console.WriteLine("label        frames  correct");
    foreach (RestTruthLabel l in Enum.GetValues<RestTruthLabel>())
    {
        if (l == RestTruthLabel.Transition) continue;
        Console.WriteLine($"{l,-12} {total.GetValueOrDefault(l),6}  {pass.GetValueOrDefault(l),7}");
    }
    Console.WriteLine($"{"transition",-12} {total.GetValueOrDefault(RestTruthLabel.Transition),6}  (excluded)");
    Console.WriteLine($"buttons checked: {buttons}, worst rect edge error {worstEdge:F1} design px, smallest identity margin {(buttons > 0 ? minMargin : 0):F1}");
    static string Stats(List<double> v)
    {
        if (v.Count == 0) return "n/a";
        var s = v.Order().ToList();
        return $"mean {s.Average():F2} / p99 {s[(int)(0.99 * (s.Count - 1))]:F2} / max {s[^1]:F2} ms";
    }
    Console.WriteLine($"time per frame: FHD {Stats(times)}; 2x upscaled (4K, {times4K.Count} frames) {Stats(times4K)}");
    Console.WriteLine($"unlabelled frames in the inputs: {unlabelled.Count}; labelled but not found: {missing.Count}");
    foreach (string u in unlabelled.Take(20)) Console.WriteLine($"  unlabelled: {u}");
    if (transitions.Count > 0)
    {
        Console.WriteLine("transitions (excluded):");
        foreach (string s in transitions) Console.WriteLine($"  {s}");
    }
    if (failures.Count > 0)
    {
        Console.WriteLine($"FAILURES ({failures.Count}):");
        foreach (string s in failures) Console.WriteLine($"  {s}");
        return 1;
    }
    Console.WriteLine("all labelled frames correct");
    return 0;
}

static int Scan(List<string> inputs)
{
    foreach (string f in Files(inputs))
    {
        RestSiteResult r = RestSiteDetector.Detect(ImageLoader.Load(f));
        string buttons = string.Join(' ', r.Buttons.Select(b =>
            $"{b.Option}@{b.Rect.X:F0},{b.Rect.Y:F0},{b.Rect.Width:F0},{b.Rect.Height:F0}"));
        Console.WriteLine($"{Path.GetFileName(f)}\t{(r.ChoiceScreen ? "choice" : "-")}\t{(r.CardGridOpen ? "grid" : "-")}\t{buttons}\t{r.Detail}");
    }
    return 0;
}

static int Render(List<string> inputs, string outDir)
{
    foreach (string f in Files(inputs))
    {
        ScreenFrame frame = ImageLoader.Load(f);
        RenderOne(f, frame, RestSiteDetector.Detect(frame), outDir);
    }
    return 0;
}

static void RenderOne(string path, in ScreenFrame frame, RestSiteResult r, string outDir)
{
    Directory.CreateDirectory(outDir);
    var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
    using var bmp = new SKBitmap(info);
    ReadOnlySpan<byte> src = frame.Bgra.Span;
    Span<byte> dst = bmp.GetPixelSpan();
    for (int y = 0; y < frame.Height; y++)
        src.Slice(y * frame.Stride, frame.Width * 4).CopyTo(dst.Slice(y * bmp.RowBytes, frame.Width * 4));
    using var canvas = new SKCanvas(bmp);
    RefTransform t = RefTransform.For(frame.Width, frame.Height);
    using var pen = new SKPaint { Color = SKColors.Cyan, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * t.Scale };
    using var slotPen = new SKPaint { Color = SKColors.Magenta, Style = SKPaintStyle.Stroke, StrokeWidth = 1 * t.Scale };
    using var font = new SKFont { Size = 22 * t.Scale };
    using var text = new SKPaint { Color = SKColors.Yellow };
    foreach (RestButton b in r.Buttons)
    {
        RectF p = t.ToPixels(b.Rect), s = t.ToPixels(b.Slot);
        canvas.DrawRect(p.X, p.Y, p.Width, p.Height, pen);
        canvas.DrawRect(s.X, s.Y, s.Width, s.Height, slotPen);
        canvas.DrawText($"{b.Option} {b.State} r{b.Residual:F1}", p.X, p.Bottom + 24 * t.Scale, font, text);
    }
    canvas.DrawText(r.CardGridOpen ? "GRID" : r.ChoiceScreen ? "CHOICE" : "-", 20 * t.Scale, 140 * t.Scale, font, text);
    using SKData data = bmp.Encode(SKEncodedImageFormat.Jpeg, 85);
    using FileStream fs = File.Create(Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + "_rest.jpg"));
    data.SaveTo(fs);
}

static int Signatures(string iconDir, string? outPath)
{
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated>");
    sb.AppendLine("// Generated by tools/RestBench: RestBench signatures <dir with images/ui/rest_site/option_*.png>.");
    sb.AppendLine("// Numbers only (4x3 cell mean colours per icon), no game imagery. Regenerate after a game update.");
    sb.AppendLine("// </auto-generated>");
    sb.AppendLine("namespace Sts2Overlay.Vision.Rest;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Colour signatures of the rest-site option icons (see <see cref=\"RestIconSignature\"/>): the icon as drawn");
    sb.AppendLine("/// normally (hsv shader v = 1) and while hovered (v = 1.2, clipped per pixel). Cells row-major, RGB.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("public static class RestIconSignatures");
    sb.AppendLine("{");
    sb.AppendLine("    public static readonly (RestOption Option, byte[] Normal, byte[] Bright)[] All =");
    sb.AppendLine("    [");
    int found = 0;
    foreach (RestOption o in Enum.GetValues<RestOption>().Where(o => o != RestOption.Unknown))
    {
        string stem = "option_" + o.ToString().ToLowerInvariant();
        string? file = Directory.EnumerateFiles(iconDir, stem + ".*", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            Console.Error.WriteLine($"missing {stem}");
            continue;
        }
        ScreenFrame icon = ImageLoader.Load(file);
        Console.Error.WriteLine($"{stem}: {icon.Width}x{icon.Height}");
        string Bytes(float[] s) => string.Join(", ", s.Select(v => ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture)));
        sb.AppendLine($"        (RestOption.{o},");
        sb.AppendLine($"            [{Bytes(RestIconSignature.FromImage(icon))}],");
        sb.AppendLine($"            [{Bytes(RestIconSignature.FromImage(icon, 1.2f))}]),");
        found++;
    }
    sb.AppendLine("    ];");
    sb.AppendLine("}");
    if (outPath is null) Console.Write(sb.ToString());
    else File.WriteAllText(outPath, sb.ToString().Replace("\r\n", "\n"));
    Console.Error.WriteLine($"{found} icon(s)");
    return found > 0 ? 0 : 1;
}

/// <summary>Nearest-neighbour x2, standing in for a native 4K capture.</summary>
static ScreenFrame Upscale2(in ScreenFrame f)
{
    int w = f.Width * 2, h = f.Height * 2;
    byte[] dst = new byte[w * h * 4];
    ReadOnlySpan<byte> src = f.Bgra.Span;
    for (int y = 0; y < h; y++)
    {
        ReadOnlySpan<byte> row = src.Slice((y / 2) * f.Stride, f.Width * 4);
        Span<byte> d = dst.AsSpan(y * w * 4, w * 4);
        for (int x = 0; x < f.Width; x++)
        {
            row.Slice(x * 4, 4).CopyTo(d.Slice(x * 8, 4));
            row.Slice(x * 4, 4).CopyTo(d.Slice(x * 8 + 4, 4));
        }
    }
    return ScreenFrame.FromPacked(dst, w, h);
}
