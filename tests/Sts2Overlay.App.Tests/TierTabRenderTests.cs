using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.App.Views;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The real PanelWindow XAML laid out and rendered offscreen (never shown) with a filled 티어표 tab: XAML and binding
/// mistakes only surface at run time, so this is the check that the page builds, binds and fits 5 tiles per row.
/// </summary>
public class TierTabRenderTests
{
    [Fact]
    public void Tier_tab_lays_out_five_tiles_per_row_binds_cleanly_and_switches_language() => WpfThread.Run(Run);

    private sealed class ErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null) Errors.Add(message);
        }
    }

    private static void Run()
    {
        var errors = new ErrorListener();
        PresentationTraceSources.Refresh(); // WPF traces binding errors only under a debugger unless tracing is refreshed on
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

        var vm = new PanelViewModel { SelectedTab = PanelTab.Tiers };
        Fill(vm.Tiers);
        var window = new PanelWindow(vm, new AppSettings(), new OverlayCaptureVisibility(true));
        var root = (FrameworkElement)window.Content;

        foreach (var scale in new[] { 1.0, 1.5 })
        {
            vm.UiScale = scale;
            Layout(root);
            var texts = Texts(root);
            Assert.Contains("티어표", texts);
            Assert.Contains(texts, t => t.StartsWith("티어는 카드 자체의 기본 평가입니다.", StringComparison.Ordinal));
            Assert.Contains("무색", texts);
            Assert.Contains("협동 카드", texts);
            Assert.Contains("미평가 · 1장", texts);
            Assert.Contains("1막 A", texts);
            Assert.Contains("✓2", texts);

            var scroll = (ScrollViewer)window.FindName("Scroll");
            var tiles = Descendants(root).OfType<StackPanel>().Where(p => p.DataContext is TierCardVm && p.Width == 62).ToArray();
            Assert.Equal(88, tiles.Length);
            Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
            // The S group has 6 cards: 5 on the first row, 1 on the second, all inside the scroll area (no horizontal scroll).
            var sRow = tiles.Where(t => ((TierCardVm)t.DataContext).TierText == "S")
                .Select(t => t.TransformToAncestor(scroll).Transform(new Point(0, 0))).ToArray();
            Assert.Equal(6, sRow.Length);
            Assert.Equal(5, sRow.Count(p => Math.Abs(p.Y - sRow[0].Y) < 1));
            Assert.All(sRow, p => Assert.True(p.X + 62 <= scroll.ViewportWidth + 0.5, $"tile at x={p.X} past the viewport {scroll.ViewportWidth}"));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root); // throws if a template cannot be drawn

        // Hover tooltip of the owned tile (WPF builds it only when it opens, so lay it out by hand).
        var owned = Descendants(root).OfType<StackPanel>().First(p => p.DataContext is TierCardVm { Owned: 2 });
        var tip = (ToolTip)owned.ToolTip;
        tip.DataContext = owned.DataContext;
        Layout(tip);
        var tipTexts = Texts(tip);
        Assert.Contains("A Very Long English Card Name That Must Ellipsize", tipTexts);
        Assert.Contains("티어 S · 희귀 · 공격", tipTexts);
        Assert.Contains("덱에 2장", tipTexts);

        using (Loc.Use(Lang.Eng))
        {
            UiText.Instance.RaiseAll();
            Layout(root);
            var texts = Texts(root);
            Assert.Contains("Tiers", texts);
            Assert.Contains("Tiers rate the card on its own. Reward rankings also use your deck and top players' picks, so they can differ.", texts);
        }
        UiText.Instance.RaiseAll();

        PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
        var ours = errors.Errors.ToArray();
        Assert.True(ours.Length == 0, "binding errors:\n" + string.Join("\n", ours));
    }

    private static void Fill(TierListVm t)
    {
        var noop = new RelayCommand(_ => { });
        t.Characters = new[] { "아이언클래드", "사일런트", "디펙트", "네크로바인더", "리젠트", "무색" }.Select((n, i) => new TierChipVm(n, i == 0, noop)).ToArray();
        t.TypeChips = [new("공격", true, noop), new("스킬", true, noop), new("파워", false, noop)];
        t.RarityChips = [new("일반", true, noop), new("고급", true, noop), new("희귀", true, noop)];
        t.OptionChips = [new("협동 카드", false, noop, "tip")];
        t.SummaryText = "87장 중 8장 표시";
        var s = Enumerable.Range(1, 6).Select(i => new TierCardVm
        {
            Id = $"CARD.S{i}",
            Name = i == 1 ? "A Very Long English Card Name That Must Ellipsize" : $"카드 {i}",
            TierText = "S",
            CostText = i == 2 ? "X" : "1",
            IsCoop = i == 3,
            IsEstimate = i == 4,
            DetailText = "티어 S · 희귀 · 공격",
        }).ToArray();
        s[0].Owned = 2;
        s[1].SetActMarker("1막 A", "A");
        var b = new TierCardVm { Id = "CARD.B1", Name = "비", TierText = "B", Tip = "tip" };
        var unrated = new TierCardVm { Id = "CARD.NEW", Name = "New", TierText = "?" };
        t.Groups =
        [
            new TierGroupVm("S", "6장", s),
            new TierGroupVm("B", "1장", [b]),
            // ~90 tiles like a real pool: the scroll area then shows its vertical scroll bar, the narrowest case.
            new TierGroupVm("D", "80장", Enumerable.Range(1, 80).Select(i => new TierCardVm { Id = $"CARD.D{i}", Name = $"D {i}", TierText = "D" }).ToArray()),
            new TierGroupVm("?", "미평가 · 1장", [unrated]),
        ];
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            yield return child;
            foreach (var x in Descendants(child)) yield return x;
        }
    }

    private static List<string> Texts(DependencyObject root)
        => Descendants(root).OfType<TextBlock>().Select(t => t.Text).Where(s => !string.IsNullOrEmpty(s)).ToList();
}
