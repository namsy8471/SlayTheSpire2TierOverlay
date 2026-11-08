using Sts2Overlay.App.Integration.Fakes;

namespace Sts2Overlay.App.Infrastructure;

/// <summary>
/// Command line:
/// <code>
/// Sts2Overlay.exe [--demo [reward|shop|other]] [--pack &lt;dir&gt;] [--catalog &lt;file&gt;] [--show-in-capture] [--selftest]
///                 [--prepare [--force]] [--game-dir &lt;dir&gt;]
/// </code>
/// <c>--prepare</c> only generates data/local (card art, localization, catalog) from the installed game and exits;
/// <c>--force</c> rebuilds even when manifest.json says it is up to date.
/// <c>--demo</c> uses fake game-state/recognition so the UI can be seen without the game.
/// <c>--show-in-capture</c> keeps our windows visible to screenshots (for checking the layout).
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
                default:
                    AppLog.Warn($"unknown argument '{a}'");
                    break;
            }
        }
        return o;
    }
}
