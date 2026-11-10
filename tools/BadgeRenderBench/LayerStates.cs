using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.Views;
using Sts2Overlay.Core.Map;

namespace Sts2Overlay.Tools.BadgeRenderBench;

/// <summary>
/// One of the overlay's three full-screen layers (badges, map marks, event markers), driven the way AppController
/// drives it: placed over the "game" (the primary monitor), filled with a screen's worth of marks, or emptied.
/// </summary>
internal sealed class Layer
{
    private readonly Action<int> _fill;
    private readonly Action _empty;
    private readonly Func<(int X, int Y)?> _probe;

    private readonly Action<bool>? _setOn;
    private readonly Action<bool>? _setReady;

    private Layer(string kind, Window window, Action<PixelRect> place, Action<int> fill, Action empty, Func<(int X, int Y)?> probe,
        Action<bool>? setOn = null, Action<bool>? setReady = null)
    {
        Kind = kind;
        Window = window;
        Place = place;
        _fill = fill;
        _empty = empty;
        _probe = probe;
        _setOn = setOn;
        _setReady = setReady;
    }

    /// <summary>The controller expects marks soon (SetLayerReady); nothing for the badge layer.</summary>
    public void Ready(bool ready) => _setReady?.Invoke(ready);

    public string Kind { get; }
    public Window Window { get; }
    public Action<PixelRect> Place { get; }

    /// <summary>The map and event layers trim themselves when empty (LayerTrimmer); the controller turns them on and off.</summary>
    public bool Trims => _setOn is not null;

    /// <summary>What the controller does when the game is in front: Show(), or SetLayerOn(true) for a trimming layer.</summary>
    public void Show()
    {
        if (_setOn is { } on) on(true);
        else Window.Show();
    }

    public void Hide()
    {
        if (_setOn is { } on) on(false);
        else Window.Hide();
    }

    /// <summary>Shows a screen's worth of marks, <paramref name="shift"/> px off (a re-recognised screen).</summary>
    public void Fill(int shift) => _fill(shift);

    /// <summary>What the app does when the screen has nothing to mark: the window stays shown, its content is cleared.</summary>
    public void Empty() => _empty();

    /// <summary>A screen pixel (physical) inside the first mark, for the on-screen probe; null when there is none.</summary>
    public (int X, int Y)? ProbePoint() => _probe();

    public static readonly string[] Kinds = ["badge", "map", "event"];

    /// <summary>
    /// false (default): the layers are excluded from screen capture, as in the app by default. true: visible to screen
    /// reads, for the on-screen probe (an excluded window is invisible to GetPixel on the screen DC).
    /// </summary>
    public static bool ShowInCapture { get; set; }

    /// <summary>A new layer window of <paramref name="kind"/>, HWND created and placed over the screen, not shown.</summary>
    public static Layer Create(string kind, Screen screen)
    {
        var visibility = new OverlayCaptureVisibility(ShowInCapture);
        var k = Math.Min(screen.Width / 1920.0, screen.Height / 1080.0);
        Layer layer;
        switch (kind)
        {
            case "badge":
            {
                var w = new BadgeWindow(visibility);
                var content = Content.For("full-reward", screen);
                layer = new Layer(kind, w, w.SetLayerRect, s => w.ShowBadges(content.Specs(s), content.Skip, content.Removal, false), w.Clear, () =>
                {
                    if (w.Model.Badges.Count == 0) return null;
                    var b = w.Model.Badges[0];
                    var dpi = Dpi(w);
                    // Top strip of the pill, right of the tier tile: the pill's dark background.
                    return ((int)((b.Left + 120 * b.Scale) * dpi), (int)((b.Top + 6 * b.Scale) * dpi));
                });
                break;
            }
            case "map":
            {
                var w = new MapOverlayWindow(visibility);
                layer = new Layer(kind, w, w.SetLayerRect, s => w.ShowPlan(MapPlan(s)), w.Clear, () =>
                {
                    if (!w.HasMarks) return null;
                    // On the recommended node's gold ring, right of the node at shift 0: radius 42 design px, 5 px stroke
                    // drawn inside the ellipse's bounds, so 39.5 is mid-stroke.
                    var t = Sts2Overlay.Vision.RefTransform.For(screen.Width, screen.Height);
                    return ((int)((960 + 39.5) * t.Scale + t.OffsetX), (int)(830 * t.Scale + t.OffsetY));
                }, w.SetLayerOn, w.SetLayerReady);
                break;
            }
            case "event":
            {
                var w = new EventMarkerWindow(visibility);
                PixelRect R(double x, double y, double ww, double h) => new((int)(x * k), (int)(y * k), (int)(ww * k), (int)(h * k));
                layer = new Layer(kind, w, w.SetLayerRect,
                    s => w.Show(Enumerable.Range(0, 3).Select(i => new EventMarkerSpec(R(980 + s, 560 + i * 95 + s, 700, 75), i + 1, i == 0,
                        (40 - i * 7).ToString(CultureInfo.InvariantCulture), i == 2 ? "죽음 위험" : null)).ToArray()),
                    w.Clear, () =>
                    {
                        if (w.Model.Markers.Count == 0) return null;
                        var m = w.Model.Markers[0];
                        var dpi = Dpi(w);
                        return ((int)((m.Left + 6 * m.Scale) * dpi), (int)((m.Top + 22 * m.Scale) * dpi));
                    }, w.SetLayerOn, w.SetLayerReady);
                break;
            }
            default:
                throw new ArgumentException($"unknown layer {kind}");
        }
        new WindowInteropHelper(layer.Window).EnsureHandle();
        layer.Place(screen.Rect);
        return layer;
    }

    private static double Dpi(Window w) => VisualTreeDpi(w) is var d && d > 0 ? d : 1.0;

    private static double VisualTreeDpi(Window w) => System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX;

    /// <summary>A path down the map with a recommended node and a "?" odds chip, in design px.</summary>
    private static MapMarkPlan MapPlan(int shift)
    {
        var lines = new List<MapLineMark>();
        for (var i = 0; i < 6; i++)
        {
            lines.Add(new MapLineMark(900 + shift, 950 - i * 140, 960 + shift, 830 - i * 140, MapLineKind.Best));
            lines.Add(new MapLineMark(900 + shift, 950 - i * 140, 820 + shift, 830 - i * 140, MapLineKind.Alternative));
        }
        return new MapMarkPlan(lines,
        [
            new MapNodeMark(new MapCoord(3, 1), 960 + shift, 830, MapNodeMarkKind.Recommended, null),
            new MapNodeMark(new MapCoord(2, 1), 820 + shift, 830, MapNodeMarkKind.UnknownOdds, "몬스터 60% · 상점 20%"),
        ]);
    }
}

/// <summary>Private bytes and working set of this process, GPU memory of this process and of DWM.</summary>
internal readonly record struct MemSample(double PrivateMb, double WorkingSetMb, double GpuDedicatedMb, double GpuSharedMb, double DwmDedicatedMb, double DwmSharedMb);

internal static class Memory
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public int Cb;
        public int PageFaultCount;
        public nint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    private static Gpu? _gpu;

    public static MemSample Read()
    {
        var c = new Counters { Cb = Marshal.SizeOf<Counters>() };
        GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.Cb);
        _gpu ??= new Gpu();
        var (ded, sh, dwmDed, dwmSh) = _gpu.Read();
        return new MemSample(c.PrivateUsage / 1048576.0, c.WorkingSetSize / 1048576.0, ded, sh, dwmDed, dwmSh);
    }

    /// <summary>"\GPU Process Memory(pid_N_*)\Dedicated Usage" and "Shared Usage" through PDH, summed over adapters.</summary>
    private sealed class Gpu
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

        private const uint PdhFmtLarge = 0x400;
        private const int PdhMoreData = unchecked((int)0x800007D2);

        private readonly IntPtr _query, _dedicated, _shared;
        private readonly string _self = $"pid_{Environment.ProcessId}_";
        private readonly string? _dwm = Process.GetProcessesByName("dwm").FirstOrDefault() is { } p ? $"pid_{p.Id}_" : null;

        public Gpu()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
            PdhAddEnglishCounter(_query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _dedicated);
            PdhAddEnglishCounter(_query, @"\GPU Process Memory(*)\Shared Usage", IntPtr.Zero, out _shared);
        }

        public (double Ded, double Shared, double DwmDed, double DwmShared) Read()
        {
            if (_query == IntPtr.Zero) return (double.NaN, double.NaN, double.NaN, double.NaN);
            PdhCollectQueryData(_query);
            var ded = Sum(_dedicated);
            var sh = Sum(_shared);
            return (Pick(ded, _self), Pick(sh, _self), Pick(ded, _dwm), Pick(sh, _dwm));
        }

        private static double Pick(Dictionary<string, long> values, string? prefix)
            => prefix is null ? double.NaN : values.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(kv => kv.Value) / 1048576.0;

        private static Dictionary<string, long> Sum(IntPtr counter)
        {
            var result = new Dictionary<string, long>();
            uint size = 0;
            if (PdhGetFormattedCounterArray(counter, PdhFmtLarge, ref size, out _, IntPtr.Zero) != PdhMoreData || size == 0) return result;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(counter, PdhFmtLarge, ref size, out var count, buffer) != 0) return result;
                // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then { CStatus (4) + padding (4), 8-byte union }.
                for (var i = 0; i < count; i++)
                {
                    var item = buffer + i * 24;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    result[name] = Marshal.ReadInt64(item + 16);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }
    }
}

/// <summary>
/// Question 1: what each state of a layer window holds. One fresh process per layer kind and set
/// (<c>--layer-mem badge,map,event --sets 3</c>), each walking the states in order:
/// <code>
/// base       WPF up, app resources loaded, no layer window
/// created    window and HWND created, placed over the screen, never shown
/// content    shown with a screen's worth of marks
/// empty      content cleared, window still shown (what the app does today when there is nothing to mark)
/// hidden     marks again, then cleared and Hide() (Visibility.Hidden); sampled at +0.2 s, +1 s, +5 s
/// collapsed  marks again, then cleared and Visibility.Collapsed
/// shrunk     marks again, then cleared and cut to 1x1 px while shown (+ after a frame on the device, + regrown)
/// shrunk-hidden / hidden-shrunk   cut to 1x1 then hidden / hidden then cut to 1x1 (+ regrown)
/// closed     marks again, then Close()
/// </code>
/// The map and event layers trim themselves when empty (LayerTrimmer), so for them the walk is the app's own: content,
/// empty, empty past the trim delay, marks again, closed. The raw states above are the badge layer's (the same kind of
/// window, never trimmed).
/// Values are deltas against "base". GPU memory is the process's own (dedicated + shared, from the GPU Process Memory
/// counters); DWM's is shown too since a layered window's bitmap is also kept by the compositor.
/// </summary>
internal static class LayerMemBench
{
    public static int RunParent(string[] kinds, int sets)
    {
        var all = new Dictionary<(string Kind, string State), List<MemSample>>();
        var order = new List<string>();
        var total = kinds.Length * sets;
        var done = 0;
        var started = Stopwatch.StartNew();
        for (var set = 0; set < sets; set++)
        {
            foreach (var kind in kinds)
            {
                if (GameGuard.IsGameRunning())
                {
                    Console.Error.WriteLine("the game started: stopping");
                    return 3;
                }
                var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--layer-mem-child {kind}") { RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var f = line.Split(' ');
                    if (f.Length != 8 || f[0] != "M") continue;
                    double D(int i) => double.Parse(f[i], CultureInfo.InvariantCulture);
                    var key = (kind, f[1]);
                    if (!all.TryGetValue(key, out var list)) all[key] = list = [];
                    list.Add(new MemSample(D(2), D(3), D(4), D(5), D(6), D(7)));
                    if (!order.Contains(f[1])) order.Add(f[1]);
                }
                done++;
                var eta = DateTime.Now.AddSeconds(started.Elapsed.TotalSeconds / done * (total - done));
                Console.WriteLine($"  {done}/{total} processes ({100.0 * done / total:0}%), eta {eta:HH:mm:ss}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"layer memory per state, MB above 'base' (median of {sets} processes; GPU = this process's dedicated + shared):");
        Console.WriteLine($"  {"layer",-6} {"state",-14} {"private",8} {"workset",8} {"gpu ded",8} {"gpu shr",8} {"dwm ded",8} {"dwm shr",8}");
        foreach (var kind in kinds)
        {
            if (!all.TryGetValue((kind, "base"), out var baseList)) continue;
            foreach (var state in order)
            {
                if (!all.TryGetValue((kind, state), out var xs)) continue;
                double M(Func<MemSample, double> f) => Stats.Median(xs.Select(f)) - Stats.Median(baseList.Select(f));
                Console.WriteLine(FormattableString.Invariant(
                    $"  {kind,-6} {state,-14} {M(x => x.PrivateMb),8:0.0} {M(x => x.WorkingSetMb),8:0.0} {M(x => x.GpuDedicatedMb),8:0.0} {M(x => x.GpuSharedMb),8:0.0} {M(x => x.DwmDedicatedMb),8:0.0} {M(x => x.DwmSharedMb),8:0.0}"));
            }
        }
        Console.WriteLine("  (base absolute, first layer: " + string.Join(", ", kinds.Take(1).Select(k => all.TryGetValue((k, "base"), out var b)
            ? FormattableString.Invariant($"private {Stats.Median(b.Select(x => x.PrivateMb)):0} MB, gpu {Stats.Median(b.Select(x => x.GpuDedicatedMb + x.GpuSharedMb)):0} MB, dwm {Stats.Median(b.Select(x => x.DwmDedicatedMb + x.DwmSharedMb)):0} MB")
            : "-")) + ")");
        return 0;
    }

    public static async Task<int> RunChildAsync(string kind)
    {
        var screen = Screen.Primary();
        // Base: WPF's render stack already up (D3D device, render thread, fonts), the way it is in the app before a
        // layer is created: a small layer window of the same kind, shown with marks, then emptied and left shown.
        var primer = Layer.Create(kind, screen);
        primer.Place(new PixelRect(0, 0, 200, 200));
        primer.Show();
        primer.Fill(0);
        await Settle(primer.Window);
        primer.Empty();
        await Sample("base", primer.Window, 1500);

        var layer = Layer.Create(kind, screen);
        await Sample("created", layer.Window);
        if (layer.Trims) return await TrimmingLayerAsync(layer, primer);

        layer.Show();
        layer.Fill(0);
        await Sample("content", layer.Window);

        layer.Empty();
        await Sample("empty", layer.Window);

        layer.Fill(7);
        await Settle(layer.Window);
        layer.Empty();
        await Settle(layer.Window);
        layer.Hide();
        await Sample("hidden+0.2s", layer.Window, 200);
        await Sample("hidden+1s", layer.Window, 800);
        await Sample("hidden+5s", layer.Window, 4000);

        layer.Show();
        layer.Fill(0);
        await Sample("reshown", layer.Window);

        layer.Empty();
        await Settle(layer.Window);
        layer.Window.Visibility = Visibility.Collapsed;
        await Sample("collapsed+1s", layer.Window, 1000);

        // Shrunk: emptied, then the window cut to 1x1 px while still shown (WPF re-creates its surface at that size).
        layer.Window.Visibility = Visibility.Visible;
        layer.Fill(7);
        await Settle(layer.Window);
        layer.Empty();
        await Settle(layer.Window);
        Shrink(layer.Window);
        await Sample("shrunk+1s", layer.Window, 1000);
        await Sample("shrunk+5s", layer.Window, 4000);
        // WPF frees a released D3D surface only after a later frame on the same device: draw one on the primer.
        await Poke(primer);
        await Sample("shrunk+frame", layer.Window);

        layer.Place(screen.Rect);
        layer.Fill(0);
        await Sample("regrown", layer.Window);
        await Poke(primer);
        await Sample("regrown+frame", layer.Window);

        // Shrunk and hidden: the state a rarely used layer would sit in.
        layer.Empty();
        await Settle(layer.Window);
        Shrink(layer.Window);
        layer.Hide();
        await Poke(primer);
        await Sample("shrunk-hidden", layer.Window);

        layer.Place(screen.Rect);
        layer.Show();
        layer.Fill(0);
        await Sample("regrown2", layer.Window);

        // Hidden first, then shrunk while hidden (no resize of a shown layered window).
        layer.Empty();
        await Settle(layer.Window);
        layer.Hide();
        Shrink(layer.Window);
        await Poke(primer);
        await Sample("hidden-shrunk", layer.Window);

        layer.Place(screen.Rect);
        layer.Fill(0);
        layer.Show();
        await Sample("regrown3", layer.Window);

        layer.Window.Close();
        await Sample("closed+1s", primer.Window, 1000);
        await Poke(primer);
        await Sample("closed+frame", primer.Window);
        primer.Window.Close();
        return 0;
    }

    /// <summary>
    /// Leak check for trimming: <paramref name="cycles"/> rounds of marks → empty → 1x1 → full size again, memory after
    /// each (<c>--layer-cycles map 10</c>). A trim that leaked a surface per round would show as steady growth.
    /// </summary>
    public static async Task<int> RunCyclesAsync(string kind, int cycles)
    {
        var screen = Screen.Primary();
        var layer = Layer.Create(kind, screen);
        layer.Show();
        layer.Fill(0);
        await Settle(layer.Window);
        layer.Empty();
        await Sample("full-empty", layer.Window);
        var started = Stopwatch.StartNew();
        for (var i = 0; i < cycles; i++)
        {
            layer.Fill(i % 2 * 7);
            await Settle(layer.Window);
            layer.Empty();
            await Settle(layer.Window);
            // A trimming layer (map, event) trims itself after its delay; the badge layer is cut by hand.
            if (layer.Trims) await Task.Delay(6000);
            else Shrink(layer.Window);
            await Sample($"trimmed#{i + 1}", layer.Window, 500);
            layer.Place(screen.Rect);
            layer.Fill(0);
            await Sample($"marks#{i + 1}", layer.Window, 500);
            layer.Empty();
            var eta = DateTime.Now.AddSeconds(started.Elapsed.TotalSeconds / (i + 1) * (cycles - i - 1));
            Console.Error.WriteLine($"  {i + 1}/{cycles} cycles ({100.0 * (i + 1) / cycles:0}%), eta {eta:HH:mm:ss}");
        }
        layer.Window.Close();
        return 0;
    }

    /// <summary>
    /// The map and event layers as the app drives them now (LayerTrimmer): on, marks, emptied, then left empty past the
    /// trim delay (5 s) so they hide and shrink on their own, and marks again.
    /// </summary>
    private static async Task<int> TrimmingLayerAsync(Layer layer, Layer primer)
    {
        layer.Show();
        layer.Fill(0);
        await Sample("content", layer.Window);
        layer.Empty();
        await Sample("empty+1s", layer.Window);
        await Sample("empty+7s", layer.Window, 5500);
        await Poke(primer);
        await Sample("trimmed+frame", layer.Window);
        Console.Error.WriteLine($"  after the delay: shown {layer.Window.IsVisible}");
        layer.Fill(7);
        await Sample("marks-again", layer.Window);
        layer.Window.Close();
        await Sample("closed+1s", primer.Window, 1000);
        primer.Window.Close();
        return 0;
    }

    private static async Task Poke(Layer primer)
    {
        primer.Fill(3);
        await Settle(primer.Window);
        primer.Empty();
        await Settle(primer.Window);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>1x1 px at the window's corner, z-order and activation untouched.</summary>
    internal static void Shrink(Window w) => SetWindowPos(new WindowInteropHelper(w).Handle, IntPtr.Zero, 0, 0, 1, 1, 0x0004 | 0x0010 | 0x0002);

    private static async Task Settle(Window w)
    {
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task Sample(string state, Window? w, int waitMs = 1000)
    {
        if (w is not null) await Settle(w);
        await Task.Delay(waitMs);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var m = Memory.Read();
        Console.WriteLine(FormattableString.Invariant(
            $"M {state} {m.PrivateMb:0.00} {m.WorkingSetMb:0.00} {m.GpuDedicatedMb:0.00} {m.GpuSharedMb:0.00} {m.DwmDedicatedMb:0.00} {m.DwmSharedMb:0.00}"));
    }
}
