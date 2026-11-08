using System.Diagnostics;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Tests.Map;
using Xunit.Abstractions;

namespace Sts2Overlay.Core.Tests.Forecast;

public class ArchetypeForecasterTests(ITestOutputHelper output)
{
    private const string Char = "CHARACTER.TEST";
    private const string Key = "CARD.KEY";
    private const string Archetype = "test.key";

    private static string RealPackDir => Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1");
    private static string CommittedCatalog => Path.Combine(TestPaths.RepoRoot, "data", "catalog", "cards.v0.107.1.json");

    private static readonly Lazy<DataPack> RealPack = new(() => DataPackLoader.LoadDirectory(RealPackDir));
    private static readonly Lazy<ForecastCardPool> RealPool = new(() => ForecastCardPool.FromCatalogJson(File.ReadAllText(CommittedCatalog)));

    /// <summary>
    /// One archetype whose only card is <see cref="Key"/> (owning it = commit 1 = complete). <see cref="Key"/> is S tier, every
    /// filler is D tier, so the greedy picker takes Key whenever offered and skips everything else.
    /// </summary>
    private static (DataPack Pack, ForecastCardPool Pool) KeyPack(PoolRarity keyRarity, int commons, int uncommons = 0, int rares = 0)
    {
        var cards = new List<PoolCard> { new(Key, Char, keyRarity, "attack") };
        void Fill(string prefix, int n, PoolRarity r)
        {
            for (var i = 0; i < n; i++) cards.Add(new PoolCard($"CARD.{prefix}{i}", Char, r, "skill"));
        }
        Fill("C", commons, PoolRarity.Common);
        Fill("U", uncommons, PoolRarity.Uncommon);
        Fill("R", rares, PoolRarity.Rare);

        var tiers = cards.ToDictionary(c => c.Id, c => new CardTierEntry { Tier = c.Id == Key ? Tier.S : Tier.D }, StringComparer.Ordinal);
        var pack = new DataPack
        {
            Tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal) { [Char] = new TierPack { Character = Char, Cards = tiers } },
            Archetypes =
            [
                new Archetype
                {
                    Id = Archetype, Character = Char, Names = new() { ["kor"] = "열쇠 덱" }, FullWeight = 1.0,
                    Cards = new ArchetypeCards { Core = [Key] },
                },
            ],
        };
        return (pack, new ForecastCardPool(cards));
    }

    private static RunState State(int act = 1, int floor = 1, params string[] deck) => new()
    {
        Character = Char,
        Act = act,
        Floor = floor,
        Deck = deck.Select(id => new DeckCard(new CardRef(id))).ToArray(),
    };

    private static RewardEvent[] Rewards(RewardKind kind, int n, int act = 1) => Enumerable.Repeat(new RewardEvent(kind, act), n).ToArray();

    [Fact]
    public void UniformPool_MatchesClosedForm()
    {
        // All 5 cards common → every rarity roll that misses the empty uncommon/rare pools falls back to any card,
        // so each reward is 3 distinct cards out of 5: P(Key in a reward) = 3/5, P(seen in 2 rewards) = 1 − (2/5)² = 0.84.
        var (pack, pool) = KeyPack(PoolRarity.Common, commons: 4);
        var f = new ArchetypeForecaster(pack, pool);
        var result = f.Forecast(
            new ForecastRequest { State = State(), FixedSchedule = Rewards(RewardKind.Normal, 2) },
            new ForecastOptions { Simulations = 8000, Seed = 7 });

        var key = Assert.Single(result.Archetypes);
        Assert.Equal(0.84, key.EndOfRun.PComplete, 2);
        Assert.Equal(key.EndOfRun.PComplete, key.EndOfRun.PAllCore, 9);
        Assert.Equal(2.0, result.ExpectedUntilEnd.Normal, 9);
    }

    [Fact]
    public void BossReward_OffersOnlyRares()
    {
        var (rarePack, rarePool) = KeyPack(PoolRarity.Rare, commons: 10, rares: 2); // rares = {Key, R0, R1}: all three offered
        var rare = new ArchetypeForecaster(rarePack, rarePool).Forecast(
            new ForecastRequest { State = State(), FixedSchedule = [new RewardEvent(RewardKind.Boss, 1)] },
            new ForecastOptions { Simulations = 500, Seed = 1 });
        Assert.Equal(1.0, rare.Archetypes[0].EndOfRun.PComplete, 9);

        var (commonPack, commonPool) = KeyPack(PoolRarity.Common, commons: 10, rares: 3);
        var common = new ArchetypeForecaster(commonPack, commonPool).Forecast(
            new ForecastRequest { State = State(), FixedSchedule = [new RewardEvent(RewardKind.Boss, 1)] },
            new ForecastOptions { Simulations = 500, Seed = 1 });
        Assert.Equal(0.0, common.Archetypes[0].EndOfRun.PComplete, 9);
    }

    [Fact]
    public void HigherRareOffset_RaisesChanceOfRareCard()
    {
        var (pack, pool) = KeyPack(PoolRarity.Rare, commons: 20, uncommons: 20); // Key is the only rare
        var f = new ArchetypeForecaster(pack, pool);
        var schedule = Rewards(RewardKind.Normal, 3);
        double P(double offset) => f.Forecast(
            new ForecastRequest { State = State(floor: 10), RarityOffset = offset, FixedSchedule = schedule },
            new ForecastOptions { Simulations = 4000, Seed = 3 }).Archetypes[0].EndOfRun.PComplete;

        var low = P(ForecastOdds.OffsetReset);
        var high = P(ForecastOdds.OffsetMax);
        output.WriteLine($"P(rare key in 3 rewards): offset -5% → {low:P1}, offset 40% → {high:P1}");
        // 9 slots; rare chance 3% + offset, offset −5% → +3% in 1% steps: 1 − Π(1 − p) ≈ 0.19.
        Assert.InRange(low, 0.15, 0.24);
        Assert.True(high > 0.9, $"high offset should almost guarantee the only rare: {high}");
    }

    [Fact]
    public void Ascension7_UsesScarcityOdds()
    {
        var (pack, pool) = KeyPack(PoolRarity.Rare, commons: 20, uncommons: 20);
        var f = new ArchetypeForecaster(pack, pool);
        double P(int asc) => f.Forecast(
            new ForecastRequest { State = State(floor: 10) with { Ascension = asc }, RarityOffset = 0, FixedSchedule = Rewards(RewardKind.Elite, 4) },
            new ForecastOptions { Simulations = 6000, Seed = 5 }).Archetypes[0].EndOfRun.PComplete;
        Assert.True(P(0) > P(7) + 0.05);
    }

    [Fact]
    public void AlreadyComplete_IsCertain_AndNextBossNeverExceedsEnd()
    {
        var (pack, pool) = KeyPack(PoolRarity.Common, commons: 30);
        var f = new ArchetypeForecaster(pack, pool);
        var done = f.Forecast(new ForecastRequest { State = State(1, 1, Key) }, new ForecastOptions { Simulations = 200 });
        Assert.True(done.Archetypes[0].IsCompleteNow);
        Assert.Equal(1.0, done.Archetypes[0].NextBoss.PComplete);

        var open = f.Forecast(new ForecastRequest { State = State(2, 20) }, new ForecastOptions { Simulations = 2000 });
        var a = open.Archetypes[0];
        Assert.True(a.NextBoss.PComplete <= a.EndOfRun.PComplete);
        Assert.True(open.ExpectedUntilNextBoss.Total < open.ExpectedUntilEnd.Total);
        Assert.Equal(0, open.ExpectedUntilNextBoss.Boss); // the boss's own reward is after the "next boss" horizon
        Assert.Equal(1.0, open.ExpectedUntilEnd.Boss, 9); // act 2 boss rewards; act 3 boss gives none
    }

    [Fact]
    public void SameRequest_GivesSameNumbers_AndIsCached()
    {
        var (pack, pool) = KeyPack(PoolRarity.Uncommon, commons: 15, uncommons: 15, rares: 5);
        var request = new ForecastRequest { State = State(1, 3) };
        var a = new ArchetypeForecaster(pack, pool).Forecast(request, new ForecastOptions { Simulations = 300 });
        var bForecaster = new ArchetypeForecaster(pack, pool);
        var b = bForecaster.Forecast(request, new ForecastOptions { Simulations = 300 });
        Assert.Equal(a.Seed, b.Seed); // seed derived from the state, stable across instances/processes
        Assert.Equal(a.Archetypes[0].EndOfRun.PComplete, b.Archetypes[0].EndOfRun.PComplete);
        Assert.False(b.FromCache);
        Assert.True(bForecaster.Forecast(request, new ForecastOptions { Simulations = 300 }).FromCache);
    }

    [Fact]
    public void Map_RemainingPathDrivesTheCurrentAct()
    {
        // start → monster → elite → boss, standing on the monster: only the elite is left before the boss.
        var map = new MapBuilder(3, 1, (0, 1))
            .Add(0, 1, MapNodeType.Monster, (0, 2))
            .Add(0, 2, MapNodeType.Elite, (0, 3))
            .Build(current: new MapCoord(0, 1));
        var (pack, pool) = KeyPack(PoolRarity.Common, commons: 30);
        var r = new ArchetypeForecaster(pack, pool).Forecast(
            new ForecastRequest { State = State(1, 2), Map = map },
            new ForecastOptions { Simulations = 200, Seed = 1 });

        Assert.True(r.UsedMap);
        Assert.Equal(new RewardCounts(0, 1, 0, 0), r.ExpectedUntilNextBoss);
        Assert.True(r.ExpectedUntilEnd.Total > 10); // acts 2-3 from the typical layout
    }

    [Theory]
    [InlineData(1, 1, 14)]
    [InlineData(1, 8, 7)]
    [InlineData(1, 16, 0)]
    [InlineData(2, 17, 13)]
    [InlineData(3, 40, 4)]
    public void RemainingContentFloors_FromGlobalFloor(int act, int floor, int expected)
        => Assert.Equal(expected, RewardScheduler.RemainingContentFloors(act, floor));

    [Fact]
    public void CommittedCatalog_PoolMatchesCatalogCounts()
    {
        var pool = RealPool.Value;
        var defect = pool.CharacterCards("CHARACTER.DEFECT", multiplayer: false).ToArray();
        Assert.Equal(20, defect.Count(c => c.Rarity == PoolRarity.Common));
        Assert.Equal(35, defect.Count(c => c.Rarity == PoolRarity.Uncommon));
        Assert.Equal(25, defect.Count(c => c.Rarity == PoolRarity.Rare));
        Assert.Equal(82, pool.CharacterCards("CHARACTER.DEFECT", multiplayer: true).Count());
        Assert.Equal(53, pool.ColorlessCards(multiplayer: false).Count());
        Assert.DoesNotContain(defect, c => c.Id.StartsWith("CARD.STRIKE", StringComparison.Ordinal)); // basics never offered
    }

    [Fact]
    public void RealPack_InitialCommitMatchesDetector_AndIsFast()
    {
        var pack = RealPack.Value;
        var forecaster = new ArchetypeForecaster(pack, RealPool.Value);
        var run = RunHistoryReader.ReadFile(TestPaths.AllRuns[^1]);
        var me = LocalPlayer.Resolve(run);
        var timeline = DeckTimeline.Build(run, me.Id);
        var state = timeline.StateBefore(Math.Min(8, run.Nodes.Count - 1));

        forecaster.Forecast(new ForecastRequest { State = state }, new ForecastOptions { Simulations = 50, Seed = 99 }); // warm-up (JIT, model)
        var sw = Stopwatch.StartNew();
        var result = forecaster.Forecast(new ForecastRequest { State = state }, new ForecastOptions { Simulations = 1500 });
        sw.Stop();
        output.WriteLine($"{state.Character} floor {state.Floor}: 1500 sims in {result.ElapsedMs:0.0} ms (call {sw.Elapsed.TotalMilliseconds:0.0} ms), pool {result.PoolSize}");
        foreach (var a in result.Archetypes)
        {
            output.WriteLine($"  {a.Archetype.Id}: now {a.CurrentCommit:P0} → boss {a.NextBoss.PComplete:P0}, end {a.EndOfRun.PComplete:P0} (primary {a.EndOfRun.PPrimary:P0})");
        }

        Assert.True(result.PoolSize >= 75);
        Assert.True(sw.Elapsed.TotalMilliseconds < 1000, $"1500 sims took {sw.Elapsed.TotalMilliseconds} ms");
        var detected = ArchetypeDetector.Detect(state, pack).ToDictionary(s => s.Archetype.Id);
        foreach (var a in result.Archetypes)
        {
            Assert.Equal(detected[a.Archetype.Id].Commit, a.CurrentCommit, 9);
            Assert.Equal(detected[a.Archetype.Id].CompletionPercent, a.CurrentCompletionPercent, 9);
            Assert.InRange(a.EndOfRun.PComplete, 0, 1);
            Assert.Equal(1.0, a.EndOfRun.PStageAtLeast[0], 9);
        }
        Assert.InRange(result.Archetypes.Sum(a => a.EndOfRun.PPrimary), 0.0, 1.0 + 1e-9);
    }

    [Fact]
    public void EnginePolicy_RunsAndAgreesRoughlyWithGreedy()
    {
        var (pack, pool) = KeyPack(PoolRarity.Common, commons: 4);
        var f = new ArchetypeForecaster(pack, pool);
        var request = new ForecastRequest { State = State(), FixedSchedule = Rewards(RewardKind.Normal, 2) };
        var engine = f.Forecast(request, new ForecastOptions { Simulations = 400, Seed = 2, Policy = PickPolicy.Engine });
        Assert.Equal(PickPolicy.Engine, engine.Policy);
        Assert.Equal(0.84, engine.Archetypes[0].EndOfRun.PComplete, 1);
    }
}
