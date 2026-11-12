using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration.CombatAdvice;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.App.Views;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The real PanelWindow XAML laid out offscreen with the in-fight play order filled: the section binds cleanly, shows its
/// rows above the tabs, stays small, and collapses when empty.
/// </summary>
public class CombatAdviceRenderTests
{
    [Fact]
    public void Play_order_section_binds_shows_its_rows_and_collapses_when_empty() => WpfThread.Run(Run);

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

    private sealed class Names : ICombatNames
    {
        public string Card(string id, bool upgraded) => (id == "BASH" ? "강타" : "수비") + (upgraded ? "+" : "");

        public string? Potion(string id) => "화염 포션";

        public string? Monster(string id) => "턱벌레";
    }

    private static void Run()
    {
        var errors = new ErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        using var kor = Loc.Use(Lang.Kor);

        var vm = new PanelViewModel { SelectedTab = PanelTab.Deck };
        var window = new PanelWindow(vm, new AppSettings(), new OverlayCaptureVisibility(true));
        var root = (FrameworkElement)window.Content;
        Layout(root);
        var emptyHeight = root.ActualHeight;
        Assert.DoesNotContain(Texts(root), t => t.Contains("추천 순서", StringComparison.Ordinal)); // collapsed

        var message = new CombatAdviceMessage(CombatAdviceState.Advice)
        {
            Turn = 2,
            Energy = 3,
            Steps =
            [
                new CombatAdviceStep(CombatStepKind.Card, "BASH", Hand: 2, Target: "JAW_WORM", TargetSlot: 0),
                new CombatAdviceStep(CombatStepKind.Potion, "FIRE_POTION", Target: "JAW_WORM", TargetSlot: 1, TargetAmbiguous: true),
                new CombatAdviceStep(CombatStepKind.Card, "DEFEND_IRONCLAD", Upgraded: true, Hand: 0),
            ],
            Ending = CombatEnding.EndTurn,
            Uncertain = true,
            Note = "made-up note",
        };
        vm.Combat = CombatAdvicePresenter.Build(message, inCombat: true, new Names());
        Layout(root);
        var texts = Texts(root);
        Assert.Contains("이번 턴 추천 순서 · 2턴", texts);
        Assert.Contains("불확실", texts);
        Assert.Contains("1.", texts);
        Assert.Contains("강타", texts);
        Assert.Contains("  →  턱벌레", texts);
        Assert.Contains("  →  턱벌레 (2번째)", texts);
        Assert.Contains("포션", texts);
        Assert.Contains("수비+", texts);
        Assert.Contains("턴 종료", texts);
        // Small: header + 4 rows add well under 130 DIPs at 100 %.
        var grown = root.ActualHeight - emptyHeight;
        Assert.InRange(grown, 40, 130);
        // Above the tabs: the header sits higher than the 덱 tab button.
        var header = Descendants(root).OfType<TextBlock>().First(t => t.Text == "이번 턴 추천 순서 · 2턴");
        var tab = Descendants(root).OfType<RadioButton>().First();
        Assert.True(header.TransformToAncestor(root).Transform(new Point(0, 0)).Y < tab.TransformToAncestor(root).Transform(new Point(0, 0)).Y);

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root); // throws if a template cannot be drawn

        // A draw stop: the closing row is muted (TextDim, normal weight), unlike the gold "턴 종료".
        var endTurnRow = Descendants(root).OfType<TextBlock>().First(t => t.Text == "턴 종료");
        Assert.Equal(window.FindResource("Gold"), endTurnRow.Foreground);
        vm.Combat = CombatAdvicePresenter.Build(message with { Ending = CombatEnding.AdviseAfterPlay, Uncertain = false, Note = "next steps depend on a random result" },
            inCombat: true, new Names());
        Layout(root);
        texts = Texts(root);
        Assert.Contains("… 이 카드를 낸 뒤 다시 추천", texts);
        Assert.DoesNotContain("턴 종료", texts);
        Assert.DoesNotContain("불확실", texts);
        var pauseRow = Descendants(root).OfType<TextBlock>().First(t => t.Text == "… 이 카드를 낸 뒤 다시 추천");
        Assert.Equal(window.FindResource("TextDim"), pauseRow.Foreground);
        Assert.Equal(FontWeights.Normal, pauseRow.FontWeight);
        Assert.Contains("무작위 결과", Assert.IsType<string>(((FrameworkElement)VisualTreeHelper.GetParent(pauseRow)).ToolTip));

        vm.Combat = CombatAdvicePresenter.Build(new CombatAdviceMessage(CombatAdviceState.Lost) { Reason = "made-up reason" }, inCombat: true, new Names());
        Layout(root);
        texts = Texts(root);
        Assert.Contains("이 전투는 추적이 끊겼습니다", texts);
        Assert.DoesNotContain("턴 종료", texts);

        vm.Combat = CombatAdviceSectionVm.Empty;
        Layout(root);
        Assert.Equal(emptyHeight, root.ActualHeight, 0.5);

        PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
        Assert.True(errors.Errors.Count == 0, "binding errors:\n" + string.Join("\n", errors.Errors));
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
        => Descendants(root).OfType<TextBlock>().Where(t => t.IsVisible || IsShownInLayout(t)).Select(t => t.Text).Where(s => !string.IsNullOrEmpty(s)).ToList();

    /// <summary>Offscreen windows are never "visible": count a text when it and its ancestors are not collapsed.</summary>
    private static bool IsShownInLayout(UIElement e)
    {
        for (DependencyObject? d = e; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is UIElement u && u.Visibility != Visibility.Visible) return false;
        }
        return true;
    }
}
