using System.Buffers.Binary;
using System.IO.Compression;
using Sts2Overlay.Resources.Pck;
using ZstdSharp;

namespace Sts2Overlay.Tools.TitleBench;

/// <summary>
/// Reads the raw TrueType/OpenType bytes of a font straight out of the game's pack, in memory only.
///
/// Godot imports a font as <c>.godot/imported/&lt;name&gt;.ttf-&lt;hash&gt;.fontdata</c>: a FontFile resource
/// saved with FileAccessCompressed ("RSCC" header, zstd blocks). The resource keeps the original font file
/// as a PackedByteArray, so after decompressing we only have to find the sfnt header and its length.
/// </summary>
internal static class GameFonts
{
    public static byte[] Load(PckReader pck, string fileName)
    {
        string prefix = $".godot/imported/{fileName}-";
        PckEntry entry = pck.Entries.FirstOrDefault(e => e.Path.StartsWith(prefix, StringComparison.Ordinal) && e.Path.EndsWith(".fontdata", StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"No imported font '{fileName}' in the pack.");
        return ExtractSfnt(Decompress(pck.ReadAllBytes(entry)));
    }

    /// <summary>
    /// Godot core/io/file_access_compressed.cpp: "RSCC", u32 mode, u32 block size, u32 total size,
    /// u32 compressed size of each of (total / block size + 1) blocks, then the blocks.
    /// Modes (core/io/compression.h): 0 FastLZ, 1 Deflate, 2 Zstd, 3 Gzip.
    /// </summary>
    public static byte[] Decompress(byte[] raw)
    {
        if (raw.Length < 16 || raw[0] != 'R' || raw[1] != 'S' || raw[2] != 'C' || raw[3] != 'C')
            return raw; // stored uncompressed
        int mode = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
        int blockSize = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8));
        int total = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(12));
        int blocks = total / blockSize + 1;
        var output = new byte[total];
        int pos = 16 + 4 * blocks, outPos = 0;
        using var zstd = new Decompressor();
        for (int b = 0; b < blocks && outPos < total; b++)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(16 + 4 * b));
            int expect = Math.Min(blockSize, total - outPos);
            ReadOnlySpan<byte> src = raw.AsSpan(pos, size);
            int written = mode switch
            {
                2 => zstd.Unwrap(src, output.AsSpan(outPos, expect)),
                1 => Inflate(src, output.AsSpan(outPos, expect)),
                _ => throw new NotSupportedException($"RSCC compression mode {mode}"),
            };
            if (written != expect) throw new InvalidDataException($"RSCC block {b}: {written} of {expect} bytes");
            pos += size;
            outPos += written;
        }
        return output;
    }

    private static int Inflate(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        using var z = new ZLibStream(new MemoryStream(src.ToArray()), CompressionMode.Decompress);
        int n = 0;
        while (n < dst.Length)
        {
            int r = z.Read(dst[n..]);
            if (r == 0) break;
            n += r;
        }
        return n;
    }

    /// <summary>Finds an embedded sfnt (TrueType 0x00010000 / "OTTO" / "true") whose 'head' table checks out.</summary>
    public static byte[] ExtractSfnt(byte[] data)
    {
        for (int i = 0; i + 12 < data.Length; i++)
        {
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
            if (tag is not (0x00010000 or 0x4F54544F or 0x74727565)) continue;
            int numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 4));
            if (numTables is < 4 or > 64 || i + 12 + 16 * numTables > data.Length) continue;
            long end = 0;
            bool headOk = false, valid = true;
            for (int t = 0; t < numTables && valid; t++)
            {
                int rec = i + 12 + 16 * t;
                for (int k = 0; k < 4; k++)
                    if (data[rec + k] is < 0x20 or > 0x7E) valid = false;
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(rec + 8));
                uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(rec + 12));
                if (i + (long)offset + length > data.Length) { valid = false; break; }
                end = Math.Max(end, offset + length);
                if (data[rec] == 'h' && data[rec + 1] == 'e' && data[rec + 2] == 'a' && data[rec + 3] == 'd' && length >= 16)
                    headOk = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i + (int)offset + 12)) == 0x5F0F3CF5;
            }
            if (valid && headOk) return data.AsSpan(i, (int)end).ToArray();
        }
        throw new InvalidDataException("No sfnt font found in the resource.");
    }
}
