using System.Windows.Media;
using System.Windows.Threading;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.ViewModels;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// The 추천 tab's offer goes through <see cref="DeferredOffer{T}"/>: a new card reward / shop offer is written to the panel
/// one dispatcher pass after its badges, so the badge layer reaches the screen without waiting for the panel's rows.
/// <code>
///  ApplyRecognition ─ badges ─▶ Render pass (badges laid out, handed to the render thread) ─▶ Loaded (perf "painted")
///        └─ DeferPanelOffer ──────────────┴─ posts ──────────────────────────────────────────▶ Background: panel rows
/// </code>
/// Every other write (clear, stale mark, event options, language refresh) goes through <see cref="PanelOffer"/> and is
/// immediate; it also drops a still-pending deferred offer, so old data can never land after it.
/// </summary>
public sealed partial class AppController
{
    private DeferredOffer<OfferSectionVm>? _panelOffer;

    private DeferredOffer<OfferSectionVm> PanelOfferSlot => _panelOffer ??= new DeferredOffer<OfferSectionVm>(
        () => _vm.Offer,
        (offer, selectTab) =>
        {
            _vm.Offer = offer;
            if (selectTab) _vm.SelectedTab = PanelTab.Offer;
        },
        AfterNextRender);

    /// <summary>
    /// Runs <paramref name="action"/> at Background priority once the next render pass has started. Background alone
    /// sits below Render (layout + render of the badge change) and Loaded (the perf log's "painted" mark), but when WPF
    /// is still waiting for the previous frame to be presented no render pass is queued yet, and a Background item then
    /// runs first: the panel's rows would be laid out in the badges' pass after all. Posting it from
    /// CompositionTarget.Rendering (raised after that pass's layout) rules that out; the timer covers a render pass that
    /// never comes (no display, a locked session).
    /// </summary>
    private void AfterNextRender(Action action)
    {
        var posted = false;
        EventHandler? onRendering = null;
        DispatcherTimer? fallback = null;
        void Post()
        {
            if (posted) return;
            posted = true;
            CompositionTarget.Rendering -= onRendering;
            fallback?.Stop();
            _ui.BeginInvoke(action, DispatcherPriority.Background);
        }
        onRendering = (_, _) => Post();
        fallback = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Post(), _ui);
        CompositionTarget.Rendering += onRendering;
    }

    /// <summary>The 추천 tab's offer, counting one still waiting for its pass. Setting it writes at once.</summary>
    private OfferSectionVm PanelOffer
    {
        get => PanelOfferSlot.Current;
        set => PanelOfferSlot.Set(value);
    }

    /// <summary>A new card reward / shop offer: the panel shows it (and switches tab) after the badges are drawn.</summary>
    private void DeferPanelOffer(OfferSectionVm offer, bool selectTab) => PanelOfferSlot.Defer(offer, selectTab);
}
