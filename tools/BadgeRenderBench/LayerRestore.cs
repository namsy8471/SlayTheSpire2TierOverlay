using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Sts2Overlay.Tools.BadgeRenderBench;

/// <summary>
/// Question 2: what it costs to bring a layer back from each state to "marks painted". Usage:
/// <c>--layer-restore badge --rounds N --dwell MS</c>. One process, one layer kind; every round visits the states in a
/// shuffled order (interleaved, so CPU load from other jobs hits them all alike). Before each sample the layer sits in
/// the state for <c>--dwell</c> ms (so a lazy release of its surface has happened).
/// <code>
/// empty               shown, content cleared (today)            → Fill
/// hidden              cleared, then Hide()                      → Show(), Fill
/// hidden-fill         cleared, then Hide()                      → Fill, Show()
/// closed              window closed                             → new window, Show(), Fill
/// shrunk              cleared, cut to 1x1 px while shown        → full size, Fill
/// shrunk-hidden       cut to 1x1 while shown, then Hide()       → full size, Show(), Fill
/// shrunk-hidden-fill  cut to 1x1 while shown, then Hide()       → full size, Fill, Show()
/// hidden-shrunk-fill  Hide(), then cut to 1x1 while hidden      → full size, Fill, Show()
/// self-trim           (map, event) left empty past the trim delay: the layer's own LayerTrimmer → Fill
/// self-trim-ready     as self-trim, then SetLayerReady(true) and --dwell ms (the screen was seen coming) → Fill
/// </code>
/// For the map and event layers Show/Hide are the controller's SetLayerOn(true/false).
/// Times (ms from the change): the UI thread's Loaded-priority callback (what the app's perf log calls "render"), the
/// first render pass, and "into": what entering the state cost the UI thread. With <c>--flash</c> the layer is made
/// visible to screen reads and put over a small magenta backdrop, and a pixel inside the first mark is polled from
/// another thread (GetPixel on the screen DC): "screen" is when it changed, "P" lines every colour it showed in turn,
/// and a colour that is neither the backdrop nor the finished mark is reported as a frame in between (black or stale).
/// </summary>
internal static class LayerRestoreBench
{
    private static readonly string[] RawStates = ["empty", "hidden", "hidden-fill", "closed", "shrunk", "shrunk-hidden", "shrunk-hidden-fill", "hidden-shrunk-fill"];

    /// <summary>
    /// The map and event layers trim themselves (LayerTrimmer): "self-trim" leaves the layer empty past its delay
    /// (5 s), so it hides and shrinks on its own, and brings it back with plain marks, the app's real path.
    /// </summary>
    private const int SelfTrimDwellMs = 6500;

    private readonly record struct Restore(double Loaded, double Rendering, double Screen);

    public static async Task<int> RunAsync(string kind, int rounds, int dwellMs, bool flash = false)
    {
        var screen = Screen.Primary();
        var load = new CpuLoad();
        // Flash check: the layers must be visible to screen reads (they are excluded from capture by default, and then
        // GetPixel sees only what is under them), over a solid backdrop, so any colour that is neither the backdrop
        // nor the finished mark is a frame in between.
        Layer.ShowInCapture = flash;
        var layer = Layer.Create(kind, screen);
        string[] states = layer.Trims ? [.. RawStates, "self-trim", "self-trim-ready"] : RawStates;
        Console.WriteLine($"layer {kind}: {screen.Width}x{screen.Height} px, {rounds} rounds x {states.Length} states, dwell {dwellMs} ms");
        var results = states.ToDictionary(s => s, _ => new List<Restore>());
        var into = states.ToDictionary(s => s, _ => new List<double>());
        var loads = new List<double>();
        var rng = new Random(4321);

        layer.Show();
        // Warm-up: the process's first marks (JIT, templates) are not what is measured here.
        for (var i = 0; i < 3; i++)
        {
            layer.Fill(0);
            await Settle(layer.Window);
            layer.Empty();
            await Settle(layer.Window);
        }
        layer.Fill(0);
        await Settle(layer.Window);
        // Only with --flash: an excluded-from-capture layer is invisible to GetPixel, so the probe would time the desktop.
        var probe = flash ? layer.ProbePoint() : null;
        layer.Empty();
        await Settle(layer.Window);
        var probeCost = probe is { } pp ? ScreenPixel.CostMs(pp.X, pp.Y) : double.NaN;
        Console.WriteLine(probe is { } q
            ? FormattableString.Invariant($"on-screen probe at ({q.X},{q.Y}), one GetPixel {probeCost:0.0} ms")
            : "no on-screen probe for this layer");
        Backdrop? backdrop = flash && probe is { } bpos ? Backdrop.Open(bpos.X, bpos.Y) : null;
        var inBetween = states.ToDictionary(s => s, _ => new List<string>());

        var total = rounds * states.Length;
        var done = 0;
        var started = Stopwatch.StartNew();
        for (var round = 0; round < rounds; round++)
        {
            load.Reset();
            foreach (var state in states.OrderBy(_ => rng.Next()))
            {
                if (GameGuard.IsGameRunning())
                {
                    Console.Error.WriteLine("the game started: stopping");
                    return 3;
                }
                // Into the state (from "shown, empty"), timed too: a trim costs the UI thread something as well.
                var i0 = Stopwatch.GetTimestamp();
                switch (state)
                {
                    case "hidden-shrunk-fill":
                        layer.Hide();
                        LayerMemBench.Shrink(layer.Window);
                        break;
                    case "hidden":
                    case "hidden-fill":
                        layer.Hide();
                        break;
                    case "closed":
                        layer.Window.Close();
                        break;
                    case "shrunk":
                        LayerMemBench.Shrink(layer.Window);
                        break;
                    case "shrunk-hidden":
                    case "shrunk-hidden-fill":
                        LayerMemBench.Shrink(layer.Window);
                        layer.Hide();
                        break;
                }
                var i1 = await layer.Window.Dispatcher.InvokeAsync(Stopwatch.GetTimestamp, DispatcherPriority.Loaded);
                into[state].Add(Ms(i0, i1));
                await Task.Delay(state.StartsWith("self-trim", StringComparison.Ordinal) ? SelfTrimDwellMs : dwellMs);
                if (state == "self-trim-ready")
                {
                    // The controller saw the screen coming (map hint, event room) before the marks: SetLayerReady.
                    layer.Ready(true);
                    await Task.Delay(dwellMs);
                }
                if (state == "self-trim" && layer.Window.IsVisible) Console.WriteLine("  self-trim: the layer is still shown after the delay");
                backdrop?.Below(layer.Window);
                var before = probe is { } bp ? ScreenPixel.Read(bp.X, bp.Y) : 0u;
                var watcher = probe is { } wp ? ScreenPixel.WatchAsync(wp.X, wp.Y, before, 500)
                    : Task.FromResult((double.NaN, new List<(double Ms, uint Color)>()));
                var stateLayer = layer;
                var sample = await Time(() =>
                {
                    switch (state)
                    {
                        case "empty":
                        case "self-trim":
                        case "self-trim-ready":
                            stateLayer.Fill(0);
                            break;
                        case "hidden":
                            stateLayer.Show();
                            stateLayer.Fill(0);
                            break;
                        case "hidden-fill":
                            stateLayer.Fill(0);
                            stateLayer.Show();
                            break;
                        case "closed":
                            stateLayer = Layer.Create(kind, screen);
                            stateLayer.Show();
                            stateLayer.Fill(0);
                            break;
                        case "shrunk":
                            stateLayer.Place(screen.Rect);
                            stateLayer.Fill(0);
                            break;
                        case "shrunk-hidden":
                            stateLayer.Place(screen.Rect);
                            stateLayer.Show();
                            stateLayer.Fill(0);
                            break;
                        case "shrunk-hidden-fill":
                        case "hidden-shrunk-fill":
                            stateLayer.Place(screen.Rect);
                            stateLayer.Fill(0);
                            stateLayer.Show();
                            break;
                    }
                }, stateLayer.Window.Dispatcher, ScreenPixel.Start);
                var (onScreen, colors) = await watcher;
                layer = stateLayer;
                results[state].Add(sample with { Screen = onScreen });
                // Colours the probe pixel went through: before → ... → the mark. More than one step is a frame in between.
                if (probe is not null)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"P {kind} {state} before {before:X6} loaded {sample.Loaded:0} : {string.Join(" ", colors.Select(c => $"{c.Ms:0}ms={c.Color:X6}"))}"));
                    // Over the backdrop: anything but the backdrop and the finished mark is a frame in between.
                    if (backdrop is not null && colors.Count > 0)
                    {
                        var final = colors[^1].Color;
                        var odd = colors.Where(c => c.Color != final && c.Color != before).ToList();
                        if (odd.Count > 0) inBetween[state].Add(string.Join(" ", odd.Select(c => FormattableString.Invariant($"{c.Ms:0}ms={c.Color:X6}"))) + FormattableString.Invariant($" then {final:X6}"));
                    }
                    if (backdrop is not null && colors.Count == 0) inBetween[state].Add("mark never seen on screen");
                }
                // Back to "shown, empty".
                layer.Ready(false);
                await Settle(layer.Window);
                layer.Empty();
                await Settle(layer.Window);
                done++;
            }
            loads.Add(load.Percent());
            var eta = DateTime.Now.AddSeconds(started.Elapsed.TotalSeconds / done * (total - done));
            Console.WriteLine($"  {done}/{total} ({100.0 * done / total:0}%), round {round + 1}/{rounds}, machine CPU {loads[^1]:0}%, eta {eta:HH:mm:ss}");
        }
        layer.Window.Close();
        backdrop?.Close();

        Console.WriteLine();
        Console.WriteLine($"machine CPU during the run: median {Stats.Median(loads):0}% (min {loads.Min():0}, max {loads.Max():0})");
        if (backdrop is not null)
        {
            Console.WriteLine($"{kind}: frames in between (neither the backdrop nor the finished mark) at the probe pixel, per state:");
            foreach (var (state, xs) in inBetween)
            {
                Console.WriteLine($"  {state,-20} {xs.Count}/{results[state].Count} samples");
                foreach (var x in xs.Take(3)) Console.WriteLine($"      {x}");
            }
        }
        Console.WriteLine($"{kind}: from state -> marks (ms from the change; median, p10, p90):");
        foreach (var (state, xs) in results)
        {
            Console.WriteLine($"  {state,-12} loaded   {Stats.Line(xs.Select(x => x.Loaded).ToArray())}");
            Console.WriteLine($"  {"",-12} render   {Stats.Line(xs.Select(x => x.Rendering).ToArray())}");
            if (probe is not null) Console.WriteLine($"  {"",-12} screen   {Stats.Line(xs.Select(x => x.Screen).Where(x => !double.IsNaN(x)).ToArray())}  (missed {xs.Count(x => double.IsNaN(x.Screen))})");
            Console.WriteLine($"  {"",-12} into    {Stats.Line(into[state])}  (entering the state: action -> Loaded)");
        }
        foreach (var (state, xs) in into)
            foreach (var x in xs)
                Console.WriteLine(FormattableString.Invariant($"I {kind} {state} {x:0.00}"));
        // One line per sample for ratio work across processes.
        foreach (var (state, xs) in results)
            foreach (var x in xs)
                Console.WriteLine(FormattableString.Invariant($"R {kind} {state} {x.Loaded:0.00} {x.Rendering:0.00} {x.Screen:0.00}"));
        return 0;
    }

    private static async Task<Restore> Time(Action change, Dispatcher dispatcher, Action<long> started)
    {
        long rendering = 0;
        EventHandler? onRendering = null;
        onRendering = (_, _) =>
        {
            rendering = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering -= onRendering;
        };
        var t0 = Stopwatch.GetTimestamp();
        started(t0);
        change();
        CompositionTarget.Rendering += onRendering;
        var t2 = await dispatcher.InvokeAsync(Stopwatch.GetTimestamp, DispatcherPriority.Loaded);
        CompositionTarget.Rendering -= onRendering;
        return new Restore(Ms(t0, t2), rendering == 0 ? double.NaN : Ms(t0, rendering), double.NaN);
    }

    internal static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    private static async Task Settle(Window w)
    {
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}

/// <summary>
/// A small solid magenta window just under the layer around the probe pixel (topmost, never activated), so the probe
/// reads a known colour wherever the layer draws nothing, whatever the desktop behind is doing.
/// </summary>
internal sealed class Backdrop
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10;
    private static readonly IntPtr Topmost = new(-1);
    private readonly Window _window;
    private readonly IntPtr _hwnd;

    private Backdrop(Window window, IntPtr hwnd)
    {
        _window = window;
        _hwnd = hwnd;
    }

    public static Backdrop Open(int x, int y)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Topmost = true, Focusable = false, Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF)), Width = 50, Height = 50,
        };
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(hwnd, Topmost, x - 60, y - 60, 120, 120, NoActivate);
        w.Show();
        SetWindowPos(hwnd, Topmost, x - 60, y - 60, 120, 120, NoActivate);
        return new Backdrop(w, hwnd);
    }

    /// <summary>Puts <paramref name="layer"/> above the backdrop (both topmost).</summary>
    public void Below(Window layer)
    {
        SetWindowPos(_hwnd, Topmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
        SetWindowPos(new System.Windows.Interop.WindowInteropHelper(layer).Handle, Topmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
    }

    public void Close() => _window.Close();
}

/// <summary>Reads one pixel of the composed screen (GetPixel on the screen DC) from a background thread.</summary>
internal static class ScreenPixel
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr hdc, int x, int y);

    private static long _start;

    /// <summary>The change's start time, for <see cref="WatchAsync"/> (set on the UI thread just before the change).</summary>
    public static void Start(long t0) => Interlocked.Exchange(ref _start, t0);

    public static uint Read(int x, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        try
        {
            return GetPixel(dc, x, y);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    public static double CostMs(int x, int y)
    {
        var t = Stopwatch.GetTimestamp();
        for (var i = 0; i < 10; i++) Read(x, y);
        return LayerRestoreBench.Ms(t, Stopwatch.GetTimestamp()) / 10;
    }

    /// <summary>
    /// Polls for <paramref name="timeoutMs"/>: when the pixel first differed from <paramref name="before"/> (ms from
    /// <see cref="Start"/>, NaN if never), and every colour it showed in turn (to catch a frame that is neither the old
    /// screen nor the new marks, e.g. a black or stale surface while a window is resized).
    /// </summary>
    public static Task<(double FirstChange, List<(double Ms, uint Color)> Colors)> WatchAsync(int x, int y, uint before, int timeoutMs)
    {
        Interlocked.Exchange(ref _start, 0);
        return Task.Run(() =>
        {
            var dc = GetDC(IntPtr.Zero);
            var colors = new List<(double, uint)>();
            var first = double.NaN;
            try
            {
                var limit = Stopwatch.GetTimestamp() + timeoutMs * Stopwatch.Frequency / 1000;
                var last = before;
                while (Stopwatch.GetTimestamp() < limit)
                {
                    var c = GetPixel(dc, x, y);
                    var start = Interlocked.Read(ref _start);
                    if (start == 0 || c == 0xFFFFFFFF || c == last) continue;
                    var ms = LayerRestoreBench.Ms(start, Stopwatch.GetTimestamp());
                    if (double.IsNaN(first)) first = ms;
                    colors.Add((ms, c));
                    last = c;
                }
                return (first, colors);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
        });
    }
}
