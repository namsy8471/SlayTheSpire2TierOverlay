using System.Globalization;
using System.Windows;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;
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
    private readonly bool _excludeFromCapture;
    private IntPtr _hwnd;
    private PixelRect _layer;
    private IReadOnlyList<BadgeSpec> _specs = [];
    private (PixelRect Below, string Score)? _skip;
    private bool _showBounds;

    public BadgeWindow(bool excludeFromCapture)
    {
        _excludeFromCapture = excludeFromCapture;
        InitializeComponent();
        DataContext = Model;
        SourceInitialized += (_, _) =>
        {
            _hwnd = OverlayWindowHelper.Handle(this);
            OverlayWindowHelper.SetExStyle(_hwnd, WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, true);
            if (_excludeFromCapture) OverlayWindowHelper.SetExcludeFromCapture(_hwnd, true);
        };
        // Moving to a monitor with another DPI changes the physical→DIP factor: lay out again.
        DpiChanged += (_, _) => Relayout();
    }

    public BadgeLayerViewModel Model { get; } = new();

    public IntPtr Hwnd => _hwnd;

    public bool HasBadges => _specs.Count > 0 || _skip is not null;

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
    /// Shows badges for the given cards. <paramref name="skipScore"/> non-null adds a "스킵 추천" pill under the cards.
    /// <paramref name="showBounds"/> draws the card outlines (demo/debug).
    /// </summary>
    public void ShowBadges(IReadOnlyList<BadgeSpec> specs, string? skipScore, bool showBounds)
    {
        _specs = specs;
        _showBounds = showBounds;
        _skip = null;
        if (skipScore is not null && specs.Count > 0)
        {
            var l = specs.Min(s => s.CardBounds.X);
            var r = specs.Max(s => s.CardBounds.Right);
            var b = specs.Max(s => s.CardBounds.Bottom);
            _skip = (PixelRect.FromLtrb(l, b, r, b + 1), skipScore);
        }
        Relayout();
    }

    public void Clear() => ShowBadges([], null, false);

    private void Relayout()
    {
        if (_hwnd == IntPtr.Zero || _layer.IsEmpty)
        {
            Model.Badges = [];
            Model.Bounds = [];
            Model.Skip = null;
            return;
        }

        // GetDpiForWindow: the DPI of the monitor this window is on (per-monitor V2), e.g. 144 at 150 %.
        var dpi = Math.Max(1u, GetDpiForWindow(_hwnd)) / 96.0;
        var area = new Size(_layer.Width / dpi, _layer.Height / dpi);

        var badges = new List<BadgeVm>(_specs.Count);
        var bounds = new List<BoundsVm>(_specs.Count);
        foreach (var s in _specs)
        {
            var card = BadgeLayout.ToLayerDips(s.CardBounds, _layer, dpi);
            var scale = BadgeLayout.ScaleFor(card.Width) * _scaleMultiplier;
            var size = new Size(BadgeLayout.BaseWidth * scale, BadgeLayout.BaseHeight * scale);
            var pos = BadgeLayout.Place(card, size, area, gap: 6 * scale);
            badges.Add(new BadgeVm(pos.X, pos.Y, scale, s.TierText, s.ScoreText, s.IsBest, s.IsEstimate, s.IsUpgraded));
            if (_showBounds) bounds.Add(new BoundsVm(card.X, card.Y, card.Width, card.Height));
        }

        BadgeVm? skip = null;
        if (_skip is { } sk)
        {
            var row = BadgeLayout.ToLayerDips(sk.Below, _layer, dpi);
            var scale = badges.Count > 0 ? badges.Min(b => b.Scale) : 1.0;
            var size = new Size(BadgeLayout.BaseWidth * scale, BadgeLayout.BaseHeight * scale);
            var left = Math.Clamp(row.X + (row.Width - size.Width) / 2, 2, Math.Max(2, area.Width - size.Width - 2));
            var top = Math.Min(row.Y + 12 * scale, area.Height - size.Height - 2);
            skip = new BadgeVm(left, top, scale, "", sk.Score, true, false, false);
        }

        Model.Badges = badges;
        Model.Bounds = bounds;
        Model.Skip = skip;
    }

    internal static string FormatScore(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
}
