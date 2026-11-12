using System.IO;
using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Integration.CombatAdvice;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;

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
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.EndTurn, Bash, DefendPlus), inCombat: true, TestNames);

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
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.Win, Bash, DefendPlus), inCombat: true, TestNames);

        Assert.Equal("This turn's plays · Turn 2", vm.Title);
        Assert.Equal(["Bash → Jaw Worm", "Defend+", "Finish!"], vm.Steps.Select(s => s.Text));
        Assert.Equal("Card 3 from the left in your hand", vm.Steps[0].Tip);
        Assert.Equal("uncertain", CombatAdvicePresenter.UncertainTag);
    }

    [Theory]
    [InlineData(CombatEnding.EndTurn, "턴 종료")]
    [InlineData(CombatEnding.Win, "마무리!")]
    [InlineData(CombatEnding.None, null)]
    public void The_closing_row_follows_the_ending(CombatEnding ending, string? last)
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(Advice(ending, DefendPlus), inCombat: true, TestNames);

        Assert.Equal(last is null ? 1 : 2, vm.Steps.Count);
        if (last is not null) Assert.Equal(last, vm.Steps[^1].Name);
        else Assert.Equal("수비+", vm.Steps[^1].Name);
    }

    [Fact]
    public void A_turn_with_nothing_to_play_shows_only_end_turn()
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.EndTurn), inCombat: true, TestNames);

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
            new CombatAdviceStep(CombatStepKind.Unknown, "SOMETHING_NEW")), inCombat: true, TestNames);

        Assert.Equal(["화염 포션 → 턱벌레", "Made Up Potion", "SOMETHING_NEW"], vm.Steps.Select(s => s.Text));
        Assert.Equal(new string?[] { "포션", "포션", null }, vm.Steps.Select(s => s.KindTag));
        Assert.All(vm.Steps, s => Assert.Null(s.Tip)); // no hand position for potions
    }

    [Fact]
    public void Uncertain_advice_has_the_tag_with_the_note_as_tooltip()
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.EndTurn, Bash) with { Uncertain = true, Note = "made-up note" }, inCombat: true, TestNames);

        Assert.True(vm.IsUncertain);
        Assert.Equal("made-up note", vm.UncertainTip);
        Assert.Equal("불확실", UiText.Instance.CombatUncertainTag);
    }

    [Fact]
    public void Long_orders_are_cut_with_a_more_row()
    {
        using var _ = Loc.Use(Lang.Kor);
        var steps = Enumerable.Repeat(DefendPlus, CombatAdvicePresenter.MaxSteps + 3).ToArray();
        var vm = CombatAdvicePresenter.Build(Advice(CombatEnding.EndTurn, steps), inCombat: true, TestNames);

        Assert.Equal(CombatAdvicePresenter.MaxSteps + 2, vm.Steps.Count);
        Assert.Equal("… 3개 더", vm.Steps[^2].Name);
        Assert.Equal("턴 종료", vm.Steps[^1].Name);
    }

    [Theory]
    [InlineData(CombatAdviceState.Lost, "이 전투는 추적이 끊겼습니다")]
    [InlineData(CombatAdviceState.Unsupported, "이 전투는 아직 지원하지 않습니다")]
    public void Lost_and_unsupported_show_one_muted_line_with_the_reason(CombatAdviceState state, string text)
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = CombatAdvicePresenter.Build(new CombatAdviceMessage(state) { Reason = "made-up reason" }, inCombat: true, TestNames);

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
        Assert.Equal("Lost track of this fight", CombatAdvicePresenter.Build(new CombatAdviceMessage(CombatAdviceState.Lost), true, TestNames).StatusText);
        Assert.Null(CombatAdvicePresenter.Build(new CombatAdviceMessage(CombatAdviceState.Lost), true, TestNames).StatusTip);
        Assert.Equal("This fight is not supported yet", CombatAdvicePresenter.Build(new CombatAdviceMessage(CombatAdviceState.Unsupported), true, TestNames).StatusText);
    }

    [Fact]
    public void Hidden_when_idle_without_a_line_or_outside_a_fight()
    {
        var advice = Advice(CombatEnding.EndTurn, Bash);
        Assert.Same(CombatAdviceSectionVm.Empty, CombatAdvicePresenter.Build(CombatAdviceMessage.Idle, inCombat: true, TestNames));
        Assert.Same(CombatAdviceSectionVm.Empty, CombatAdvicePresenter.Build(null, inCombat: true, TestNames));
        // The tracker says no fight (map, reward, game closed): hidden whatever the helper said last.
        Assert.Same(CombatAdviceSectionVm.Empty, CombatAdvicePresenter.Build(advice, inCombat: false, TestNames));
        Assert.Same(CombatAdviceSectionVm.Empty, CombatAdvicePresenter.Build(new CombatAdviceMessage(CombatAdviceState.Lost), inCombat: false, TestNames));
        Assert.False(CombatAdviceSectionVm.Empty.HasContent);
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
