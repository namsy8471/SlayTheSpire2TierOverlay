using System.Diagnostics;
using Sts2Overlay.App.Interop;
using Sts2Overlay.App.Services;
using Sts2Overlay.Vision;
using Xunit.Abstractions;
using AppRect = Sts2Overlay.App.Integration.PixelRect;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// Leak check for the real Windows.Graphics.Capture path: starts and stops a capture session hundreds of times against a
/// small test window parked behind every other window, checks every frame pixel for pixel, and compares the process
/// handle count, private bytes and live WinRT delegate objects before and after. Opt-in (it needs a desktop session and
/// opens a window): set STS2_WGC_SOAK to the number of cycles, e.g. 200. Never runs while the game runs.
/// </summary>
[Collection(ComObjectsCollection.Name)]
public class WgcSessionSoakTests(ITestOutputHelper output)
{
    private const int Width = 320;
    private const int Height = 240;

    [WgcSoakFact]
    public void Session_restarts_release_everything()
    {
        var cycles = int.Parse(Environment.GetEnvironmentVariable(WgcSoakFactAttribute.Variable)!, System.Globalization.CultureInfo.InvariantCulture);
        var src = Pattern();
        using var window = new ReplayWindow();
        var origin = Dpi.PrimaryMonitorRect();
        window.Show(src, origin.X + 20, origin.Y + 20);
        var rect = new AppRect(origin.X + 20, origin.Y + 20, Width, Height);
        var info = new GameWindowInfo(window.Hwnd, Environment.ProcessId, rect, rect, false, false, false, false);
        var handlersAtStart = ComEventHandler.LiveObjects;

        // Phase 1: one capturer (one D3D device), a new session every cycle, as the app does after Suspend.
        using (var wgc = new WgcCapturer())
        {
            for (var i = 0; i < 20; i++) Cycle(wgc, info, src); // warm-up: device, JIT, DWM
            var before = Snapshot();
            for (var i = 1; i <= cycles; i++)
            {
                Cycle(wgc, info, src);
                if (i % 100 == 0 || i == cycles)
                {
                    var now = Snapshot();
                    output.WriteLine($"session cycles {i}/{cycles}: handles {now.Handles}, private bytes {now.PrivateBytes / 1048576.0:0.0} MB, live delegate objects {ComEventHandler.LiveObjects}");
                }
            }
            wgc.Suspend();
            Report("session start/stop", cycles, before, Snapshot());
        }

        // Phase 2: a new capturer per cycle: device, session and teardown through Dispose (Reset).
        var devCycles = Math.Max(1, cycles / 4);
        var before2 = Snapshot();
        for (var i = 1; i <= devCycles; i++)
        {
            using (var wgc = new WgcCapturer()) Cycle(wgc, info, src);
            if (i % 25 == 0 || i == devCycles) output.WriteLine($"capturer cycles {i}/{devCycles}");
        }
        Report("capturer create/dispose", devCycles, before2, Snapshot());

        // Every handler object handed to WGC must have been released by WGC (WGC may let go a moment later).
        var sw = Stopwatch.StartNew();
        while (ComEventHandler.LiveObjects != handlersAtStart && sw.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(20);
        output.WriteLine($"live WinRT delegate objects: {ComEventHandler.LiveObjects} (at start {handlersAtStart})");
        Assert.Equal(handlersAtStart, ComEventHandler.LiveObjects);
    }

    private static void Cycle(WgcCapturer wgc, GameWindowInfo info, ScreenFrame src)
    {
        wgc.Suspend(); // the next Capture starts a new session
        var frame = wgc.Capture(info);
        ReplayWindow.Pump();
        Assert.Equal("wgc", wgc.Name);
        Assert.False(wgc.LastFrameReused);
        var stats = FrameCompare.Compare(src.Bgra.Span, src.Stride, frame.Bgra, frame.Stride, Width, Height);
        Assert.Equal(0, stats.Mismatched);
    }

    private readonly record struct Usage(int Handles, long PrivateBytes);

    private static Usage Snapshot()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(200); // let WGC finish releasing on its threads
        using var p = Process.GetCurrentProcess();
        return new Usage(p.HandleCount, p.PrivateMemorySize64);
    }

    private void Report(string phase, int count, Usage before, Usage after)
    {
        output.WriteLine($"{phase} x{count}: handles {before.Handles} -> {after.Handles} ({after.Handles - before.Handles:+0;-0;0}), "
            + $"private bytes {before.PrivateBytes / 1048576.0:0.0} -> {after.PrivateBytes / 1048576.0:0.0} MB ({(after.PrivateBytes - before.PrivateBytes) / 1048576.0:+0.0;-0.0;0.0})");
        // Windows itself keeps one "ALPC Port" handle per capture session started while no other session is alive in the
        // process (seen on Windows 11 10.0.26300 with a bare session created straight through the ABI, and absent while a
        // second session stays open); the C#/WinRT build kept pool "Section" handles instead. That costs at most one
        // handle per cycle, which we cannot release, so the bound allows exactly that plus noise (thread pool, driver
        // caches). Anything we leak per session (a texture, an event, a device) adds more and fails it.
        Assert.True(after.Handles - before.Handles < count + 30, $"{phase}: handle count grew {before.Handles} -> {after.Handles}");
        Assert.True(after.PrivateBytes - before.PrivateBytes < 64L * 1048576, $"{phase}: private bytes grew {before.PrivateBytes} -> {after.PrivateBytes}");
    }

    /// <summary>A gradient with a distinct value per pixel row/column, fully opaque.</summary>
    private static ScreenFrame Pattern()
    {
        var bytes = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width + x) * 4;
                bytes[i] = (byte)x;
                bytes[i + 1] = (byte)y;
                bytes[i + 2] = (byte)(x ^ y);
                bytes[i + 3] = 255;
            }
        }
        return ScreenFrame.FromPacked(bytes, Width, Height);
    }
}

/// <summary>Runs only when STS2_WGC_SOAK is set and the game is not running (xunit 2 skips via the attribute).</summary>
internal sealed class WgcSoakFactAttribute : FactAttribute
{
    public const string Variable = "STS2_WGC_SOAK";

    public WgcSoakFactAttribute()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(Variable), out var n) || n <= 0)
        {
            Skip = $"set {Variable} to a cycle count (opens a small window and runs real capture sessions)";
        }
        else if (GameRunning())
        {
            Skip = "the game is running";
        }
        else if (!WgcCapturer.IsSupported())
        {
            Skip = "Windows.Graphics.Capture is not supported here";
        }
    }

    private static bool GameRunning()
    {
        var procs = Process.GetProcessesByName("SlayTheSpire2");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
}
