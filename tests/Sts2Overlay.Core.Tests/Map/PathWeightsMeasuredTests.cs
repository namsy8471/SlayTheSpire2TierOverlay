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

    /// <summary>A pack whose path weights isolate the HP table: no strength scaling, uniform damage, no extra healing.</summary>
    private static DataPack TablePack()
    {
        var pack = TestPacks.DefectPack();
        var w = pack.Weights.Path;
        w.StrengthDamageSlope = 0;
        w.DamageShape = new PathDamageShape();
        w.RoomHeal = new PathRoomHeal();
        return pack;
    }

    private static PathOption Score(DataPack pack, RunState state, MapGraph map)
        => new PathAdvisor(pack).ScorePath(state, map, [new MapCoord(0, 1), map.Boss]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EachAct_UsesItsOwnHpLoss(int act)
    {
        var pack = TablePack();
        var w = pack.Weights.Path;

        var monster = Score(pack, State(1.0, act: act), OneRoom(MapNodeType.Monster, act));
        var elite = Score(pack, State(1.0, act: act), OneRoom(MapNodeType.Elite, act));

        Assert.Equal(1 - w.HpLoss.Monster[act], monster.ExpectedBossHp, 9);
        Assert.Equal(1 - w.HpLoss.Elite[act], elite.ExpectedBossHp, 9);
    }

    [Fact]
    public void CoopTable_AppliesOnlyToMultiplayer_AndNullFallsBack()
    {
        var pack = TablePack();
        var w = pack.Weights.Path;
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
        var pack = TablePack();
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

        Assert.True(safe.BossArrivalChance > 0.9999); // a gamma tail is never exactly 0, but negligible here
        Assert.InRange(risky.BossArrivalChance, 0.0, 0.999);
    }

    [Theory]
    [InlineData(1, 0.3, 0.5)]
    [InlineData(2, 0.3, 0.5)]
    [InlineData(3, 0.5, 0.3)]
    public void GammaDeathChance_MatchesClosedForms(int shape, double hp, double mean)
    {
        // Erlang tail: shape 1 = e^(-x), shape 2 = e^(-x)(1 + x), shape 3 = e^(-x)(1 + x + x²/2), x = hp / (mean / shape).
        var x = hp / (mean / shape);
        var expected = Math.Exp(-x) * shape switch { 1 => 1, 2 => 1 + x, _ => 1 + x + x * x / 2 };
        Assert.Equal(expected, PathScorer.DeathChance(hp, mean, shape), 12);
        Assert.Equal(PathScorer.DeathChance(hp, mean), PathScorer.DeathChance(hp, mean, 0), 12); // 0 = uniform
    }

    [Fact]
    public void GammaLossIfSurvived_IsBelowTheMeanAndBelowHp()
    {
        // Far from death the kept HP is almost hp - mean; near death the expected hit given survival is smaller.
        Assert.Equal(0.1, PathScorer.LossIfSurvived(1.0, 0.1, 2), 3);
        var near = PathScorer.LossIfSurvived(0.2, 0.3, 2);
        Assert.InRange(near, 0.0, 0.2);
        Assert.True(near < 0.3);
        // Shape 1 (exponential): E[D | D < h] = m - h e^(-h/m) / (1 - e^(-h/m)).
        var m = 0.2;
        var h = 0.3;
        Assert.Equal(m - h * Math.Exp(-h / m) / (1 - Math.Exp(-h / m)), PathScorer.LossIfSurvived(h, m, 1), 12);
    }

    [Fact]
    public void SoftRestRule_HealsInExpectation_AroundTheThreshold()
    {
        var pack = TablePack();
        var w = pack.Weights.Path;
        w.RestHealBelowHp = 0.5;
        w.RestHealSoftness = 0.1;
        var map = OneRoom(MapNodeType.RestSite);

        // At the threshold: 50% chance to heal → expected heal is half of RestHeal.
        Assert.Equal(0.5 + 0.5 * w.RestHeal, Score(pack, State(0.5), map).ExpectedBossHp, 9);
        // Far below: almost surely heals; far above: almost surely upgrades.
        Assert.True(Score(pack, State(0.1), map).ExpectedBossHp > 0.1 + 0.95 * w.RestHeal);
        Assert.True(Score(pack, State(0.69), map).ExpectedBossHp < 0.69 + 0.15 * w.RestHeal);

        w.RestHealSoftness = 0; // hard threshold
        Assert.Equal(0.4 + w.RestHeal, Score(pack, State(0.4), map).ExpectedBossHp, 9);
        Assert.Equal(0.6, Score(pack, State(0.6), map).ExpectedBossHp, 9);

        w.RestHealBelowHp = null; // old value rule: heals at 60% because healing is worth more than an upgrade
        Assert.Equal(0.6 + w.RestHeal, Score(pack, State(0.6), map).ExpectedBossHp, 9);
    }

    [Fact]
    public void RoomHeal_IsAddedAfterTheRoom_CappedAtFullHp()
    {
        var pack = TablePack();
        var w = pack.Weights.Path;
        w.RoomHeal = new PathRoomHeal { Monster = 0.05, Shop = 0.1 };

        Assert.Equal(0.6 - w.HpLoss.Monster[1] + 0.05, Score(pack, State(0.6), OneRoom(MapNodeType.Monster)).ExpectedBossHp, 9);
        Assert.Equal(1.0, Score(pack, State(0.95), OneRoom(MapNodeType.Shop)).ExpectedBossHp, 9);
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
