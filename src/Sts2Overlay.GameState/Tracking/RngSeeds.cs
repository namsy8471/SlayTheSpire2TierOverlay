using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// Which generation of the game's seed arithmetic a run uses. The generator itself (xoshiro256** seeded by SplitMix64,
/// <see cref="GameRng"/>) never changed; what changed is how a seed string and a stream name become the 64-bit seed.
/// </summary>
public enum RngScheme
{
    /// <summary>
    /// v0.108 and older (run saves up to schema 18): the 32-bit string hash (now <c>StringHelper.GetDeterministicHashCodeOld</c>)
    /// and every seed truncated to <c>uint</c> (<c>new Rng(uint seed, string name)</c>: <c>seed + (uint)hash(name)</c>).
    /// </summary>
    Legacy32,

    /// <summary>
    /// v0.109 and newer (run saves from schema 19): <c>StringHelper.GetDeterministicHashCode</c> is XxHash64 of the
    /// UTF-8 bytes (seed 0) and seeds are <c>ulong</c> with 64-bit wrap-around (<c>Rng(ulong seed, string name)</c>).
    /// </summary>
    Xx64,
}

/// <summary>
/// The one place that turns a run's seed string into the seeds of its random streams, for both <see cref="RngScheme"/>s
/// (decompiled v0.111.0 <c>RunRngSet</c>, <c>Player.InitializeSeed</c>, <c>PlayerRngSet</c>, <c>EventModel.BeginEvent</c>,
/// <c>Rng(ulong, string)</c>; v0.107.1 as the repo ported it before).
/// <para>
/// Saves written by v0.108 or older and loaded by a newer game are migrated (<c>SerializableRunV18ToV19</c>): the seed
/// string gets an <c>old</c> prefix, the run seed of such a run stays the old 32-bit hash of the rest
/// (<c>RunRngSet</c>: <c>(uint)GetDeterministicHashCodeOld(seed[3..])</c>), and the saved streams' states are computed
/// with the old arithmetic. Streams made after the migration (events, the map jitter) add the new 64-bit name hash to
/// that run seed.
/// </para>
/// </summary>
public static class RngSeeds
{
    /// <summary>First run-save <c>schema_version</c> with 64-bit seeds (<c>SerializableRunV18ToV19</c> migrates to it).</summary>
    public const int FirstXx64SaveSchema = 19;

    /// <summary>
    /// First game version with 64-bit seeds. Measured on run-history files: every vanilla v0.108.0 Neow replays with
    /// <see cref="RngScheme.Legacy32"/>, every v0.109.0 / v0.109.1 / v0.110.1 / v0.111.0 one with <see cref="RngScheme.Xx64"/>.
    /// </summary>
    public const string FirstXx64Version = "v0.109.0";

    /// <summary>What a run uses when nothing says otherwise: the game the overlay targets from v0.111 on.</summary>
    public const RngScheme Default = RngScheme.Xx64;

    /// <summary>Prefix the save migration puts in front of a pre-v0.109 seed string.</summary>
    public const string MigratedPrefix = "old";

    /// <summary>The scheme of a run save by its <c>schema_version</c>; null when the save has none (0).</summary>
    public static RngScheme? ForSaveSchema(int schemaVersion) =>
        schemaVersion <= 0 ? null : schemaVersion >= FirstXx64SaveSchema ? RngScheme.Xx64 : RngScheme.Legacy32;

    /// <summary>
    /// The scheme of a game version (<c>build_id</c> of a run-history file, <c>release=</c> in godot.log), e.g.
    /// <c>v0.108.0</c> → Legacy32. Unreadable or missing → <see cref="Default"/>.
    /// </summary>
    public static RngScheme ForGameVersion(string? version)
    {
        if (ParseVersion(version) is not { } v || ParseVersion(FirstXx64Version) is not { } first) return Default;
        return v.CompareTo(first) >= 0 ? RngScheme.Xx64 : RngScheme.Legacy32;
    }

    /// <summary>True for a seed string that a save migration prefixed (a run started before v0.109).</summary>
    public static bool IsMigrated(string seed) => seed.StartsWith(MigratedPrefix, StringComparison.Ordinal);

    /// <summary>A text's hash as the scheme adds it to a seed (StringHelper.GetDeterministicHashCode / ...Old).</summary>
    public static ulong Hash(RngScheme scheme, string text) =>
        scheme == RngScheme.Legacy32 ? (uint)LegacyHash(text) : XxHash64.Hash(text);

    /// <summary>The run seed (<c>RunRngSet.Seed</c>) of a seed string.</summary>
    public static ulong RunSeed(RngScheme scheme, string seed)
    {
        if (scheme == RngScheme.Legacy32) return (uint)LegacyHash(seed);
        return IsMigrated(seed) ? (uint)LegacyHash(seed[MigratedPrefix.Length..]) : XxHash64.Hash(seed);
    }

    /// <summary>
    /// <c>new Rng(seed, name)</c>: the name's hash added to the seed. Legacy32 keeps the old <c>uint</c> arithmetic
    /// (the seed is truncated first); Xx64 wraps around at 64 bits.
    /// </summary>
    public static ulong Named(RngScheme scheme, ulong seed, string name) => unchecked(
        scheme == RngScheme.Legacy32 ? (uint)((uint)seed + (uint)LegacyHash(name)) : seed + XxHash64.Hash(name));

    /// <summary>A run-level stream (<c>RunRngSet</c> streams such as <c>up_front</c>, or <c>map_jitter_0</c>).</summary>
    public static ulong RunStream(RngScheme scheme, string seed, string name) => Named(scheme, RunSeed(scheme, seed), name);

    /// <summary>
    /// The player's seed (<c>PlayerRngSet.Seed</c>, saved as <c>players[].rng.seed</c>): <c>hash(seed string) + slot</c>
    /// (Player.InitializeSeed). A migrated run keeps the seed its old save had.
    /// </summary>
    public static ulong PlayerSeed(RngScheme scheme, string seed, int slot)
    {
        if (scheme == RngScheme.Xx64 && IsMigrated(seed)) return PlayerSeed(RngScheme.Legacy32, seed[MigratedPrefix.Length..], slot);
        return unchecked(scheme == RngScheme.Legacy32 ? (uint)(LegacyHash(seed) + slot) : XxHash64.Hash(seed) + (ulong)slot);
    }

    /// <summary>
    /// A player stream (<c>rewards</c>, <c>shops</c>, <c>transformations</c>: PlayerRngSet.CreateRng). The migration
    /// computed a migrated run's player streams with the old arithmetic, and they continue from there.
    /// </summary>
    public static ulong PlayerStream(RngScheme scheme, string seed, int slot, string name)
    {
        if (scheme == RngScheme.Xx64 && IsMigrated(seed))
            return PlayerStream(RngScheme.Legacy32, seed[MigratedPrefix.Length..], slot, name);
        return Named(scheme, PlayerSeed(scheme, seed, slot), name);
    }

    /// <summary>
    /// An event's own stream (EventModel.BeginEvent): <c>runSeed + (shared ? 0 : slot) + hash(id entry)</c>, with
    /// <paramref name="eventId"/> either <c>EVENT.NEOW</c> or the entry <c>NEOW</c>.
    /// </summary>
    public static ulong EventStream(RngScheme scheme, string seed, int slot, string eventId, bool shared = false)
    {
        string entry = eventId.StartsWith("EVENT.", StringComparison.Ordinal) ? eventId["EVENT.".Length..] : eventId;
        ulong run = RunSeed(scheme, seed);
        int add = shared ? 0 : slot;
        return unchecked(scheme == RngScheme.Legacy32
            ? (uint)((int)(uint)run + add) + (uint)LegacyHash(entry)
            : (ulong)((long)run + add) + XxHash64.Hash(entry));
    }

    /// <summary>
    /// StringHelper.GetDeterministicHashCodeOld (the only hash before v0.109): two interleaved djb2-style hashes over
    /// the UTF-16 chars.
    /// </summary>
    public static int LegacyHash(string s)
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

    /// <summary>"v0.109.0" / "0.109" / "v0.110.1-beta" → (0, 109, 0) style tuple for comparison; null when unreadable.</summary>
    private static (int, int, int)? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        string[] parts = s.Split('.');
        if (parts.Length is < 2 or > 4) return null;
        var n = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int x)) return null;
            if (i < 3) n[i] = x;
        }
        return (n[0], n[1], n[2]);
    }
}

/// <summary>
/// XXH64 (Yann Collet's xxHash, 64-bit) over a string's UTF-8 bytes with seed 0: what v0.109+ hashes seed strings and
/// stream names with (<c>XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(s), 0)</c> in StringHelper). Written out here
/// instead of taking the System.IO.Hashing package for one function.
/// </summary>
internal static class XxHash64
{
    private const ulong P1 = 11400714785074694791UL;
    private const ulong P2 = 14029467366897019727UL;
    private const ulong P3 = 1609587929392839161UL;
    private const ulong P4 = 9650029242287828579UL;
    private const ulong P5 = 2870177450012600261UL;

    public static ulong Hash(string text)
    {
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        Span<byte> bytes = max <= 256 ? stackalloc byte[max] : new byte[max];
        int n = Encoding.UTF8.GetBytes(text, bytes);
        return Hash(bytes[..n], 0);
    }

    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed)
    {
        unchecked
        {
            int len = data.Length, i = 0;
            ulong h;
            if (len >= 32)
            {
                // Four lanes eat 32-byte stripes, then are folded together.
                ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
                for (; i + 32 <= len; i += 32)
                {
                    v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data[i..]));
                    v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 8)..]));
                    v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 16)..]));
                    v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 24)..]));
                }
                h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7)
                    + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
                h = Merge(h, v1);
                h = Merge(h, v2);
                h = Merge(h, v3);
                h = Merge(h, v4);
            }
            else h = seed + P5;

            h += (ulong)len;
            for (; i + 8 <= len; i += 8)
                h = BitOperations.RotateLeft(h ^ Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data[i..])), 27) * P1 + P4;
            if (i + 4 <= len)
            {
                h = BitOperations.RotateLeft(h ^ (BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * P1), 23) * P2 + P3;
                i += 4;
            }
            for (; i < len; i++)
                h = BitOperations.RotateLeft(h ^ (data[i] * P5), 11) * P1;

            // Avalanche: spread every input bit over the whole result.
            h ^= h >> 33;
            h *= P2;
            h ^= h >> 29;
            h *= P3;
            h ^= h >> 32;
            return h;
        }
    }

    private static ulong Round(ulong acc, ulong input) => unchecked(BitOperations.RotateLeft(acc + input * P2, 31) * P1);

    private static ulong Merge(ulong acc, ulong lane) => unchecked((acc ^ Round(0, lane)) * P1 + P4);
}
