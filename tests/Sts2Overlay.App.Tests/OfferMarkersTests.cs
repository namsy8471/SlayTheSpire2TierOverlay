using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;

namespace Sts2Overlay.App.Tests;

/// <summary>What the on-game marks say: chips, the skip pill's reason and the removal marker text.</summary>
public class OfferMarkersTests
{
    private static ScoreBreakdown Score(params (string Key, string Label, double Value)[] parts)
        => new(parts.Select(p => new ScoreComponent(p.Key, p.Label, p.Value)));

    [Fact]
    public void Card_chips_keep_the_two_strongest_reasons_with_their_sign()
    {
        ReasonChip[] chips = [new("need", "광역 부족", 24.4), new("dilution", "덱 희석", -6.2), new("combo", "콤보", 3)];
        var marks = OfferMarkers.CardChips(chips);
        Assert.Equal(2, marks.Count);
        Assert.Equal("광역 부족 +24", marks[0].Text);
        Assert.True(marks[0].Positive);
        Assert.Equal("덱 희석 -6", marks[1].Text);
        Assert.False(marks[1].Positive);
    }

    [Fact]
    public void Chips_that_round_to_zero_are_dropped()
    {
        var marks = OfferMarkers.CardChips([new("combo", "콤보", 0.3), new("need", "방어 부족", 8)]);
        Assert.Equal(["방어 부족 +8"], marks.Select(m => m.Text));
    }

    private static OfferEvaluation Offer(bool skip, ScoreBreakdown skipScore) => new()
    {
        Cards = [],
        Skip = skipScore,
        SkipRecommended = skip,
    };

    [Fact]
    public void Skip_pill_names_the_strongest_reason_other_than_the_base()
    {
        var eval = Offer(true, Score((ComponentKeys.SkipBase, "기본 스킵", 30), (ComponentKeys.SkipDeckSize, "덱 크기", 12.2)));
        SkipSpec skip = Assert.IsType<SkipSpec>(OfferMarkers.Skip(eval, v => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal("42", skip.ScoreText);
        Assert.Equal("덱 크기 +12", skip.Reason);
    }

    [Fact]
    public void Skip_pill_falls_back_to_the_base_and_is_absent_when_skip_is_not_recommended()
    {
        var onlyBase = Offer(true, Score((ComponentKeys.SkipBase, "기본 스킵", 30)));
        Assert.Equal("기본 스킵 +30", OfferMarkers.Skip(onlyBase, v => "30")!.Reason);
        Assert.Null(OfferMarkers.Skip(Offer(false, Score((ComponentKeys.SkipBase, "기본 스킵", 30))), v => ""));
    }

    private static ShopItemEvaluation Item(ShopItemKind kind, string? target = null) => new()
    {
        Kind = kind,
        Card = kind == ShopItemKind.Card ? new CardRef("CARD.ZAP") : null,
        Price = 75,
        Value = Score(("v", "가치", 10)),
        RemovalTarget = target,
        Affordable = true,
    };

    private static ShopEvaluation Shop(params ShopItemEvaluation[] buy) => new()
    {
        Items = buy,
        RecommendedPurchase = buy,
        Skip = Score(),
    };

    [Fact]
    public void Removal_marker_names_the_card_to_remove_only_when_removal_is_recommended()
    {
        static string Name(string id) => id == "CARD.STRIKE_DEFECT" ? "타격" : id;
        Assert.Equal("제거 추천: 타격", OfferMarkers.RemovalText(Shop(Item(ShopItemKind.Card), Item(ShopItemKind.CardRemoval, "CARD.STRIKE_DEFECT")), Name));
        Assert.Equal("제거 추천", OfferMarkers.RemovalText(Shop(Item(ShopItemKind.CardRemoval)), Name));
        Assert.Null(OfferMarkers.RemovalText(Shop(Item(ShopItemKind.Card)), Name));
    }
}
