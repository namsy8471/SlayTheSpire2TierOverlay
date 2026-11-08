using System.Numerics;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// The game's seeded random number generator, ported bit for bit (MegaCrit.Sts2.Core.Random.MegaRandom + Rng):
/// xoshiro256** seeded by SplitMix64. Every call draws exactly one 64-bit output and bumps <see cref="Counter"/>,
/// which is why the game can save a stream as (seed, counter) and why we can replay one from the run seed.
/// </summary>
internal sealed class GameRng
{
    private ulong _s0, _s1, _s2, _s3;

    public GameRng(uint seed)
    {
        ulong x = seed;
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    /// <summary>A named stream (<c>new Rng(seed, "rewards")</c>): the name's hash is added to the seed.</summary>
    public GameRng(uint seed, string name) : this(unchecked(seed + (uint)DeterministicHash(name))) { }

    /// <summary>Number of values drawn so far (Rng.Counter).</summary>
    public int Counter { get; private set; }

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

    /// <summary>StringHelper.GetDeterministicHashCode: two interleaved djb2-style hashes (stable across runs, unlike string.GetHashCode).</summary>
    public static int DeterministicHash(string s)
    {
        int a = 352654597, b = a;
        unchecked
        {
            for (int i = 0; i < s.Length; i += 2)
            {
                a = ((a << 5) + a) ^ s[i];
                if (i == s.Length - 1) break;
                b = ((b << 5) + b) ^ s[i + 1];
            }
            return a + b * 1566083941;
        }
    }
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
