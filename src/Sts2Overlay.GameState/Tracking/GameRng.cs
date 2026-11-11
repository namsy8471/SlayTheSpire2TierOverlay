using System.Numerics;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// The game's seeded random number generator, ported bit for bit (MegaCrit.Sts2.Core.Random.MegaRandom + Rng):
/// xoshiro256** seeded by SplitMix64. Every call draws exactly one 64-bit output and bumps <see cref="Counter"/>,
/// which is why the game can save a stream as (seed, counter) and why we can replay one from the run seed.
/// <para>
/// The seed is a <c>ulong</c> since v0.109 (<c>MegaRandom(ulong seed)</c>); older builds passed a <c>uint</c>, which
/// widens to the same SplitMix64 input. How a seed is made from the run's seed string is <see cref="RngSeeds"/>' job.
/// </para>
/// </summary>
internal sealed class GameRng
{
    private ulong _s0, _s1, _s2, _s3;

    public GameRng(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    /// <summary>A named stream (<c>new Rng(seed, "rewards")</c>): the name's hash is added to the seed (<see cref="RngSeeds.Named"/>).</summary>
    public GameRng(RngScheme scheme, ulong seed, string name) : this(RngSeeds.Named(scheme, seed, name)) { }

    /// <summary>Number of values drawn so far (Rng.Counter).</summary>
    public int Counter { get; private set; }

    /// <summary>The four xoshiro256** state words, as v0.109+ saves keep them (<c>rng.rngs.&lt;name&gt;.s0</c> ... <c>s3</c>).</summary>
    public (ulong S0, ulong S1, ulong S2, ulong S3) State => (_s0, _s1, _s2, _s3);

    private static ulong SplitMix64(ref ulong x)
    {
        ulong z = unchecked(x += 11400714819323198485UL);
        z = unchecked((z ^ (z >> 30)) * 13787848793156543929UL);
        z = unchecked((z ^ (z >> 27)) * 10723151780598845931UL);
        return z ^ (z >> 31);
    }

    /// <summary>One raw xoshiro256** output.</summary>
    public ulong NextULong()
    {
        Counter++;
        ulong result = unchecked(BitOperations.RotateLeft(_s1 * 5, 7) * 9);
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    public static double ToDouble(ulong raw) => (raw >> 11) * 1.1102230246251565E-16;

    /// <summary>Rng.NextFloat(): the double narrowed to float (this is the value the log prints).</summary>
    public static float ToFloat(ulong raw) => (float)ToDouble(raw);

    /// <summary>Rng.NextInt(max) / NextInt(0, max) / NextItem: (int)(NextDouble() * max).</summary>
    public static int ToInt(ulong raw, int maxExclusive) => (int)(ToDouble(raw) * maxExclusive);

    public double NextDouble() => ToDouble(NextULong());
    public float NextFloat() => ToFloat(NextULong());
    public int NextInt(int maxExclusive) => ToInt(NextULong(), maxExclusive);

    /// <summary>ListExtensions.UnstableShuffle (Fisher-Yates from the end).</summary>
    public void Shuffle<T>(IList<T> list)
    {
        for (int n = list.Count - 1; n > 0; n--)
        {
            int k = NextInt(n + 1);
            (list[n], list[k]) = (list[k], list[n]);
        }
    }

    /// <summary>
    /// The pre-v0.109 string hash (StringHelper.GetDeterministicHashCodeOld; v0.107.1 called it GetDeterministicHashCode):
    /// two interleaved djb2-style hashes, stable across runs unlike string.GetHashCode. v0.109+ uses
    /// <see cref="XxHash64"/>; <see cref="RngSeeds.Hash"/> picks by scheme.
    /// </summary>
    public static int DeterministicHash(string s) => RngSeeds.LegacyHash(s);
}

/// <summary>A stream whose values are kept, so a value can be looked up by counter and searched for.</summary>
internal sealed class RngTape(GameRng rng)
{
    private readonly List<ulong> _values = [];

    /// <summary>The raw value the stream's call number <paramref name="counter"/> (0-based) returns.</summary>
    public ulong At(int counter)
    {
        while (_values.Count <= counter) _values.Add(rng.NextULong());
        return _values[counter];
    }

    /// <summary>First counter in [from, to) whose NextFloat equals <paramref name="value"/>, or -1.</summary>
    public int FindFloat(float value, int from, int to)
    {
        for (int c = Math.Max(from, 0); c < to; c++)
            if (GameRng.ToFloat(At(c)) == value) return c;
        return -1;
    }
}
