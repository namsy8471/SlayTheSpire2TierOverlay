using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// A run rebuilt from the game log alone (co-op guest) has placeholder HP and gold: the flag travels from the tracker to the
/// panel, which shows "?" instead of the starting values and explains the missing map and the estimated deck.
/// </summary>
public class UnknownVitalsTests
{
    private static readonly RunState Run = new() { Character = "CHARACTER.SILENT", Act = 2, Floor = 20, Hp = 70, MaxHp = 70, Gold = 640, IsMultiplayer = true };

    private static TrackedRun Tracked(bool fromSave) => new()
    {
        State = Run,
        FromSave = fromSave,
        HpGoldReliable = fromSave,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void Adapter_carries_the_unreliable_hp_gold_flag_onto_the_run_state()
    {
        var guest = GameStateAdapter.Map(Tracked(fromSave: false));
        Assert.False(guest.Run!.VitalsKnown);
        Assert.True(guest.LogOnly);
        Assert.Equal(70, guest.Run.Hp); // the values stay (scorers still need numbers), only the flag says they are placeholders

        var host = GameStateAdapter.Map(Tracked(fromSave: true));
        Assert.True(host.Run!.VitalsKnown);
        Assert.False(host.LogOnly);
        Assert.Same(Run, host.Run); // unchanged when reliable
    }

    [Fact]
    public void Run_header_shows_unknown_hp_and_gold_as_question_marks()
    {
        var unknown = Run with { VitalsKnown = false };
        using (Loc.Use(Lang.Kor))
        {
            Assert.Equal("사일런트 · 2막 20층 · HP ? · 골드 ? · 로그 추정", AppController.RunHeader(unknown));
            Assert.Equal("사일런트 · 2막 20층 · HP 70/70 · 640골드", AppController.RunHeader(Run));
        }
        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("The Silent · Act 2 Floor 20 · HP ? · Gold ? · from log", AppController.RunHeader(unknown));
            Assert.Equal("The Silent · Act 2 Floor 20 · HP ? · Gold ? · Ascension 5 · from log", AppController.RunHeader(unknown with { Ascension = 5 }));
            Assert.Equal("The Silent · Act 2 Floor 20 · HP 70/70 · 640 Gold", AppController.RunHeader(Run));
        }
    }

    [Fact]
    public void Path_tab_explains_that_a_coop_guest_gets_no_map()
    {
        var guest = GameStateAdapter.Map(Tracked(fromSave: false));
        Assert.True(AppController.IsCoopGuestWithoutMap(guest));
        Assert.False(AppController.IsCoopGuestWithoutMap(GameStateAdapter.Map(Tracked(fromSave: true))));
        Assert.False(AppController.IsCoopGuestWithoutMap(guest with { Run = Run with { IsMultiplayer = false, VitalsKnown = false } }));

        Assert.True(PathSectionVm.CoopGuestEmpty.ShowGuestNoMap);
        Assert.False(PathSectionVm.CoopGuestEmpty.ShowNotReady);
        Assert.True(PathSectionVm.Empty.ShowNotReady);
        Assert.False(PathSectionVm.Empty.ShowGuestNoMap);
        Assert.False(new PathSectionVm().ShowNotReady || new PathSectionVm().ShowGuestNoMap); // has content: neither

        using (Loc.Use(Lang.Kor)) Assert.Contains("협동 게스트는 지도 정보를 받을 수 없어", UiText.Instance.PathEmptyCoopGuest);
        using (Loc.Use(Lang.Eng)) Assert.Equal("No path advice\nCo-op guests do not receive the map, so no path advice.", UiText.Instance.PathEmptyCoopGuest);
    }

    [Fact]
    public void Deck_note_shop_footer_and_event_caution_are_worded_in_both_languages()
    {
        using (Loc.Use(Lang.Kor))
        {
            Assert.Equal("로그로 추정한 덱입니다 (상점 구매 등은 빠질 수 있음)", AppController.LogDeckNote());
            Assert.Equal("추천 구매 (골드 모름, 가격 색 기준): A + B", AppController.ShopFooterGoldUnknown(["A", "B"]));
            Assert.Equal("추천 구매 없음 (골드 모름)", AppController.ShopFooterGoldUnknown([]));
            Assert.Equal("HP 모름: 체력 손실 주의", AppController.HpUnknownText());
        }
        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("Deck rebuilt from the log (shop purchases etc. may be missing)", AppController.LogDeckNote());
            Assert.Equal("Buy (Gold unknown, by price colors): A + B", AppController.ShopFooterGoldUnknown(["A", "B"]));
            Assert.Equal("Buy nothing (Gold unknown)", AppController.ShopFooterGoldUnknown([]));
            Assert.Equal("HP unknown: mind the HP loss", AppController.HpUnknownText());
        }
    }
}
