using System.Globalization;
using System.Windows;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Views;

/// <summary>
/// The badge layer: a transparent, always-click-through, topmost window exactly over the game's client area.
///
/// Win32 styles (set once the HWND exists):
/// - WS_EX_LAYERED: per-pixel alpha (WPF already sets it because AllowsTransparency=True). Fully transparent pixels
///   show the game.
/// - WS_EX_TRANSPARENT: hit testing skips this window, so EVERY click goes to the game, even on a badge.
/// - WS_EX_NOACTIVATE: never takes focus from the game.
/// - WS_EX_TOOLWINDOW: no taskbar button / Alt+Tab entry.
/// </summary>
public partial class BadgeWindow : Window
{
    private readonly OverlayCaptureVisibility _captureVisibility;
    private IntPtr _hwnd;
    private PixelRect _layer;
    private IReadOnlyList<BadgeSpec> _specs = [];
    private SkipSpec? _skip;
    private RemovalSpec? _removal;
    private bool _showBounds;

    public BadgeWindow(OverlayCaptureVisibility captureVisibility)
    {
        _captureVisibility = captureVisibility;
        InitializeComponent();
        DataContext = Model;
        SourceInitialized += (_, _) =>
        {
            _hwnd = OverlayWindowHelper.Handle(this);
            OverlayWindowHelper.SetExStyle(_hwnd, WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, true);
            _captureVisibility.Register(_hwnd);
            Closed += (_, _) => _captureVisibility.Unregister(_hwnd);
        };
        // Moving to a monitor with another DPI changes the physical→DIP factor: lay out again.
        DpiChanged += (_, _) => Relayout();
    }

    public BadgeLayerViewModel Model { get; } = new();

    public IntPtr Hwnd => _hwnd;

    public bool HasBadges => _specs.Count > 0 || _skip is not null || _removal is not null;

    /// <summary>
    /// User "UI 배율" relative to auto (override / auto). Badges are sized from the card width, which already follows the
    /// game's resolution, so at the auto setting this is 1 and only a manual override makes them bigger or smaller.
    /// </summary>
    public double ScaleMultiplier
    {
        get => _scaleMultiplier;
        set
        {
            if (Math.Abs(value - _scaleMultiplier) < 0.001) return;
            _scaleMultiplier = value;
            Relayout();
        }
    }

    private double _scaleMultiplier = 1.0;

    /// <summary>Covers <paramref name="gameRect"/> (physical px). Re-lays out the badges when the rect changes.</summary>
    public void SetLayerRect(PixelRect gameRect)
    {
        if (_hwnd == IntPtr.Zero || gameRect.IsEmpty || gameRect == _layer) return;
        _layer = gameRect;
        OverlayWindowHelper.SetPhysicalBounds(_hwnd, gameRect);
        Relayout();
    }

    /// <summary>
    /// Shows badges for the given cards, with their reason chips and ribbons (see <see cref="BadgeSpec"/>).
    /// <paramref name="skip"/> non-null adds a "스킵 추천!" pill under the cards; <paramref name="removal"/> non-null marks
    /// the shop's card-removal service. <paramref name="showBounds"/> draws the recognised card and price boxes (demo,
    /// and the debug outline mode).
    /// </summary>
    public void ShowBadges(IReadOnlyList<BadgeSpec> specs, SkipSpec? skip, RemovalSpec? removal, bool showBounds)
    {
        _specs = specs;
        _showBounds = showBounds;
        _skip = specs.Count > 0 ? skip : null;
        _removal = removal;
        Relayout();
    }

    public void Clear() => ShowBadges([], null, null, false);

    private void Relayout()
    {
        if (_hwnd == IntPtr.Zero || _layer.IsEmpty)
        {
            Model.Badges = [];
            Model.Bounds = [];
            Model.Ribbons = [];
            Model.ChipStacks = [];
            Model.Skip = null;
            Model.Removal = null;
            return;
        }

        // GetDpiForWindow: the DPI of the monitor this window is on (per-monitor V2), e.g. 144 at 150 %.
        var dpi = Math.Max(1u, GetDpiForWindow(_hwnd)) / 96.0;
        // All placement math lives in BadgeLayout (pure, unit-tested, shared with VisionBench's preview renderer).
        var frame = BadgeLayout.Compute(
            _specs.Select(s => ToPx(s.CardBounds)).ToArray(),
            _specs.Select(s => s.PriceBounds is { } p ? ToPx(p) : (PxBox?)null).ToArray(),
            ToPx(_layer), dpi, _scaleMultiplier, _skip is not null, _showBounds,
            _specs.Select(s => new CardMarks(s.Chips?.Count ?? 0, s.Ribbon is not null)).ToArray(),
            _removal is { } rm ? ToPx(rm.Coin) : null);

        var badges = new List<BadgeVm>(_specs.Count);
        var ribbons = new List<RibbonVm>();
        var chipStacks = new List<ChipStackVm>();
        for (var i = 0; i < _specs.Count; i++)
        {
            var (s, b) = (_specs[i], frame.Badges[i]);
            badges.Add(new BadgeVm(b.Box.X, b.Box.Y, b.Scale, s.TierText, s.ScoreText, s.IsBest, s.IsEstimate, s.IsUpgraded, s.Ribbon is not null));
            if (frame.Ribbons[i] is { } r) ribbons.Add(new RibbonVm(r.Box.X, r.Box.Y, r.Scale, s.Ribbon!));
            // The layout caps the stack at two rows; show the same chips it made room for.
            if (frame.Chips[i] is { } c) chipStacks.Add(new ChipStackVm(c.Box.X, c.Box.Y, c.Box.Width, c.Scale, s.Chips!.Take(c.Count).ToArray()));
        }
        Model.Badges = badges;
        Model.Ribbons = ribbons;
        Model.ChipStacks = chipStacks;
        Model.Bounds = frame.CardOutlines.Select(r => new BoundsVm(r.X, r.Y, r.Width, r.Height, false))
            .Concat(frame.PriceOutlines.Select(r => new BoundsVm(r.X, r.Y, r.Width, r.Height, true))).ToArray();
        Model.Skip = frame.Skip is { } sk ? new SkipVm(sk.Box.X, sk.Box.Y, sk.Scale, _skip!.ScoreText, _skip.Reason) : null;
        Model.Removal = frame.Removal is { } m ? new MarkerVm(m.Box.X, m.Box.Y, m.Scale, _removal!.Text) : null;
    }

    private static PxBox ToPx(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    internal static string FormatScore(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
}
