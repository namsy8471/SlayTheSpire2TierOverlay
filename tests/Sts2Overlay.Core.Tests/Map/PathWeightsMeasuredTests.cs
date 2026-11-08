using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Tests.Scoring;
using static Sts2Overlay.Core.Tests.Map.TestMaps;

namespace Sts2Overlay.Core.Tests.Map;

/// <summary>The measured-weight plumbing: per-act HP tables, the co-op table, treasure gold, boss arrival chance.</summary>
public class PathWeightsMeasuredTests
{
    /// <summary>start → one room of <paramref name="type"/> → boss.</summary>
    private static MapGraph OneRoom(MapNodeType type, int act = 1)
        => new MapBuilder(2, act, (0, 1)).Add(0, 1, type, (0, 2)).Build();

    private static PathOption Score(DataPack pack, RunState state, MapGraph map)
        => new PathAdvisor(pack).ScorePath(state, map, [new MapCoord(0, 1), map.Boss]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EachAct_UsesItsOwnHpLoss(int act)
    {
        var pack = TestPacks.DefectPack();
        var w = pack.Weights.Path;
        w.StrengthDamageSlope = 0; // deck strength does not scale damage, so the table value is the loss

        var monster = Score(pack, State(1.0, act: act), OneRoom(MapNodeType.Monster, act));
        var elite = Score(pack, State(1.0, act: act), OneRoom(MapNodeType.Elite, act));

        Assert.Equal(1 - w.HpLoss.Monster[act], monster.ExpectedBossHp, 9);
        Assert.Equal(1 - w.HpLoss.Elite[act], elite.ExpectedBossHp, 9);
    }

    [Fact]
    public void CoopTable_AppliesOnlyToMultiplayer_AndNullFallsBack()
    {
        var pack = TestPacks.DefectPack();
        var w = pack.Weights.Path;
        w.StrengthDamageSlope = 0;
        var map = OneRoom(MapNodeType.Elite);
        var solo = State(1.0);
        var coop = solo with { IsMultiplayer = true };

        w.CoopHpLoss = null;
        Assert.Equal(Score(pack, solo, map).ExpectedBossHp, Score(pack, coop, map).ExpectedBossHp, 12);

        w.CoopHpLoss = new PathHpLoss { Elite = new() { [1] = 0.45 } };
        Assert.Equal(1 - w.HpLoss.Elite[1], Score(pack, solo, map).ExpectedBossHp, 9);
        Assert.Equal(1 - 0.45, Score(pack, coop, map).ExpectedBossHp, 9);
    }

    [Fact]
    public void StrengthSlope_ScalesTheTableValue()
    {
        var pack = TestPacks.DefectPack();
        var w = pack.Weights.Path;
        w.StrengthDamageSlope = 1.0;
        var r = Score(pack, State(1.0), OneRoom(MapNodeType.Monster));
        var strength = new PathAdvisor(pack).Recommend(State(1.0), OneRoom(MapNodeType.Monster)).DeckStrength;
        Assert.Equal(1 - w.HpLoss.Monster[1] * (1 + 0.5 - strength), r.ExpectedBossHp, 9);
    }

    [Fact]
    public void TreasureGold_CanMakeALaterShopAffordable()
    {
        var pack = TestPacks.DefectPack();
        var w = pack.Weights.Path;
        w.TreasureGold = 40;
        var map = new MapBuilder(3, 1, (0, 1))
            .Add(0, 1, MapNodeType.Treasure, (0, 2))
            .Add(0, 2, MapNodeType.Shop, (0, 3))
            .Build();
        var state = State(1.0, gold: w.ShopGoldThreshold - w.TreasureGold, deck: [new DeckCard(new CardRef(TestPacks.OrbCore))]);

        var o = new PathAdvisor(pack).ScorePath(state, map, [new(0, 1), new(0, 2), map.Boss]);

        Assert.Equal(w.ShopValue, o.Score.ValueOf(PathComponentKeys.Shop), 9);
    }

    [Fact]
    public void BossArrivalChance_IsOneWhenSafe_AndLowerAfterARiskyElite()
    {
        var safe = Score(Pack, State(1.0), OneRoom(MapNodeType.Monster));
        var risky = Score(Pack, State(0.2), OneRoom(MapNodeType.Elite));

        Assert.Equal(1.0, safe.BossArrivalChance, 12);
        Assert.InRange(risky.BossArrivalChance, 0.0, 0.999);
    }

    [Fact]
    public void HpTables_LoadFromWeightsJson_PartialOverride()
    {
        var w = DataPackLoader.LoadWeights("""{ "path": { "hpLoss": { "elite": { "2": 0.5 } }, "coopHpLoss": { "boss": { "1": 0.6 } } } }""");

        Assert.Equal(0.5, w.Path.HpLoss.Elite[2]);
        Assert.Equal(new PathWeights().HpLoss.Elite[1], w.Path.HpLoss.Elite[1]); // untouched act keeps its default
        Assert.Equal(new PathWeights().HpLoss.Monster[3], w.Path.HpLoss.Monster[3]);
        Assert.Equal(0.6, w.Path.HpLossFor(multiplayer: true).For(MapNodeType.Boss, 1));
        Assert.Equal(0.6, w.Path.HpLossFor(multiplayer: true).For(MapNodeType.Boss, 3)); // falls back to the closest lower act
        Assert.Equal(w.Path.HpLoss.Boss[1], w.Path.HpLossFor(multiplayer: false).For(MapNodeType.Boss, 1));
    }

    [Fact]
    public void LegacyTable_IsTheOldGuessTimesTheOldActMultiplier()
    {
        var legacy = PathHpLoss.Legacy();
        Assert.Equal(0.20, legacy.Elite[1]);
        Assert.Equal(0.25, legacy.Elite[2]);
        Assert.Equal(0.29, legacy.Elite[3], 9);
        Assert.Equal(0.0, legacy.For(MapNodeType.RestSite, 1));
    }
}
