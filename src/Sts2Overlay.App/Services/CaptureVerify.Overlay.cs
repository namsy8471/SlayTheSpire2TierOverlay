using System.IO;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Vision;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Part 3 of <c>--verify-capture</c>: the overlay can be visible to OBS / screenshots without ever ending up in the
/// frames we recognise (<see cref="OverlayCaptureVisibility"/>).
///
/// A "game" window (a real capture, or a pattern) and an "overlay" window (a magenta block over its middle, NOT hidden
/// from capture) are shown together.
/// 1. Behind all windows (the user sees nothing): the app's capturer, window capture of the game, must return the game
///    pixels exactly although the overlay covers part of it, and the overlay must stay visible to capture.
/// 2. With <c>--verify-gdi-on-top</c> (both windows ~1 s on top, in the top-left corner): a plain GDI grab DOES show the
///    magenta block (so OBS display capture does too), the GDI fallback with the visibility hook never does (the block
///    is hidden and DWM has recomposed before BitBlt), and after a window capture the block is visible again. Ten
///    rounds of switching, so every frame right after a switch is checked.
/// </summary>
internal static partial class CaptureVerify
{
    private const int OverlayRounds = 10;

    private static int OverlayCheck(AppOptions options, Action<string> output)
    {
        var src = OverlaySource(options);
        var block = SolidFrame(Math.Min(300, src.Width / 2), Math.Min(120, src.Height / 2), 255, 0, 255);
        var origin = Interop.Dpi.PrimaryMonitorRect();
        int x = origin.X + 60, y = origin.Y + 60;
        int ox = x + (src.Width - block.Width) / 2, oy = y + (src.Height - block.Height) / 2;
        var rect = new Integration.PixelRect(x, y, src.Width, src.Height);
        var blockArea = (long)block.Width * block.Height;

        var failures = 0;
        using var game = new ReplayWindow();
        using var overlay = new ReplayWindow();
        var visibility = new OverlayCaptureVisibility(showInCapture: true);
        visibility.Register(overlay.Hwnd);
        var info = new GameWindowInfo(game.Hwnd, Environment.ProcessId, rect, rect, false, false, false, false);
        using var app = FrameCapturerFactory.Create("auto", visibility.BeforeCapture);

        // ---- 1. behind everything: window capture ignores the overlay ----
        ShowStack(game, src, x, y, overlay, block, ox, oy, topmost: false);
        var behindOk = 0;
        for (var i = 0; i < 3; i++)
        {
            app.Suspend(); // new WGC session: its first frame is the window's current content
            var f = app.Capture(info);
            ReplayWindow.Pump();
            var s = FrameCompare.Compare(src.Bgra.Span, src.Stride, f.Bgra, f.Stride, src.Width, src.Height);
            if (app.Name == "wgc" && s.Mismatched == 0) behindOk++;
        }
        var shownToCapture = AffinityOf(overlay.Hwnd) == WDA_NONE;
        var ok = behindOk == 3 && shownToCapture && !visibility.IsExcluded;
        failures += ok ? 0 : 1;
        output(CaptureBench.Inv($"{(ok ? "" : "FAIL ")}overlay over the game window: window capture returned the game pixels exactly {behindOk}/3, overlay {(shownToCapture ? "visible to screen capture (OBS/screenshots)" : "HIDDEN from screen capture")}"));

        if (!options.VerifyGdiOnTop)
        {
            output("overlay vs gdi fallback: skipped (needs both windows briefly ON TOP of the game; opt in with --verify-gdi-on-top)");
            return failures;
        }

        // ---- 2. on top: plain GDI sees the overlay, the hooked GDI fallback never does ----
        using var plainGdi = new ScreenCapturer();
        using var gdiFallback = new FallbackFrameCapturer(null, new ScreenCapturer()) { BeforeCapture = visibility.BeforeCapture };
        int seenWhenShown = 0, cleanGdi = 0, cleanWindow = 0, shownAgain = 0;
        try
        {
            ShowStack(game, src, x, y, overlay, block, ox, oy, topmost: true);
            for (var round = 0; round < OverlayRounds; round++)
            {
                // The overlay is visible to capture here (start, or after the previous window capture).
                Thread.Sleep(60);
                ReplayWindow.Pump();
                var plain = plainGdi.Capture(rect);
                var ps = FrameCompare.Compare(src.Bgra.Span, src.Stride, plain.Bgra, plain.Stride, src.Width, src.Height);
                if (ps.Mismatched == blockArea) seenWhenShown++;
                if (round > 0 && AffinityOf(overlay.Hwnd) == WDA_NONE) shownAgain++;

                // Switch to the GDI fallback: hide + DwmFlush happen inside, right before BitBlt. On another thread, as
                // in the app (capture worker hides windows owned by the UI thread, which is blocked meanwhile).
                FrameCompare.Stats gs = default;
                var worker = new Thread(() =>
                {
                    var g = gdiFallback.Capture(info);
                    gs = FrameCompare.Compare(src.Bgra.Span, src.Stride, g.Bgra, g.Stride, src.Width, src.Height);
                });
                worker.Start();
                if (!worker.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("gdi fallback capture on the worker thread hung");
                if (gs.Mismatched == 0 && visibility.IsExcluded) cleanGdi++;
                else output(CaptureBench.Inv($"  round {round}: gdi fallback frame has {gs.Mismatched} overlay pixels"));

                // And back to window capture: the overlay is shown again.
                app.Suspend();
                var w = app.Capture(info);
                var ws = FrameCompare.Compare(src.Bgra.Span, src.Stride, w.Bgra, w.Stride, src.Width, src.Height);
                if (ws.Mismatched == 0 && !visibility.IsExcluded) cleanWindow++;
                ReplayWindow.Pump();
            }
        }
        finally
        {
            ShowStack(game, src, x, y, overlay, block, ox, oy, topmost: false);
        }
        ok = seenWhenShown == OverlayRounds && cleanGdi == OverlayRounds && cleanWindow == OverlayRounds && shownAgain == OverlayRounds - 1;
        failures += ok ? 0 : 1;
        output(CaptureBench.Inv($"{(ok ? "" : "FAIL ")}overlay vs gdi fallback, {OverlayRounds} switches: plain GDI saw the overlay {seenWhenShown}/{OverlayRounds} (= OBS display capture shows it), gdi fallback frames clean {cleanGdi}/{OverlayRounds}, window capture clean {cleanWindow}/{OverlayRounds}, overlay shown again after window capture {shownAgain}/{OverlayRounds - 1}"));
        return failures;
    }

    /// <summary>Game window at the bottom (or top) of the z-order with the overlay directly above it.</summary>
    private static void ShowStack(ReplayWindow game, ScreenFrame src, int x, int y, ReplayWindow overlay, ScreenFrame block, int ox, int oy, bool topmost)
    {
        if (topmost)
        {
            game.Show(src, x, y, topmost: true);
            overlay.Show(block, ox, oy, topmost: true); // the later topmost call lands above
        }
        else
        {
            overlay.Show(block, ox, oy);
            game.Show(src, x, y); // HWND_BOTTOM: now under the overlay, both under everything else
        }
        Thread.Sleep(50);
        ReplayWindow.Pump();
    }

    /// <summary>A real saved capture (960x540 crop) when there is one, else a deterministic pattern.</summary>
    private static ScreenFrame OverlaySource(AppOptions options)
    {
        var dir = options.VerifyDir ?? AppPaths.CapturesDir;
        var file = Directory.Exists(dir) ? Directory.GetFiles(dir, "reward_*.png").Order(StringComparer.Ordinal).FirstOrDefault() : null;
        if (file is not null)
        {
            var img = ImageLoader.Load(file);
            if (img.Width >= 960 && img.Height >= 540) return Crop(img, 0, 0, 960, 540);
        }
        var bytes = new byte[960 * 540 * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var p = i / 4;
            bytes[i] = (byte)(p % 960 / 4);
            bytes[i + 1] = (byte)(p / 960 / 3);
            bytes[i + 2] = (byte)(p * 7 % 200);
            bytes[i + 3] = 255;
        }
        return ScreenFrame.FromPacked(bytes, 960, 540);
    }

    private static ScreenFrame SolidFrame(int w, int h, byte r, byte g, byte b)
    {
        var bytes = new byte[w * h * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = b;
            bytes[i + 1] = g;
            bytes[i + 2] = r;
            bytes[i + 3] = 255;
        }
        return ScreenFrame.FromPacked(bytes, w, h);
    }

    private static uint AffinityOf(IntPtr hwnd) => GetWindowDisplayAffinity(hwnd, out var a) ? a : uint.MaxValue;
}
