using Sts2Overlay.GameState.Log;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// The game's RNG port (xoshiro256** + SplitMix64, MegaRandom/Rng) and the replays built on it. Expected values come
/// from a real run's log (seed ZSN877JR14: its "Card rarity: Rolled X" lines) and from an independent Python port.
/// </summary>
public class GameRngTests
{
    [Fact]
    public void Deterministic_hash_matches_the_game()
    {
        // StringHelper.GetDeterministicHashCode, values from the Python port.
        Assert.Equal(3835919258u, (uint)GameRng.DeterministicHash("rewards"));
        Assert.Equal(497466721u, (uint)GameRng.DeterministicHash("niche"));
        Assert.Equal(757602046u, (uint)GameRng.DeterministicHash(""));
        Assert.Equal(3452614589u, (uint)GameRng.DeterministicHash("a"));
    }

    [Fact]
    public void Rewards_stream_reproduces_logged_rarity_rolls()
    {
        // Run ZSN877JR14 logged these rolls; they sit at these counters of each player's Rewards stream.
        uint runSeed = (uint)GameRng.DeterministicHash("ZSN877JR14");
        var p0 = new RngTape(new GameRng(runSeed, "rewards"));
        Assert.Equal(0.6362035f, GameRng.ToFloat(p0.At(4)));
        Assert.Equal(0.70350623f, GameRng.ToFloat(p0.At(7)));
        Assert.Equal(0.4104836f, GameRng.ToFloat(p0.At(10)));
        var p1 = new RngTape(new GameRng(runSeed + 1, "rewards"));
        Assert.Equal(0.7654361f, GameRng.ToFloat(p1.At(4)));
        Assert.Equal(4, p0.FindFloat(0.6362035f, 0, 100));
    }

    [Fact]
    public void Located_rolls_give_the_upgrade_roll_two_values_later()
    {
        var replay = new RewardRollReplay("ZSN877JR14", 0);
        CardRarityRolledEvent Roll(float v) => new(v, 0.03, 0) { RollFloat = v };
        List<OfferSlot?> slots = replay.Locate([Roll(0.6362035f), Roll(0.70350623f), Roll(0.4104836f)]);
        Assert.Equal([4, 7, 10], slots.Select(s => s!.Counter));
        var tape = new RngTape(new GameRng((uint)GameRng.DeterministicHash("ZSN877JR14"), "rewards"));
        Assert.Equal(GameRng.ToFloat(tape.At(6)), slots[0]!.UpgradeRoll);
        // A roll that isn't in my stream (another player's) is skipped, not forced.
        Assert.Null(Assert.Single(replay.Locate([Roll(0.123456f)])));
    }

    [Theory]
    [InlineData(0.25f, "Common", 1, 0, true)]      // act 2: 25%, compared as decimal with <=
    [InlineData(0.2500001f, "Common", 1, 0, false)]
    [InlineData(0.1f, "Rare", 2, 0, false)]        // rares never roll an upgrade
    [InlineData(0.2f, "Uncommon", 1, 7, false)]    // ascension 7 (Scarcity): 12.5% per act
    [InlineData(0.2f, "Uncommon", 2, 7, true)]
    public void Upgrade_odds_follow_act_and_ascension(float roll, string rarity, int actIndex, int ascension, bool upgraded) =>
        Assert.Equal(upgraded, RewardRollReplay.Upgrades(roll, rarity, actIndex, ascension));

    [Fact]
    public void Shuffle_is_fisher_yates_from_the_end()
    {
        var rng = new GameRng(12345);
        var copy = new GameRng(12345);
        var list = new List<int> { 0, 1, 2, 3, 4 };
        rng.Shuffle(list);
        var expected = new List<int> { 0, 1, 2, 3, 4 };
        for (int n = 4; n > 0; n--)
        {
            int k = copy.NextInt(n + 1);
            (expected[n], expected[k]) = (expected[k], expected[n]);
        }
        Assert.Equal(expected, list);
        Assert.Equal(4, rng.Counter);
    }
}
