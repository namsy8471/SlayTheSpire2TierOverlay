using System.Windows;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Views;

/// <summary>What one event marker says, next to the button at <see cref="Button"/> (physical screen pixels).</summary>
/// <param name="Rank">1 = best. The best option gets the "추천!" pill.</param>
/// <param name="Warning">Short tag for an option to avoid ("죽음 위험", "잠김"), or null.</param>
public sealed record EventMarkerSpec(PixelRect Button, int Rank, bool IsBest, string ScoreText, string? Warning);

/// <summary>A placed marker, DIPs relative to the layer.</summary>
public sealed record EventMarkerVm(double Left, double Top, double Scale, bool IsBest, string RankText, string ScoreText, string? Warning)
{
    public bool IsOther => !IsBest;
    public bool HasWarning => Warning is not null;
}

public sealed class EventMarkerLayerViewModel : ObservableObject
{
    private IReadOnlyList<EventMarkerVm> _markers = [];

    public IReadOnlyList<EventMarkerVm> Markers
    {
        get => _markers;
        set => Set(ref _markers, value);
    }
}

/// <summary>Marker placement (pure, unit-tested).</summary>
public static class EventMarkerLayout
{
    /// <summary>Design height of the "추천!" pill in DIPs before scaling; it is sized to half a button.</summary>
    public const double PillHeight = 44;

    /// <summary>
    /// Top-left and scale of a marker for a button: just right of it, centred on it vertically, half its height,
    /// and pulled back inside the layer if the window is narrow.
    /// </summary>
    public static (double Left, double Top, double Scale) Place(PxBox button, PxBox layer, double dpiScale, double multiplier, double markerWidthDip)
    {
        var b = BadgeLayout.ToLayerDips(button, layer, dpiScale);
        var scale = Math.Clamp(b.Height * 0.5 / PillHeight, 0.4, 3.0) * multiplier;
        var gap = b.Height * 0.12;
        var areaWidth = layer.Width / dpiScale;
        var left = Math.Min(b.Right + gap, areaWidth - markerWidthDip * scale - 2);
        var top = b.Y + (b.Height - PillHeight * scale) / 2;
        return (Math.Max(2, left), Math.Max(2, top), scale);
    }
}

/// <summary>
/// The event marker layer: transparent, click-through, topmost, over the game's client area (like
/// <see cref="BadgeWindow"/>; its capture visibility is managed by <see cref="OverlayCaptureVisibility"/> the same way).
/// </summary>
public partial class EventMarkerWindow : Window
{
    private IntPtr _hwnd;
    private PixelRect _layer;
    private IReadOnlyList<EventMarkerSpec> _specs = [];

    public EventMarkerWindow(OverlayCaptureVisibility captureVisibility)
    {
        InitializeComponent();
        DataContext = Model;
        SourceInitialized += (_, _) =>
        {
            _hwnd = OverlayWindowHelper.Handle(this);
            OverlayWindowHelper.SetExStyle(_hwnd, WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, true);
            captureVisibility.Register(_hwnd);
            Closed += (_, _) => captureVisibility.Unregister(_hwnd);
        };
        DpiChanged += (_, _) => Relayout();
    }

    public EventMarkerLayerViewModel Model { get; } = new();

    public IntPtr Hwnd => _hwnd;

    public bool HasMarkers => _specs.Count > 0;

    /// <summary>User UI scale relative to auto, as for the card badges.</summary>
    public double ScaleMultiplier { get; set; } = 1.0;

    public void SetLayerRect(PixelRect gameRect)
    {
        if (_hwnd == IntPtr.Zero || gameRect.IsEmpty || gameRect == _layer) return;
        _layer = gameRect;
        OverlayWindowHelper.SetPhysicalBounds(_hwnd, gameRect);
        Relayout();
    }

    public void Show(IReadOnlyList<EventMarkerSpec> specs)
    {
        _specs = specs;
        Relayout();
    }

    public void Clear() => Show([]);

    private void Relayout()
    {
        if (_hwnd == IntPtr.Zero || _layer.IsEmpty || _specs.Count == 0)
        {
            Model.Markers = [];
            return;
        }
        var dpi = Math.Max(1u, GetDpiForWindow(_hwnd)) / 96.0;
        var layer = new PxBox(_layer.X, _layer.Y, _layer.Width, _layer.Height);
        Model.Markers = _specs.Select(s =>
        {
            var (left, top, scale) = EventMarkerLayout.Place(new PxBox(s.Button.X, s.Button.Y, s.Button.Width, s.Button.Height),
                layer, dpi, ScaleMultiplier, s.IsBest ? 170 : 130);
            // The small pill is shorter than the "추천!" one: centre it on the button too.
            if (!s.IsBest) top += (EventMarkerLayout.PillHeight - 32) / 2 * scale;
            return new EventMarkerVm(left, top, scale, s.IsBest, $"{s.Rank}위", s.ScoreText, s.Warning);
        }).ToArray();
    }
}
