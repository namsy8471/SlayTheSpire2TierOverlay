using Sts2Overlay.App.Integration;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The panel's offer is written one dispatcher pass after the badges (Integration/DeferredOffer.cs): only the newest
/// value may ever land, and a direct write (clear, stale mark, event options) beats anything still pending.
/// </summary>
public class DeferredOfferTests
{
    /// <summary>A fake panel + dispatcher: writes are recorded, posted actions run when the test says so.</summary>
    private sealed class Panel
    {
        public string Shown = "empty";
        public readonly List<(string Value, bool SelectTab)> Writes = [];
        public readonly Queue<Action> Queue = new();
        public readonly DeferredOffer<string> Slot;

        public Panel() => Slot = new DeferredOffer<string>(() => Shown, (v, tab) =>
        {
            Shown = v;
            Writes.Add((v, tab));
        }, Queue.Enqueue);

        public void RunPosted()
        {
            while (Queue.TryDequeue(out var a)) a();
        }
    }

    [Fact]
    public void Deferred_value_is_written_only_when_the_posted_pass_runs()
    {
        var p = new Panel();
        p.Slot.Defer("reward", selectTab: true);
        Assert.Equal("empty", p.Shown);       // the badges' pass: the panel still shows the old offer
        Assert.Equal("reward", p.Slot.Current); // but the controller already reads the new one
        Assert.True(p.Slot.IsPending);
        p.RunPosted();
        Assert.Equal([("reward", true)], p.Writes);
        Assert.False(p.Slot.IsPending);
    }

    [Fact]
    public void A_newer_offer_replaces_a_pending_one_and_the_old_callback_does_nothing()
    {
        var p = new Panel();
        p.Slot.Defer("reward A", selectTab: false);
        p.Slot.Defer("reward B", selectTab: false);
        p.RunPosted();
        Assert.Equal([("reward B", false)], p.Writes);
    }

    [Fact]
    public void An_old_callback_running_after_a_newer_one_writes_nothing()
    {
        var p = new Panel();
        p.Slot.Defer("reward A", selectTab: false);
        var oldCallback = p.Queue.Dequeue();
        p.Slot.Defer("reward B", selectTab: false);
        p.RunPosted();     // B lands
        oldCallback();     // A's pass comes late (out of order): must not bring A back
        Assert.Equal([("reward B", false)], p.Writes);
        Assert.Equal("reward B", p.Shown);
    }

    [Fact]
    public void Clearing_before_the_pass_wins_over_the_pending_offer()
    {
        var p = new Panel();
        p.Slot.Defer("shop", selectTab: false);
        p.Slot.Set("empty");  // ResetOffer / the screen closed before the panel's pass
        p.RunPosted();
        Assert.Equal([("empty", false)], p.Writes);
        Assert.Equal("empty", p.Shown);
    }

    [Fact]
    public void Stale_marking_reads_the_pending_offer_and_writes_at_once()
    {
        var p = new Panel();
        p.Slot.Defer("reward", selectTab: false);
        // ClearBadges: PanelOffer = PanelOffer with { IsStale = true } — built from the pending offer, not the old one.
        p.Slot.Set(p.Slot.Current + " (stale)");
        Assert.Equal("reward (stale)", p.Shown);
        p.RunPosted();
        Assert.Equal([("reward (stale)", false)], p.Writes);
    }

    [Fact]
    public void A_tab_switch_asked_by_a_replaced_offer_is_kept()
    {
        var p = new Panel();
        p.Slot.Defer("reward (reading 2 of 3)", selectTab: true); // the screen opened: switch to 추천
        p.Slot.Defer("reward", selectTab: false);                 // same screen, now complete: SelectTab says no again
        p.RunPosted();
        Assert.Equal([("reward", true)], p.Writes);
    }

    [Fact]
    public void A_direct_write_carries_a_pending_tab_switch_and_then_forgets_it()
    {
        var p = new Panel();
        p.Slot.Defer("reward", selectTab: true);
        p.Slot.Set("reward (stale)");
        p.Slot.Set("event");
        Assert.Equal([("reward (stale)", true), ("event", false)], p.Writes);
    }

    [Fact]
    public void Flush_writes_the_pending_value_now_and_the_posted_pass_then_does_nothing()
    {
        var p = new Panel();
        p.Slot.Defer("reward", selectTab: false);
        p.Slot.Flush();
        Assert.Equal("reward", p.Shown);
        p.Slot.Flush(); // nothing pending: no second write
        p.RunPosted();
        Assert.Equal([("reward", false)], p.Writes);
    }

    [Fact]
    public void Without_a_pending_value_current_is_what_the_panel_shows()
    {
        var p = new Panel { Shown = "event options" };
        Assert.Equal("event options", p.Slot.Current);
        Assert.False(p.Slot.IsPending);
    }
}
