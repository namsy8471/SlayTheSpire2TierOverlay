using Sts2Overlay.App.Controllers;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.App.Tests;

/// <summary>What the panel and the game layer say matches what the scorers know: the co-op caveat and the event-marker tags.</summary>
public class TruthfulLabelsTests
{
    [Fact]
    public void Coop_note_is_a_standing_line_above_the_flip_hint()
    {
        using (Loc.Use(Lang.Kor))
        {
            Assert.Equal("솔로 승천 10 랭커 기록 기준입니다 (협동 보정 없음)", AppController.WithCoopNote(null, coop: true));
            var both = AppController.WithCoopNote("박빙 힌트", coop: true)!;
            Assert.StartsWith("솔로 승천 10", both);
            Assert.EndsWith("박빙 힌트", both);
        }

        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("Based on solo Ascension 10 top-player picks (no co-op adjustment)", AppController.WithCoopNote(null, coop: true));
        }

        Assert.Null(AppController.WithCoopNote(null, coop: false));
        Assert.Equal("hint", AppController.WithCoopNote("hint", coop: false));
    }

    [Fact]
    public void Event_marker_tags_an_option_without_data()
    {
        var none = new EventOptionEvaluation { Option = new EventOptionDef { Key = "X.pages.INITIAL.options.A" }, Score = new ScoreBreakdown([]), HasUnmodelled = true };
        var partly = none with { Score = new ScoreBreakdown([new ScoreComponent(EventComponentKeys.Gold, "골드 +50", 5)]) };
        Assert.True(none.HasNoData);
        Assert.False(partly.HasNoData);
        using (Loc.Use(Lang.Kor))
        {
            Assert.Equal("정보 없음", AppController.UnmodelledText(none));
            Assert.Equal("일부 추정", AppController.UnmodelledText(partly));
        }

        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("No data", AppController.UnmodelledText(none));
            Assert.Equal("Partly estimated", AppController.UnmodelledText(partly));
        }
    }
}
