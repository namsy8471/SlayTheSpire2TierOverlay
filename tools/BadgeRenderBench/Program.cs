using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Sts2Overlay.App;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.App.Views;

namespace Sts2Overlay.Tools.BadgeRenderBench;

// Usage:
//   BadgeRenderBench [--rounds N] [--variants a,b,..] [--churn N]   warm process, variants alternated in rounds
//   BadgeRenderBench --cold N [--variants a]                        N fresh processes, first show of each
//   BadgeRenderBench --hold S --windows K --variants a              K layers held S seconds (read GPU memory outside)
//   BadgeRenderBench --decode <card_art dir> N                      no window: thumbnail decode time
//   BadgeRenderBench --panel <card_art dir> ROUNDS                  no window: panel offer tab layout, offscreen
// Variants: full-* = the window covers the screen, fit-* = only the box around the badges, -hidden = Show() in the
// timed change, -sw = software rendering, -seq = reward and shop screens in turn on one window.
//
// What one sample is (the app's perf log "render" stage, AppController.Perf.cs): ShowBadges on the real BadgeWindow,
// then a Dispatcher callback at Loaded priority. Pieces: the first CompositionTarget.Rendering after the change
// (when WPF's render pass starts) and the first LayoutUpdated (layout done) when --instrument is on.

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // No window: how long the panel's card thumbnails take to decode on the UI thread (CardArtProvider.Decode).
        if (args is ["--decode", var artDir, var count]) return DecodeBench.Run(artDir, int.Parse(count, CultureInfo.InvariantCulture));
        // No window either: the panel's offer tab laid out offscreen after a new offer.
        if (args is ["--panel", var panelArt, var rounds]) return PanelBench.Run(panelArt, int.Parse(rounds, CultureInfo.InvariantCulture));
        if (GameGuard.IsGameRunning())
        {
            Console.Error.WriteLine("Slay the Spire 2 is running: this bench shows windows over the screen, so it does not run now.");
            return 3;
        }
        var opts = Options.Parse(args);
        if (opts.Cold > 0 && !opts.Child) return ColdRunner.Run(opts);
        var app = new BenchApp(opts) { Resources = AppResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        return app.Run();
    }

    /// <summary>
    /// App.xaml's resources (brushes, converters, font) that BadgeWindow.xaml looks up, loaded as loose XAML. The App
    /// class itself is not created: Application's constructor queues OnStartup, and App's OnStartup is the overlay.
    /// </summary>
    internal static ResourceDictionary AppResources()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("App.xaml")!;
        var xaml = new System.IO.StreamReader(stream).ReadToEnd()
            .Replace("x:Class=\"Sts2Overlay.App.App\"", "", StringComparison.Ordinal)
            .Replace("clr-namespace:Sts2Overlay.App.Views\"", "clr-namespace:Sts2Overlay.App.Views;assembly=Sts2Overlay\"", StringComparison.Ordinal)
            .Replace("<Application ", "<ResourceDictionary ", StringComparison.Ordinal)
            .Replace("</Application>", "</ResourceDictionary>", StringComparison.Ordinal)
            .Replace("ShutdownMode=\"OnExplicitShutdown\"", "", StringComparison.Ordinal)
            .Replace("<Application.Resources>", "", StringComparison.Ordinal)
            .Replace("</Application.Resources>", "", StringComparison.Ordinal);
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
    }
}

/// <summary>Runs the bench once the dispatcher is up, then exits with its code.</summary>
internal sealed class BenchApp(Options? opts) : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            var code = 1;
            try
            {
                if (opts is not null) code = await Bench.RunAsync(opts);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
            }
            Shutdown(code);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
    }
}

internal sealed record Options(int Rounds, string[] Variants, bool Instrument, int Cold, bool Child, int Churn, int Shows, int FpsMs, int HoldSeconds = 0, int HoldWindows = 3)
{
    public static readonly string[] AllVariants =
    [
        "full-reward", "fit-reward", "full-reward-hidden", "full-reward-sw", "full-shop", "fit-shop", "full-tiny", "full-reward-seq",
    ];

    public static Options Parse(string[] args)
    {
        int rounds = 15, cold = 0, churn = 0, shows = 3, fpsMs = 0, holdSeconds = 0, holdWindows = 3;
        string[] variants = AllVariants;
        bool instrument = true, child = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rounds": rounds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--variants": variants = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                case "--plain": instrument = false; break;
                case "--cold": cold = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--child": child = true; break;
                case "--churn": churn = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--shows": shows = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--fps": fpsMs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--hold": holdSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--windows": holdWindows = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"unknown argument {args[i]}");
            }
        }
        return new Options(rounds, variants, instrument, cold, child, churn, shows, fpsMs, holdSeconds, holdWindows);
    }
}

internal static class GameGuard
{
    public static bool IsGameRunning() => Process.GetProcessesByName("SlayTheSpire2").Length > 0;
}

/// <summary>One timed badge change. All times in ms from the ShowBadges call.</summary>
internal readonly record struct Sample(double Apply, double Rendering, double Layout, double Loaded);

internal static class Bench
{
    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    public static async Task<int> RunAsync(Options o)
    {
        var screen = Screen.Primary();
        if (o.HoldSeconds > 0) return await HoldAsync(o, screen);
        var load = new CpuLoad();
        if (!o.Child)
        {
            Console.WriteLine($"primary monitor {screen.Width}x{screen.Height} px, {screen.Dpi} dpi, {screen.RefreshHz} Hz; render tier {RenderCapability.Tier >> 16}; " +
                              $"{Environment.ProcessorCount} logical CPUs; rounds {o.Rounds}, shows per window {o.Shows}, instrumented {o.Instrument}");
        }
        // Results per variant and show index (0 = first show on a fresh window).
        var results = o.Variants.ToDictionary(v => v, _ => Enumerable.Range(0, o.Shows).Select(_ => new List<Sample>()).ToArray());
        var churn = o.Variants.ToDictionary(v => v, _ => new List<double>());
        var frames = o.Variants.ToDictionary(v => v, _ => new List<double>());
        var memory = o.Variants.ToDictionary(v => v, _ => new List<double>());
        var loads = new List<double>();
        var rng = new Random(1234);
        var total = o.Rounds * o.Variants.Length;
        var done = 0;
        var started = Stopwatch.StartNew();
        for (var round = 0; round < o.Rounds; round++)
        {
            load.Reset();
            foreach (var v in o.Variants.OrderBy(_ => rng.Next()))
            {
                if (GameGuard.IsGameRunning())
                {
                    Console.Error.WriteLine("the game started: stopping");
                    return 3;
                }
                var w = await RunWindowAsync(v, screen, o);
                for (var s = 0; s < w.Samples.Count; s++) results[v][s].Add(w.Samples[s]);
                churn[v].AddRange(w.Churn);
                frames[v].AddRange(w.FrameMs);
                memory[v].Add(w.PrivateMb);
                done++;
            }
            loads.Add(load.Percent());
            if (!o.Child)
            {
                var eta = started.Elapsed.TotalSeconds / done * (total - done);
                Console.WriteLine($"  {done}/{total} windows, round {round + 1}/{o.Rounds}, machine CPU {loads[^1]:0}% (eta {eta:0}s)");
            }
        }

        if (o.Child)
        {
            // One line per show for the parent: variant show apply rendering layout loaded
            foreach (var (v, shows) in results)
                for (var s = 0; s < shows.Length; s++)
                    foreach (var x in shows[s])
                        Console.WriteLine(FormattableString.Invariant($"S {v} {s} {x.Apply:0.00} {x.Rendering:0.00} {x.Layout:0.00} {x.Loaded:0.00}"));
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"machine CPU during the run: median {Stats.Median(loads):0}% (min {loads.Min():0}, max {loads.Max():0})");
        Report.Print(results, o.Instrument);
        if (o.Churn > 0)
        {
            Console.WriteLine();
            Console.WriteLine("back-to-back changes (ShowBadges -> Loaded, then the next one at once):");
            foreach (var (v, xs) in churn) Console.WriteLine($"  {v,-20} {Stats.Line(xs)}");
        }
        if (o.FpsMs > 0)
        {
            Console.WriteLine();
            Console.WriteLine("continuous redraw (content opacity changed every frame): ms between render passes");
            foreach (var (v, xs) in frames) Console.WriteLine($"  {v,-20} {Stats.Line(xs)}");
        }
        Console.WriteLine();
        Console.WriteLine("process private memory growth while the window showed its badges (MB):");
        foreach (var (v, xs) in memory) Console.WriteLine($"  {v,-20} {Stats.Line(xs)}");
        return 0;
    }

    /// <summary>One short-lived window: shown, then <see cref="Options.Shows"/> timed badge changes (each from empty), then closed.</summary>
    /// <summary>
    /// Memory of the overlay's three layers (badges, map, events): <see cref="Options.HoldWindows"/> badge windows of the
    /// first variant, all showing badges, held for a few seconds so GPU memory can be read from outside
    /// (Get-Counter "\GPU Process Memory(pid_N*)\Dedicated Usage").
    /// </summary>
    private static async Task<int> HoldAsync(Options o, Screen screen)
    {
        var variant = o.Variants[0];
        var content = Content.For(variant, screen);
        var layer = variant.StartsWith("fit", StringComparison.Ordinal) ? content.Fit : screen.Rect;
        var before = PrivateBytes();
        var windows = new List<BadgeWindow>();
        try
        {
            for (var i = 0; i < o.HoldWindows; i++)
            {
                var w = new BadgeWindow(new OverlayCaptureVisibility(false));
                new WindowInteropHelper(w).EnsureHandle();
                w.SetLayerRect(layer);
                w.Show();
                w.ShowBadges(content.Specs(i * 5), content.Skip, content.Removal, false);
                windows.Add(w);
            }
            await Settle(windows[0]);
            Console.WriteLine($"HOLD pid {Environment.ProcessId} {variant} x{o.HoldWindows}: private +{(PrivateBytes() - before) / 1048576.0:0} MB");
            await Task.Delay(o.HoldSeconds * 1000);
        }
        finally
        {
            foreach (var w in windows) w.Close();
        }
        return 0;
    }

    private sealed record WindowResult(List<Sample> Samples, List<double> Churn, List<double> FrameMs, double PrivateMb);

    private static async Task<WindowResult> RunWindowAsync(string variant, Screen screen, Options o)
    {
        var content = Content.For(variant, screen);
        var hidden = variant.Contains("-hidden");
        var software = variant.EndsWith("-sw", StringComparison.Ordinal);
        var layer = variant.StartsWith("fit", StringComparison.Ordinal) ? content.Fit : screen.Rect;

        var before = PrivateBytes();
        var window = new BadgeWindow(new OverlayCaptureVisibility(false));
        new WindowInteropHelper(window).EnsureHandle();
        if (software && HwndSource.FromHwnd(window.Hwnd)?.CompositionTarget is { } target) target.RenderMode = RenderMode.SoftwareOnly;
        window.SetLayerRect(layer);
        if (!hidden) window.Show();
        await Settle(window);

        var samples = new List<Sample>();
        var churn = new List<double>();
        var frameMs = new List<double>();
        double grownMb;
        try
        {
            // "-seq": a session's screens in turn (reward, shop, reward, ...) on one window.
            var shop = variant.Contains("-seq") ? Content.For("shop", screen) : null;
            for (var s = 0; s < o.Shows; s++)
            {
                var shift = s * 7; // a few pixels apart, like a re-recognised card
                var c = shop is not null && s % 2 == 1 ? shop : content;
                samples.Add(await TimeChange(window, () =>
                {
                    if (hidden && !window.IsVisible) window.Show();
                    window.ShowBadges(c.Specs(shift), c.Skip, c.Removal, false);
                }, o.Instrument));
                window.Clear();
                if (hidden) window.Hide();
                await Settle(window);
            }
            if (hidden) window.Show();
            for (var i = 0; i < o.Churn; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                window.ShowBadges(content.Specs(i % 2 * 9), content.Skip, content.Removal, false);
                var t1 = await window.Dispatcher.InvokeAsync(Stopwatch.GetTimestamp, DispatcherPriority.Loaded);
                churn.Add(Ms(t0, t1));
            }
            if (o.FpsMs > 0) frameMs.AddRange(await FrameIntervals(window, o.FpsMs));
            window.ShowBadges(content.Specs(0), content.Skip, content.Removal, false);
            await Settle(window);
            grownMb = (PrivateBytes() - before) / 1048576.0;
        }
        finally
        {
            window.Close();
        }
        return new WindowResult(samples, churn, frameMs, grownMb);
    }

    /// <summary>
    /// Redraws the whole badge content every frame for <paramref name="ms"/> (opacity 1 / 0.99, no layout) and returns
    /// the gaps between render passes: what one present of this window costs when nothing else is in the way.
    /// </summary>
    private static async Task<List<double>> FrameIntervals(BadgeWindow window, int ms)
    {
        var target = (UIElement)window.Content;
        var stamps = new List<long>();
        EventHandler onRendering = (_, _) =>
        {
            stamps.Add(Stopwatch.GetTimestamp());
            target.Opacity = stamps.Count % 2 == 0 ? 1.0 : 0.99;
        };
        CompositionTarget.Rendering += onRendering;
        await Task.Delay(ms);
        CompositionTarget.Rendering -= onRendering;
        target.Opacity = 1.0;
        return stamps.Skip(2).Zip(stamps.Skip(3), (a, b) => Ms(a, b)).ToList();
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public int Cb;
        public int PageFaultCount;
        public nint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    private static long PrivateBytes()
    {
        var c = new ProcessMemoryCounters { Cb = Marshal.SizeOf<ProcessMemoryCounters>() };
        GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.Cb);
        return c.PrivateUsage;
    }

    private static async Task<Sample> TimeChange(BadgeWindow window, Action change, bool instrument)
    {
        long rendering = 0, layout = 0;
        EventHandler? onRendering = null;
        EventHandler? onLayout = null;
        var t0 = Stopwatch.GetTimestamp();
        change();
        var t1 = Stopwatch.GetTimestamp();
        if (instrument)
        {
            onRendering = (_, _) =>
            {
                rendering = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering -= onRendering;
            };
            onLayout = (_, _) =>
            {
                layout = Stopwatch.GetTimestamp();
                window.LayoutUpdated -= onLayout;
            };
            CompositionTarget.Rendering += onRendering;
            window.LayoutUpdated += onLayout;
        }
        // Exactly what AppController.OnRecognized queues after ApplyRecognition.
        var t2 = await window.Dispatcher.InvokeAsync(Stopwatch.GetTimestamp, DispatcherPriority.Loaded);
        CompositionTarget.Rendering -= onRendering;
        window.LayoutUpdated -= onLayout;
        return new Sample(Ms(t0, t1), rendering == 0 ? double.NaN : Ms(t0, rendering), layout == 0 ? double.NaN : Ms(t0, layout), Ms(t0, t2));
    }

    /// <summary>Lets the previous change finish drawing (render thread included) before the next one is timed.</summary>
    private static async Task Settle(Window w)
    {
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(120);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}

/// <summary>A reward or shop screen's worth of badges, laid out like the game draws it at this monitor's size.</summary>
internal sealed class Content
{
    private readonly Func<int, BadgeSpec[]> _specs;

    private Content(Func<int, BadgeSpec[]> specs, PixelRect fit, SkipSpec? skip, RemovalSpec? removal)
    {
        _specs = specs;
        Fit = fit;
        Skip = skip;
        Removal = removal;
    }

    public PixelRect Fit { get; }
    public SkipSpec? Skip { get; }
    public RemovalSpec? Removal { get; }

    public BadgeSpec[] Specs(int shift) => _specs(shift);

    public static Content For(string variant, Screen screen)
    {
        // Game 1920x1080 design units → this monitor (the game fills the screen).
        var k = Math.Min(screen.Width / 1920.0, screen.Height / 1080.0);
        PixelRect R(double x, double y, double w, double h) => new((int)(x * k), (int)(y * k), (int)(w * k), (int)(h * k));
        ChipVm[] chips = [new("광역 부족 +24", true), new("덱 크기 -6", false)];

        if (variant.Contains("shop"))
        {
            var cards = new List<(PixelRect Card, PixelRect Price)>();
            for (var i = 0; i < 5; i++) cards.Add((R(330 + i * 190, 190, 160, 225), R(370 + i * 190, 420, 80, 30)));
            for (var i = 0; i < 2; i++) cards.Add((R(330 + i * 190, 520, 160, 225), R(370 + i * 190, 750, 80, 30)));
            var coin = R(1450, 620, 120, 120);
            return new Content(shift => cards.Select((c, i) => new BadgeSpec(Shift(c.Card, shift), Tier(i), (70 - i * 4).ToString(CultureInfo.InvariantCulture),
                    i == 1, false, false, c.Price, null, i == 1 ? "구매!" : null)).ToArray(),
                Union(cards.Select(c => c.Card).Append(coin), (int)(60 * k)), null, new RemovalSpec(coin, "제거 추천: 타격"));
        }

        var count = variant.Contains("tiny") ? 1 : 3;
        var reward = Enumerable.Range(0, count).Select(i => R(560 + i * 300, 330, 240, 340)).ToArray();
        return new Content(shift => reward.Select((c, i) => new BadgeSpec(Shift(c, shift), Tier(i), (80 - i * 9).ToString(CultureInfo.InvariantCulture),
                i == 0, i == 2, i == 1, null, count > 1 ? chips : null, count > 1 && i == 0 ? "추천!" : null)).ToArray(),
            Union(reward.Append(R(760, 900, 400, 80)), (int)(60 * k)),
            count > 1 ? new SkipSpec("41", "덱 크기 +12") : null, null);
    }

    private static readonly string[] Tiers = ["S", "A", "B", "C", "D"];

    private static string Tier(int i) => Tiers[i % Tiers.Length];

    private static PixelRect Shift(PixelRect r, int d) => r with { X = r.X + d, Y = r.Y + d };

    private static PixelRect Union(IEnumerable<PixelRect> rs, int margin)
    {
        var list = rs.ToList();
        int l = list.Min(r => r.X) - margin, t = list.Min(r => r.Y) - margin, ri = list.Max(r => r.Right) + margin, b = list.Max(r => r.Bottom) + margin;
        return new PixelRect(l, t, ri - l, b - t);
    }
}

internal readonly record struct Screen(int Width, int Height, int Dpi, int RefreshHz)
{
    public PixelRect Rect => new(0, 0, Width, Height);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    public static Screen Primary()
    {
        var dc = GetDC(IntPtr.Zero);
        var hz = GetDeviceCaps(dc, 116); // VREFRESH
        ReleaseDC(IntPtr.Zero, dc);
        return new Screen(GetSystemMetrics(0), GetSystemMetrics(1), (int)GetDpiForSystem(), hz);
    }
}

/// <summary>Whole-machine CPU use since <see cref="Reset"/> (GetSystemTimes), to report how loaded the PC was.</summary>
internal sealed class CpuLoad
{
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private long _idle, _busy;

    public void Reset() => (_idle, _busy) = Read();

    public double Percent()
    {
        var (idle, busy) = Read();
        var di = idle - _idle;
        var db = busy - _busy;
        return db <= 0 ? 0 : 100.0 * (db - di) / db;
    }

    private static (long Idle, long Total) Read()
    {
        GetSystemTimes(out var idle, out var kernel, out var user);
        return (idle, kernel + user); // kernel time includes idle
    }
}

internal static class Stats
{
    public static double Median(IEnumerable<double> xs) => Pct(xs, 0.5);

    public static double Pct(IEnumerable<double> xs, double p)
    {
        var a = xs.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        if (a.Length == 0) return double.NaN;
        var i = p * (a.Length - 1);
        var lo = (int)Math.Floor(i);
        var hi = Math.Min(a.Length - 1, lo + 1);
        return a[lo] + (a[hi] - a[lo]) * (i - lo);
    }

    public static string Line(IReadOnlyCollection<double> xs)
        => xs.Count == 0 ? "-" : FormattableString.Invariant($"median {Median(xs),6:0.0}  p10 {Pct(xs, 0.1),6:0.0}  p90 {Pct(xs, 0.9),6:0.0}  min {xs.Min(),6:0.0}  max {xs.Max(),6:0.0}  n {xs.Count}");
}

internal static class Report
{
    public static void Print(Dictionary<string, List<Sample>[]> results, bool instrumented)
    {
        Console.WriteLine("ShowBadges -> Loaded callback (ms), per variant and show (#1 = first show on a fresh window):");
        foreach (var (v, shows) in results)
        {
            for (var s = 0; s < shows.Length; s++)
            {
                var xs = shows[s];
                if (xs.Count == 0) continue;
                Console.WriteLine($"  {v,-20} #{s + 1}  {Stats.Line(xs.Select(x => x.Loaded).ToArray())}");
                if (instrumented)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"  {"",-20}     apply {Stats.Median(xs.Select(x => x.Apply)),5:0.0}  render pass starts {Stats.Median(xs.Select(x => x.Rendering)),5:0.0}  layout done {Stats.Median(xs.Select(x => x.Layout)),5:0.0}"));
                }
            }
        }
    }
}

/// <summary>Fresh processes: the very first badges a session shows (templates, fonts and JIT all cold).</summary>
internal static class ColdRunner
{
    public static int Run(Options o)
    {
        var variants = string.Join(',', o.Variants);
        var results = new Dictionary<string, List<Sample>[]>();
        for (var i = 0; i < o.Cold; i++)
        {
            if (GameGuard.IsGameRunning())
            {
                Console.Error.WriteLine("the game started: stopping");
                return 3;
            }
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--child --rounds 1 --variants {variants} --shows {o.Shows}{(o.Instrument ? "" : " --plain")}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var f = line.Split(' ');
                if (f.Length != 7 || f[0] != "S") continue;
                var shows = results.TryGetValue(f[1], out var have) ? have : results[f[1]] = Enumerable.Range(0, o.Shows).Select(_ => new List<Sample>()).ToArray();
                double D(int k) => double.Parse(f[k], CultureInfo.InvariantCulture);
                shows[int.Parse(f[2], CultureInfo.InvariantCulture)].Add(new Sample(D(3), D(4), D(5), D(6)));
            }
            Console.WriteLine($"  {i + 1}/{o.Cold} processes");
        }
        Console.WriteLine("cold processes:");
        Report.Print(results, o.Instrument);
        return 0;
    }
}
