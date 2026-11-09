using System.Windows.Threading;
using Sts2Overlay.App.Views;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// Gets the overlay out of the way of the game's own hover tips (see Views/HoverYield.cs for why z-order can't).
///
/// <code>
///  every 50 ms while the overlay is shown:
///  GetCursorPos ─▶ on a hover-tip zone of a shown layer, or on the top bar / relic row?
///               ─▶ HoverYieldState (hide at once, show again 300 ms after leaving)
///               ─▶ every layer fades out / in together
/// </code>
///
/// <para>All layers move together: a tip from one layer's zone (an event option) can land on another layer's marks,
/// and a single state means one rule for the player to learn: "point at something, the overlay steps aside".</para>
/// <para>Polling, not mouse hooks: our layers are click-through (WS_EX_TRANSPARENT), so they never see the mouse, and a
/// global low-level hook would sit in every mouse event of the whole system just to learn what one cheap
/// GetCursorPos call 20 times a second tells us.</para>
/// </summary>
public sealed partial class AppController
{
    private const int HoverPollMs = 50;

    private readonly HoverYieldState _hoverYield = new();
    private DispatcherTimer? _hoverTimer;

    private void InitHoverYield()
    {
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Input, _ui) { Interval = TimeSpan.FromMilliseconds(HoverPollMs) };
        _hoverTimer.Tick += (_, _) => CheckHover();
    }

    private IEnumerable<IHoverYield> HoverLayers()
    {
        yield return _badges;
        if (_mapLayer is not null) yield return _mapLayer;
        if (_eventMarkers is not null) yield return _eventMarkers;
    }

    /// <summary>Called from <see cref="UpdateVisibility"/>: watch the mouse only while the overlay is on screen.</summary>
    private void UpdateHoverWatch(bool overlayShown)
    {
        if (_hoverTimer is null) return;
        if (overlayShown)
        {
            if (!_hoverTimer.IsEnabled) _hoverTimer.Start();
            return;
        }
        _hoverTimer.Stop();
        // Hidden by Ctrl+F8 or the game lost focus while a tip was up: come back fully drawn next time.
        if (_hoverYield.Reset()) ApplyHoverHidden(false);
    }

    private void CheckHover()
    {
        var over = false;
        var layers = HoverLayers().Where(l => l.IsShowingMarks).ToArray();
        if (layers.Length > 0 && _tracker.Current is { CaptureRect.IsEmpty: false } win && GetCursorPos(out var p))
        {
            var r = win.CaptureRect;
            var game = new PxBox(r.X, r.Y, r.Width, r.Height);
            over = HoverGeometry.Contains([HoverGeometry.TopStrip(game)], p.X, p.Y, grow: 0)
                   || layers.Any(l => HoverGeometry.Contains(l.HoverZones(), p.X, p.Y));
        }
        if (_hoverYield.Update(over, Environment.TickCount64)) ApplyHoverHidden(_hoverYield.Hidden);
    }

    private void ApplyHoverHidden(bool hidden)
    {
        // Every layer, also the empty ones: marks that appear while a tip is up must not pop in over it.
        foreach (var layer in HoverLayers()) layer.SetHoverHidden(hidden);
    }
}
