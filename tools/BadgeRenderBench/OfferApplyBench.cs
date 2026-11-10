using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.App.Views;

namespace Sts2Overlay.Tools.BadgeRenderBench;

/// <summary>
/// A new offer applied the way AppController.ApplyRecognition applies it, with the real panel AND the real badge layer
/// shown in this one process (WPF lays out and renders every window on the one UI thread). SHOWS windows, so it refuses
/// to run while the game runs. Modes:
/// <list type="bullet">
/// <item><c>sync</c>: panel offer + tab, then the badges, in one pass (the app before the panel offer was deferred).</item>
/// <item><c>defer-bg</c>: the badges, then the panel offer posted at Background priority.</item>
/// <item><c>defer-render</c>: the badges, then the panel offer posted at Background from the next render pass
/// (CompositionTarget.Rendering), so it can never be laid out in the badges' pass (the app's DeferredOffer post).</item>
/// </list>
/// "-busy" = the badge layer drew a frame just before (positions moved, as while the player hovers a card), so WPF may
/// still be waiting for that frame's present when the new offer comes.
/// Times from the apply call (ms): <c>render pass starts</c> = first CompositionTarget.Rendering (raised at the start
/// of a render pass, before its layout), <c>badges drawn</c> = a Loaded-priority callback queued after the badge change
/// (runs after that pass's layout and render: the app's perf "painted"), <c>panel drawn</c> = the same after the panel
/// got its rows (in sync mode the same pass as the badges). "panel in badge pass" counts samples whose panel rows were
/// written before the first render pass, i.e. laid out together with the badges.
/// </summary>
internal static class OfferApplyBench
{
    private static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;

    private sealed record Sample(double Render, double Loaded, double Panel, bool PanelFirst);

    public static int Run(string artDir, int rounds)
    {
        var app = new LiveApp(() => RunAsync(artDir, rounds)) { Resources = Program.AppResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        return app.Run();
    }

    private static async Task<int> RunAsync(string artDir, int rounds)
    {
        var screen = Screen.Primary();
        var vm = new PanelViewModel { SelectedTab = PanelTab.Offer, IsCollapsed = false };
        var panel = new PanelWindow(vm, new AppSettings(), new OverlayCaptureVisibility(false));
        // The panel saves its position into the user's settings.json 0.6 s after it moves: stop that timer every time.
        var saveTimer = (DispatcherTimer)typeof(PanelWindow).GetField("_saveTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
        panel.LocationChanged += (_, _) => saveTimer.Stop();
        panel.Show();
        var badges = new BadgeWindow(new OverlayCaptureVisibility(false));
        new WindowInteropHelper(badges).EnsureHandle();
        badges.SetLayerRect(screen.Rect);
        badges.Show();

        var ids = Directory.Exists(artDir)
            ? Directory.EnumerateFiles(artDir, "*.webp", SearchOption.AllDirectories)
                .Where(f => !Path.GetDirectoryName(f)!.EndsWith("beta", StringComparison.Ordinal))
                .Select(f => "CARD." + Path.GetFileNameWithoutExtension(f).ToUpperInvariant()).Distinct().ToArray()
            : [];
        Console.WriteLine($"primary monitor {screen.Width}x{screen.Height} px, {screen.Dpi} dpi, {screen.RefreshHz} Hz; {ids.Length} portraits; rounds {rounds}");

        var modes = new[] { "sync", "defer-bg", "defer-render" };
        var kinds = new[] { "reward", "shop" };
        var keys = (from m in modes from k in kinds from b in new[] { "", "-busy" } select (Mode: m, Kind: k + b)).ToArray();
        var results = keys.ToDictionary(k => k, _ => new List<Sample>());
        var load = new CpuLoad();
        var loads = new List<double>();
        var rng = new Random(5);
        var next = 0;
        var started = Stopwatch.StartNew();
        try
        {
            for (var r = 0; r < rounds; r++)
            {
                load.Reset();
                foreach (var key in keys.OrderBy(_ => rng.Next()))
                {
                    if (GameGuard.IsGameRunning())
                    {
                        Console.Error.WriteLine("the game started: stopping");
                        return 3;
                    }
                    var shop = key.Kind.StartsWith("shop", StringComparison.Ordinal);
                    var content = Content.For(shop ? "full-shop" : "full-reward", screen);
                    var n = shop ? 7 : 3;
                    // The previous screen: its offer still in the tab (stale), no badges.
                    vm.Offer = Offer(n, ids, artDir, ref next) with { IsStale = true };
                    badges.Clear();
                    await Settle(panel);
                    var offer = Offer(n, ids, artDir, ref next);
                    if (key.Kind.EndsWith("-busy", StringComparison.Ordinal))
                    {
                        // A badge frame of the same screen a moment ago (hovered card lifted): apply right after it.
                        badges.ShowBadges(content.Specs(5), content.Skip, content.Removal, false);
                        await NextRender();
                    }
                    results[key].Add(await TimeApply(key.Mode, vm, badges, content, offer));
                }
                loads.Add(load.Percent());
                var done = r + 1;
                var eta = DateTime.Now.AddSeconds(started.Elapsed.TotalSeconds / done * (rounds - done));
                Console.WriteLine($"  {done}/{rounds} rounds ({100.0 * done / rounds:0}%), machine CPU {loads[^1]:0}%, eta {eta:HH:mm:ss}");
            }
        }
        finally
        {
            badges.Close();
            panel.Close();
        }

        Console.WriteLine();
        Console.WriteLine($"machine CPU during the run: median {Stats.Median(loads):0}% (min {loads.Min():0}, max {loads.Max():0})");
        Console.WriteLine("offer applied -> (ms, medians; p90 in brackets):");
        Console.WriteLine($"  {"mode",-13} {"kind",-12} {"render pass starts",20} {"badges drawn (Loaded)",24} {"panel drawn (Loaded)",20} {"panel in badge pass",20}");
        foreach (var key in keys)
        {
            var xs = results[key];
            string M(Func<Sample, double> f) => FormattableString.Invariant($"{Stats.Median(xs.Select(f)),6:0.0} [{Stats.Pct(xs.Select(f), 0.9),6:0.0}]");
            Console.WriteLine($"  {key.Mode,-13} {key.Kind,-12} {M(x => x.Render),20} {M(x => x.Loaded),24} {M(x => x.Panel),20} {xs.Count(x => x.PanelFirst),14}/{xs.Count}");
        }
        return 0;
    }

    private static async Task<Sample> TimeApply(string mode, PanelViewModel vm, BadgeWindow badges, Content content, OfferSectionVm offer)
    {
        long render = 0, panelWrite = 0, panelDone = 0;
        EventHandler onRendering = (_, _) =>
        {
            if (render == 0) render = Stopwatch.GetTimestamp();
        };
        void WritePanel()
        {
            panelWrite = Stopwatch.GetTimestamp();
            vm.Offer = offer;
            vm.SelectedTab = PanelTab.Offer;
            // After the pass that lays the rows out and renders them (Loaded runs after Render).
            _ = badges.Dispatcher.BeginInvoke(() => panelDone = Stopwatch.GetTimestamp(), DispatcherPriority.Loaded);
        }

        var t0 = Stopwatch.GetTimestamp();
        switch (mode)
        {
            case "sync": // ApplyRecognition before: _vm.Offer, _vm.SelectedTab, then _badges.ShowBadges
                WritePanel();
                badges.ShowBadges(content.Specs(0), content.Skip, content.Removal, false);
                break;
            case "defer-bg":
                badges.ShowBadges(content.Specs(0), content.Skip, content.Removal, false);
                _ = badges.Dispatcher.BeginInvoke(WritePanel, DispatcherPriority.Background);
                break;
            case "defer-render":
                badges.ShowBadges(content.Specs(0), content.Skip, content.Removal, false);
                AfterNextRender(badges.Dispatcher, WritePanel);
                break;
            default: throw new ArgumentException(mode);
        }
        CompositionTarget.Rendering += onRendering;
        var loaded = await badges.Dispatcher.InvokeAsync(Stopwatch.GetTimestamp, DispatcherPriority.Loaded);
        // Wait for the panel's own pass (deferred modes), at most a second.
        for (var i = 0; i < 100 && panelDone == 0; i++) await Task.Delay(10);
        CompositionTarget.Rendering -= onRendering;
        return new Sample(Ms(t0, render), Ms(t0, loaded), panelDone == 0 ? double.NaN : Ms(t0, panelDone), panelWrite != 0 && panelWrite < render);
    }

    /// <summary>The app's DeferredOffer post (AppController.PanelOffer.cs): Background priority, from the next render pass.</summary>
    private static void AfterNextRender(Dispatcher dispatcher, Action action)
    {
        EventHandler? onRendering = null;
        onRendering = (_, _) =>
        {
            CompositionTarget.Rendering -= onRendering;
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        };
        CompositionTarget.Rendering += onRendering;
    }

    private static Task NextRender()
    {
        var tcs = new TaskCompletionSource();
        EventHandler? h = null;
        h = (_, _) =>
        {
            CompositionTarget.Rendering -= h;
            tcs.TrySetResult();
        };
        CompositionTarget.Rendering += h;
        return tcs.Task;
    }

    private static OfferSectionVm Offer(int n, string[] ids, string artDir, ref int next)
    {
        // A fresh provider per offer: a new screen's portraits were not seen before (decoded off the UI thread, IsAsync).
        var provider = new CardArtProvider(artDir, null);
        var rows = new OfferRowVm[n];
        for (var i = 0; i < n; i++)
        {
            rows[i] = new OfferRowVm
            {
                Name = $"Card {next}",
                TierText = "SABCD"[next % 5].ToString(),
                ScoreText = (40 + next % 50).ToString(System.Globalization.CultureInfo.InvariantCulture),
                BarWidth = 30 + next % 60,
                IsBest = i == 0,
                SubText = $"{i + 1}위 · 데이터 신뢰도 {50 + next % 40}%",
                Chips = [new ChipVm("광역 부족 +24", true), new ChipVm("덱 크기 -6", false)],
                Art = ids.Length > 0 ? provider.For(ids[next % ids.Length]) : null,
                PriceText = n == 7 ? (50 + next % 100).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            };
            next++;
        }
        return new OfferSectionVm { Title = "카드 보상", ConfidenceText = "보통", Rows = rows, Footer = $"추천: Card {next - n}" };
    }

    private static async Task Settle(Window w)
    {
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}

/// <summary>Runs one async bench once the dispatcher is up, then exits with its code.</summary>
internal sealed class LiveApp(Func<Task<int>> run) : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            var code = 1;
            try
            {
                code = await run();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
            }
            Shutdown(code);
        });
    }
}
