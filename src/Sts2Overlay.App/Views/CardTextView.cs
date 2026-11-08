using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Sts2Overlay.Core.Text;

namespace Sts2Overlay.App.Views;

/// <summary>
/// <c>TextBlock.Inlines</c> cannot be bound, so <c>views:CardTextView.Runs="{Binding Body}"</c> rebuilds them from
/// <see cref="CardTextRun"/>s: keywords gold, upgraded numbers green, energy as small orbs like on the card.
/// </summary>
public static class CardTextView
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.RegisterAttached(
        "Runs", typeof(IReadOnlyList<CardTextRun>), typeof(CardTextView), new PropertyMetadata(null, OnRunsChanged));

    public static IReadOnlyList<CardTextRun>? GetRuns(DependencyObject d) => (IReadOnlyList<CardTextRun>?)d.GetValue(RunsProperty);

    public static void SetRuns(DependencyObject d, IReadOnlyList<CardTextRun>? value) => d.SetValue(RunsProperty, value);

    // The game's rich-text colours (decompiled Localization text effects): [gold] #EFC851, [green] #7FFF00 (upgraded values),
    // [blue] #87CEEB, [purple] #EE82EE.
    private static readonly Brush Keyword = Frozen(0xEF, 0xC8, 0x51);
    private static readonly Brush Changed = Frozen(0x7F, 0xFF, 0x00);
    private static readonly Brush Blue = Frozen(0x87, 0xCE, 0xEB);
    private static readonly Brush Purple = Frozen(0xEE, 0x82, 0xEE);
    private static readonly Brush EnergyFill = Frozen(0xF0, 0x8A, 0x24);
    private static readonly Brush EnergyRim = Frozen(0xFF, 0xE0, 0xA0);
    private static readonly Brush Star = Frozen(0xF7, 0xDC, 0x6F);

    private static void OnRunsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<CardTextRun> runs) return;
        foreach (var r in runs)
        {
            switch (r.Style)
            {
                case CardTextStyle.Energy:
                    AddIcons(tb, r.Text, EnergyOrb);
                    break;
                case CardTextStyle.Star:
                    // starIcons repeats the icon (never a number); 0 prints nothing.
                    if (int.TryParse(r.Text, out int stars))
                        for (int i = 0; i < stars; i++) tb.Inlines.Add(new Run("★") { Foreground = Star });
                    break;
                default:
                    var run = new Run(r.Text);
                    switch (r.Style)
                    {
                        case CardTextStyle.Keyword: run.Foreground = Keyword; break;
                        case CardTextStyle.Changed: run.Foreground = Changed; run.FontWeight = FontWeights.Bold; break;
                        case CardTextStyle.Blue: run.Foreground = Blue; break;
                        case CardTextStyle.Purple: run.Foreground = Purple; break;
                    }
                    tb.Inlines.Add(run);
                    break;
            }
        }
    }

    /// <summary>The game's energyIcons formatter: 1–3 icons are drawn one by one, any other count as the number + one icon.</summary>
    private static void AddIcons(TextBlock tb, string count, Func<Inline> icon)
    {
        if (int.TryParse(count, out int n) && n is >= 1 and <= 3)
        {
            for (int i = 0; i < n; i++) tb.Inlines.Add(icon());
            return;
        }
        tb.Inlines.Add(new Run(count));
        tb.Inlines.Add(icon());
    }

    private static Inline EnergyOrb() => new InlineUIContainer(new Ellipse
    {
        Width = 11,
        Height = 11,
        Fill = EnergyFill,
        Stroke = EnergyRim,
        StrokeThickness = 1,
        Margin = new Thickness(1, 0, 1, -1),
    }) { BaselineAlignment = BaselineAlignment.Center };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
