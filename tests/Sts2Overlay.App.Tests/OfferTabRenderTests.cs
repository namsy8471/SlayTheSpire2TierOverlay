using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.App.Views;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The 추천 tab keeps its row elements from offer to offer (Views/ReusedRows.cs). The real PanelWindow is laid out and
/// rendered offscreen (never shown): rows that showed other offers before must draw pixel for pixel like a panel that
/// shows the offer from the start, and the row elements must really be kept.
/// </summary>
public class OfferTabRenderTests
{
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

    [Fact]
    public void Kept_rows_draw_exactly_like_fresh_rows_and_are_not_rebuilt() => WpfThread.Run(() =>
    {
        var errors = new ErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            var vm = new PanelViewModel { SelectedTab = PanelTab.Offer };
            var kept = (FrameworkElement)new PanelWindow(vm, new AppSettings(), new OverlayCaptureVisibility(true)).Content;

            vm.Offer = Reward();
            Layout(kept);
            var firstRow = RowBorders(kept)[0];
            Assert.Equal(4, RowBorders(kept).Length);

            foreach (var offer in new[] { Shop(), Reward(skipBest: true), Shop() with { IsStale = true } })
            {
                vm.Offer = offer;
                Layout(kept);
                Assert.Same(firstRow, RowBorders(kept)[0]); // the first row's elements were kept, not rebuilt

                var freshVm = new PanelViewModel { SelectedTab = PanelTab.Offer, Offer = offer };
                var fresh = (FrameworkElement)new PanelWindow(freshVm, new AppSettings(), new OverlayCaptureVisibility(true)).Content;
                Layout(fresh);
                Assert.Equal(Texts(fresh), Texts(kept));
                Assert.Equal(fresh.ActualWidth, kept.ActualWidth);
                Assert.Equal(fresh.ActualHeight, kept.ActualHeight);
                var (a, b) = (Pixels(fresh), Pixels(kept));
                var diff = a.Zip(b).Count(p => p.First != p.Second);
                Assert.True(diff == 0, $"{offer.Title}: {diff} bytes differ between kept and fresh rows");
            }
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
        }
        Assert.True(errors.Errors.Count == 0, "binding errors:\n" + string.Join("\n", errors.Errors));
    });

    private static OfferSectionVm Reward(bool skipBest = false) => new()
    {
        Title = "카드 보상",
        ConfidenceText = "보통",
        Rows =
        [
            new OfferRowVm
            {
                Name = "강타", TierText = "S", ScoreText = "82", BarWidth = 90, IsBest = !skipBest, SubText = "1위 · 데이터 신뢰도 80%",
                Chips = [new ChipVm("광역 부족 +24", true), new ChipVm("덱 크기 -6", false)],
            },
            new OfferRowVm { Name = "수비", TierText = "C", ScoreText = "40", BarWidth = 44, IsUpgraded = true, SubText = "핵심 · 방어\n2위 · 데이터 신뢰도 50%" },
            new OfferRowVm { Name = "분노", TierText = "B", ScoreText = "61", BarWidth = 66, IsEstimate = true, SubText = "3위", Chips = [new ChipVm("연계 +5", true)] },
            new OfferRowVm { Name = "스킵", IsSkip = true, ScoreText = "38", BarWidth = 40, IsBest = skipBest, SubText = "카드를 받지 않음" },
        ],
        Footer = skipBest ? "추천: 스킵" : "추천: 강타",
    };

    private static OfferSectionVm Shop() => new()
    {
        Title = "상점",
        ConfidenceText = "확신",
        ConfidenceKind = "Sure",
        FlipHint = "협동: 동료가 고르면 바뀔 수 있음",
        Rows =
        [
            new OfferRowVm { Name = "칼날 회오리", TierText = "A", ScoreText = "70", BarWidth = 77, PriceText = "75", SubText = "구매 가치 70 · 골드당 0.93", Chips = [new ChipVm("광역 +12", true)] },
            new OfferRowVm { Name = "닻 (유물)", TierText = "B", ScoreText = "55", BarWidth = 60, PriceText = "150", IsBest = true, SubText = "구매 가치 55",
                Relics = [RelicInfoProvider.Empty.For("RELIC.ANCHOR", "kor")] },
            new OfferRowVm { Name = "타격", TierText = "D", ScoreText = "12", BarWidth = 13, PriceText = "45", Unaffordable = true, SubText = "골드 부족",
                Chips = [new ChipVm("덱 크기 -6", false), new ChipVm("약함 -3", false), new ChipVm("중복 -2", false)] },
            new OfferRowVm { Name = "방어", TierText = "C", ScoreText = "30", BarWidth = 33, PriceText = "50", IsUpgraded = true, IsEstimate = true, SubText = "4위" },
            new OfferRowVm { Name = "카드 제거 (타격)", ScoreText = "44", BarWidth = 48, PriceText = "75", SubText = "덱 압축" },
        ],
        Footer = "추천 구매: 닻",
    };

    /// <summary>The outer Border of each offer row (OfferRowTemplate's "Bd").</summary>
    private static Border[] RowBorders(DependencyObject root)
        => Descendants(root).OfType<Border>().Where(b => b.DataContext is OfferRowVm && b.Margin == new Thickness(0, 0, 0, 6)).ToArray();

    private static byte[] Pixels(FrameworkElement root)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
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

    /// <summary>The texts that are drawn: every element up to the root is Visible (never shown, so IsVisible is always false).</summary>
    private static List<string> Texts(DependencyObject root)
        => Descendants(root).OfType<TextBlock>().Where(t => Shown(t, root)).Select(t => t.Text).ToList();

    private static bool Shown(DependencyObject d, DependencyObject root)
    {
        for (var e = d; e is not null && !ReferenceEquals(e, root); e = VisualTreeHelper.GetParent(e))
            if (e is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }
}
