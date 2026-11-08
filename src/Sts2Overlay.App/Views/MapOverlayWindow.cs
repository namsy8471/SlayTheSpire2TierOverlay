using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;
using Sts2Overlay.Core.Map;
using RefTransform = Sts2Overlay.Vision.RefTransform;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Views;

/// <summary>
/// The map layer: a transparent, click-through, topmost window over the game's client area that draws the
/// recommended path and the "추천!" node on the game's own map screen (see <see cref="MapMarkLayout"/>).
///
/// <para>
/// It is a separate window from the card badges (<see cref="BadgeWindow"/>) so the two never fight over one canvas:
/// the map marks come and go with the map screen at their own rate. Same Win32 styles as the badges: layered,
/// WS_EX_TRANSPARENT (every click goes to the game), no activation, no taskbar entry, excluded from capture.
/// </para>
/// <para>
/// Marks are built in the game's 1920x1080 design space. One transform on the canvas maps that to the window:
/// design → game pixels (<see cref="RefTransform"/>: scale + letterbox offset) → WPF DIPs (÷ monitor DPI). So a mark
/// is exactly as big, relative to the map, at FHD 100 % as at 4K 150 %.
/// </para>
/// </summary>
public sealed class MapOverlayWindow : Window
{
    private static readonly Brush Gold = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)));
    private static readonly Brush GoldDark = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x05)));
    private static readonly Brush Shadow = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private static readonly Brush AltLine = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xF4, 0xD6)));
    private static readonly Brush ChipBack = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1B, 0x16)));
    private static readonly Brush Partner = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)));
    private static readonly Brush PartnerDark = Frozen(new SolidColorBrush(Color.FromRgb(0x05, 0x26, 0x38)));

    private readonly bool _excludeFromCapture;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private IntPtr _hwnd;
    private PixelRect _layer;
    private MapMarkPlan _plan = MapMarkPlan.Empty;
    private double _scaleMultiplier = 1.0;

    public MapOverlayWindow(bool excludeFromCapture)
    {
        _excludeFromCapture = excludeFromCapture;
        Title = "STS2 Map Marks";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Left = 0;
        Top = 0;
        Width = 100;
        Height = 100;
        if (Application.Current?.TryFindResource("UiFont") is FontFamily font) FontFamily = font;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            _hwnd = OverlayWindowHelper.Handle(this);
            OverlayWindowHelper.SetExStyle(_hwnd, WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, true);
            if (_excludeFromCapture) OverlayWindowHelper.SetExcludeFromCapture(_hwnd, true);
        };
        DpiChanged += (_, _) => Redraw();
    }

    public IntPtr Hwnd => _hwnd;

    public bool HasMarks => !_plan.IsEmpty;

    /// <summary>User "UI 배율" relative to auto: scales the marks' size (not their positions).</summary>
    public double ScaleMultiplier
    {
        get => _scaleMultiplier;
        set
        {
            if (Math.Abs(value - _scaleMultiplier) < 0.001) return;
            _scaleMultiplier = value;
            Redraw();
        }
    }

    /// <summary>Covers <paramref name="gameRect"/> (physical px).</summary>
    public void SetLayerRect(PixelRect gameRect)
    {
        if (_hwnd == IntPtr.Zero || gameRect.IsEmpty || gameRect == _layer) return;
        _layer = gameRect;
        OverlayWindowHelper.SetPhysicalBounds(_hwnd, gameRect);
        Redraw();
    }

    public void ShowPlan(MapMarkPlan plan)
    {
        if (ReferenceEquals(plan, _plan) || (plan.IsEmpty && _plan.IsEmpty)) return;
        _plan = plan;
        Redraw();
    }

    public void Clear() => ShowPlan(MapMarkPlan.Empty);

    private void Redraw()
    {
        _canvas.Children.Clear();
        if (_hwnd == IntPtr.Zero || _layer.IsEmpty || _plan.IsEmpty) return;

        var dpi = Math.Max(1u, GetDpiForWindow(_hwnd)) / 96.0;
        var t = RefTransform.For(_layer.Width, _layer.Height);
        _canvas.RenderTransform = new MatrixTransform(t.Scale / dpi, 0, 0, t.Scale / dpi, t.OffsetX / dpi, t.OffsetY / dpi);
        // Nothing over the game's top bar (HP, gold, potions).
        _canvas.Clip = new RectangleGeometry(new Rect(0, MapMarkLayout.VisibleTop, 1920, MapMarkLayout.VisibleBottom - MapMarkLayout.VisibleTop));
        var m = _scaleMultiplier;

        // Faint alternatives first, so the recommended path is drawn over them.
        foreach (var l in _plan.Lines.Where(l => l.Kind == MapLineKind.Alternative))
            _canvas.Children.Add(new Line
            {
                X1 = l.X1, Y1 = l.Y1, X2 = l.X2, Y2 = l.Y2,
                Stroke = AltLine, StrokeThickness = 3 * m,
                StrokeDashArray = [3, 2.5], StrokeDashCap = PenLineCap.Round,
            });
        foreach (var l in _plan.Lines.Where(l => l.Kind == MapLineKind.Best))
        {
            _canvas.Children.Add(new Line { X1 = l.X1, Y1 = l.Y1, X2 = l.X2, Y2 = l.Y2, Stroke = Shadow, StrokeThickness = 11 * m, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            _canvas.Children.Add(new Line { X1 = l.X1, Y1 = l.Y1, X2 = l.X2, Y2 = l.Y2, Stroke = Gold, StrokeThickness = 6 * m, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }

        foreach (var n in _plan.Nodes)
        {
            switch (n.Kind)
            {
                case MapNodeMarkKind.Recommended:
                    Ring(n.X, n.Y, 42 * m, Shadow, 9 * m);
                    Ring(n.X, n.Y, 42 * m, Gold, 5 * m);
                    // Ribbon above the node with a small pointer down to it.
                    var tip = new Polygon
                    {
                        Points = [new Point(n.X - 11 * m, n.Y - 58 * m), new Point(n.X + 11 * m, n.Y - 58 * m), new Point(n.X, n.Y - 45 * m)],
                        Fill = Gold,
                    };
                    _canvas.Children.Add(tip);
                    Label(n.Caption ?? "추천!", n.X, n.Y - 58 * m, 26 * m, GoldDark, Gold, anchorBottom: true, bold: true);
                    break;
                case MapNodeMarkKind.UnknownOdds:
                    Label(n.Caption ?? "", n.X, n.Y + 40 * m, 14 * m, Brushes.White, ChipBack, anchorBottom: false, bold: false);
                    break;
                case MapNodeMarkKind.Partner:
                    var r = Ring(n.X, n.Y, 36 * m, Partner, 3.5 * m);
                    r.StrokeDashArray = [2.5, 1.5];
                    LabelRightAligned(n.Caption ?? "동료", n.X - 44 * m, n.Y, 15 * m, PartnerDark, Partner);
                    break;
            }
        }
    }

    private Ellipse Ring(double cx, double cy, double r, Brush stroke, double thickness)
    {
        var e = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = stroke, StrokeThickness = thickness };
        Canvas.SetLeft(e, cx - r);
        Canvas.SetTop(e, cy - r);
        _canvas.Children.Add(e);
        return e;
    }

    /// <summary>A rounded label centred on <paramref name="cx"/>, with its bottom (or top) edge at <paramref name="y"/>.</summary>
    private void Label(string text, double cx, double y, double size, Brush fg, Brush bg, bool anchorBottom, bool bold)
    {
        var b = MakeLabel(text, size, fg, bg, bold);
        b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(b, cx - b.DesiredSize.Width / 2);
        Canvas.SetTop(b, anchorBottom ? y - b.DesiredSize.Height : y);
        _canvas.Children.Add(b);
    }

    /// <summary>A label whose right edge is at <paramref name="right"/>, vertically centred on <paramref name="cy"/>.</summary>
    private void LabelRightAligned(string text, double right, double cy, double size, Brush fg, Brush bg)
    {
        var b = MakeLabel(text, size, fg, bg, bold: true);
        b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(b, right - b.DesiredSize.Width);
        Canvas.SetTop(b, cy - b.DesiredSize.Height / 2);
        _canvas.Children.Add(b);
    }

    private static Border MakeLabel(string text, double size, Brush fg, Brush bg, bool bold) => new()
    {
        Background = bg,
        CornerRadius = new CornerRadius(size * 0.35),
        Padding = new Thickness(size * 0.45, size * 0.12, size * 0.45, size * 0.16),
        Child = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = fg,
        },
    };

    private static Brush Frozen(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
