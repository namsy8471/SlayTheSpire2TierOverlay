using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.Vision;
using static Sts2Overlay.App.Interop.NativeMethods;
using VisionCard = Sts2Overlay.Vision.RecognizedCard;
using VisionRect = Sts2Overlay.Vision.PixelRect;

namespace Sts2Overlay.App.Services;

/// <summary>
/// <c>--verify-capture [count]</c>: proves the WGC backend returns exactly the pixels GDI would, and that recognition on
/// them is identical. Writes <c>verify_capture.txt</c>; exit code 0 = pass.
///
/// 1. Live: on the running game (must be in front, or GDI sees other windows) grab WGC, GDI, WGC. Pixels equal in both
///    WGC grabs are "static"; on those, GDI must equal WGC exactly. Windows stacked above the game (other overlays) are
///    left out: GDI sees them, window capture by design does not. Also checks frame size, stride and that no border or
///    cursor shows up (both would be static WGC pixels that differ from GDI).
/// 2. Replay: real reward/shop captures are shown 1:1 in a layered window BEHIND everything (WGC still sees it, the user
///    does not), captured through WGC, and compared with the PNG byte for byte; the raw recogniser result (kind, ids,
///    upgrade flags, scores, bounds) must be identical too. An odd-sized crop exercises padded row pitch.
/// </summary>
internal static class CaptureVerify
{
    private const int LiveRounds = 5;

    public static int Run(AppOptions options)
    {
        CaptureBench.AttachParentConsole();
        var lines = new List<string>();
        void Out(string s)
        {
            lines.Add(s);
            AppLog.Info("verify: " + s);
            Console.WriteLine(s);
        }

        var outDir = Path.GetFullPath(options.BenchOut ?? AppPaths.CapturesDir);
        Directory.CreateDirectory(outDir);
        if (!WgcCapturer.IsSupported())
        {
            Out("wgc not supported on this system: nothing to verify");
            return 1;
        }
        var recognizer = LoadRecognizer(outDir, Out);
        var failures = 0;
        failures += LiveCheck(options, recognizer, outDir, Out, out var liveGdi);
        failures += ReplayCheck(options, recognizer, liveGdi, Out);
        Out(failures == 0 ? "VERIFY PASS" : $"VERIFY FAIL ({failures} failed checks)");
        var report = Path.Combine(outDir, "verify_capture.txt");
        File.WriteAllLines(report, lines);
        Console.WriteLine($"report: {report}");
        return failures;
    }

    // ---- 1. live game ------------------------------------------------------------------------------------

    private static int LiveCheck(AppOptions options, CardRecognizer? recognizer, string outDir, Action<string> output, out CapturedFrame? liveGdi)
    {
        liveGdi = null;
        var processName = options.BenchTarget ?? GameWindowTracker.ProcessName;
        var target = CaptureBench.FindWindow(processName);
        if (target is null)
        {
            output($"live: no window for '{processName}', skipped");
            return 0;
        }
        output(CaptureBench.Inv($"live: {processName} window {target.WindowRect}, capture {target.CaptureRect}, foreground {target.IsForeground}"));
        if (!target.IsForeground)
        {
            output("live: target not in front (GDI would capture the windows covering it), skipped");
            return 0;
        }

        var failures = 0;
        using var gdi = new ScreenCapturer();
        using var wgc = new WgcCapturer();
        for (var round = 1; round <= LiveRounds; round++)
        {
            var t = CaptureBench.Refresh(target);
            var sample = SampleAroundGdi(t, wgc, gdi);
            GetCursorPos(out var cursor);
            var g = sample.Gdi;
            var reference = sample.Reference;
            liveGdi ??= g;

            var expected = t.CaptureRect;
            var sizeOk = new[] { reference, g }.All(f => f.Width == expected.Width && f.Height == expected.Height && f.Stride == f.Width * 4 && f.ScreenRect == expected);
            var occluders = WindowsAbove(t.Hwnd, expected).Select(r => r.Offset(-expected.X, -expected.Y)).ToArray();
            var stats = FrameCompare.CompareMasked(g.Bgra, reference.Bgra, sample.Stable, g.Width, g.Height, g.Stride, occluders);
            var controlStats = FrameCompare.CompareMasked(sample.Control.Bgra, reference.Bgra, sample.Stable, g.Width, g.Height, g.Stride, occluders);
            var (bestDx, bestDy, atZero, best) = FrameCompare.BestOffset(g.Bgra, reference.Bgra, sample.Stable, g.Width, g.Height, g.Stride, 2);
            // Exact: GDI matches every unchanged pixel. Animated: the control WGC frame also misses some, so the scene changed
            // in between the sampled frames and this round can't judge pixels (size and alignment still count).
            var aligned = sizeOk && bestDx == 0 && bestDy == 0;
            var verdict = !aligned ? "FAIL" : stats.Mismatched == 0 ? "exact" : controlStats.Mismatched > 0 ? "animated" : "FAIL";
            var ok = verdict != "FAIL";
            output(CaptureBench.Inv($"live #{round} {verdict}: size {(sizeOk ? "ok" : "WRONG")} {g.Width}x{g.Height} stride {g.Stride}; {sample.Frames} WGC frames sampled around the {sample.GdiMs:0} ms GDI grab; unchanged pixels {stats.Compared / (double)(g.Width * g.Height) * 100:0.0}% compared: GDI {stats.Mismatched} differ (max {stats.MaxDiff}), control WGC frame {controlStats.Mismatched} differ (max {controlStats.MaxDiff}); alignment best offset ({bestDx},{bestDy}) [{best} vs {atZero} mismatches at (0,0) on a sample]; cursor at {cursor.X},{cursor.Y}; excluded {occluders.Length} window(s) above the game{(occluders.Length > 0 ? ": " + string.Join(" ", occluders) : "")}"));
            if (!ok)
            {
                failures++;
                FramePng.Save(g, Path.Combine(outDir, $"verify_live{round}_gdi.png"));
                FramePng.Save(reference, Path.Combine(outDir, $"verify_live{round}_wgc.png"));
            }

            if (round == 1 && recognizer is not null)
            {
                // The two frames are not the same instant (the game animates), so this is informational; the strict
                // recognition check is the replay.
                var rg = recognizer.Recognize(ToScreenFrame(g));
                var rw = recognizer.Recognize(ToScreenFrame(reference));
                var recognition = Canonical(rg) == Canonical(rw) ? "identical" : Summary(rg) == Summary(rw) ? "same cards, scores differ (animation)" : "DIFFER";
                output($"live recognition: gdi {Summary(rg)} | wgc {Summary(rw)} | {recognition}");
            }
            Thread.Sleep(100);
        }
        return failures;
    }

    private sealed record LiveSample(CapturedFrame Reference, CapturedFrame Gdi, CapturedFrame Control, bool[] Stable, int Frames, double GdiMs);

    /// <summary>
    /// Grabs WGC frames continuously on a helper thread from before until after one GDI grab. A pixel counts as stable
    /// when it had the same value in every one of those frames, i.e. it did not change while GDI was reading the screen.
    /// </summary>
    private static LiveSample SampleAroundGdi(GameWindowInfo t, WgcCapturer wgc, ScreenCapturer gdi)
    {
        CapturedFrame? reference = null;
        bool[]? stable = null;
        CapturedFrame? control = null;
        var frames = 0;
        var stop = false;
        var takeControl = false;
        Exception? error = null;
        var sampler = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    var f = wgc.Capture(t);
                    if (reference is null)
                    {
                        reference = Snapshot(f);
                        stable = new bool[f.Width * f.Height];
                        Array.Fill(stable, true);
                    }
                    else if (wgc.LastFrameReused)
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                    else if (Volatile.Read(ref takeControl) && control is null)
                    {
                        // Control: one WGC frame kept OUT of the mask and judged exactly like the GDI frame. Whatever it
                        // gets "wrong" is animation the sampling missed, not a capture error.
                        control = Snapshot(f);
                    }
                    else
                    {
                        FrameCompare.ClearChanged(stable!, reference.Bgra, f.Bgra, f.Width, f.Height, f.Stride);
                    }
                    Interlocked.Increment(ref frames);
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }) { IsBackground = true, Name = "verify-wgc-sampler" };
        sampler.Start();

        bool WaitFor(Func<bool> condition)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(3)) return false;
                Thread.Sleep(1);
            }
            return true;
        }

        WaitFor(() => Volatile.Read(ref frames) >= 3 || error is not null);
        Volatile.Write(ref takeControl, true);
        WaitFor(() => Volatile.Read(ref frames) >= 7 || error is not null);
        var t0 = Stopwatch.GetTimestamp();
        var g = Snapshot(gdi.Capture(t));
        var gdiMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var after = Volatile.Read(ref frames);
        WaitFor(() => Volatile.Read(ref frames) >= after + 3 || error is not null);
        Volatile.Write(ref stop, true);
        sampler.Join();
        if (error is not null) throw new InvalidOperationException("WGC sampler failed", error);
        if (reference is null || stable is null || control is null) throw new TimeoutException("no WGC frames");
        return new LiveSample(reference, g, control, stable, frames, gdiMs);
    }

    private static CapturedFrame Snapshot(CapturedFrame f)
        => new((byte[])f.Bgra.Clone(), f.Width, f.Height, f.Stride, f.ScreenRect, f.At); // capturers reuse their buffer

    /// <summary>Visible windows above <paramref name="target"/> in z-order that overlap <paramref name="area"/>.</summary>
    private static List<Integration.PixelRect> WindowsAbove(IntPtr target, Integration.PixelRect area)
    {
        var result = new List<Integration.PixelRect>();
        var own = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == target) return false; // EnumWindows goes top to bottom: everything after this is below the game
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (Interop.D3D11Interop.DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out var cloak, sizeof(int)) == 0 && cloak != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own) return true;
            if (Interop.D3D11Interop.DwmGetWindowAttribute(hwnd, Interop.D3D11Interop.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
            {
                GetWindowRect(hwnd, out r);
            }
            var hit = Integration.PixelRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom).Intersect(area);
            if (!hit.IsEmpty) result.Add(hit);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // ---- 2. replay real captures ---------------------------------------------------------------------------

    private static int ReplayCheck(AppOptions options, CardRecognizer? recognizer, CapturedFrame? liveGdi, Action<string> output)
    {
        var dir = options.VerifyDir ?? AppPaths.CapturesDir;
        var rewards = options.VerifyCount / 2;
        var files = Pick(dir, "reward_*.png", rewards, recognizer)
            .Concat(Pick(dir, "shop_*.png", options.VerifyCount - rewards, recognizer))
            .Concat(Pick(dir, "manual_*.png", 4, recognizer: null))
            .ToList();
        output($"replay: {files.Count} saved captures from {dir}{(liveGdi is not null ? " + the live 4K GDI frame" : "")}, shown behind all windows and captured with WGC");

        var failures = 0;
        using var window = new ReplayWindow();
        using var wgc = new WgcCapturer();
        var origin = Interop.Dpi.PrimaryMonitorRect();
        if (liveGdi is not null)
        {
            failures += CheckOne($"live GDI frame {liveGdi.Width}x{liveGdi.Height}", ToScreenFrame(liveGdi), origin.X, origin.Y, window, wgc, recognizer, output);
        }
        foreach (var file in files)
        {
            var src = ImageLoader.Load(file);
            failures += CheckOne(Path.GetFileName(file), src, origin.X, origin.Y, window, wgc, recognizer, output);
        }
        if (files.Count == 0) return failures;

        // Odd width: 1001 px * 4 = 4004 bytes per row, which the driver pads (row pitch > width * 4).
        var first = ImageLoader.Load(files[0]);
        var crop = Crop(first, 123, 457, 1001, 601);
        failures += CheckOne("odd-size crop 1001x601", crop, origin.X + 50, origin.Y + 50, window, wgc, recognizer: null, output);
        failures += ResizeAndClose(first, origin.X, origin.Y, output);
        if (options.VerifyGdiOnTop)
        {
            failures += GdiParity(crop, origin.X + 40, origin.Y + 40, window, wgc, output);
        }
        else
        {
            output("gdi vs wgc on a static window: skipped (needs a window briefly ON TOP of the game; opt in with --verify-gdi-on-top)");
        }
        return failures;
    }

    /// <summary>
    /// The app's capturer (WGC, GDI fallback) on a window behind the game that changes size between captures (same
    /// session: exercises the frame-pool Recreate path) and is then destroyed (exercises GraphicsCaptureItem.Closed and
    /// the fallback). Pixels must match the source exactly at every size.
    /// </summary>
    private static int ResizeAndClose(ScreenFrame full, int x, int y, Action<string> output)
    {
        var failures = 0;
        var window = new ReplayWindow();
        using var capturer = FrameCapturerFactory.Create("auto");
        int[][] sizes = [[800, 600], [1001, 601], [1280, 720], [640, 480], [1919, 1079]];
        foreach (var size in sizes)
        {
            var src = Crop(full, 0, 0, Math.Min(size[0], full.Width), Math.Min(size[1], full.Height));
            window.Show(src, x, y);
            var rect = new Integration.PixelRect(x, y, src.Width, src.Height);
            var frame = capturer.Capture(new GameWindowInfo(window.Hwnd, Environment.ProcessId, rect, rect, false, false, false, false));
            ReplayWindow.Pump();
            var stats = FrameCompare.Compare(src.Bgra.Span, src.Stride, frame.Bgra, frame.Stride, src.Width, src.Height);
            var ok = capturer.Name == "wgc" && frame.Width == src.Width && frame.Height == src.Height && stats.Mismatched == 0;
            failures += ok ? 0 : 1;
            output(CaptureBench.Inv($"{(ok ? "" : "FAIL ")}resize to {src.Width}x{src.Height} (same session): backend {capturer.Name}, {stats.Mismatched} pixels differ"));
        }

        var last = new Integration.PixelRect(x, y, Math.Min(1919, full.Width), Math.Min(1079, full.Height));
        var hwnd = window.Hwnd;
        window.Dispose(); // window closed while the session runs
        Thread.Sleep(100);
        ReplayWindow.Pump();
        try
        {
            capturer.Capture(new GameWindowInfo(hwnd, Environment.ProcessId, last, last, false, false, false, false));
            var ok = capturer.Name == "gdi";
            failures += ok ? 0 : 1;
            output($"{(ok ? "" : "FAIL ")}window closed: capture fell back to {capturer.Name} for that frame (WGC session dropped, see app.log)");
        }
        catch (Exception ex)
        {
            failures++;
            output($"FAIL window closed: capture threw {ex.GetType().Name}: {ex.Message}");
        }
        return failures;
    }

    private static int CheckOne(string name, ScreenFrame src, int x, int y, ReplayWindow window, WgcCapturer wgc, CardRecognizer? recognizer, Action<string> output)
    {
        window.Show(src, x, y);
        // A new session's first frame is the window's current content, so the frame can't be from the previous image.
        wgc.Suspend();
        var rect = new Integration.PixelRect(x, y, src.Width, src.Height);
        var info = new GameWindowInfo(window.Hwnd, Environment.ProcessId, rect, rect, false, false, false, false);
        var frame = wgc.Capture(info);
        ReplayWindow.Pump();

        var sizeOk = frame.Width == src.Width && frame.Height == src.Height && frame.ScreenRect == rect;
        var stats = sizeOk
            ? FrameCompare.Compare(src.Bgra.Span, src.Stride, frame.Bgra, frame.Stride, src.Width, src.Height)
            : default;
        var line = new StringBuilder(CaptureBench.Inv($"replay {name}: size {(sizeOk ? "ok" : $"WRONG {frame.Width}x{frame.Height}")}, {stats.Mismatched} pixels differ (max {stats.MaxDiff}), row pitch path {wgc.LastRowPitch} for {src.Width * 4} bytes/row"));
        var ok = sizeOk && stats.Mismatched == 0;
        if (recognizer is not null && sizeOk)
        {
            var rs = recognizer.Recognize(src);
            var rw = recognizer.Recognize(ToScreenFrame(frame));
            var same = Canonical(rs) == Canonical(rw);
            ok &= same;
            line.Append($"; recognition {(same ? "identical" : "DIFFERS")}: {Summary(rw)}");
            if (!same) line.Append($" (png: {Summary(rs)})");
        }
        output((ok ? "" : "FAIL ") + line);
        return ok ? 0 : 1;
    }

    /// <summary>
    /// GDI and WGC on the SAME static content: the image is shown on top (click-through, ~0.15 s, top-left corner) so
    /// GDI can see it, grabbed by both backends, and both are compared with the source and with each other.
    /// </summary>
    private static int GdiParity(ScreenFrame src, int x, int y, ReplayWindow window, WgcCapturer wgc, Action<string> output)
    {
        var rect = new Integration.PixelRect(x, y, src.Width, src.Height);
        using var gdi = new ScreenCapturer();
        CapturedFrame g, w;
        try
        {
            window.Show(src, x, y, topmost: true);
            Thread.Sleep(120); // let DWM compose it on screen
            ReplayWindow.Pump();
            g = Snapshot(gdi.Capture(rect));
            wgc.Suspend();
            w = Snapshot(wgc.Capture(new GameWindowInfo(window.Hwnd, Environment.ProcessId, rect, rect, false, false, false, false)));
        }
        finally
        {
            window.Show(src, x, y, topmost: false);
        }
        var gs = FrameCompare.Compare(src.Bgra.Span, src.Stride, g.Bgra, g.Stride, src.Width, src.Height);
        var ws = FrameCompare.Compare(src.Bgra.Span, src.Stride, w.Bgra, w.Stride, src.Width, src.Height);
        var gw = FrameCompare.Compare(g.Bgra, g.Stride, w.Bgra, w.Stride, src.Width, src.Height);
        var ok = gs.Mismatched == 0 && ws.Mismatched == 0 && gw.Mismatched == 0;
        output(CaptureBench.Inv($"{(ok ? "" : "FAIL ")}gdi vs wgc on the same static window {src.Width}x{src.Height}: gdi-vs-source {gs.Mismatched} differ (max {gs.MaxDiff}), wgc-vs-source {ws.Mismatched} (max {ws.MaxDiff}), gdi-vs-wgc {gw.Mismatched} (max {gw.MaxDiff})"));
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Up to <paramref name="count"/> files spread evenly over the collection (different runs, characters, screens),
    /// preferring ones the recogniser classifies as reward/shop (many captures are taken a moment before the screen opens).
    /// </summary>
    private static List<string> Pick(string dir, string pattern, int count, CardRecognizer? recognizer)
    {
        if (count <= 0 || !Directory.Exists(dir)) return [];
        var all = Directory.GetFiles(dir, pattern).Order(StringComparer.Ordinal).ToArray();
        var candidates = Spread(all, recognizer is null ? count : count * 6);
        if (recognizer is null) return candidates.Take(count).ToList();
        var recognised = candidates.Where(f => recognizer.Recognize(ImageLoader.Load(f)).Kind != Sts2Overlay.Vision.ScreenKind.Other).Take(count).ToList();
        return recognised.Concat(candidates.Except(recognised)).Take(count).ToList();
    }

    private static string[] Spread(string[] all, int count)
        => all.Length <= count ? all : Enumerable.Range(0, count).Select(i => all[(int)((i + 0.5) * all.Length / count)]).ToArray();

    private static ScreenFrame Crop(ScreenFrame f, int x, int y, int w, int h)
    {
        var bytes = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
        {
            f.Bgra.Span.Slice((y + row) * f.Stride + x * 4, w * 4).CopyTo(bytes.AsSpan(row * w * 4));
        }
        return ScreenFrame.FromPacked(bytes, w, h);
    }

    // ---- recognition -------------------------------------------------------------------------------------

    private static CardRecognizer? LoadRecognizer(string outDir, Action<string> output)
    {
        var artRoot = Path.Combine(AppPaths.LocalDataDir, "pck", "card_art");
        if (!Directory.Exists(artRoot))
        {
            output($"recognition: no card art at {artRoot}, recognition comparison skipped");
            return null;
        }
        var catalog = AppPaths.CommittedCatalogFile;
        // Cache next to the report, so the verifier never writes into data/local.
        var db = CardArtDatabase.LoadOrBuild(artRoot, File.Exists(catalog) ? catalog : null, Path.Combine(outDir, "card-art.verify.bin"), _ => { });
        return new CardRecognizer(db);
    }

    private static ScreenFrame ToScreenFrame(CapturedFrame f) => new(f.Bgra, f.Width, f.Height, f.Stride);

    private static string Summary(ScreenRecognition r)
        => $"{r.Kind} [{string.Join(", ", r.Cards.Select(c => c.CardId + (c.Upgraded ? "+" : "")))}]";

    /// <summary>Everything the recogniser decided, with floats round-trippable, so equal strings mean equal results.</summary>
    private static string Canonical(ScreenRecognition r)
        => $"{r.Kind}|{r.Gate.RewardLikely}|{r.Gate.ShopLikely}|{r.Gate.Detail}|{r.Detail}|{string.Join(";", r.Slots.Select(CanonicalSlot))}|{string.Join(";", r.Cards.Select(CanonicalSlot))}";

    private static string CanonicalSlot(VisionCard s) => string.Create(CultureInfo.InvariantCulture,
        $"{s.Slot}/{s.SlotKind}/{s.CardId}/{s.Upgraded}/{s.Score:R}/{s.Margin:R}/{s.Accepted}/{s.AmbiguousWith}/{s.UpgradeEvidence:R}/{s.UpgradeKnown}/{s.NodeScale:R}/{CanonicalRect(s.ArtBounds)}/{CanonicalRect(s.CardBounds)}/{CanonicalRect(s.PriceBounds)}/{s.Price}/{string.Join("+", s.Candidates.Select(c => c.ToString()))}");

    private static string CanonicalRect(VisionRect? b) => b is { } v ? $"{v.X},{v.Y},{v.Width},{v.Height}" : "-";

    // ---- native ------------------------------------------------------------------------------------------

    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}

/// <summary>Exact pixel comparison (B, G, R; alpha ignored: GDI leaves it 0, WGC 255).</summary>
internal static class FrameCompare
{
    public readonly record struct Stats(long Compared, long Mismatched, int MaxDiff, Integration.PixelRect MismatchBounds);

    public static Stats Compare(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB, int width, int height)
    {
        var acc = new Accumulator();
        for (var y = 0; y < height; y++)
        {
            var ra = a.Slice(y * strideA, width * 4);
            var rb = b.Slice(y * strideB, width * 4);
            for (var x = 0; x < width; x++)
            {
                acc.Add(x, y, PixelDiff(ra, rb, x * 4));
            }
        }
        return acc.Result();
    }

    /// <summary>Clears <paramref name="stable"/> wherever <paramref name="frame"/> differs from <paramref name="reference"/> (B, G, R).</summary>
    public static void ClearChanged(bool[] stable, byte[] reference, byte[] frame, int width, int height, int stride)
    {
        for (var y = 0; y < height; y++)
        {
            var r = MemoryMarshal.Cast<byte, uint>(reference.AsSpan(y * stride, width * 4));
            var f = MemoryMarshal.Cast<byte, uint>(frame.AsSpan(y * stride, width * 4));
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (((r[x] ^ f[x]) & 0x00FFFFFF) != 0) stable[row + x] = false;
            }
        }
    }

    /// <summary>Compares <paramref name="test"/> with <paramref name="reference"/> where <paramref name="stable"/> is set, outside <paramref name="exclude"/>.</summary>
    public static Stats CompareMasked(byte[] test, byte[] reference, bool[] stable, int width, int height, int stride, IReadOnlyList<Integration.PixelRect> exclude)
    {
        var acc = new Accumulator();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!stable[y * width + x] || Excluded(exclude, x, y)) continue;
                acc.Add(x, y, PixelDiff(test, reference, y * stride + x * 4));
            }
        }
        return acc.Result();
    }

    /// <summary>
    /// Shifts <paramref name="test"/> by up to ±<paramref name="range"/> px against <paramref name="reference"/> (stable
    /// pixels, every 7th row and column) and returns the shift with the fewest mismatches. (0, 0) = the crop is aligned.
    /// </summary>
    public static (int Dx, int Dy, long AtZero, long Best) BestOffset(byte[] test, byte[] reference, bool[] stable, int width, int height, int stride, int range)
    {
        var (bx, by, bm) = (0, 0, long.MaxValue);
        long atZero = 0;
        for (var dy = -range; dy <= range; dy++)
        {
            for (var dx = -range; dx <= range; dx++)
            {
                long mismatches = 0;
                for (var y = range; y < height - range; y += 7)
                {
                    for (var x = range; x < width - range; x += 7)
                    {
                        if (!stable[y * width + x]) continue;
                        if (PixelDiff(test, reference, (y + dy) * stride + (x + dx) * 4, y * stride + x * 4) != 0) mismatches++;
                    }
                }
                if (dx == 0 && dy == 0) atZero = mismatches;
                if (mismatches < bm || (mismatches == bm && dx == 0 && dy == 0)) (bx, by, bm) = (dx, dy, mismatches);
            }
        }
        return (bx, by, atZero, bm);
    }

    private static bool Excluded(IReadOnlyList<Integration.PixelRect> rects, int x, int y)
    {
        foreach (var r in rects)
        {
            if (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom) return true;
        }
        return false;
    }

    private static int PixelDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int i) => PixelDiff(a, b, i, i);

    private static int PixelDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int ia, int ib)
        => Math.Max(Math.Abs(a[ia] - b[ib]), Math.Max(Math.Abs(a[ia + 1] - b[ib + 1]), Math.Abs(a[ia + 2] - b[ib + 2])));

    private struct Accumulator()
    {
        private long _compared;
        private long _mismatched;
        private int _max;
        private int _l = int.MaxValue, _t = int.MaxValue, _r = int.MinValue, _b = int.MinValue;

        public void Add(int x, int y, int diff)
        {
            _compared++;
            if (diff == 0) return;
            _mismatched++;
            _max = Math.Max(_max, diff);
            _l = Math.Min(_l, x);
            _t = Math.Min(_t, y);
            _r = Math.Max(_r, x + 1);
            _b = Math.Max(_b, y + 1);
        }

        public readonly Stats Result()
            => new(_compared, _mismatched, _max, _mismatched == 0 ? default : Integration.PixelRect.FromLtrb(_l, _t, _r, _b));
    }
}

/// <summary>
/// A borderless layered window showing a BGRA image 1:1 (UpdateLayeredWindow copies our pixels as they are, no scaling),
/// parked at the bottom of the z-order. Uses the system "Static" class, so no window procedure of our own is needed.
/// </summary>
internal sealed class ReplayWindow : IDisposable
{
    private const uint WS_POPUP = 0x80000000;
    private const uint ULW_ALPHA = 0x02;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    public ReplayWindow()
    {
        // WS_EX_TRANSPARENT: clicks go through to the game during the short moment the window is on top.
        Hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "Static", "Sts2Overlay capture verify", WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed");
    }

    public IntPtr Hwnd { get; }

    public void Show(ScreenFrame frame, int x, int y, bool topmost = false)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = frame.Width,
            biHeight = -frame.Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };
        var dib = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) throw new System.ComponentModel.Win32Exception("CreateDIBSection failed");
            unsafe
            {
                var dst = new Span<byte>((void*)bits, frame.Width * frame.Height * 4);
                PixelCopy.CopyRows(frame.Bgra.Span, frame.Stride, dst, frame.Width * 4, frame.Width * 4, frame.Height);
                for (var i = 3; i < dst.Length; i += 4) dst[i] = 255; // opaque: premultiplied == straight colour
            }
            var old = SelectObject(memDc, dib);
            var dstPt = new POINT { X = x, Y = y };
            var size = new SIZE { cx = frame.Width, cy = frame.Height };
            var srcPt = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(Hwnd, screenDc, ref dstPt, ref size, memDc, ref srcPt, 0, ref blend, ULW_ALPHA))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "UpdateLayeredWindow failed");
            }
            SelectObject(memDc, old);
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        // Show and set the z-order in ONE call: showing first would put the window above the game for a moment.
        // HWND_BOTTOM also clears a previous topmost state.
        SetWindowPos(Hwnd, topmost ? HWND_TOPMOST : HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Pump();
    }

    /// <summary>Handles pending messages so Windows doesn't mark the window "not responding" (and ghost it) during the check.</summary>
    public static void Pump()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    public void Dispose() => DestroyWindow(Hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src,
        uint key, ref BLENDFUNCTION blend, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);
}
