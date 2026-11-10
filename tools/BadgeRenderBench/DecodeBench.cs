using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace Sts2Overlay.Tools.BadgeRenderBench;

/// <summary>
/// Times what the panel's offer rows do on the UI thread for each card not seen before: the same decode + resize as
/// CardArtProvider.Decode (WebP portrait → 96x73 thumbnail). Shows no window, so it may run while the game runs.
/// </summary>
internal static class DecodeBench
{
    public static int Run(string artDir, int count)
    {
        var files = Directory.EnumerateFiles(artDir, "*.webp", SearchOption.AllDirectories).Take(count).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"no .webp under {artDir}");
            return 1;
        }
        var ms = new List<double>();
        foreach (var f in files)
        {
            var t0 = Stopwatch.GetTimestamp();
            using (var src = SKBitmap.Decode(f))
            using (var resized = src.Resize(new SKImageInfo(96, 73, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell)))
            {
                GC.KeepAlive(resized);
            }
            ms.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        }
        Console.WriteLine($"thumbnail decode, {files.Length} portraits (first one includes Skia start-up): first {ms[0]:0.0} ms");
        Console.WriteLine($"  rest: {Stats.Line(ms.Skip(1).ToArray())}");
        return 0;
    }
}
