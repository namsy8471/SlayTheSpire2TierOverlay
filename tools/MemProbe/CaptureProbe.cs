using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Sts2Overlay.App.Interop;
using Sts2Overlay.App.Services;
using Sts2Overlay.Vision;
using static Sts2Overlay.App.Interop.D3D11Interop;
using static Sts2Overlay.App.Interop.WgcInterop;
using AppRect = Sts2Overlay.App.Integration.PixelRect;

namespace MemProbe;

/// <summary>
/// The capture side on its own, with the app's real <see cref="WgcCapturer"/> on a test window (no game needed):
///
///   MemProbe capture [--width 3840] [--height 2160] [--polls 20] [--hz 3]
///       memory after each capture step (first capture, polls, Suspend, resume, Dispose) + this process's GPU memory;
///       every captured frame is compared with the window's pixels (must be 0 different)
///   MemProbe devices
///       D3D11 device cost per adapter / creation flag / feature level, each in a fresh child process ("device" below)
///   MemProbe device [--adapter default|N] [--flags 0x20] [--levels default|11_0] [--driver hw|warp]
///   MemProbe poolage [--polls 12] [--hz 3]
///       how old the frame a poll gets is, with a frame pool of 2 buffers and of 1, on a window that redraws all the time
///
/// The test window is the verifier's <see cref="ReplayWindow"/>: a layered window at the bottom of the z-order, so it
/// never covers anything and WGC still sees its content.
/// </summary>
internal static class CaptureProbe
{
    // ------------------------------------------------------------------ capture

    public static int Capture(Dictionary<string, string> opts)
    {
        PerMonitorDpi();
        int width = Int(opts, "width", 3840), height = Int(opts, "height", 2160), polls = Int(opts, "polls", 20);
        double hz = double.Parse(opts.GetValueOrDefault("hz") ?? "3", CultureInfo.InvariantCulture);
        Console.WriteLine($"MemProbe capture: test window {width}x{height}, {polls} polls at {hz} Hz, adapters: " +
            string.Join(", ", GpuMemory.Adapters().Select(a => $"[{a.Index}] {a.Name}{(a.Software ? " (software)" : "")}")));
        var images = new[] { Pattern(width, height, 0), Pattern(width, height, 1) };
        var origin = Dpi.PrimaryMonitorRect();
        Console.WriteLine($"primary monitor {origin}");
        using var window = new ReplayWindow();
        window.Show(images[0], origin.X, origin.Y);
        var rect = new AppRect(origin.X, origin.Y, width, height);
        var info = new GameWindowInfo(window.Hwnd, Environment.ProcessId, rect, rect, false, false, false, false);

        var report = new MemReport(Console.Out);
        report.Header();
        Row(report, "a. baseline (test window shown)");
        var wgc = new WgcCapturer();
        var (firstMs, firstDiff) = CaptureOnce(wgc, info, images[0]);
        Console.WriteLine(Inv($"   (first capture {firstMs:F0} ms: device + session + first frame; {firstDiff} pixels differ, row pitch {wgc.LastRowPitch})"));
        Row(report, "b. first capture");
        int mismatched = Poll(wgc, window, info, images, polls, hz, origin, "c");
        Row(report, $"c. + {polls} polls");
        wgc.Suspend();
        Row(report, "d. Suspend()");
        Thread.Sleep(1000);
        Row(report, "d2. Suspend() + 1 s");
        var (resumedMs, resumedDiff) = CaptureOnce(wgc, info, images[0]);
        Console.WriteLine(Inv($"   (capture after Suspend {resumedMs:F0} ms; {resumedDiff} pixels differ)"));
        Row(report, "e. capture after Suspend");
        mismatched += Poll(wgc, window, info, images, Math.Max(4, polls / 4), hz, origin, "f");
        Row(report, "f. + polls");
        mismatched += TrimStep(wgc, window, info, images[0], origin, report);
        wgc.Dispose();
        Row(report, "g. Dispose()");
        Console.WriteLine(mismatched == 0 ? "frames: all identical to the window's pixels" : $"FRAMES DIFFER: {mismatched} frames with mismatched pixels");
        GC.KeepAlive(images);
        return mismatched == 0 ? 0 : 1;
    }

    /// <summary>Polls like the capture worker: a new picture before each look (so WGC has a new frame), then Capture.</summary>
    private static int Poll(WgcCapturer wgc, ReplayWindow window, GameWindowInfo info, ScreenFrame[] images, int polls, double hz, AppRect origin, string label)
    {
        var acquire = new List<double>();
        var copy = new List<double>();
        var total = new List<double>();
        int reused = 0, bad = 0;
        var progress = new Progress(label + ". polls", polls);
        for (int i = 0; i < polls; i++)
        {
            var image = images[(i + 1) % 2];
            window.Show(image, origin.X, origin.Y);
            Thread.Sleep(TimeSpan.FromSeconds(1 / hz));
            ReplayWindow.Pump();
            var t0 = Stopwatch.GetTimestamp();
            var frame = wgc.Capture(info);
            var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (wgc.LastFrameReused)
            {
                reused++;
                // The old picture is still in the buffer; show it again next round.
                continue;
            }
            if (Diff(frame, image) != 0) bad++;
            acquire.Add(wgc.LastTimings.AcquireMs);
            copy.Add(wgc.LastTimings.CopyMs);
            total.Add(ms);
            progress.Step(i + 1);
        }
        Console.WriteLine(Inv($"   ({label}: {total.Count} new frames, {reused} reused; capture median {Median(total):F2} ms (acquire {Median(acquire):F2}, copy {Median(copy):F2}), p90 {P90(total):F2} ms; {bad} frames differ) [measured under full CPU load]"));
        return bad;
    }

    /// <summary>The long-idle trim (CapturePipeline.MaybeTrim: Trim + one background gen2), then a capture that re-creates it all.</summary>
    private static int TrimStep(WgcCapturer wgc, ReplayWindow window, GameWindowInfo info, ScreenFrame image, AppRect origin, MemReport report)
    {
        wgc.Trim();
        GC.Collect(2, GCCollectionMode.Forced, blocking: false);
        Thread.Sleep(500);
        Row(report, "f2. Trim() (long idle)");
        window.Show(image, origin.X, origin.Y);
        var (ms, diff) = CaptureOnce(wgc, info, image);
        Console.WriteLine(Inv($"   (capture after Trim {ms:F0} ms: new device + session + first frame; {diff} pixels differ) [measured under full CPU load]"));
        Row(report, "f3. capture after Trim");
        return diff == 0 ? 0 : 1;
    }

    /// <summary>
    /// One capture, timed and compared. Its own non-inlined method so the CapturedFrame (and with it the capturer's buffer)
    /// is not kept alive by a JIT temporary of the caller through the memory rows that follow.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (double Ms, long Diff) CaptureOnce(WgcCapturer wgc, GameWindowInfo info, ScreenFrame image)
    {
        var sw = Stopwatch.StartNew();
        var frame = wgc.Capture(info);
        var ms = sw.Elapsed.TotalMilliseconds;
        return (ms, Diff(frame, image));
    }

    private static void Row(MemReport report, string step)
    {
        report.Row(step);
        Console.WriteLine("   " + GpuMemory.Line());
    }

    // ------------------------------------------------------------------ sessions

    /// <summary>
    ///   MemProbe sessions [--cycles 20] [--width 1919] [--height 1079] [--cold]
    /// How long a NEW capture session takes to deliver its first frame (Suspend, then Capture), the way the verifier
    /// captures every replay: the app's default 1 s timeout against a 10 s one, interleaved.
    /// </summary>
    public static int Sessions(Dictionary<string, string> opts)
    {
        PerMonitorDpi();
        int cycles = Int(opts, "cycles", 20), width = Int(opts, "width", 1919), height = Int(opts, "height", 1079);
        var cold = opts.ContainsKey("cold"); // Trim instead of Suspend: every capture also creates a new device
        var image = Pattern(width, height, 0);
        var origin = Dpi.PrimaryMonitorRect();
        using var window = new ReplayWindow();
        window.Show(image, origin.X, origin.Y);
        var rect = new AppRect(origin.X, origin.Y, width, height);
        var info = new GameWindowInfo(window.Hwnd, Environment.ProcessId, rect, rect, false, false, false, false);
        var capturers = new[] { (Name: "1 s (app)", Wgc: new WgcCapturer()), (Name: "10 s", Wgc: new WgcCapturer(firstFrameTimeout: TimeSpan.FromSeconds(10))) };
        var times = capturers.Select(_ => new List<double>()).ToArray();
        var failures = new int[capturers.Length];
        var progress = new Progress("sessions", cycles);
        for (int c = 0; c < cycles; c++)
        {
            for (int k = 0; k < capturers.Length; k++)
            {
                int i = (k + c) % capturers.Length;
                var wgc = capturers[i].Wgc;
                if (cold) wgc.Trim();
                else wgc.Suspend();
                ReplayWindow.Pump();
                var t0 = Stopwatch.GetTimestamp();
                try
                {
                    var (ms, diff) = CaptureOnce(wgc, info, image);
                    times[i].Add(ms);
                    if (diff != 0) Console.WriteLine($"   {capturers[i].Name}: {diff} pixels differ");
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
                {
                    failures[i]++;
                    Console.WriteLine(Inv($"   {capturers[i].Name}: {ex.GetType().Name} after {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms: {ex.Message}"));
                    // What the app's next poll does: the same capturer again, no Suspend/Trim in between.
                    var t1 = Stopwatch.GetTimestamp();
                    try
                    {
                        var (_, diff) = CaptureOnce(wgc, info, image);
                        Console.WriteLine(Inv($"     next capture: ok after {Stopwatch.GetElapsedTime(t1).TotalMilliseconds:F0} ms, {diff} pixels differ"));
                    }
                    catch (Exception again) when (again is TimeoutException or InvalidOperationException)
                    {
                        Console.WriteLine(Inv($"     next capture: {again.GetType().Name} after {Stopwatch.GetElapsedTime(t1).TotalMilliseconds:F0} ms"));
                    }
                }
            }
            progress.Step(c + 1);
        }
        Console.WriteLine($"new {(cold ? "device + session" : "session")} -> first frame, {width}x{height}, {cycles} cycles [measured under CPU load]:");
        for (int i = 0; i < capturers.Length; i++)
        {
            var t = times[i];
            Console.WriteLine(Inv($"   timeout {capturers[i].Name,-9}: {failures[i]} failed; capture median {Median(t):F0} ms, p90 {P90(t):F0} ms, max {(t.Count > 0 ? t.Max() : 0):F0} ms, over 1000 ms {t.Count(v => v > 1000)}"));
            capturers[i].Wgc.Dispose();
        }
        return 0;
    }

    // ------------------------------------------------------------------ devices

    public static int Devices()
    {
        var adapters = GpuMemory.Adapters();
        var runs = new List<string[]>
        {
            new[] { "--adapter", "default", "--flags", "0x20" },  // what WgcCapturer does today
            new[] { "--adapter", "default", "--flags", "0x28" },  // + PREVENT_INTERNAL_THREADING_OPTIMIZATIONS
            new[] { "--adapter", "default", "--flags", "0x21" },  // + SINGLETHREADED (measure only)
            new[] { "--adapter", "default", "--flags", "0x20", "--levels", "11_0" },
            new[] { "--adapter", "default", "--flags", "0x20", "--levels", "10_0" },
        };
        foreach (var a in adapters.Where(a => !a.Software)) runs.Add(["--adapter", a.Index.ToString(CultureInfo.InvariantCulture), "--flags", "0x20"]);
        runs.Add(["--adapter", "default", "--flags", "0x20", "--driver", "warp"]);
        var progress = new Progress("devices", runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!, ["device", .. runs[i]]) { RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Console.Write(output);
            }
            progress.Step(i + 1);
        }
        return 0;
    }

    public static unsafe int Device(Dictionary<string, string> opts)
    {
        string adapterArg = opts.GetValueOrDefault("adapter") ?? "default";
        uint flags = Convert.ToUInt32(opts.GetValueOrDefault("flags") ?? "0x20", 16);
        string levels = opts.GetValueOrDefault("levels") ?? "default";
        bool warp = opts.GetValueOrDefault("driver") == "warp";
        _ = GpuMemory.Total(); // loads dxgi.dll and builds the factory once, before the baseline
        var baseline = Snapshot();

        var sw = Stopwatch.StartNew();
        var (device, context) = CreateDevice(adapterArg, flags, levels, warp);
        double createMs = sw.Elapsed.TotalMilliseconds;
        var created = Snapshot();
        long luid = GpuMemory.DeviceAdapterLuid(device);
        string adapterName = GpuMemory.Adapters().FirstOrDefault(a => a.Luid == luid)?.Name ?? "?";

        // The WinRT wrapper the frame pool takes (as WgcCapturer.EnsureDevice).
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in IID_IDXGIDevice, out var dxgi));
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
        Marshal.Release(dxgi);
        var wrapped = Snapshot();

        // One 4K staging texture, copied into once and mapped (what a capture adds on top of the device).
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = 3840, Height = 2160, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
            Usage = D3D11_USAGE_STAGING, CPUAccessFlags = D3D11_CPU_ACCESS_READ,
        };
        Marshal.ThrowExceptionForHR(CreateTexture2D(device, desc, out var staging));
        Marshal.ThrowExceptionForHR(Map(context, staging, out _));
        Unmap(context, staging);
        var withStaging = Snapshot();

        // D3D11 destroys released resources lazily, on a later flush of the immediate context.
        Release(ref staging);
        Thread.Sleep(300);
        var stagingReleased = Snapshot();
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)Slot(context, 111))(context); // ID3D11DeviceContext::Flush
        Thread.Sleep(300);
        var flushed = Snapshot();
        Marshal.Release(inspectable);
        Release(ref context);
        Release(ref device);
        Thread.Sleep(300);
        var released = Snapshot();

        sw.Restart();
        (device, context) = CreateDevice(adapterArg, flags, levels, warp);
        double recreateMs = sw.Elapsed.TotalMilliseconds;
        var recreated = Snapshot();
        Release(ref context);
        Release(ref device);

        Console.WriteLine(
            Inv($"device {adapterName} ({adapterArg}{(warp ? ", WARP" : "")}) flags 0x{flags:X2} levels {levels}: create {createMs:F0} ms -> private +{Mb(created.Private - baseline.Private):F1} MB (ws +{Mb(created.Ws - baseline.Ws):F1}, gpu +{Mb(created.Gpu - baseline.Gpu):F1}); ") +
            Inv($"WinRT wrapper +{Mb(wrapped.Private - created.Private):F1}; 4K staging+map +{Mb(withStaging.Private - wrapped.Private):F1} (gpu +{Mb(withStaging.Gpu - wrapped.Gpu):F1}); ") +
            Inv($"staging released {Mb(stagingReleased.Private - withStaging.Private):+0.0;-0.0} (gpu {Mb(stagingReleased.Gpu - withStaging.Gpu):+0.0;-0.0}), then Flush {Mb(flushed.Private - withStaging.Private):+0.0;-0.0} (gpu {Mb(flushed.Gpu - withStaging.Gpu):+0.0;-0.0}); ") +
            Inv($"all released: private +{Mb(released.Private - baseline.Private):F1} over baseline; recreate {recreateMs:F1} ms -> +{Mb(recreated.Private - baseline.Private):F1}"));
        return 0;
    }

    private static unsafe (IntPtr Device, IntPtr Context) CreateDevice(string adapterArg, uint flags, string levels, bool warp)
    {
        IntPtr adapter = adapterArg == "default" ? IntPtr.Zero : GpuMemory.AdapterPointer(int.Parse(adapterArg, CultureInfo.InvariantCulture));
        try
        {
            int driver = warp ? D3D_DRIVER_TYPE_WARP : adapter != IntPtr.Zero ? 0 /* UNKNOWN: required with an explicit adapter */ : D3D_DRIVER_TYPE_HARDWARE;
            int[] fl = levels switch { "11_0" => [0xb000], "10_0" => [0xa000], _ => [] };
            fixed (int* p = fl)
            {
                Marshal.ThrowExceptionForHR(D3D11CreateDevice(adapter, driver, IntPtr.Zero, flags, fl.Length == 0 ? IntPtr.Zero : (IntPtr)p, (uint)fl.Length,
                    D3D11_SDK_VERSION, out var device, out _, out var context));
                return (device, context);
            }
        }
        finally
        {
            if (adapter != IntPtr.Zero) Marshal.Release(adapter);
        }
    }

    private readonly record struct Usage(long Private, long Ws, long Gpu);

    private static Usage Snapshot()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var p = Process.GetCurrentProcess();
        return new Usage(p.PrivateMemorySize64, p.WorkingSet64, GpuMemory.Total());
    }

    // ------------------------------------------------------------------ poolage

    /// <summary>
    /// A small window that redraws as fast as it can (a frame counter in its first pixel), captured through a raw WGC
    /// session the way WgcCapturer keeps one: the FrameArrived handler holds only the newest frame. Each poll reads the
    /// counter of the frame it gets and how long ago the window showed it.
    /// </summary>
    public static int PoolAge(Dictionary<string, string> opts)
    {
        PerMonitorDpi();
        int polls = Int(opts, "polls", 12);
        double hz = double.Parse(opts.GetValueOrDefault("hz") ?? "3", CultureInfo.InvariantCulture);
        using var animator = new Animator(64, 64);
        foreach (int buffers in new[] { 2, 1, 2, 1 })
        {
            var ages = new List<double>();
            int none = 0;
            using (var session = new RawSession(animator.Hwnd, buffers))
            {
                Thread.Sleep(300); // first frames
                var progress = new Progress($"poolage {buffers} buffer(s)", polls);
                for (int i = 0; i < polls; i++)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(1 / hz));
                    int counter = session.ReadLatestCounter();
                    long now = Stopwatch.GetTimestamp();
                    if (counter < 0)
                    {
                        none++;
                    }
                    else
                    {
                        ages.Add(Stopwatch.GetElapsedTime(animator.ShownAt(counter), now).TotalMilliseconds);
                    }
                    progress.Step(i + 1);
                }
                Console.WriteLine(Inv($"pool {buffers} buffer(s): {ages.Count} polls got a frame, {none} none; frame age at poll median {Median(ages):F0} ms, max {(ages.Count > 0 ? ages.Max() : 0):F0} ms; window redraws ~{animator.Rate:F0}/s; frames arrived {session.Arrived} [measured under full CPU load]"));
            }
        }
        return 0;
    }

    /// <summary>A 64x64 ReplayWindow on its own thread, redrawn in a loop with a counter in pixel (0, 0).</summary>
    private sealed class Animator : IDisposable
    {
        private readonly Thread _thread;
        private readonly long[] _shownAt = new long[1 << 20];
        private readonly ManualResetEventSlim _ready = new();
        private volatile bool _stop;
        private int _counter;
        private readonly long _start = Stopwatch.GetTimestamp();

        public Animator(int w, int h)
        {
            _thread = new Thread(() =>
            {
                using var window = new ReplayWindow();
                var bytes = new byte[w * h * 4];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 4 == 3 ? 255 : 128);
                var origin = Dpi.PrimaryMonitorRect();
                Hwnd = window.Hwnd;
                window.Show(ScreenFrame.FromPacked(bytes, w, h), origin.X + 10, origin.Y + 10);
                _ready.Set();
                while (!_stop)
                {
                    int c = _counter + 1;
                    bytes[0] = (byte)c;
                    bytes[1] = (byte)(c >> 8);
                    bytes[2] = (byte)(c >> 16);
                    window.Show(ScreenFrame.FromPacked(bytes, w, h), origin.X + 10, origin.Y + 10);
                    _shownAt[c & (_shownAt.Length - 1)] = Stopwatch.GetTimestamp();
                    Volatile.Write(ref _counter, c);
                    Thread.Sleep(5);
                }
            }) { IsBackground = true, Name = "poolage-animator" };
            _thread.Start();
            _ready.Wait();
        }

        public IntPtr Hwnd { get; private set; }

        public long ShownAt(int counter) => _shownAt[counter & (_shownAt.Length - 1)];

        public double Rate => Volatile.Read(ref _counter) / Stopwatch.GetElapsedTime(_start).TotalSeconds;

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }
    }

    /// <summary>A bare WGC session on a window: device, item, free-threaded pool of N buffers, newest frame kept.</summary>
    private sealed class RawSession : IDisposable
    {
        private IntPtr _d3d, _ctx, _device, _item, _pool, _session, _staging, _latest;
        private readonly Lock _lock = new();
        private long _token;
        private int _arrived;

        public RawSession(IntPtr hwnd, int buffers)
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out _d3d, out _, out _ctx));
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_d3d, in IID_IDXGIDevice, out var dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
            Marshal.Release(dxgi);
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in IID_IDirect3DDevice, out _device));
            Marshal.Release(inspectable);
            var interop = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", IID_IGraphicsCaptureItemInterop);
            Marshal.ThrowExceptionForHR(CreateCaptureItem(interop, false, hwnd, out _item));
            Marshal.Release(interop);
            _pool = CreateFreeThreadedFramePool(_device, PixelFormatB8G8R8A8UIntNormalized, buffers, GetItemSize(_item));
            _token = AddFrameArrived(_pool, sender =>
            {
                var f = TryGetNextFrame(sender);
                if (f == IntPtr.Zero) return;
                IntPtr old;
                lock (_lock)
                {
                    old = _latest;
                    _latest = f;
                }
                Interlocked.Increment(ref _arrived);
                CloseAndRelease(ref old);
            });
            _session = CreateCaptureSession(_pool, _item);
            SetCursorCaptureEnabled(_session, false);
            try
            {
                SetBorderRequired(_session, false);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException)
            {
                // border stays; it is outside the pixel we read
            }
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
                Usage = D3D11_USAGE_STAGING, CPUAccessFlags = D3D11_CPU_ACCESS_READ,
            };
            Marshal.ThrowExceptionForHR(CreateTexture2D(_d3d, desc, out _staging));
            StartCapture(_session);
        }

        public int Arrived => Volatile.Read(ref _arrived);

        /// <summary>Takes the newest frame (as WgcCapturer.TakeLatestFrame), reads its pixel (0, 0), releases it; -1 = none.</summary>
        public unsafe int ReadLatestCounter()
        {
            IntPtr f;
            lock (_lock)
            {
                f = _latest;
                _latest = IntPtr.Zero;
            }
            if (f == IntPtr.Zero) return -1;
            try
            {
                var surface = GetFrameSurface(f);
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in IID_IDirect3DDxgiInterfaceAccess, out var access));
                Marshal.Release(surface);
                Marshal.ThrowExceptionForHR(GetDxgiInterface(access, IID_ID3D11Texture2D, out var texture));
                Marshal.Release(access);
                CopySubresourceRegion(_ctx, _staging, texture, new D3D11_BOX { Right = 1, Bottom = 1, Back = 1 });
                Marshal.Release(texture);
                Marshal.ThrowExceptionForHR(Map(_ctx, _staging, out var mapped));
                var p = (byte*)mapped.Data;
                int counter = p[0] | (p[1] << 8) | (p[2] << 16);
                Unmap(_ctx, _staging);
                return counter;
            }
            finally
            {
                CloseAndRelease(ref f);
            }
        }

        public void Dispose()
        {
            IntPtr latest;
            lock (_lock)
            {
                latest = _latest;
                _latest = IntPtr.Zero;
            }
            CloseAndRelease(ref latest);
            CloseAndRelease(ref _session);
            RemoveFrameArrived(_pool, _token);
            CloseAndRelease(ref _pool);
            Release(ref _item);
            Release(ref _staging);
            Release(ref _device);
            Release(ref _ctx);
            Release(ref _d3d);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Physical pixels everywhere, like the app (its manifest says PerMonitorV2); else a scaled monitor stretches the test window.</summary>
    private static void PerMonitorDpi()
    {
        if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) Console.WriteLine("(could not switch to per-monitor DPI awareness)");
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>Opaque test picture, a different value in most pixels; <paramref name="variant"/> flips red.</summary>
    internal static ScreenFrame Pattern(int w, int h, int variant)
    {
        var bytes = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                bytes[i] = (byte)x;
                bytes[i + 1] = (byte)y;
                bytes[i + 2] = (byte)(((x >> 8) | ((y >> 8) << 4)) ^ (variant * 0x80));
                bytes[i + 3] = 255;
            }
        }
        return ScreenFrame.FromPacked(bytes, w, h);
    }

    private static long Diff(Sts2Overlay.App.Integration.CapturedFrame f, ScreenFrame src)
        => f.Width != src.Width || f.Height != src.Height
            ? long.MaxValue
            : FrameCompare.Compare(src.Bgra.Span, src.Stride, f.Bgra, f.Stride, src.Width, src.Height).Mismatched;

    private static int Int(Dictionary<string, string> o, string key, int fallback)
        => o.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    internal static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.Order().ToList();
        return s[s.Count / 2];
    }

    internal static double P90(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.Order().ToList();
        return s[Math.Min(s.Count - 1, s.Count * 9 / 10)];
    }

    private static double Mb(long bytes) => bytes / 1048576.0;

    internal static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>"label 7/20 (35 %), ETA 14:03:12" lines, at most every 2 s plus the last one.</summary>
internal sealed class Progress(string label, int total)
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private TimeSpan _last = TimeSpan.FromSeconds(-10);

    public void Step(int done)
    {
        if (done < total && _sw.Elapsed - _last < TimeSpan.FromSeconds(2)) return;
        _last = _sw.Elapsed;
        var eta = DateTime.Now + TimeSpan.FromTicks(_sw.Elapsed.Ticks / Math.Max(1, done) * (total - done));
        Console.WriteLine($"   {label} {done}/{total} ({done * 100 / Math.Max(1, total)} %), ETA {eta:HH:mm:ss}");
    }
}
