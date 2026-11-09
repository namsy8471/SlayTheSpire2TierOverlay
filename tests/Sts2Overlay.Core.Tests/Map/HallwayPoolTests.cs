using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Tests.Scoring;
using static Sts2Overlay.Core.Tests.Map.TestMaps;

namespace Sts2Overlay.Core.Tests.Map;

/// <summary>
/// Easy/hard hallway pools: each act's first <see cref="PathWeights.EasyFights"/> hallway fights use the easy loss,
/// later ones the hard loss; "?" rooms use up a fight by their fight chance; card rewards lose value within an act.
/// </summary>
public class HallwayPoolTests
{
    /// <summary>Uniform damage, no healing, no strength scaling, so the simulated HP is exactly 1 − Σ losses.</summary>
    private static DataPack Pack(Action<PathWeights>? tweak = null)
    {
        var pack = TestPacks.DefectPack();
        var w = pack.Weights.Path;
        w.StrengthDamageSlope = 0;
        w.DamageShape = new PathDamageShape();
        w.RoomHeal = new PathRoomHeal();
        w.HpLoss.Monster = new() { [1] = 0.05, [2] = 0.10 };
        w.HpLoss.HardMonster = new() { [1] = 0.12, [2] = 0.15 };
        w.HpLoss.Unknown = new() { [1] = 0.0 };
        w.EasyFights = new() { [1] = 3, [2] = 2 };
        tweak?.Invoke(w);
        return pack;
    }

    /// <summary>start → the given rooms in one line → boss.</summary>
    private static MapGraph Line(int act, params MapNodeType[] rooms)
    {
        var b = new MapBuilder(rooms.Length + 1, act, (0, 1));
        for (var i = 0; i < rooms.Length; i++) b.Add(0, i + 1, rooms[i], (0, i + 2));
        return b.Build();
    }

    private static PathOption Walk(DataPack pack, MapGraph map, double hp = 1.0, int act = 1)
        => new PathAdvisor(pack).ScorePath(State(hp, act: act), map,
            Enumerable.Range(1, map.Boss.Row).Select(r => new MapCoord(0, r)).ToArray());

    [Fact]
    public void Act1_FourthHallwayFightUsesTheHardLoss()
    {
        var pack = Pack();
        var m = MapNodeType.Monster;

        var o = Walk(pack, Line(1, m, m, m, m, m));

        Assert.Equal(1 - 3 * 0.05 - 2 * 0.12, o.ExpectedBossHp, 9);
        Assert.Equal(2, o.HardFights, 9);
    }

    [Fact]
    public void Act2_HasTwoEasyFights()
    {
        var pack = Pack();
        var m = MapNodeType.Monster;

        var o = Walk(pack, Line(2, m, m, m), act: 2);

        Assert.Equal(1 - 2 * 0.10 - 0.15, o.ExpectedBossHp, 9);
        Assert.Equal(1, o.HardFights, 9);
    }

    [Fact]
    public void FightsAlreadyDoneThisAct_StartInTheHardPool()
    {
        var pack = Pack();
        var map = Line(1, MapNodeType.Monster, MapNodeType.Monster) with { NormalFightsDone = 2 };

        var o = Walk(pack, map);

        Assert.Equal(1 - 0.05 - 0.12, o.ExpectedBossHp, 9);
    }

    [Fact]
    public void WithoutTheSaveCounter_MonsterRoomsWalkedAreCounted()
    {
        var pack = Pack();
        var map = Line(1, MapNodeType.Monster, MapNodeType.Monster, MapNodeType.Monster, MapNodeType.Monster);
        var walked = map with { Current = new MapCoord(0, 2), Visited = [map.Start, new(0, 1), new(0, 2)] };

        // Two fights done: the next is the act's last easy fight, then the hard pool.
        var o = new PathAdvisor(pack).ScorePath(State(1.0), walked, [new MapCoord(0, 3), new MapCoord(0, 4), map.Boss]);

        Assert.Equal(1 - 0.05 - 0.12, o.ExpectedBossHp, 9);
    }

    [Fact]
    public void SaveCanListFewerEasyFights()
    {
        var pack = Pack();
        var map = Line(1, MapNodeType.Monster, MapNodeType.Monster) with { EasyFights = 1 };

        Assert.Equal(1 - 0.05 - 0.12, Walk(pack, map).ExpectedBossHp, 9);
    }

    [Fact]
    public void UnknownRoom_UsesUpAnEasyFightByItsFightChance()
    {
        var pack = Pack(w => w.UnknownFightChance = 0.25);
        var m = MapNodeType.Monster;

        // ?, ? → half an easy fight used in expectation; then M, M (easy), M (half easy, half hard).
        var o = Walk(pack, Line(1, MapNodeType.Unknown, MapNodeType.Unknown, m, m, m));

        Assert.Equal(1 - 0.05 - 0.05 - (0.5 * 0.05 + 0.5 * 0.12), o.ExpectedBossHp, 9);
        Assert.Equal(0.5, o.HardFights, 9);
    }

    [Fact]
    public void EmptyHardTable_FallsBackToTheSinglePool()
    {
        var pack = Pack(w => w.HpLoss.HardMonster.Clear());
        var m = MapNodeType.Monster;

        Assert.Equal(1 - 5 * 0.05, Walk(pack, Line(1, m, m, m, m, m)).ExpectedBossHp, 9);
    }

    [Fact]
    public void CardRewards_LoseValueWithinTheAct_DownToTheFloor()
    {
        var pack = Pack(w =>
        {
            w.CardRewardDecay = new() { [1] = 0.2 };
            w.CardRewardFloor = 0.5;
            w.WeakDeckCardBonus = 0;
            w.MonsterReward = 10;
            w.HpLoss.Monster = new() { [1] = 0.0 };
            w.HpLoss.HardMonster = new() { [1] = 0.0 };
        });
        var m = MapNodeType.Monster;

        var o = Walk(pack, Line(1, m, m, m, m));

        // 10 × (1, 0.8, 0.6, max(0.5, 0.4))
        Assert.Equal(10 * (1 + 0.8 + 0.6 + 0.5), o.Score.ValueOf(PathComponentKeys.Monster), 9);
    }

    [Fact]
    public void LongHallwayChain_LosesToTheSameRoomsWithUnknownsInBetween_InAct1()
    {
        // Same number of rooms: five hallway fights in a row vs three, then two "?" rooms. With the measured
        // defaults the 4th and 5th hallway fights are hard-pool fights and should no longer win.
        var pack = TestPacks.DefectPack();
        var m = MapNodeType.Monster;
        var q = MapNodeType.Unknown;
        var map = new MapBuilder(7, 1, (0, 1), (1, 1))
            .Add(0, 1, m, (0, 2)).Add(0, 2, m, (0, 3)).Add(0, 3, m, (0, 4)).Add(0, 4, m, (0, 5)).Add(0, 5, m, (0, 6))
            .Add(1, 1, m, (1, 2)).Add(1, 2, m, (1, 3)).Add(1, 3, m, (1, 4)).Add(1, 4, q, (1, 5)).Add(1, 5, q, (0, 6))
            .Add(0, 6, MapNodeType.RestSite, (0, 7))
            .Build();

        var r = new PathAdvisor(pack).Recommend(State(0.8), map);

        Assert.Equal(new MapCoord(1, 1), r.NextStep);
        var chain = r.Top.First(o => o.Coords[0] == new MapCoord(0, 1));
        Assert.Equal(2, chain.HardFights, 9);
        using (Loc.Use(Lang.Kor)) Assert.Contains("어려운 일반 전투 2", new PathAdvisor(pack).Recommend(State(0.8), map).Top.First(o => o.Coords[0] == new MapCoord(0, 1)).Chips);
    }
}
