using Sts2Overlay.App.Controllers;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Rest;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Rest;

namespace Sts2Overlay.App.Tests;

/// <summary>Where the rest-site "추천!" goes: the advised button, and the cards to upgrade in the Smith grid.</summary>
public class RestMarksTests
{
    private static readonly CardRef Bash = new("CARD.BASH"), Strike = new("CARD.STRIKE"), Defend = new("CARD.DEFEND"), Glacier = new("CARD.GLACIER");

    private static string Name(CardRef c) => c.Id.Replace("CARD.", "");

    private static UpgradeCandidate Up(CardRef card, int rank, params string[] chips) => new()
    {
        Card = card,
        Rank = rank,
        Score = new ScoreBreakdown([new ScoreComponent(UpgradeComponentKeys.Upgrade, "강화", 10 - rank)]),
        Chips = chips.Select(c => new ReasonChip(c, c, 5)).ToArray(),
    };

    private static RestSiteAdvice Advice(RestAction action, RestConfidence confidence = RestConfidence.Sure, params UpgradeCandidate[] ups) => new()
    {
        Action = action,
        Confidence = confidence,
        Reason = "이유",
        Upgrades = ups,
    };

    // Two buttons as the game lays them out (Heal left of Smith); the drawn picture is 10 % larger than the slot (hover).
    private static RestSiteResult Choice(bool smithEnabled = true) => new(true,
    [
        new RestButton(RestOption.Heal, 0, new RectF(690, 400, 220, 220), new RectF(700, 410, 200, 200), RestButtonState.Hovered, true, 10, 50),
        new RestButton(RestOption.Smith, 1, new RectF(1020, 410, 200, 200), new RectF(1020, 410, 200, 200), RestButtonState.Normal, smithEnabled, 10, 50),
    ], false, "");

    [Fact]
    public void Heal_advice_marks_the_heal_slot_not_its_hover_picture()
    {
        var plan = RestMarks.Choice(Choice(), Advice(RestAction.Heal), Name);
        var mark = Assert.Single(plan.Rects);
        Assert.Equal(ScreenRectMarkKind.Best, mark.Kind);
        Assert.Equal((700f, 410f, 200f, 200f), (mark.X, mark.Y, mark.Width, mark.Height));
        Assert.Equal("추천!", mark.Ribbon);
        Assert.Equal("이유", mark.Note);
    }

    [Fact]
    public void Smith_advice_names_the_card_to_upgrade_and_close_calls_say_so()
    {
        var plan = RestMarks.Choice(Choice(), Advice(RestAction.Smith, RestConfidence.Close, Up(Bash, 1)), Name);
        var mark = Assert.Single(plan.Rects);
        Assert.Equal(1020f, mark.X);
        Assert.Equal("추천! (박빙)", mark.Ribbon);
        Assert.Equal("강화할 카드: BASH\n이유", mark.Note);
    }

    [Fact]
    public void A_greyed_out_or_missing_button_gets_no_mark()
    {
        Assert.True(RestMarks.Choice(Choice(smithEnabled: false), Advice(RestAction.Smith, RestConfidence.Sure, Up(Bash, 1)), Name).IsEmpty);
        var healOnly = Choice() with { Buttons = [Choice().Buttons[1]] };
        Assert.True(RestMarks.Choice(healOnly, Advice(RestAction.Heal), Name).IsEmpty);
    }

    [Fact]
    public void Grid_marks_the_top_three_on_their_first_visible_copies_and_pins_the_ranking()
    {
        var advice = Advice(RestAction.Smith, RestConfidence.Sure, Up(Strike, 1, "피해 +3", "콤보", "세번째"), Up(Strike, 2), Up(Bash, 3), Up(Defend, 4));
        (CardRef, RectF)[] cards =
        [
            (Defend, new RectF(250, 120, 200, 280)),
            (Strike, new RectF(500, 120, 200, 280)),
            (Strike, new RectF(750, 120, 200, 280)),
            (Bash, new RectF(1000, 120, 200, 280)),
        ];
        var plan = RestMarks.Grid(cards, advice, Name);

        Assert.Equal(4, plan.Rects.Count);
        var best = plan.Rects[0];
        Assert.Equal((ScreenRectMarkKind.Best, "강화 추천!", 500f), (best.Kind, best.Ribbon, best.X));
        Assert.Equal("피해 +3 · 콤보", best.Note); // two chips at most
        // Rank 2 is a second copy of the same card: it goes on the next copy, not on top of rank 1.
        Assert.Equal((ScreenRectMarkKind.Candidate, "2순위", 750f), (plan.Rects[1].Kind, plan.Rects[1].Ribbon, plan.Rects[1].X));
        Assert.Equal((ScreenRectMarkKind.Candidate, "3순위", 1000f), (plan.Rects[2].Kind, plan.Rects[2].Ribbon, plan.Rects[2].X));
        var panel = plan.Rects[3];
        Assert.Equal(ScreenRectMarkKind.Note, panel.Kind);
        Assert.Equal((RestMarks.NoteX, RestMarks.NoteY), (panel.X, panel.Y));
        Assert.Equal("1. STRIKE\n2. STRIKE\n3. BASH", panel.Note);
    }

    [Fact]
    public void Grid_scrolled_past_the_best_card_still_shows_the_ranking_panel()
    {
        var advice = Advice(RestAction.Smith, RestConfidence.Sure, Up(Glacier, 1), Up(Bash, 2));
        var plan = RestMarks.Grid([(Defend, new RectF(250, 120, 200, 280)), (Bash, new RectF(500, 120, 200, 280))], advice, Name);

        Assert.Equal(2, plan.Rects.Count);
        Assert.Equal("2순위", plan.Rects[0].Ribbon);
        Assert.Equal("1. GLACIER\n2. BASH", plan.Rects[1].Note);
    }

    [Fact]
    public void Nothing_to_upgrade_means_no_grid_marks()
    {
        Assert.True(RestMarks.Grid([(Defend, new RectF(250, 120, 200, 280))], Advice(RestAction.Heal), Name).IsEmpty);
    }

    [Fact]
    public void Unknown_hp_puts_no_mark_on_either_button_but_the_grid_still_ranks()
    {
        var advice = Advice(RestAction.Smith, RestConfidence.Unknown, Up(Bash, 1), Up(Strike, 2));
        Assert.True(RestMarks.Choice(Choice(), advice, Name).IsEmpty);

        var grid = RestMarks.Grid([(Strike, new RectF(250, 120, 200, 280)), (Bash, new RectF(500, 120, 200, 280))], advice, Name);
        Assert.Equal(["강화 추천!", "2순위", "강화 추천"], grid.Rects.Select(r => r.Ribbon));
    }

    [Fact]
    public void Grid_with_the_upgrade_preview_on_still_marks_the_candidates_by_card_id()
    {
        // "Show upgrades" toggled: every title is green, so the recogniser reads each card one level up.
        var advice = Advice(RestAction.Smith, RestConfidence.Sure, Up(Bash, 1), Up(Strike, 2));
        var plan = RestMarks.Grid([(Strike with { Upgrade = 1 }, new RectF(250, 120, 200, 280)), (Bash with { Upgrade = 1 }, new RectF(500, 120, 200, 280))], advice, Name);

        Assert.Equal((ScreenRectMarkKind.Best, "강화 추천!", 500f), (plan.Rects[0].Kind, plan.Rects[0].Ribbon, plan.Rects[0].X));
        Assert.Equal(("2순위", 250f), (plan.Rects[1].Ribbon, plan.Rects[1].X));
    }

    [Fact]
    public void Grid_without_the_preview_never_marks_an_already_upgraded_copy()
    {
        // Preview off (an un-upgraded card is visible): Bash+ is a real upgraded copy, and the Bash candidate scrolled away.
        var advice = Advice(RestAction.Smith, RestConfidence.Sure, Up(Bash, 1));
        var plan = RestMarks.Grid([(Defend, new RectF(250, 120, 200, 280)), (Bash with { Upgrade = 1 }, new RectF(500, 120, 200, 280))], advice, Name);

        var only = Assert.Single(plan.Rects); // just the ranking panel
        Assert.Equal(ScreenRectMarkKind.Note, only.Kind);

        // Both copies visible: the mark goes on the un-upgraded one even though the upgraded copy comes first.
        var both = RestMarks.Grid([(Bash with { Upgrade = 1 }, new RectF(250, 120, 200, 280)), (Bash, new RectF(500, 120, 200, 280))], advice, Name);
        Assert.Equal(500f, both.Rects[0].X);
    }
}
