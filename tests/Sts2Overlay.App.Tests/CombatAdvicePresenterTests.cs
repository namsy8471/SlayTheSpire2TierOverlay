using System.IO;
using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Integration.CombatAdvice;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Tracking;
using ScreenHint = Sts2Overlay.App.Integration.ScreenHint;
using TrackerScreen = Sts2Overlay.GameState.Tracking.ScreenHint;

namespace Sts2Overlay.App.Tests;

/// <summary>The "이번 턴 추천 순서" section: helper message → rows, names, targets, closing row, and when it is hidden.</summary>
public class CombatAdvicePresenterTests
{
    /// <summary>Made-up names, so the tests do not depend on the game's localization.</summary>
    private sealed class Names : ICombatNames
    {
        public string Card(string id, bool upgraded) => (id switch
        {
            "BASH" => Loc.T("강타", "Bash"),
            "DEFEND_IRONCLAD" => Loc.T("수비", "Defend"),
            _ => id,
        }) + (upgraded ? "+" : "");

        public string? Potion(string id) => id == "FIRE_POTION" ? Loc.T("화염 포션", "Fire Potion") : null;

        public string? Monster(string id) => id == "JAW_WORM" ? Loc.T("턱벌레", "Jaw Worm") : null;
    }

    private static readonly Names TestNames = new();

    private static CombatAdviceMessage Advice(CombatEnding ending, params CombatAdviceStep[] steps)
        => new(CombatAdviceState.Advice) { Turn = 2, Energy = 3, Hp = 61, Block = 0, Played = 1, Steps = steps, Ending = ending, Ms = 2.4 };

    private static readonly CombatAdviceStep Bash = new(CombatStepKind.Card, "BASH", Hand: 2, Target: "JAW_WORM", TargetSlot: 0);
    private static readonly CombatAdviceStep DefendPlus = new(CombatStepKind.Card, "DEFEND_IRONCLAD", Upgraded: true, Hand: 0);

    [Fact]
    public void Shows_the_numbered_order_with_targets_and_the_end_turn_row()
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.EndTurn, Bash, DefendPlus), CombatAdvicePlace.Fight, TestNames);

        Assert.True(vm.HasContent);
        Assert.True(vm.ShowSteps);
        Assert.Equal("이번 턴 추천 순서 · 2턴", vm.Title);
        Assert.Equal(["1.", "2.", ""], vm.Steps.Select(s => s.NumberText));
        Assert.Equal(["강타 → 턱벌레", "수비+", "턴 종료"], vm.Steps.Select(s => s.Text));
        Assert.Equal([false, false, true], vm.Steps.Select(s => s.IsEnding));
        Assert.False(vm.IsUncertain);
        Assert.Equal("에너지 3 · 체력 61 · 방어도 0 · 이번 턴 1번 행동함 · 2.4 ms", vm.DetailTip);
        Assert.Equal("손패 왼쪽에서 3번째 카드", vm.Steps[0].Tip);
    }

    [Fact]
    public void English_rows()
    {
        using var _ = Loc.Use(Lang.Eng);
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.Win, Bash, DefendPlus), CombatAdvicePlace.Fight, TestNames);

        Assert.Equal("This turn's plays · Turn 2", vm.Title);
        Assert.Equal(["Bash → Jaw Worm", "Defend+", "Finish!"], vm.Steps.Select(s => s.Text));
        Assert.Equal("Card 3 from the left in your hand", vm.Steps[0].Tip);
        Assert.Equal("uncertain", CombatAdvicePresenter.UncertainTag);
    }

    [Theory]
    [InlineData(CombatEnding.EndTurn, "턴 종료")]
    [InlineData(CombatEnding.Win, "마무리!")]
    [InlineData(CombatEnding.AdviseAfterPlay, "… 이 카드를 낸 뒤 다시 추천")]
    [InlineData(CombatEnding.None, null)]
    public void The_closing_row_follows_the_ending(CombatEnding ending, string? last)
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(Advice(ending, DefendPlus), CombatAdvicePlace.Fight, TestNames);

        Assert.Equal(last is null ? 1 : 2, vm.Steps.Count);
        if (last is not null) Assert.Equal(last, vm.Steps[^1].Name);
        else Assert.Equal("수비+", vm.Steps[^1].Name);
    }

    [Fact]
    public void A_turn_with_nothing_to_play_shows_only_end_turn()
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.EndTurn), CombatAdvicePlace.Fight, TestNames);

        Assert.Equal("턴 종료", Assert.Single(vm.Steps).Name);
    }

    [Fact]
    public void Target_position_is_added_only_when_the_helper_says_the_name_is_ambiguous()
    {
        using var _ = Loc.Use(Lang.Kor);
        Assert.Equal("턱벌레", CombatAdvicePresenter.TargetText(Bash with { TargetSlot = 1 }, TestNames));
        Assert.Equal("턱벌레 (2번째)", CombatAdvicePresenter.TargetText(Bash with { TargetSlot = 1, TargetAmbiguous = true }, TestNames));
        Assert.Equal("턱벌레", CombatAdvicePresenter.TargetText(Bash with { TargetSlot = null, TargetAmbiguous = true }, TestNames));
        Assert.Null(CombatAdvicePresenter.TargetText(DefendPlus, TestNames));

        using var en = Loc.Use(Lang.Eng);
        Assert.Equal("Jaw Worm (#2)", CombatAdvicePresenter.TargetText(Bash with { TargetSlot = 1, TargetAmbiguous = true }, TestNames));
    }

    [Fact]
    public void A_monster_without_a_name_is_shown_by_its_position_or_its_id()
    {
        var unknown = new CombatAdviceStep(CombatStepKind.Card, "BASH", Target: "MADE_UP_SLIME", TargetSlot: 2);
        using (Loc.Use(Lang.Kor)) Assert.Equal("3번째 적", CombatAdvicePresenter.TargetText(unknown, TestNames));
        using (Loc.Use(Lang.Eng)) Assert.Equal("enemy 3", CombatAdvicePresenter.TargetText(unknown, TestNames));
        using (Loc.Use(Lang.Eng)) Assert.Equal("Made Up Slime", CombatAdvicePresenter.TargetText(unknown with { TargetSlot = null }, TestNames));
        using (Loc.Use(Lang.Eng)) Assert.Equal("enemy 1", CombatAdvicePresenter.TargetText(new CombatAdviceStep(CombatStepKind.Card, "BASH", TargetSlot: 0), TestNames));
    }

    [Fact]
    public void Potions_get_a_tag_and_unknown_kinds_show_the_raw_id()
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.None,
            new CombatAdviceStep(CombatStepKind.Potion, "FIRE_POTION", Target: "JAW_WORM", TargetSlot: 0),
            new CombatAdviceStep(CombatStepKind.Potion, "MADE_UP_POTION"),
            new CombatAdviceStep(CombatStepKind.Unknown, "SOMETHING_NEW")), CombatAdvicePlace.Fight, TestNames);

        Assert.Equal(["화염 포션 → 턱벌레", "Made Up Potion", "SOMETHING_NEW"], vm.Steps.Select(s => s.Text));
        Assert.Equal(new string?[] { "포션", "포션", null }, vm.Steps.Select(s => s.KindTag));
        Assert.All(vm.Steps, s => Assert.Null(s.Tip)); // no hand position for potions
    }

    [Fact]
    public void Uncertain_advice_has_the_tag_with_the_note_as_tooltip()
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.EndTurn, Bash) with { Uncertain = true, Note = "made-up note" }, CombatAdvicePlace.Fight, TestNames);

        Assert.True(vm.IsUncertain);
        Assert.Equal("made-up note", vm.UncertainTip);
        Assert.Equal("불확실", UiText.Instance.CombatUncertainTag);
    }

    [Fact]
    public void Long_orders_are_cut_with_a_more_row()
    {
        using var _ = Loc.Use(Lang.Kor);
        var steps = Enumerable.Repeat(DefendPlus, CombatAdvicePresenter.MaxSteps + 3).ToArray();
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.EndTurn, steps), CombatAdvicePlace.Fight, TestNames);

        Assert.Equal(CombatAdvicePresenter.MaxSteps + 2, vm.Steps.Count);
        Assert.Equal("… 3개 더", vm.Steps[^2].Name);
        Assert.Equal("턴 종료", vm.Steps[^1].Name);
    }

    private const string NoteDrawn = "next steps depend on the cards drawn";

    [Fact]
    public void A_draw_stop_ends_with_a_muted_pause_row_in_korean()
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.AdviseAfterPlay, Bash, DefendPlus) with { Note = NoteDrawn }, CombatAdvicePlace.Fight, TestNames);

        Assert.Equal(["강타 → 턱벌레", "수비+", "… 이 카드를 낸 뒤 다시 추천"], vm.Steps.Select(s => s.Text));
        var pause = vm.Steps[^1];
        Assert.Equal("", pause.NumberText);
        Assert.True(pause.IsEnding);
        Assert.True(pause.IsSoft);
        Assert.Equal("다음 행동은 이 카드로 뽑을 카드에 따라 달라집니다. 아직 모르는 카드나 결과를 미리 보여 주지 않도록, 이 카드를 낸 뒤에 이어서 추천합니다.", pause.Tip);
        Assert.DoesNotContain(vm.Steps, s => s.Text.Contains("턴 종료", StringComparison.Ordinal));
        Assert.All(vm.Steps.SkipLast(1), s => Assert.False(s.IsSoft));
    }

    [Fact]
    public void A_draw_stop_in_english()
    {
        using var _ = Loc.Use(Lang.Eng);
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.AdviseAfterPlay, Bash) with { Note = NoteDrawn }, CombatAdvicePlace.Fight, TestNames);

        Assert.Equal(["Bash → Jaw Worm", "… more after this play"], vm.Steps.Select(s => s.Text));
        Assert.Equal("What comes next depends on the cards this play draws. So that nothing you could not know yet is shown, the rest is advised after this play.",
            vm.Steps[^1].Tip);
    }

    [Theory]
    [InlineData("next steps depend on the cards drawn", CombatAdvicePresenter.DrawReason.CardsDrawn, "뽑을 카드에 따라")]
    [InlineData("next steps depend on the draw pile's new order", CombatAdvicePresenter.DrawReason.PileOrder, "새 순서에 따라")]
    [InlineData("next steps depend on a random result", CombatAdvicePresenter.DrawReason.Random, "무작위 결과에 따라")]
    [InlineData("", null, "뽑거나 무작위로 정해질 결과에 따라")]
    [InlineData("made-up note", null, "뽑거나 무작위로 정해질 결과에 따라")]
    public void The_pause_tooltip_is_localized_from_the_helpers_note(string note, CombatAdvicePresenter.DrawReason? reason, string part)
    {
        using var _ = Loc.Use(Lang.Kor);
        Assert.Equal(reason, CombatAdvicePresenter.DrawReasonOf(note));
        var tip = CombatAdvicePresenter.Build(Advice(CombatEnding.AdviseAfterPlay, Bash) with { Note = note }, CombatAdvicePlace.Fight, TestNames).Steps[^1].Tip;

        Assert.Contains(part, tip);
        Assert.DoesNotContain("depend", tip); // never the helper's English note to a Korean player
    }

    [Fact]
    public void A_draw_stop_cut_by_the_row_cap_merges_the_more_row_and_the_pause()
    {
        // The draw card is the last step; with 13 steps it is hidden in the "3개 더". "… 3개 더" followed by "… 이 카드를 낸 뒤"
        // would point "이 카드" at the 10th row, so the two become one row.
        var steps = Enumerable.Repeat(DefendPlus, CombatAdvicePresenter.MaxSteps + 3).ToArray();
        using (Loc.Use(Lang.Kor))
        {
            var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.AdviseAfterPlay, steps), CombatAdvicePlace.Fight, TestNames);
            Assert.Equal(CombatAdvicePresenter.MaxSteps + 1, vm.Steps.Count);
            Assert.Equal("… 3개 더 낸 뒤 다시 추천", vm.Steps[^1].Name);
            Assert.True(vm.Steps[^1].IsSoft);
            Assert.Equal("수비+", vm.Steps[^2].Name);
        }
        using (Loc.Use(Lang.Eng))
        {
            var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.AdviseAfterPlay, steps), CombatAdvicePlace.Fight, TestNames);
            Assert.Equal("… 3 more, then new advice", vm.Steps[^1].Name);
        }

        // Exactly at the cap nothing is hidden: the plain pause row.
        using (Loc.Use(Lang.Kor))
        {
            var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.AdviseAfterPlay, steps[..CombatAdvicePresenter.MaxSteps]), CombatAdvicePlace.Fight, TestNames);
            Assert.Equal(CombatAdvicePresenter.MaxSteps + 1, vm.Steps.Count);
            Assert.Equal("… 이 카드를 낸 뒤 다시 추천", vm.Steps[^1].Name);
        }
    }

    [Fact]
    public void A_draw_stop_keeps_the_uncertain_tag_but_not_the_english_draw_note()
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.AdviseAfterPlay, Bash) with { Uncertain = true, Note = NoteDrawn }, CombatAdvicePlace.Fight, TestNames);
        Assert.True(vm.IsUncertain);
        Assert.Equal("계산한 손패가 실제 손패와 다를 수 있습니다", vm.UncertainTip); // the pause row's tooltip already says why it stops

        vm = CombatAdvicePresenter.Build(Advice(CombatEnding.AdviseAfterPlay, Bash) with { Note = NoteDrawn }, CombatAdvicePlace.Fight, TestNames);
        Assert.False(vm.IsUncertain);
    }

    [Theory]
    [InlineData(CombatAdviceState.Lost, "이 전투는 추적이 끊겼습니다")]
    [InlineData(CombatAdviceState.Unsupported, "이 전투는 아직 지원하지 않습니다")]
    public void Lost_and_unsupported_show_one_muted_line_with_the_reason(CombatAdviceState state, string text)
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(new CombatAdviceMessage(state) { Reason = "made-up reason" }, CombatAdvicePlace.Fight, TestNames);

        Assert.True(vm.HasContent);
        Assert.False(vm.ShowSteps);
        Assert.Equal(text, vm.StatusText);
        Assert.Equal("made-up reason", vm.StatusTip);
        Assert.Empty(vm.Steps);
    }

    [Fact]
    public void Lost_and_unsupported_have_english_texts()
    {
        using var _ = Loc.Use(Lang.Eng);
        Assert.Equal("Lost track of this fight", CombatAdvicePresenter.Build(new CombatAdviceMessage(CombatAdviceState.Lost), CombatAdvicePlace.Fight, TestNames).StatusText);
        Assert.Null(CombatAdvicePresenter.Build(new CombatAdviceMessage(CombatAdviceState.Lost), CombatAdvicePlace.Fight, TestNames).StatusTip);
        Assert.Equal("This fight is not supported yet", CombatAdvicePresenter.Build(new CombatAdviceMessage(CombatAdviceState.Unsupported), CombatAdvicePlace.Fight, TestNames).StatusText);
    }

    [Fact]
    public void Hidden_when_idle_without_a_line_or_outside_a_fight()
    {
        var advice = Advice(CombatEnding.EndTurn, Bash);
        Assert.Same(CombatAdviceSectionVm.Empty, CombatAdvicePresenter.Build(CombatAdviceMessage.Idle, CombatAdvicePlace.Fight, TestNames));
        Assert.Same(CombatAdviceSectionVm.Empty, CombatAdvicePresenter.Build(null, CombatAdvicePlace.Fight, TestNames));
        // The tracker says no fight (map, reward, game closed): hidden whatever the helper said last.
        Assert.Same(CombatAdviceSectionVm.Empty, CombatAdvicePresenter.Build(advice, CombatAdvicePlace.Elsewhere, TestNames));
        Assert.Same(CombatAdviceSectionVm.Empty, CombatAdvicePresenter.Build(new CombatAdviceMessage(CombatAdviceState.Lost), CombatAdvicePlace.Elsewhere, TestNames));
        Assert.False(CombatAdviceSectionVm.Empty.HasContent);
    }

    // ---- where the section shows: fights, and event rooms whose fight the helper follows ---------------------------
    // An event can start a fight in its own room (Punch Off). The tracker's hint is not Combat there until the first
    // card is played, so in an event room the helper's own state decides.

    [Theory]
    // helper running, game window, tracker hint, in an event room → place
    [InlineData(true, true, ScreenHint.Combat, false, CombatAdvicePlace.Fight)]
    [InlineData(true, true, ScreenHint.None, true, CombatAdvicePlace.EventRoom)]
    [InlineData(true, true, ScreenHint.None, false, CombatAdvicePlace.Elsewhere)] // treasure room, unknown screen
    [InlineData(true, true, ScreenHint.Map, false, CombatAdvicePlace.Elsewhere)]
    [InlineData(true, true, ScreenHint.RewardLikely, false, CombatAdvicePlace.Elsewhere)]
    [InlineData(true, true, ScreenHint.ShopLikely, false, CombatAdvicePlace.Elsewhere)]
    [InlineData(true, true, ScreenHint.RestSite, false, CombatAdvicePlace.Elsewhere)]
    [InlineData(true, true, ScreenHint.MainMenu, false, CombatAdvicePlace.Elsewhere)]
    [InlineData(false, true, ScreenHint.Combat, false, CombatAdvicePlace.Elsewhere)] // helper not running (co-op, feature off, restarting)
    [InlineData(false, true, ScreenHint.None, true, CombatAdvicePlace.Elsewhere)]
    [InlineData(true, false, ScreenHint.Combat, false, CombatAdvicePlace.Elsewhere)] // the game is gone
    [InlineData(true, false, ScreenHint.None, true, CombatAdvicePlace.Elsewhere)]
    public void The_place_comes_from_the_helper_the_game_window_and_the_tracker(bool helper, bool game, ScreenHint hint, bool eventRoom, CombatAdvicePlace place)
        => Assert.Equal(place, CombatAdvicePresenter.PlaceOf(helper, game, hint, eventRoom));

    [Fact]
    public void The_snapshot_flags_an_event_room_and_only_that()
    {
        static GameStateSnapshot Snap(TrackerScreen screen) => GameStateAdapter.Map(new TrackedRun
        {
            State = new RunState { Character = "CHARACTER.IRONCLAD", Hp = 60, MaxHp = 80 },
            Screen = screen,
            FromSave = true,
            UpdatedAt = DateTime.UtcNow,
        });

        var inEvent = Snap(TrackerScreen.Event);
        Assert.True(inEvent.InEventRoom);
        Assert.Equal(ScreenHint.None, inEvent.Hint); // other consumers keep seeing an event room as "no card screen hint"
        Assert.Equal(CombatAdvicePlace.EventRoom, CombatAdvicePresenter.PlaceOf(true, true, inEvent.Hint, inEvent.InEventRoom));

        // The tracker switches an event's fight to Combat at its first card play: then it is a plain fight.
        var fighting = Snap(TrackerScreen.Combat);
        Assert.False(fighting.InEventRoom);
        Assert.Equal(CombatAdvicePlace.Fight, CombatAdvicePresenter.PlaceOf(true, true, fighting.Hint, fighting.InEventRoom));

        foreach (var screen in new[] { TrackerScreen.Unknown, TrackerScreen.Map, TrackerScreen.RewardScreenLikely, TrackerScreen.ShopLikely, TrackerScreen.RestSite,
                     TrackerScreen.Treasure, TrackerScreen.MainMenu })
        {
            var snap = Snap(screen);
            Assert.False(snap.InEventRoom);
            Assert.Equal(CombatAdvicePlace.Elsewhere, CombatAdvicePresenter.PlaceOf(true, true, snap.Hint, snap.InEventRoom));
        }
        Assert.False(GameStateSnapshot.Empty.InEventRoom);
    }

    [Fact]
    public void Shown_in_a_fight_and_in_an_event_room_unless_the_helper_is_idle()
    {
        using var _ = Loc.Use(Lang.Kor);
        var advice = Advice(CombatEnding.EndTurn, Bash);
        var lost = new CombatAdviceMessage(CombatAdviceState.Lost) { Encounter = "PUNCH_OFF_EVENT_ENCOUNTER", Reason = "made-up reason" };
        var unsupported = new CombatAdviceMessage(CombatAdviceState.Unsupported) { Reason = "made-up reason" };

        foreach (var place in new[] { CombatAdvicePlace.Fight, CombatAdvicePlace.EventRoom })
        {
            var vm = CombatAdvicePresenter.Build(advice, place, TestNames);
            Assert.True(vm.HasContent && vm.ShowSteps);
            Assert.Equal(["강타 → 턱벌레", "턴 종료"], vm.Steps.Select(s => s.Text));
            Assert.Null(vm.PreviewHint); // exact advice with an action already seen: a fight that is surely on
            Assert.Equal("이 전투는 추적이 끊겼습니다", CombatAdvicePresenter.Build(lost, place, TestNames).StatusText);
            Assert.Equal("이 전투는 아직 지원하지 않습니다", CombatAdvicePresenter.Build(unsupported, place, TestNames).StatusText);
            // No fight followed (the event has none, the fight option was not taken, or the fight is over) or no line yet.
            Assert.Same(CombatAdviceSectionVm.Empty, CombatAdvicePresenter.Build(CombatAdviceMessage.Idle, place, TestNames));
            Assert.Same(CombatAdviceSectionVm.Empty, CombatAdvicePresenter.Build(null, place, TestNames));
        }
        foreach (var message in new[] { advice, lost, unsupported, CombatAdviceMessage.Idle, null })
            Assert.Same(CombatAdviceSectionVm.Empty, CombatAdvicePresenter.Build(message, CombatAdvicePlace.Elsewhere, TestNames));
    }

    // ---- the event page is still open: "전투를 고르면" ----------------------------------------------------------------

    /// <summary>What the helper sends when the event page opens: the fight's first turn, before any option is picked.</summary>
    private static readonly CombatAdviceMessage EventFirstTurn = new(CombatAdviceState.Advice)
    {
        Encounter = "PUNCH_OFF_EVENT_ENCOUNTER",
        Turn = 1,
        Energy = 3,
        Hp = 61,
        Block = 0,
        Played = 0,
        Steps = [Bash, DefendPlus],
        Ending = CombatEnding.EndTurn,
        Uncertain = true,
        Note = "event fight from PUNCH_OFF: assumes the options I_CAN_TAKE_THEM, then FIGHT",
    };

    [Fact]
    public void In_an_event_room_the_unsure_first_turn_is_marked_if_you_fight_in_korean()
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(EventFirstTurn, CombatAdvicePlace.EventRoom, TestNames);

        Assert.True(vm.HasContent && vm.ShowSteps);
        Assert.Equal("이번 턴 추천 순서 · 1턴", vm.Title);
        Assert.Equal("전투를 고르면", vm.PreviewHint);
        Assert.Equal("이벤트에서 전투를 고른다고 가정한 첫 턴 추천입니다. 아직 고르기 전이라면 미리 보기로만 참고하세요. 계산한 손패가 실제 손패와 다를 수 있습니다.", vm.PreviewTip);
        Assert.False(vm.IsUncertain); // one tag: the preview says why it is unsure
        Assert.Equal(["강타 → 턱벌레", "수비+", "턴 종료"], vm.Steps.Select(s => s.Text)); // the order itself is shown as usual
        // Never the helper's English note (with its option ids) to a Korean player.
        Assert.DoesNotContain("assumes", vm.PreviewTip);
        Assert.DoesNotContain("assumes", vm.UncertainTip);
        Assert.DoesNotContain("PUNCH_OFF", vm.PreviewTip + vm.UncertainTip + vm.DetailTip);
    }

    [Fact]
    public void In_an_event_room_the_unsure_first_turn_is_marked_if_you_fight_in_english()
    {
        using var _ = Loc.Use(Lang.Eng);
        var vm = CombatAdvicePresenter.Build(EventFirstTurn, CombatAdvicePlace.EventRoom, TestNames);

        Assert.Equal("This turn's plays · Turn 1", vm.Title);
        Assert.Equal("if you fight", vm.PreviewHint);
        Assert.Equal("The first turn's order, assuming you pick the event's fight option. If you have not chosen yet, it is only a preview. The simulated hand may differ from the real one.",
            vm.PreviewTip);
        Assert.False(vm.IsUncertain);
        Assert.Equal(["Bash → Jaw Worm", "Defend+", "End turn"], vm.Steps.Select(s => s.Text));
    }

    [Fact]
    public void The_if_you_fight_mark_goes_once_the_helper_reports_progress()
    {
        using var _ = Loc.Use(Lang.Kor);
        static CombatAdviceSectionVm InEvent(CombatAdviceMessage m) => CombatAdvicePresenter.Build(m, CombatAdvicePlace.EventRoom, TestNames);
        Assert.True(CombatAdvicePresenter.IsEventPreview(EventFirstTurn));
        Assert.True(CombatAdvicePresenter.IsEventPreview(EventFirstTurn with { Played = null })); // not reported = nothing seen

        // An action seen: the fight is on. Still unsure → the normal "불확실" tag, with a localized reason.
        var played = InEvent(EventFirstTurn with { Played = 1, Steps = [DefendPlus] });
        Assert.Null(played.PreviewHint);
        Assert.Null(played.PreviewTip);
        Assert.True(played.IsUncertain);
        Assert.Equal("이벤트에서 시작한 전투라서, 이벤트에서 고른 선택지를 가정해 계산했습니다. 계산한 손패가 실제 손패와 다를 수 있습니다.", played.UncertainTip);

        // A later turn.
        var later = InEvent(EventFirstTurn with { Turn = 2 });
        Assert.Null(later.PreviewHint);
        Assert.True(later.IsUncertain);

        // The helper is sure.
        var exact = InEvent(EventFirstTurn with { Uncertain = false, Note = "" });
        Assert.Null(exact.PreviewHint);
        Assert.False(exact.IsUncertain);

        // A turn the helper did not number cannot be called the first one.
        Assert.Null(InEvent(EventFirstTurn with { Turn = null }).PreviewHint);
    }

    [Fact]
    public void A_fight_the_tracker_already_sees_is_never_a_preview()
    {
        // Hint = Combat (a combat room, or an event's fight after its first card): the same line is a normal, unsure section.
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(EventFirstTurn, CombatAdvicePlace.Fight, TestNames);

        Assert.Null(vm.PreviewHint);
        Assert.True(vm.IsUncertain);
        Assert.StartsWith("이벤트에서 시작한 전투라서", vm.UncertainTip);
    }

    [Theory]
    [InlineData("event fight from PUNCH_OFF: assumes the options I_CAN_TAKE_THEM, then FIGHT", true)]
    [InlineData("Event fight from THE_LANTERN_KEY: assumes the options KEEP_THE_KEY, then FIGHT", true)]
    [InlineData("a card drawn by a relic is not in the log yet", false)]
    [InlineData("next steps depend on the cards drawn", false)]
    [InlineData("", false)]
    public void The_event_fight_note_is_known_by_its_start(string note, bool known)
    {
        Assert.Equal(known, CombatAdvicePresenter.IsEventFightNote(note));
        using (Loc.Use(Lang.Eng))
        {
            var tip = CombatAdvicePresenter.Build(Advice(CombatEnding.EndTurn, Bash) with { Uncertain = true, Note = note }, CombatAdvicePlace.Fight, TestNames).UncertainTip;
            if (known) Assert.Equal("This fight started from an event, so the options picked in the event are assumed. The simulated hand may differ from the real one.", tip);
            else Assert.DoesNotContain("started from an event", tip);
        }
    }

    [Fact]
    public void App_names_map_log_ids_to_catalog_cards_with_the_upgrade_mark()
    {
        // No catalog: the prettified id, which shows that "BASH" was looked up as "CARD.BASH".
        var names = new CombatNames(CardInfoProvider.Load(null, new DataPack()), CombatTextProvider.Empty);
        using var _ = Loc.Use(Lang.Eng);
        Assert.Equal("Bash", names.Card("BASH", upgraded: false));
        Assert.Equal("Strike Ironclad+", names.Card("STRIKE_IRONCLAD", upgraded: true));
        Assert.Equal("Bash", names.Card("CARD.BASH", upgraded: false));
        Assert.Null(names.Monster("JAW_WORM"));
        Assert.Null(names.Potion("FIRE_POTION"));
    }

    [Fact]
    public void Monster_and_potion_names_come_from_the_extracted_localization()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sts2-combat-text-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "kor"));
            Directory.CreateDirectory(Path.Combine(dir, "eng"));
            // Made-up texts in the game's key layout: "<ID>.name" (monsters), "<ID>.title" (potions), move titles skipped.
            File.WriteAllText(Path.Combine(dir, "kor", "monsters.json"), """{ "TEST_WORM.name": "[gold]시험 벌레[/gold]", "TEST_WORM.moves.BITE.title": "물기" }""");
            File.WriteAllText(Path.Combine(dir, "eng", "monsters.json"), """{ "TEST_WORM.name": "Test Worm", "ONLY_ENG.name": "Only English" }""");
            File.WriteAllText(Path.Combine(dir, "kor", "potions.json"), """{ "TEST_POTION.title": "시험 물약", "TEST_POTION.description": "설명" }""");

            var text = CombatTextProvider.Load(dir);
            using (Loc.Use(Lang.Kor))
            {
                Assert.Equal("시험 벌레", text.MonsterName("TEST_WORM"));
                Assert.Equal("시험 벌레", text.MonsterName("MONSTER.TEST_WORM"));
                Assert.Equal("Only English", text.MonsterName("ONLY_ENG")); // the other language before nothing
                Assert.Null(text.MonsterName("BITE"));
                Assert.Equal("시험 물약", text.PotionName("POTION.TEST_POTION"));
            }
            using (Loc.Use(Lang.Eng))
            {
                Assert.Equal("Test Worm", text.MonsterName("TEST_WORM"));
                Assert.Equal("시험 물약", text.PotionName("TEST_POTION")); // no English file
                Assert.Null(text.PotionName("NOPE"));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
