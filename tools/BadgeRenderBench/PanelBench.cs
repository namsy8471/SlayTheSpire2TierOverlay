using System.Diagnostics;
using System.IO;
using System.Windows;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.App.Views;

namespace Sts2Overlay.Tools.BadgeRenderBench;

/// <summary>
/// The other half of the "render" interval: the panel's 추천 tab is rebuilt in the same UI pass as the badges
/// (ApplyRecognition sets the offer, then the badges). Lays out the real PanelWindow content OFFSCREEN (no HWND, no
/// window shown, so it may run while the game runs) after a new offer, with and without card portraits.
/// Each sample uses a fresh CardArtProvider, so every portrait is decoded as for a card not seen before this session.
/// </summary>
internal static class PanelBench
{
    public static int Run(string artDir, int rounds)
    {
        var app = new BenchApp(() => Task.FromResult(0)) { Resources = Program.AppResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        GC.KeepAlive(app);
        var vm = new PanelViewModel { SelectedTab = PanelTab.Offer, IsCollapsed = false };
        // Never shown: no HWND, so the panel never saves its position into the user's settings.
        var panel = new PanelWindow(vm, new AppSettings(), new OverlayCaptureVisibility(false));
        var root = (FrameworkElement)panel.Content;
        Layout(root);

        var ids = Directory.EnumerateFiles(artDir, "*.webp", SearchOption.AllDirectories)
            .Where(f => !Path.GetDirectoryName(f)!.EndsWith("beta", StringComparison.Ordinal))
            .Select(f => "CARD." + Path.GetFileNameWithoutExtension(f).ToUpperInvariant()).Distinct().ToArray();
        var variants = new[] { "reward-art", "reward-noart", "shop-art", "shop-noart" };
        var results = variants.ToDictionary(v => v, _ => new List<double>());
        var rng = new Random(7);
        var next = 0;
        for (var r = 0; r < rounds; r++)
        {
            foreach (var v in variants.OrderBy(_ => rng.Next()))
            {
                var provider = new CardArtProvider(artDir, null);
                provider.For(ids[0]); // builds the file index outside the timing (the app builds it once per session)
                var n = v.StartsWith("shop", StringComparison.Ordinal) ? 7 : 3;
                var rows = Enumerable.Range(0, n).Select(i => new OfferRowVm
                {
                    Name = $"Card {i}",
                    TierText = "A",
                    ScoreText = "71",
                    BarWidth = 60 + i,
                    IsBest = i == 0,
                    SubText = "1위 · 신뢰도 50%",
                    Chips = [new ChipVm("광역 부족 +24", true), new ChipVm("덱 크기 -6", false)],
                    Art = v.EndsWith("-art", StringComparison.Ordinal) ? provider.For(ids[next++ % ids.Length]) : null,
                    PriceText = n == 7 ? "75" : null,
                }).ToArray();
                vm.Offer = OfferSectionVm.Empty;
                Layout(root);
                var t0 = Stopwatch.GetTimestamp();
                vm.Offer = new OfferSectionVm { Title = "카드 보상", ConfidenceText = "보통", Rows = rows, Footer = "footer" };
                Layout(root);
                results[v].Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            Console.WriteLine($"  {r + 1}/{rounds} rounds");
        }
        Console.WriteLine("panel 추천 tab: new offer -> layout done (ms, offscreen, no render pass):");
        foreach (var (v, xs) in results) Console.WriteLine($"  {v,-14} {Stats.Line(xs)}");
        return 0;
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
    }
}
