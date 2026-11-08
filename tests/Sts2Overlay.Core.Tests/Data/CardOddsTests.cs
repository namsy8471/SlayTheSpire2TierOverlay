using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.Data;

/// <summary>Numbers from SPIKE §5 (Odds/CardRarityOdds.cs, code-confirmed).</summary>
public class CardOddsTests
{
    private const int Precision = 9;
    private static readonly CardOddsWeights W = new Weights().CardOdds;

    [Theory]
    [InlineData(OfferContext.CardReward, 0, 0.03, 0.37)]
    [InlineData(OfferContext.CardReward, 7, 0.0149, 0.37)]
    [InlineData(OfferContext.EliteReward, 0, 0.10, 0.40)]
    [InlineData(OfferContext.EliteReward, 7, 0.05, 0.40)]
    [InlineData(OfferContext.BossReward, 0, 1.0, 0.0)]
    [InlineData(OfferContext.BossReward, 10, 1.0, 0.0)]
    [InlineData(OfferContext.Shop, 0, 0.09, 0.37)]
    [InlineData(OfferContext.Shop, 7, 0.045, 0.37)]
    [InlineData(OfferContext.CardReward, 6, 0.03, 0.37)]
    [InlineData(OfferContext.Event, 0, 0.03, 0.37)]
    public void TableOdds_PerSourceAndAscension(OfferContext context, int ascension, double rare, double uncommon)
    {
        var ch = CardOdds.Rarity(W, context, ascension);
        Assert.Equal(rare, ch.Rare, Precision);
        Assert.Equal(uncommon, ch.Uncommon, Precision);
        Assert.Equal(1 - rare - uncommon, ch.Common, Precision);
        Assert.Equal(rare, ch.For("Rare"), Precision);
        Assert.Equal(0, ch.For("basic"));
        Assert.Equal(0, ch.For(null));
    }

    [Fact]
    public void HighAscension_IsConfigurable_AndMultiplierAppliesWithoutExplicitValue()
    {
        var w = new Weights().CardOdds;
        w.HighAscension = 10;
        Assert.Equal(0.03, CardOdds.BaseRare(w, OfferContext.CardReward, 7), Precision);
        Assert.Equal(0.0149, CardOdds.BaseRare(w, OfferContext.CardReward, 10), Precision);

        w.EliteReward.RareHighAscension = null;
        w.RareMultiplierHighAscension = 0.25;
        Assert.Equal(0.025, CardOdds.BaseRare(w, OfferContext.EliteReward, 10), Precision);
    }

    [Fact]
    public void StartingOffset_MatchesLogLine()
    {
        // "Card rarity: Rolled 0.455, need < -0.02 for rare": 0.03 + (−0.05).
        Assert.Equal(-0.02, CardOdds.BaseRare(W, OfferContext.CardReward, 0) + W.Pity.Start, Precision);
        var ch = CardOdds.Rarity(W, OfferContext.CardReward, 0, W.Pity.Start);
        Assert.Equal(0, ch.Rare, Precision);
        Assert.Equal(0.35, ch.Uncommon, Precision);
        Assert.Equal(0.65, ch.Common, Precision);
    }

    [Fact]
    public void Pity_RaisesRareShare_AndCapsAt40Percent()
    {
        var baseOdds = CardOdds.Rarity(W, OfferContext.CardReward, 0);
        var raised = CardOdds.Rarity(W, OfferContext.CardReward, 0, 0.10);
        Assert.Equal(0.13, raised.Rare, Precision);
        Assert.Equal(0.37, raised.Uncommon, Precision);
        Assert.Equal(0.50, raised.Common, Precision);
        Assert.True(raised.Rare > baseOdds.Rare);

        var capped = CardOdds.Rarity(W, OfferContext.CardReward, 0, 0.90);
        Assert.Equal(0.03 + 0.40, capped.Rare, Precision);
        Assert.Equal(CardOdds.Rarity(W, OfferContext.CardReward, 0, 0.40), capped);

        var eliteCapped = CardOdds.Rarity(W, OfferContext.EliteReward, 0, 0.40);
        Assert.Equal(0.50, eliteCapped.Rare, Precision);
        Assert.Equal(0.40, eliteCapped.Uncommon, Precision);
        Assert.Equal(0.10, eliteCapped.Common, Precision);
    }

    [Theory]
    [InlineData(OfferContext.CardReward, 0, -0.05)]
    [InlineData(OfferContext.CardReward, 7, 0.40)]
    [InlineData(OfferContext.EliteReward, 3, 0.25)]
    [InlineData(OfferContext.Shop, 0, -0.30)]
    [InlineData(OfferContext.BossReward, 0, -0.05)]
    public void Chances_SumToOne_AndStayInRange(OfferContext context, int ascension, double offset)
    {
        var ch = CardOdds.Rarity(W, context, ascension, offset);
        Assert.Equal(1.0, ch.Common + ch.Uncommon + ch.Rare, Precision);
        Assert.InRange(ch.Common, 0, 1);
        Assert.InRange(ch.Uncommon, 0, 1);
        Assert.InRange(ch.Rare, 0, 1);
    }

    [Fact]
    public void Boss_IsAlwaysRare_EvenWithNegativeOffset()
        => Assert.Equal(new RarityChances(0, 0, 1), CardOdds.Rarity(W, OfferContext.BossReward, 0, W.Pity.Start));

    [Fact]
    public void NextPityOffset_StepsResetsAndCaps()
    {
        Assert.Equal(-0.04, CardOdds.NextPityOffset(W, -0.05, rolledRare: false, ascension: 0), Precision);
        Assert.Equal(-0.045, CardOdds.NextPityOffset(W, -0.05, rolledRare: false, ascension: 7), Precision);
        Assert.Equal(-0.05, CardOdds.NextPityOffset(W, 0.20, rolledRare: true, ascension: 0), Precision);
        Assert.Equal(0.40, CardOdds.NextPityOffset(W, 0.395, rolledRare: false, ascension: 0), Precision);
        Assert.Equal(0.40, CardOdds.NextPityOffset(W, 0.40, rolledRare: false, ascension: 0), Precision);

        // 45 non-rare cards from the start reach the cap at ascension 0.
        var offset = W.Pity.Start;
        for (var i = 0; i < 45; i++) offset = CardOdds.NextPityOffset(W, offset, false, 0);
        Assert.Equal(0.40, offset, Precision);
    }

    [Theory]
    [InlineData(OfferContext.CardReward, 1, 0, "common", 0.25)]
    [InlineData(OfferContext.CardReward, 2, 0, "uncommon", 0.50)]
    [InlineData(OfferContext.EliteReward, 3, 0, "Common", 0.75)]
    [InlineData(OfferContext.CardReward, 3, 7, "common", 0.375)]
    [InlineData(OfferContext.CardReward, 1, 7, "uncommon", 0.125)]
    [InlineData(OfferContext.CardReward, 3, 0, "rare", 0.0)]
    [InlineData(OfferContext.BossReward, 2, 0, "Rare", 0.0)]
    [InlineData(OfferContext.Shop, 3, 0, "common", 0.0)]
    [InlineData(OfferContext.CardReward, 5, 0, "common", 1.0)]
    public void UpgradeChance_ActTimes25Percent(OfferContext context, int act, int ascension, string rarity, double expected)
        => Assert.Equal(expected, CardOdds.UpgradeChance(W, context, act, ascension, rarity), Precision);

    [Fact]
    public void Validator_RejectsBadOdds()
    {
        var w = new Weights();
        w.CardOdds.EliteReward.Rare = 1.5;
        w.CardOdds.Pity.Start = 0.5; // above cap
        w.Shop.CardPrice["rare"] = 0;
        var issues = DataPackValidator.Validate(new DataPack { Weights = w });
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == "weights.cardOdds.eliteReward.rare");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == "weights.cardOdds.pity");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == "weights.shop.cardPrice.rare");
        Assert.Empty(DataPackValidator.Validate(new DataPack()));
    }
}
