using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// <see cref="RngSeeds"/>: the two generations of the game's seed arithmetic. The v0.111.0 vectors are plain numbers
/// copied from a live single-player save of run PZ4J3CV19SSW (schema 20): the save keeps the player seed and each
/// stream's full xoshiro256** state after <c>counter</c> draws, so the port has to land on the very same 256 bits.
/// </summary>
public class RngSeedsTests
{
    private const string LiveSeed = "PZ4J3CV19SSW";

    [Theory]
    [InlineData("", 0xEF46DB3751D8E999UL)]   // the reference XXH64 test vectors (seed 0)
    [InlineData("abc", 0x44BC2CF5AD770999UL)]
    public void XxHash64_matches_the_reference_vectors(string text, ulong expected) =>
        Assert.Equal(expected, XxHash64.Hash(text));

    [Fact]
    public void XxHash64_handles_every_tail_length_and_long_inputs()
    {
        // Lengths 0..70 cover the 32-byte stripes and the 8/4/1-byte tails. Self-consistency: the span and string
        // overloads agree and every length gives a different hash; the 32+ path is pinned by the save's stream states.
        var seen = new HashSet<ulong>();
        for (int n = 0; n <= 70; n++)
        {
            string s = new('x', n);
            ulong h = XxHash64.Hash(s);
            Assert.Equal(h, XxHash64.Hash(System.Text.Encoding.UTF8.GetBytes(s), 0));
            Assert.True(seen.Add(h), $"length {n} collides");
        }
        Assert.NotEqual(XxHash64.Hash("é"), XxHash64.Hash("e")); // UTF-8 bytes, not UTF-16 chars
    }

    [Fact]
    public void Player_seed_of_the_live_v0111_save()
    {
        // players[0].rng.seed = XxHash64(seed string) + slot (Player.InitializeSeed).
        Assert.Equal(17314056245423790712UL, RngSeeds.PlayerSeed(RngScheme.Xx64, LiveSeed, 0));
        Assert.Equal(17314056245423790713UL, RngSeeds.PlayerSeed(RngScheme.Xx64, LiveSeed, 1));
    }

    [Theory]
    // players[0].rng.rngs.<name>: counter, s0..s3 (PlayerRngSet streams).
    [InlineData("rewards", 100, 7470437811487826925UL, 7191976075827331288UL, 15918572014779316160UL, 11113648970953882149UL)]
    [InlineData("shops", 28, 5643433467571989290UL, 9729121668075998084UL, 10771474538256876946UL, 3862709146636572950UL)]
    [InlineData("transformations", 0, 7166276354881792554UL, 2196148207066587247UL, 1088548344139451518UL, 9999835608701426757UL)]
    public void Player_streams_reproduce_the_saved_state(string name, int counter, ulong s0, ulong s1, ulong s2, ulong s3)
    {
        var rng = new GameRng(RngSeeds.PlayerStream(RngScheme.Xx64, LiveSeed, 0, name));
        for (int i = 0; i < counter; i++) rng.NextULong();
        Assert.Equal((s0, s1, s2, s3), rng.State);
    }

    [Theory]
    // rng.rngs.<name>: the run streams (RunRngSet: new Rng(Seed, snake_case(type))).
    [InlineData("up_front", 415, 10937219914285286424UL, 16657310091350244448UL, 9433750441328952950UL, 6797511358196330367UL)]
    [InlineData("unknown_map_point", 3, 6746503582295282133UL, 9323781307094158130UL, 6673645659154050700UL, 7335803470100973397UL)]
    [InlineData("niche", 13, 16584809462258651549UL, 17052025641353933253UL, 6262134149091048271UL, 17857298647706033920UL)]
    public void Run_streams_reproduce_the_saved_state(string name, int counter, ulong s0, ulong s1, ulong s2, ulong s3)
    {
        var rng = new GameRng(RngSeeds.RunStream(RngScheme.Xx64, LiveSeed, name));
        for (int i = 0; i < counter; i++) rng.NextULong();
        Assert.Equal((s0, s1, s2, s3), rng.State);
    }

    [Fact]
    public void Live_v0111_run_offers_the_neow_options_the_game_showed()
    {
        // The game offered Golden Pearl, Nutritious Oyster, Large Capsule (button order). That profile has every
        // character unlocked; with Kaleidoscope ruled out the shuffle lands elsewhere.
        Assert.Equal(["GOLDEN_PEARL", "NUTRITIOUS_OYSTER", "LARGE_CAPSULE"], NeowOptions.Relics(LiveSeed, 0, 1, allCharactersUnlocked: true, RngScheme.Xx64));
        Assert.Equal(["WINGED_BOOTS", "PHIAL_HOLSTER", "LARGE_CAPSULE"], NeowOptions.Relics(LiveSeed, 0, 1, allCharactersUnlocked: false, RngScheme.Xx64));
        // What the v0.107.1 arithmetic and pools give for the same seed (the overlay's wrong prediction).
        Assert.Equal(["STONE_HUMIDIFIER", "FISHING_ROD", "NEOWS_BONES"], NeowOptions.Relics(LiveSeed, 0, 1, allCharactersUnlocked: true, RngScheme.Legacy32));
    }

    [Fact]
    public void Legacy32_keeps_the_v0107_uint_arithmetic()
    {
        // The formulas the v0.107.1 port used, written out: everything truncated to 32 bits.
        const string seed = "ZSN877JR14";
        uint run = (uint)GameRng.DeterministicHash(seed);
        Assert.Equal(run, RngSeeds.RunSeed(RngScheme.Legacy32, seed));
        Assert.Equal(unchecked((uint)(GameRng.DeterministicHash(seed) + 1)), RngSeeds.PlayerSeed(RngScheme.Legacy32, seed, 1));
        Assert.Equal(unchecked((uint)(GameRng.DeterministicHash(seed) + 1) + (uint)GameRng.DeterministicHash("rewards")),
                     RngSeeds.PlayerStream(RngScheme.Legacy32, seed, 1, "rewards"));
        Assert.Equal(unchecked((uint)((int)run + 1) + (uint)GameRng.DeterministicHash("NEOW")),
                     RngSeeds.EventStream(RngScheme.Legacy32, seed, 1, "EVENT.NEOW"));
        Assert.True(RngSeeds.EventStream(RngScheme.Legacy32, seed, 0, "NEOW") <= uint.MaxValue);
    }

    [Fact]
    public void Xx64_wraps_at_64_bits()
    {
        ulong run = XxHash64.Hash(LiveSeed);
        Assert.Equal(run, RngSeeds.RunSeed(RngScheme.Xx64, LiveSeed));
        Assert.Equal(unchecked(run + 2 + XxHash64.Hash("NEOW")), RngSeeds.EventStream(RngScheme.Xx64, LiveSeed, 2, "EVENT.NEOW"));
        Assert.Equal(unchecked(run + XxHash64.Hash("PAEL")), RngSeeds.EventStream(RngScheme.Xx64, LiveSeed, 2, "PAEL", shared: true));
        Assert.True(RngSeeds.EventStream(RngScheme.Xx64, LiveSeed, 0, "NEOW") > uint.MaxValue); // no truncation
    }

    [Fact]
    public void A_migrated_seed_keeps_its_old_run_seed()
    {
        // SerializableRunV18ToV19 prefixes "old"; RunRngSet then seeds with (uint)GetDeterministicHashCodeOld(rest), and
        // streams made after the migration add the 64-bit name hash. Player streams continue the old arithmetic.
        const string rest = "ZSN877JR14";
        ulong old = (uint)GameRng.DeterministicHash(rest);
        Assert.Equal(old, RngSeeds.RunSeed(RngScheme.Xx64, "old" + rest));
        Assert.Equal(unchecked(old + XxHash64.Hash("map_jitter_1")), RngSeeds.RunStream(RngScheme.Xx64, "old" + rest, "map_jitter_1"));
        Assert.Equal(RngSeeds.PlayerStream(RngScheme.Legacy32, rest, 1, "rewards"), RngSeeds.PlayerStream(RngScheme.Xx64, "old" + rest, 1, "rewards"));
        // Its Neow happened on the old game: old hash, old pools.
        Assert.Equal(NeowOptions.Relics(rest, 0, 1, true, RngScheme.Legacy32), NeowOptions.Relics("old" + rest, 0, 1, true, RngScheme.Xx64));
    }

    [Theory]
    [InlineData(16, RngScheme.Legacy32)]
    [InlineData(18, RngScheme.Legacy32)]
    [InlineData(19, RngScheme.Xx64)]
    [InlineData(20, RngScheme.Xx64)]
    public void Save_schema_picks_the_scheme(int schema, RngScheme expected) => Assert.Equal(expected, RngSeeds.ForSaveSchema(schema));

    [Fact]
    public void A_save_without_a_schema_says_nothing() => Assert.Null(RngSeeds.ForSaveSchema(0));

    [Theory]
    [InlineData("v0.107.1", RngScheme.Legacy32)]
    [InlineData("v0.108.0", RngScheme.Legacy32)]
    [InlineData("v0.109.0", RngScheme.Xx64)]
    [InlineData("v0.109.1", RngScheme.Xx64)]
    [InlineData("v0.111.0", RngScheme.Xx64)]
    [InlineData("0.110.1", RngScheme.Xx64)]
    [InlineData("v0.108", RngScheme.Legacy32)]
    [InlineData("v1.0.0", RngScheme.Xx64)]
    [InlineData(null, RngScheme.Xx64)]          // unknown → the current game
    [InlineData("garbage", RngScheme.Xx64)]
    public void Game_version_picks_the_scheme(string? version, RngScheme expected) => Assert.Equal(expected, RngSeeds.ForGameVersion(version));
}
