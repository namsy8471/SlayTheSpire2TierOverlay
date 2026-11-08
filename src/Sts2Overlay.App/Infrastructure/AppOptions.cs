using Sts2Overlay.App.Integration.Fakes;

namespace Sts2Overlay.App.Infrastructure;

/// <summary>
/// Command line:
/// <code>
/// Sts2Overlay.exe [--demo [reward|shop|other]] [--pack &lt;dir&gt;] [--catalog &lt;file&gt;] [--show-in-capture] [--selftest]
///                 [--prepare [--force]] [--game-dir &lt;dir&gt;]
/// Sts2Overlay.exe --bench-capture [frames] [--bench-target &lt;process|pid&gt;] [--bench-out &lt;dir&gt;] [--bench-soak &lt;seconds&gt;]
/// Sts2Overlay.exe --verify-capture [count] [--verify-dir &lt;dir&gt;] [--verify-gdi-on-top] [--bench-target &lt;process|pid&gt;] [--bench-out &lt;dir&gt;]
/// </code>
/// <c>--prepare</c> only generates data/local (card art, localization, catalog) from the installed game and exits;
/// <c>--force</c> rebuilds even when manifest.json says it is up to date.
/// <c>--demo</c> uses fake game-state/recognition so the UI can be seen without the game.
/// <c>--show-in-capture</c> keeps our windows visible to screenshots (for checking the layout).
/// <c>--bench-capture</c> times GDI and Windows.Graphics.Capture on the game window, writes a report and two PNGs, and exits
/// (see <see cref="Services.CaptureBench"/>). <c>--verify-capture</c> checks that both backends give identical pixels and
/// recognition results (see <see cref="Services.CaptureVerify"/>); exit code 0 = pass.
/// </summary>
public sealed record AppOptions
{
    public DemoScreen? Demo { get; init; }
    public string? PackDir { get; init; }
    public string? CatalogPath { get; init; }
    public bool ShowInCapture { get; init; }

    /// <summary>Saves one capture + runs one recognition after start-up, logs the result and exits (checks the capture path).</summary>
    public bool SelfTest { get; init; }

    /// <summary>Headless: prepare local resources, print progress to the console, exit (see <see cref="Services.PrepareCommand"/>).</summary>
    public bool Prepare { get; init; }

    /// <summary>With <see cref="Prepare"/>: rebuild even when up to date.</summary>
    public bool Force { get; init; }

    public string? GameDir { get; init; }
    /// <summary>Run the capture benchmark instead of the overlay.</summary>
    public bool BenchCapture { get; init; }

    public int BenchFrames { get; init; } = 60;

    /// <summary>Process name or id whose main window is captured; null = the game.</summary>
    public string? BenchTarget { get; init; }

    /// <summary>Report/PNG folder; null = the captures folder.</summary>
    public string? BenchOut { get; init; }

    /// <summary>Run the capture accuracy check (WGC vs GDI vs saved captures) instead of the overlay.</summary>
    public bool VerifyCapture { get; init; }

    /// <summary>How many saved reward/shop captures the accuracy check replays.</summary>
    public int VerifyCount { get; init; } = 12;

    /// <summary>Folder with saved captures to replay; null = the captures folder.</summary>
    public string? VerifyDir { get; init; }

    /// <summary>Also compare GDI and WGC on one static image shown ON TOP of everything for ~0.15 s (click-through).</summary>
    public bool VerifyGdiOnTop { get; init; }

    /// <summary>After timing: run the app's real capturer (WGC with GDI fallback) at 3 Hz for this many seconds.</summary>
    public int BenchSoakSeconds { get; init; }

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var o = new AppOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--demo":
                    var screen = Next();
                    o = o with
                    {
                        Demo = screen?.ToLowerInvariant() switch
                        {
                            "shop" => DemoScreen.Shop,
                            "other" => DemoScreen.Other,
                            _ => DemoScreen.Reward,
                        },
                    };
                    break;
                case "--pack":
                    o = o with { PackDir = Next() };
                    break;
                case "--catalog":
                    o = o with { CatalogPath = Next() };
                    break;
                case "--show-in-capture":
                    o = o with { ShowInCapture = true };
                    break;
                case "--selftest":
                    o = o with { SelfTest = true };
                    break;
                case "--prepare":
                    o = o with { Prepare = true };
                    break;
                case "--force":
                    o = o with { Force = true };
                    break;
                case "--game-dir":
                    o = o with { GameDir = Next() };
                    break;
                case "--bench-capture":
                    o = o with { BenchCapture = true };
                    if (int.TryParse(i + 1 < args.Count ? args[i + 1] : null, out var frames) && frames > 0)
                    {
                        o = o with { BenchFrames = frames };
                        i++;
                    }
                    break;
                case "--bench-target":
                    o = o with { BenchTarget = Next() };
                    break;
                case "--bench-out":
                    o = o with { BenchOut = Next() };
                    break;
                case "--verify-capture":
                    o = o with { VerifyCapture = true };
                    if (int.TryParse(i + 1 < args.Count ? args[i + 1] : null, out var count) && count > 0)
                    {
                        o = o with { VerifyCount = count };
                        i++;
                    }
                    break;
                case "--verify-gdi-on-top":
                    o = o with { VerifyGdiOnTop = true };
                    break;
                case "--verify-dir":
                    o = o with { VerifyDir = Next() };
                    break;
                case "--bench-soak":
                    o = o with { BenchSoakSeconds = int.TryParse(Next(), out var soak) ? soak : 30 };
                    break;
                default:
                    AppLog.Warn($"unknown argument '{a}'");
                    break;
            }
        }
        return o;
    }
}
