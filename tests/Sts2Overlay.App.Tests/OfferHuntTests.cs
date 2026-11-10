using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The hunt for a hinted reward / shop screen (Integration/OfferHunt.cs): dense polling from the hint change until the
/// badges show, the hint ends or the budget passes, and never again for the same hint change. Plus the capture worker's
/// sleep between looks (<see cref="CapturePipeline.NextWait"/>), which makes a rate the real rate.
/// </summary>
public class OfferHuntTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static OfferHunt Hunt() => new() { Budget = TimeSpan.FromSeconds(8) };

    [Fact]
    public void Starts_on_a_change_to_an_offer_hint_and_says_when_the_budget_ends()
    {
        var hunt = Hunt();
        Assert.False(hunt.Update(true, ScreenHint.Combat, T0, offerShown: false, T0, out var recheck)); // not an offer hint
        Assert.Null(recheck);

        var change = T0.AddSeconds(30);
        Assert.True(hunt.Update(true, ScreenHint.RewardLikely, change, false, change, out recheck));
        Assert.Equal(TimeSpan.FromSeconds(8), recheck);
        Assert.True(hunt.Update(true, ScreenHint.RewardLikely, change, false, change.AddSeconds(3), out recheck)); // a repeated snapshot
        Assert.Equal(TimeSpan.FromSeconds(5), recheck);
        Assert.True(hunt.Hunting);
    }

    [Fact]
    public void Shop_hint_hunts_too()
    {
        var hunt = Hunt();
        Assert.True(hunt.Update(true, ScreenHint.ShopLikely, T0, false, T0.AddSeconds(1), out _));
    }

    [Fact]
    public void Ends_when_the_badges_show_and_stays_off_when_they_go_away_under_the_same_hint()
    {
        var hunt = Hunt();
        Assert.True(hunt.Update(true, ScreenHint.RewardLikely, T0, false, T0.AddSeconds(1), out _));
        Assert.False(hunt.Update(true, ScreenHint.RewardLikely, T0, offerShown: true, T0.AddSeconds(2.5), out var recheck));
        Assert.Null(recheck);
        Assert.Equal(HuntEnd.Found, hunt.LastEnd);
        // Card picked or a held frame: badges gone, the hint unchanged. The normal 3 Hz watch goes on, no new hunt.
        Assert.False(hunt.Update(true, ScreenHint.RewardLikely, T0, false, T0.AddSeconds(3), out _));
    }

    [Fact]
    public void Ends_when_the_hint_leaves_the_offer_hints()
    {
        var hunt = Hunt();
        Assert.True(hunt.Update(true, ScreenHint.RewardLikely, T0, false, T0.AddSeconds(1), out _));
        var mapAt = T0.AddSeconds(2);
        Assert.False(hunt.Update(true, ScreenHint.Map, mapAt, false, mapAt, out var recheck));
        Assert.Null(recheck);
        Assert.Equal(HuntEnd.HintLeft, hunt.LastEnd);
    }

    [Fact]
    public void Ends_when_the_budget_passes_without_a_screen()
    {
        var hunt = Hunt();
        Assert.True(hunt.Update(true, ScreenHint.ShopLikely, T0, false, T0.AddSeconds(7.9), out _));
        Assert.False(hunt.Update(true, ScreenHint.ShopLikely, T0, false, T0.AddSeconds(8), out var recheck));
        Assert.Null(recheck);
        Assert.Equal(HuntEnd.RanOut, hunt.LastEnd);
        Assert.False(hunt.Update(true, ScreenHint.ShopLikely, T0, false, T0.AddMinutes(5), out _)); // the hint stays: still off
    }

    [Fact]
    public void Does_not_restart_without_a_new_hint_change_but_does_after_one()
    {
        var hunt = Hunt();
        // The hint was already RewardLikely when the app started (its first snapshot is the change): one bounded hunt.
        Assert.True(hunt.Update(true, ScreenHint.RewardLikely, T0, false, T0, out _));
        Assert.False(hunt.Update(true, ScreenHint.RewardLikely, T0, false, T0.AddSeconds(9), out _));
        // The game regains focus, snapshots keep coming with the same hint: nothing starts again.
        for (var s = 10; s < 60; s += 5) Assert.False(hunt.Update(true, ScreenHint.RewardLikely, T0, false, T0.AddSeconds(s), out _));

        // The next fight ends: a new change, a new hunt.
        var next = T0.AddSeconds(90);
        Assert.False(hunt.Update(true, ScreenHint.Combat, next.AddSeconds(-20), false, next.AddSeconds(-1), out _));
        Assert.True(hunt.Update(true, ScreenHint.RewardLikely, next, false, next, out _));
    }

    [Fact]
    public void Badges_already_up_when_the_hint_comes_need_no_hunt()
    {
        var hunt = Hunt();
        Assert.False(hunt.Update(true, ScreenHint.RewardLikely, T0, offerShown: true, T0, out var recheck));
        Assert.Null(recheck);
        Assert.False(hunt.Update(true, ScreenHint.RewardLikely, T0, false, T0.AddSeconds(1), out _));
        Assert.Equal(HuntEnd.None, hunt.LastEnd); // never hunted
    }

    [Fact]
    public void Off_when_the_rate_setting_is_zero()
    {
        var hunt = Hunt();
        Assert.False(hunt.Update(false, ScreenHint.RewardLikely, T0, false, T0, out var recheck));
        Assert.Null(recheck);
        Assert.False(hunt.Hunting);

        // Turned off in the middle of a hunt.
        var on = Hunt();
        Assert.True(on.Update(true, ScreenHint.RewardLikely, T0, false, T0, out _));
        Assert.False(on.Update(false, ScreenHint.RewardLikely, T0, false, T0.AddSeconds(1), out _));
        Assert.Equal(HuntEnd.Off, on.LastEnd);
    }

    [Fact]
    public void No_hunt_before_any_hint_change_and_none_longer_than_the_budget_after_a_clock_jump()
    {
        var hunt = Hunt();
        Assert.False(hunt.Update(true, ScreenHint.RewardLikely, default, false, T0, out _)); // the hint never changed
        Assert.False(hunt.Update(true, ScreenHint.RewardLikely, T0, false, T0.AddSeconds(-30), out _)); // clock set back
    }

    // ---- the worker's sleep between looks ----

    private static long Ms(double ms) => TimeSpan.FromMilliseconds(ms).Ticks;

    [Fact]
    public void The_worker_sleeps_the_rest_of_the_interval_not_a_whole_one_after_each_step()
    {
        // 20 Hz, a 13.5 ms step just ended: the next look starts 50 ms after this one started.
        Assert.Equal(TimeSpan.FromMilliseconds(36.5), CapturePipeline.NextWait(Ms(50), Ms(13.5), 0));
        // 3 Hz: 333 ms between starts, not 333 + the step.
        Assert.Equal(TimeSpan.FromMilliseconds(320), CapturePipeline.NextWait(Ms(333), Ms(13), 0));
    }

    [Fact]
    public void A_slow_step_still_leaves_half_an_interval_of_rest()
    {
        // A 60 ms step at 20 Hz (loaded PC): not back to back, 25 ms of rest.
        Assert.Equal(TimeSpan.FromMilliseconds(25), CapturePipeline.NextWait(Ms(50), Ms(60), 0));
    }

    [Fact]
    public void A_rate_change_long_after_the_last_step_looks_at_once()
    {
        // The hunt starts 200 ms after the last 3 Hz look: the 20 Hz look is overdue, no wait.
        Assert.Equal(TimeSpan.Zero, CapturePipeline.NextWait(Ms(50), Ms(200), Ms(187)));
        // The worker has never run (timestamps 0 = long ago).
        Assert.Equal(TimeSpan.Zero, CapturePipeline.NextWait(Ms(333), long.MaxValue / 4, long.MaxValue / 4));
    }
}
