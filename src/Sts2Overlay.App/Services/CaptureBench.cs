using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;

namespace Sts2Overlay.App.Services;

/// <summary>
/// <c>--bench-capture [frames]</c>: times both capture backends on the game window (or <c>--bench-target</c>'s window),
/// saves one PNG per backend taken back to back plus a pixel comparison, and writes <c>bench_capture.txt</c>.
///
/// Frames are taken 50 ms apart so WGC gets a new frame each time, like the app's 3 Hz capture (not a tight loop, which
/// would mostly measure "reuse the previous frame").
/// </summary>
internal static class CaptureBench
{
    private const int WarmupFrames = 3;
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(50);

    public static void Run(AppOptions options)
    {
        AttachParentConsole();
        var lines = new List<string>();
        void Out(string s)
        {
            lines.Add(s);
            AppLog.Info("bench: " + s);
            Console.WriteLine(s);
        }

        var processName = options.BenchTarget ?? GameWindowTracker.ProcessName;
        var target = FindWindow(processName);
        if (target is null)
        {
            Out($"no visible main window for process '{processName}'");
            return;
        }
        var outDir = Path.GetFullPath(options.BenchOut ?? AppPaths.CapturesDir);
        Directory.CreateDirectory(outDir);
        Out(Inv($"target {processName} pid {target.ProcessId}: window {target.WindowRect}, capture {target.CaptureRect}, foreground {target.IsForeground}, dpi x{Dpi.ScaleForRect(target.CaptureRect):0.##}"));
        Out(Inv($"{options.BenchFrames} frames per backend, {Spacing.TotalMilliseconds:0} ms apart, after {WarmupFrames} warm-up frames"));
        if (!target.IsForeground) Out("note: target is not in front, so GDI frames show whatever covers it (WGC still sees the window)");

        using var gdi = new ScreenCapturer();
        var gdiStats = Measure(gdi, target, options.BenchFrames);
        Out(gdiStats.Format("gdi"));

        CapturedFrame? wgcFrame = null;
        using var wgc = new WgcCapturer();
        if (WgcCapturer.IsSupported())
        {
            var t0 = Stopwatch.GetTimestamp();
            wgc.Capture(Refresh(target));
            Out(Inv($"wgc cold start (device + session + first frame): {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.0} ms"));
            var wgcStats = Measure(wgc, target, options.BenchFrames);
            Out(wgcStats.Format("wgc"));
            Out(Inv($"speed-up (median total): x{gdiStats.Total.Median / Math.Max(0.001, wgcStats.Total.Median):0.0}"));
        }
        else
        {
            Out("wgc: not supported on this system");
        }

        // Back to back, so the two images show (nearly) the same moment.
        target = Refresh(target);
        var gdiFrame = gdi.Capture(target);
        if (WgcCapturer.IsSupported()) wgcFrame = wgc.Capture(target);
        var gdiPng = Path.Combine(outDir, "bench_gdi.png");
        FramePng.Save(gdiFrame, gdiPng);
        Out($"saved {gdiPng}");
        if (wgcFrame is not null)
        {
            var wgcPng = Path.Combine(outDir, "bench_wgc.png");
            FramePng.Save(wgcFrame, wgcPng);
            Out($"saved {wgcPng}");
            Out(Compare(gdiFrame, wgcFrame));
        }

        if (options.BenchSoakSeconds > 0) Soak(target, TimeSpan.FromSeconds(options.BenchSoakSeconds), Out);

        var report = Path.Combine(outDir, "bench_capture.txt");
        File.WriteAllLines(report, lines);
        Console.WriteLine($"report: {report}");
    }

    /// <summary>
    /// The app's capture path (<see cref="FrameCapturerFactory"/>: WGC, GDI per failed frame) at 3 Hz while the window
    /// may be resized or closed. Counts frames per backend; stops when the window is gone.
    /// </summary>
    private static void Soak(GameWindowInfo target, TimeSpan duration, Action<string> output)
    {
        output(Inv($"soak: app capturer at 3 Hz for {duration.TotalSeconds:0} s (resize or close the window meanwhile)"));
        using var capturer = FrameCapturerFactory.Create("auto");
        var perBackend = new Dictionary<string, int>();
        var failures = 0;
        var sizes = new HashSet<string>();
        var end = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end)
        {
            var current = Refresh(target);
            if (!IsWindow(target.Hwnd) || current.CaptureRect.IsEmpty)
            {
                output("soak: window closed");
                break;
            }
            try
            {
                var frame = capturer.Capture(current);
                perBackend[capturer.Name] = perBackend.GetValueOrDefault(capturer.Name) + 1;
                sizes.Add($"{frame.Width}x{frame.Height}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures++;
                output($"soak: capture failed: {ex.GetType().Name}: {ex.Message}");
            }
            Thread.Sleep(333);
        }
        output($"soak: frames {string.Join(", ", perBackend.Select(kv => $"{kv.Key} {kv.Value}"))}; failures {failures}; {sizes.Count} distinct sizes");
    }

    internal static GameWindowInfo? FindWindow(string processName)
    {
        var procs = int.TryParse(processName, out var pid) ? TryGetById(pid) : Process.GetProcessesByName(processName);
        try
        {
            var p = procs.FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);
            return p is null ? null : GameWindowTracker.Describe(p.MainWindowHandle, p.Id, Environment.ProcessId);
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    /// <summary>Re-reads the window position, like the app's tracker does every 250 ms (the window may have moved or resized).</summary>
    internal static GameWindowInfo Refresh(GameWindowInfo target) => GameWindowTracker.Describe(target.Hwnd, target.ProcessId, Environment.ProcessId);

    private static BenchStats Measure(IFrameCapturer capturer, GameWindowInfo target, int frames)
    {
        for (var i = 0; i < WarmupFrames; i++)
        {
            capturer.Capture(Refresh(target));
            Thread.Sleep(Spacing);
        }
        var total = new List<double>(frames);
        var acquire = new List<double>(frames);
        var copy = new List<double>(frames);
        var reused = 0;
        var cpu = TimeSpan.Zero;
        using var self = Process.GetCurrentProcess();
        for (var i = 0; i < frames; i++)
        {
            var current = Refresh(target);
            self.Refresh();
            var cpu0 = self.TotalProcessorTime;
            var t0 = Stopwatch.GetTimestamp();
            capturer.Capture(current);
            total.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            self.Refresh();
            cpu += self.TotalProcessorTime - cpu0;
            acquire.Add(capturer.LastTimings.AcquireMs);
            copy.Add(capturer.LastTimings.CopyMs);
            if (capturer is WgcCapturer { LastFrameReused: true }) reused++;
            Thread.Sleep(Spacing);
        }
        return new BenchStats(Summary.Of(total), Summary.Of(acquire), Summary.Of(copy), cpu.TotalMilliseconds / frames, reused, frames);
    }

    /// <summary>Per-pixel comparison of two same-size frames (B, G, R only).</summary>
    private static string Compare(CapturedFrame a, CapturedFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return $"compare: size differs ({a.Width}x{a.Height} vs {b.Width}x{b.Height})";
        long sum = 0;
        long differing = 0;
        for (var y = 0; y < a.Height; y++)
        {
            var ra = a.Bgra.AsSpan(y * a.Stride, a.Width * 4);
            var rb = b.Bgra.AsSpan(y * b.Stride, b.Width * 4);
            for (var x = 0; x < ra.Length; x += 4)
            {
                var d0 = Math.Abs(ra[x] - rb[x]);
                var d1 = Math.Abs(ra[x + 1] - rb[x + 1]);
                var d2 = Math.Abs(ra[x + 2] - rb[x + 2]);
                sum += d0 + d1 + d2;
                if (Math.Max(d0, Math.Max(d1, d2)) > 24) differing++;
            }
        }
        var pixels = (double)a.Width * a.Height;
        return Inv($"compare gdi vs wgc: mean |diff| {sum / (pixels * 3):0.00} per channel, {differing / pixels * 100:0.00}% pixels differ by > 24 (animation between the two grabs counts too)");
    }

    internal static void AttachParentConsole()
    {
        // A WinExe has no console; when started from a terminal, print there too.
        if (Console.IsOutputRedirected || !AttachConsole(-1)) return;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
    }

    private static Process[] TryGetById(int pid)
    {
        try
        {
            return [Process.GetProcessById(pid)];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    internal static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private readonly record struct Summary(double Median, double P95, double Min, double Max, double Mean)
    {
        public static Summary Of(List<double> values)
        {
            var v = values.Order().ToArray();
            double At(double q) => v[Math.Clamp((int)Math.Ceiling(q * v.Length) - 1, 0, v.Length - 1)];
            return new Summary(At(0.5), At(0.95), v[0], v[^1], v.Average());
        }

        public override string ToString() => Inv($"median {Median:0.0} / p95 {P95:0.0} (min {Min:0.0}, max {Max:0.0}) ms");
    }

    private readonly record struct BenchStats(Summary Total, Summary Acquire, Summary Copy, double CpuMsPerFrame, int Reused, int Frames)
    {
        public string Format(string name)
            => Inv($"{name}: total {Total}; acquire {Acquire.Median:0.0}/{Acquire.P95:0.0}; CPU copy {Copy.Median:0.0}/{Copy.P95:0.0} (median/p95 ms); process CPU {CpuMsPerFrame:0.0} ms/frame")
               + (name == "wgc" ? Inv($"; {Reused}/{Frames} frames reused (no new frame from the window)") : "");
    }
}
