using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Sts2Overlay.App.Views;

/// <summary>Tier letter → brush. PLAN §8: S gold, A purple, B blue, C grey, D dark red. ConverterParameter "fg" gives a readable text colour.</summary>
public sealed class TierBrushConverter : IValueConverter
{
    private static readonly Brush S = Frozen(0xF1, 0xC4, 0x0F);
    private static readonly Brush A = Frozen(0x9B, 0x59, 0xB6);
    private static readonly Brush B = Frozen(0x34, 0x98, 0xDB);
    private static readonly Brush C = Frozen(0x95, 0xA5, 0xA6);
    private static readonly Brush D = Frozen(0x8E, 0x2B, 0x2B);
    private static readonly Brush None = Frozen(0x55, 0x5A, 0x64);
    private static readonly Brush Dark = Frozen(0x1A, 0x1A, 0x1A);
    private static readonly Brush Light = Frozen(0xFF, 0xFF, 0xFF);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tier = value as string;
        if (parameter as string == "fg") return tier is "S" or "C" ? Dark : Light;
        return tier switch
        {
            "S" => S,
            "A" => A,
            "B" => B,
            "C" => C,
            "D" => D,
            _ => None,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    // Frozen brushes are immutable and can be shared across threads and windows without overhead.
    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Enum ↔ bool/Visibility for tab buttons and tab pages: true/Visible when the value equals ConverterParameter.
/// ConvertBack (RadioButton checked) returns the parameter as the enum value.
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var match = value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal);
        return targetType == typeof(Visibility) ? (match ? Visibility.Visible : Visibility.Collapsed) : match;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string p ? Enum.Parse(targetType, p) : Binding.DoNothing;
}

/// <summary>bool → Visibility; ConverterParameter "!" inverts. Also treats null/empty strings as false.</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "!") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
