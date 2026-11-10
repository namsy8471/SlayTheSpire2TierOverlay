using System.Windows;
using System.Windows.Threading;
using Sts2Overlay.App.Interop;

namespace Sts2Overlay.App.Views;

/// <summary>
/// Gives an empty map or event layer's full-screen surface back. After <see cref="DelayMs"/> with nothing to draw the
/// window is cut to 1x1 px at the game's corner (<see cref="LayerPlacement.SetTrimmed"/>) and, a moment later, hidden;
/// just before its next marks it is hidden (if it still shows), put back to full size, the marks go in, and only then
/// is it shown again.
///
/// <para>Measured with tools/BadgeRenderBench (--layer-mem, --layer-restore, --layer-cycles), 4K:</para>
/// <list type="bullet">
/// <item>A WPF layered window keeps its full-size surface while hidden (about 60 MB of working set); only a smaller
/// window gives it back, and only once WPF has drawn a frame at the new size. A hidden window draws nothing, so the cut
/// is made while the window is still shown (one 1x1 frame, ~12 ms of UI thread under load), and the hide comes after
/// that frame (<see cref="HideAfterMs"/>).</item>
/// <item>Growing a SHOWN layered window back costs the UI thread 100-350 ms; a hidden one ~10 ms. So it always comes back
/// hidden and is shown last: ~12 ms from the marks to the Loaded callback, against ~3 ms for a layer left at full size.
/// That is why the card badges, whose latency matters most, are never trimmed.</item>
/// </list>
/// <para>Nothing the player sees changes: an empty layer draws nothing at either size, shown or not; the frame it shows
/// again with is drawn after its marks are in; it keeps following the game while trimmed (its corner moves with it, so
/// it stays on the game's monitor), and the controller keeps its place in the topmost order.</para>
/// <para>The controller turns the layer on and off with <see cref="SetOn"/> instead of Show/Hide: the window is shown
/// while it is on and not trimmed (or still drawing its 1x1 frame). When it knows marks are coming (the map is about to
/// open, an event room is open) it holds the layer ready with <see cref="SetReady"/>, so they come up at full speed;
/// the layer is trimmed only between such screens (fights, rewards, shops).</para>
/// </summary>
internal sealed class LayerTrimmer
{
    public const int DelayMs = 5000;

    /// <summary>Time for WPF to draw the 1x1 frame (and free the big surface) before the window is hidden.</summary>
    public const int HideAfterMs = 300;

    private readonly Window _window;
    private readonly LayerPlacement _placement;
    private readonly Func<IntPtr> _hwnd;
    private readonly EmptyLayerTrim _trim = new(DelayMs);
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _hideTimer;

    public LayerTrimmer(Window window, LayerPlacement placement, Func<IntPtr> hwnd)
    {
        _window = window;
        _placement = placement;
        _hwnd = hwnd;
        _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(DelayMs) };
        _timer.Tick += (_, _) => Tick();
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(HideAfterMs) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            ApplyShown();
        };
        window.Closed += (_, _) =>
        {
            _timer.Stop();
            _hideTimer.Stop();
        };
    }

    /// <summary>The controller wants the layer over the game (it is then shown unless trimmed).</summary>
    public bool IsOn { get; private set; }

    public bool IsTrimmed => _placement.Trimmed;

    private static long Now => Environment.TickCount64;

    public void SetOn(bool on)
    {
        IsOn = on;
        ApplyShown();
    }

    /// <summary>
    /// The controller expects marks soon (the map is about to open, an event room is open): bring a trimmed layer back
    /// now, shown and empty, so the marks themselves come up as fast as on a layer that was never trimmed (coming back
    /// takes ~150-200 ms on screen under load, an untrimmed layer ~25-45 ms). Not trimmed again while held.
    /// </summary>
    public void SetReady(bool ready)
    {
        if (_trim.SetHold(ready, _placement.Trimmed, Now))
        {
            Untrim();
            ApplyShown();
        }
        UpdateTimer();
    }

    /// <summary>
    /// Before the layer lays out new content (or after it was placed): full size again when it has marks, else start
    /// counting down. Call <see cref="AfterContent"/> once the content is in place.
    /// </summary>
    public void BeforeContent(bool hasMarks)
    {
        if (_trim.OnContent(hasMarks, _placement.Trimmed, Now)) Untrim();
        UpdateTimer();
    }

    private void Untrim()
    {
        // Still shown at 1x1 (within HideAfterMs of the cut): hide first, a shown layered window is slow to grow.
        _hideTimer.Stop();
        if (_window.IsVisible) _window.Hide();
        _placement.SetTrimmed(_hwnd(), false);
    }

    private void UpdateTimer()
    {
        if (!_trim.Waiting(_placement.Trimmed)) _timer.Stop();
        else if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>Shows a layer that came back from trimming, now that its marks are laid out.</summary>
    public void AfterContent() => ApplyShown();

    private void ApplyShown()
    {
        // Trimmed and still drawing its 1x1 frame: the hide timer decides.
        if (_hideTimer.IsEnabled) return;
        var show = IsOn && !_placement.Trimmed;
        if (show && !_window.IsVisible) _window.Show();
        else if (!show && _window.IsVisible) _window.Hide();
    }

    private void Tick()
    {
        // Only a shown layer is cut (see the class notes); a hidden one waits for its next tick while shown.
        if (_window.IsVisible && _trim.ShouldTrim(_placement.Trimmed, Now) && _placement.SetTrimmed(_hwnd(), true))
            _hideTimer.Start();
        UpdateTimer();
    }
}
