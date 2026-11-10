using Sts2Overlay.App.Controllers;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.App.Tests;

/// <summary>A co-op-only card the learned model has no solo record of says so on its reward / shop row.</summary>
public class CoopOnlyTagTests
{
    private static CardEvaluation Eval(bool coopOnly) => new()
    {
        Card = new CardRef("CARD.TANK"),
        DisplayTier = Tier.B,
        Score = new ScoreBreakdown([new ScoreComponent(ComponentKeys.Base, "base", 50)]),
        IsEstimate = true,
        CoopOnlyNoSoloData = coopOnly,
    };

    [Fact]
    public void Coop_only_card_row_gets_a_first_line_tag_in_both_languages()
    {
        using (Loc.Use(Lang.Kor))
        {
            Assert.Equal("협동 전용 · 솔로 기록 없음\n1위", AppController.WithCoopOnlyTag("1위", Eval(true)));
        }

        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("Co-op only · no solo data\n#1", AppController.WithCoopOnlyTag("#1", Eval(true)));
        }
    }

    [Fact]
    public void Other_rows_are_left_alone()
    {
        Assert.Equal("#1", AppController.WithCoopOnlyTag("#1", Eval(false)));
        Assert.Equal("relic", AppController.WithCoopOnlyTag("relic", null));
    }
}
