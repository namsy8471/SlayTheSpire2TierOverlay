using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Vision = Sts2Overlay.Vision;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The life of a card screen's badges: held through a missed frame, gone soon after the screen really closes (also
/// over a perfectly still next screen), never redrawn after the log cleared them, the 추천 tab yanked only by a newly
/// opened screen, and a half-read reward row holding back its verdict.
/// </summary>
public class BadgeLifecycleTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 20, 0, 0, DateTimeKind.Utc);

    private static RecognizedCard C(string id) => new(new CardRef(id, 0), new PixelRect(0, 0, 10, 10), 0.95);

    private static RecognitionResult Offer(ScreenKind kind, params string[] ids) => new(kind, ids.Select(C).ToArray());

    // ---- A: the capture loop's "unchanged picture" skip and the tracker's hold, frame by frame ----

    private static Vision.RecognizedCard VCard(int slot, string id) =>
        new(slot, Vision.CardSlotKind.Reward, id, false, 0.95f, 0.4f, true, null, 0, true, 0.8f, default, default, null, null, []);

    private static Vision.ScreenRecognition VFrame(Vision.ScreenKind kind, params Vision.RecognizedCard[] cards) =>
        new(kind, cards, kind == Vision.ScreenKind.Other ? [] : cards, new Vision.ScreenGate(false, false, ""), 1, "");

    /// <summary>
    /// Replays (frame, picture changed?) at 3 Hz through the pipeline's skip rule and the tracker, like
    /// CapturePipeline.Step + VisionRecognizer. Returns what the overlay shows after each tick.
    /// </summary>
    private static List<Vision.TrackedScreen> ReplayScreens(IEnumerable<(Vision.ScreenRecognition Frame, bool Changed)> ticks)
    {
        var tracker = new Vision.RecognitionTracker();
        var shown = new List<Vision.TrackedScreen>();
        var lastSteady = false;
        var current = Vision.TrackedScreen.None;
        var t = T0;
        foreach (var (frame, changed) in ticks)
        {
            t += TimeSpan.FromSeconds(1 / 3.0);
            if (!CapturePipeline.CanSkip(manual: false, samePicture: !changed, lastSteady))
            {
                current = tracker.Update(frame, t);
                lastSteady = CapturePipeline.IsSteady(new RecognitionResult(ScreenKind.Other, []) { Unsettled = current.Unsettled });
            }
            shown.Add(current);
        }
        return shown;
    }

    private static List<Vision.ScreenKind> Replay(IEnumerable<(Vision.ScreenRecognition Frame, bool Changed)> ticks)
        => ReplayScreens(ticks).Select(s => s.Kind).ToList();

    [Fact]
    public void A_bought_shop_card_leaves_even_when_the_picture_then_stays_still()
    {
        static Vision.RecognizedCard Shop(int slot, string id) => VCard(slot, id) with { SlotKind = Vision.CardSlotKind.ShopCharacter };
        Vision.ScreenRecognition ShopFrame(bool bEmpty, params Vision.RecognizedCard[] cards)
        {
            var slots = Enumerable.Range(0, 7).Select(i => cards.FirstOrDefault(c => c.Slot == i)
                ?? Shop(i, "?") with { CardId = null, Accepted = false, SlotEmpty = bEmpty && i == 1 }).ToList();
            return new(Vision.ScreenKind.Shop, cards, slots, new Vision.ScreenGate(false, true, ""), 1, "");
        }
        var before = ShopFrame(false, Shop(0, "A"), Shop(1, "B"));
        var after = ShopFrame(true, Shop(0, "A"));
        // B is bought: its slot turns to bare rug once, and nothing else moves afterwards.
        var shown = ReplayScreens([(before, true), (after, true), (after, false), (after, false)]);
        Assert.Equal(["A", "B"], shown[1].Cards.Select(c => c.CardId)); // one empty frame is not enough ...
        Assert.Equal(["A"], shown[2].Cards.Select(c => c.CardId));      // ... but the still frame after it is looked at
    }

    [Fact]
    public void A_closed_reward_leaves_within_three_frames_even_when_the_next_screen_is_still()
    {
        var reward = VFrame(Vision.ScreenKind.RewardCardSelect, VCard(0, "A"), VCard(1, "B"), VCard(2, "C"));
        var map = VFrame(Vision.ScreenKind.Other);
        // The reward is read, then stays still (skipped); it closes and the map fades in, then nothing moves.
        var shown = Replay([(reward, true), (reward, false), (reward, false), (map, true), (map, false), (map, false), (map, false), (map, false)]);
        Assert.Equal(Vision.ScreenKind.RewardCardSelect, shown[4]); // second missed frame: still held
        Assert.Equal(Vision.ScreenKind.Other, shown[5]);            // third: gone, 0.67 s after the first miss
        Assert.All(shown.Skip(5), k => Assert.Equal(Vision.ScreenKind.Other, k));
    }

    [Fact]
    public void A_missed_frame_after_a_long_still_reward_does_not_blink()
    {
        var reward = VFrame(Vision.ScreenKind.RewardCardSelect, VCard(0, "A"), VCard(1, "B"), VCard(2, "C"));
        var hoverBlip = VFrame(Vision.ScreenKind.Other); // a big tooltip: no row this frame
        var ticks = new List<(Vision.ScreenRecognition, bool)> { (reward, true) };
        ticks.AddRange(Enumerable.Repeat((reward, false), 30)); // 10 s still: skipped
        ticks.Add((hoverBlip, true));
        ticks.Add((reward, true));
        Assert.All(Replay(ticks), k => Assert.Equal(Vision.ScreenKind.RewardCardSelect, k));
    }

    // ---- B: after the log cleared the badges ----

    [Fact]
    public void Results_from_before_the_reset_are_dropped()
    {
        var life = new OfferLifecycle();
        var shown = Offer(ScreenKind.Reward, "A", "B", "C");
        life.Forget(shown, epoch: 1, T0);
        Assert.False(life.Accept(shown with { Epoch = 0, Unsettled = true }, T0.AddSeconds(0.1)));
        Assert.False(life.Accept(Offer(ScreenKind.Reward, "X", "Y", "Z") with { Epoch = 0 }, T0.AddSeconds(0.2)));
    }

    [Fact]
    public void The_closing_screen_is_suppressed_but_a_new_card_screen_shows_at_once()
    {
        var life = new OfferLifecycle();
        life.Forget(Offer(ScreenKind.Reward, "A", "B", "C"), epoch: 1, T0);
        // The picked card flies away, the other two fade: a fresh frame still reads them.
        Assert.False(life.Accept(Offer(ScreenKind.Reward, "A", "C") with { Epoch = 1 }, T0.AddSeconds(0.1)));
        // The second card reward of the same reward list: other cards, shown at once.
        Assert.True(life.Accept(Offer(ScreenKind.Reward, "D", "E", "F") with { Epoch = 1 }, T0.AddSeconds(0.4)));
    }

    [Fact]
    public void A_still_open_screen_comes_back_after_the_suppression()
    {
        var life = new OfferLifecycle();
        life.Forget(Offer(ScreenKind.Reward, "A", "B", "C"), epoch: 1, T0);
        Assert.False(life.Accept(Offer(ScreenKind.Reward, "A", "B", "C") with { Epoch = 1 }, T0.AddSeconds(1)));
        Assert.True(life.Accept(Offer(ScreenKind.Reward, "A", "B", "C") with { Epoch = 1 }, T0 + life.SuppressFor));
    }

    [Fact]
    public void Seeing_the_screen_closed_ends_the_suppression()
    {
        var life = new OfferLifecycle();
        life.Forget(Offer(ScreenKind.Reward, "A", "B", "C"), epoch: 1, T0);
        Assert.True(life.Accept(RecognitionResult.Other with { Epoch = 1 }, T0.AddSeconds(0.3)));
        // The same reward opened again (the player reopened it from the reward list).
        Assert.True(life.Accept(Offer(ScreenKind.Reward, "A", "B", "C") with { Epoch = 1 }, T0.AddSeconds(0.6)));
    }

    // ---- H: the 추천 tab ----

    [Fact]
    public void The_tab_switches_once_per_opened_screen()
    {
        var life = new OfferLifecycle();
        Assert.True(life.SelectTab(Offer(ScreenKind.Shop, "A", "B", "C")));
        // Prices / colours / relics / a bought card change the offer, not the screen.
        Assert.False(life.SelectTab(Offer(ScreenKind.Shop, "A", "B", "C") with { Relics = [new RecognizedShopRelic("RELIC.X", null, default, 1)] }));
        Assert.False(life.SelectTab(Offer(ScreenKind.Shop, "A", "C")));
        // The log cleared the badges but the screen is still open: no second yank.
        life.Forget(Offer(ScreenKind.Shop, "A", "C"), epoch: 1, T0);
        Assert.False(life.SelectTab(Offer(ScreenKind.Shop, "A", "C")));
        // Closed for real, then a new screen opens.
        life.ScreenClosed();
        Assert.True(life.SelectTab(Offer(ScreenKind.Reward, "D", "E", "F")));
    }

    [Fact]
    public void A_card_read_late_is_the_same_screen_but_other_cards_are_a_new_one()
    {
        var life = new OfferLifecycle();
        Assert.True(life.SelectTab(Offer(ScreenKind.Reward, "A", "B")));
        Assert.False(life.SelectTab(Offer(ScreenKind.Reward, "A", "B", "C")));
        Assert.True(life.SelectTab(Offer(ScreenKind.Reward, "D", "E", "F")));
    }

    // ---- D: a reward row with an unread card ----

    [Fact]
    public void A_half_read_row_holds_its_verdict_then_shows_it()
    {
        var life = new OfferLifecycle();
        var partial = Offer(ScreenKind.Reward, "A", "B") with { Slots = 3, ScreenId = 7 };
        Assert.Equal(PartialVerdict.Hold, life.Verdict(partial, T0));
        Assert.Equal(T0 + life.PartialWait, life.VerdictDue(partial));
        Assert.Equal(PartialVerdict.Hold, life.Verdict(partial, T0.AddSeconds(1.9)));
        Assert.Equal(PartialVerdict.ShowIncomplete, life.Verdict(partial, T0.AddSeconds(2)));
        Assert.Equal(PartialVerdict.Complete, life.Verdict(partial with { Cards = Offer(ScreenKind.Reward, "A", "B", "C").Cards }, T0.AddSeconds(2.1)));
        // Another screen starts its own wait; shops and unknown slot counts never wait.
        Assert.Equal(PartialVerdict.Hold, life.Verdict(partial with { ScreenId = 8 }, T0.AddSeconds(3)));
        Assert.Equal(PartialVerdict.Complete, life.Verdict(Offer(ScreenKind.Shop, "A") with { Slots = 7 }, T0));
        Assert.Equal(PartialVerdict.Complete, life.Verdict(Offer(ScreenKind.Reward, "A", "B"), T0));
    }

    [Fact]
    public void A_held_verdict_hides_the_pick_and_says_what_is_being_read()
    {
        using var lang = Loc.Use(Lang.Eng);
        var r = Offer(ScreenKind.Reward, "A", "B") with { Slots = 3 };
        var offer = new OfferSectionVm
        {
            Rows = [new OfferRowVm { Name = "A", IsBest = true }, new OfferRowVm { Name = "B" }, new OfferRowVm { Name = "Skip", IsSkip = true }],
            Footer = "Pick: A",
        };
        BadgeSpec[] badges = [new(default, "A", "80", true, false, false, Ribbon: "Pick!"), new(default, "B", "60", false, false, false)];
        var skip = new SkipSpec("40", null);

        var (held, heldBadges, heldSkip) = AppController.PartialRow(r, PartialVerdict.Hold, offer, badges, skip);
        Assert.Equal("Reading 2 of 3 cards…", held.Footer);
        Assert.DoesNotContain(held.Rows, x => x.IsBest);
        Assert.All(heldBadges, b => Assert.True(!b.IsBest && b.Ribbon is null));
        Assert.Equal(["A", "B"], heldBadges.Select(b => b.TierText)); // tier badges stay
        Assert.Null(heldSkip);

        var (late, lateBadges, lateSkip) = AppController.PartialRow(r, PartialVerdict.ShowIncomplete, offer, badges, skip);
        Assert.Equal("Pick: A\n1 card not read (judged from 2 of 3)", late.Footer);
        Assert.Same(badges, lateBadges);
        Assert.Same(skip, lateSkip);
    }

    [Fact]
    public void The_reading_note_is_korean_in_korean()
    {
        using var lang = Loc.Use(Lang.Kor);
        var r = Offer(ScreenKind.Reward, "A", "B") with { Slots = 3 };
        var (held, _, _) = AppController.PartialRow(r, PartialVerdict.Hold, new OfferSectionVm(), [], null);
        Assert.Equal("3장 중 2장 인식 중…", held.Footer);
    }

    // ---- G: capture pause ----

    [Fact]
    public void Capture_pauses_after_a_few_seconds_without_an_overlay_and_resumes_at_once()
    {
        var clock = new CapturePauseClock { After = TimeSpan.FromSeconds(3) };
        Assert.False(clock.ShouldPause(true, T0, out var recheck));
        Assert.Null(recheck);
        Assert.False(clock.ShouldPause(false, T0.AddSeconds(1), out recheck)); // alt-tab: keep the session for now
        Assert.Equal(TimeSpan.FromSeconds(3), recheck);
        Assert.False(clock.ShouldPause(false, T0.AddSeconds(2.5), out recheck));
        Assert.Equal(TimeSpan.FromSeconds(1.5), recheck);
        Assert.True(clock.ShouldPause(false, T0.AddSeconds(4), out recheck));
        Assert.Null(recheck);
        Assert.False(clock.ShouldPause(true, T0.AddSeconds(100), out _)); // back in front: at once
        Assert.False(clock.ShouldPause(false, T0.AddSeconds(101), out _)); // and a new grace period
    }
}
