namespace Sts2Overlay.Resources.Pck;

/// <summary>
/// Decodes BC7 (BPTC_RGBA) block-compressed pixels, the format Godot uses for VRAM textures such as the relic atlas
/// (<c>images/atlases/relic_atlas.png</c> is imported as <c>…bptc.ctex</c>, see <see cref="CtexImage"/>).
///
/// <para>A BC7 block is 16 bytes for 4x4 pixels. Its first bits name one of 8 modes; each mode splits the block into
/// 1-3 subsets (a partition table), stores two RGB(A) endpoints per subset at some precision (optionally with a
/// shared low "p-bit"), and gives every pixel a 2-4 bit index that interpolates between its subset's endpoints.
/// Layout and tables follow the D3D11 BC7 format specification.</para>
/// </summary>
public static class Bc7Decoder
{
    private readonly record struct Mode(int Subsets, int PartitionBits, int RotationBits, int IndexSelectionBits,
        int ColorBits, int AlphaBits, bool EndpointPBits, bool SharedPBits, int IndexBits, int Index2Bits);

    private static readonly Mode[] Modes =
    [
        new(3, 4, 0, 0, 4, 0, true, false, 3, 0),
        new(2, 6, 0, 0, 6, 0, false, true, 3, 0),
        new(3, 6, 0, 0, 5, 0, false, false, 2, 0),
        new(2, 6, 0, 0, 7, 0, true, false, 2, 0),
        new(1, 0, 2, 1, 5, 6, false, false, 2, 3),
        new(1, 0, 2, 0, 7, 8, false, false, 2, 2),
        new(1, 0, 0, 0, 7, 7, true, false, 4, 0),
        new(2, 6, 0, 0, 5, 5, true, false, 2, 0),
    ];

    private static readonly int[] Weights2 = [0, 21, 43, 64];
    private static readonly int[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];
    private static readonly int[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>Two-subset partitions: bit i set = pixel i (row-major) belongs to subset 1.</summary>
    private static readonly ushort[] Partition2 =
    [
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
        0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
        0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
    ];

    /// <summary>Three-subset partitions, 16 pixels each (row-major), subset 0..2.</summary>
    private static readonly string[] Partition3 =
    [
        "0011001102212222", "0001001122112221", "0000200122112211", "0222002200110111", "0000000011221122", "0011001100220022",
        "0022002211111111", "0011001122112211", "0000000011112222", "0000111111112222", "0000111122222222", "0012001200120012",
        "0112011201120112", "0122012201220122", "0011011211221222", "0011200122002220", "0001001101121122", "0111001120012200",
        "0000112211221122", "0022002200221111", "0111011102220222", "0001000122212221", "0000001101220122", "0000110022102210",
        "0122012200110000", "0012001211222222", "0110122112210110", "0000011012211221", "0022110211020022", "0110011020022222",
        "0011012201220011", "0000200022112221", "0000000211221222", "0222002200120011", "0011001200220222", "0120012001200120",
        "0000111122220000", "0120120120120120", "0120201212010120", "0011220011220011", "0011112222000011", "0101010122222222",
        "0000000021212121", "0022112200221122", "0022001100220011", "0220122102201221", "0101222222220101", "0000212121212121",
        "0101010101012222", "0222011102220111", "0002111200021112", "0000211221122112", "0222011101110222", "0002111211120002",
        "0110011001102222", "0000000021122112", "0110011022222222", "0022001100110022", "0022112211220022", "0000000000002112",
        "0002000100020001", "0222122202221222", "0101222222222222", "0111201122012220",
    ];

    private static readonly byte[] Anchor2 =
    [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    ];

    private static readonly byte[] Anchor3a =
    [
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
    ];

    private static readonly byte[] Anchor3b =
    [
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    ];

    /// <summary>
    /// Decodes a whole BC7 image (blocks row by row) into straight RGBA8, <paramref name="width"/> x <paramref name="height"/>.
    /// Partial blocks at the right/bottom edge are clipped.
    /// </summary>
    public static byte[] DecodeImage(ReadOnlySpan<byte> blocks, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        if (blocks.Length < bw * bh * 16) throw new ArgumentException($"BC7 data too short: {blocks.Length} bytes for {width}x{height}.", nameof(blocks));
        var rgba = new byte[width * height * 4];
        Span<byte> block = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                DecodeBlock(blocks.Slice((by * bw + bx) * 16, 16), block);
                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= height) break;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= width) break;
                        block.Slice((py * 4 + px) * 4, 4).CopyTo(rgba.AsSpan((y * width + x) * 4, 4));
                    }
                }
            }
        return rgba;
    }

    /// <summary>Decodes one 16-byte block into 16 RGBA pixels (64 bytes, row-major). Reserved mode 8 gives transparent black.</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        var bits = new BitReader(block);
        int modeIndex = 0;
        while (modeIndex < 8 && bits.Read(1) == 0) modeIndex++;
        if (modeIndex == 8)
        {
            rgba[..64].Clear();
            return;
        }
        Mode m = Modes[modeIndex];
        int partition = bits.Read(m.PartitionBits);
        int rotation = bits.Read(m.RotationBits);
        int indexSelection = bits.Read(m.IndexSelectionBits);

        int endpoints = m.Subsets * 2;
        Span<int> ep = stackalloc int[6 * 4]; // [endpoint][channel]
        for (int c = 0; c < 3; c++)
            for (int e = 0; e < endpoints; e++) ep[e * 4 + c] = bits.Read(m.ColorBits);
        for (int e = 0; e < endpoints; e++) ep[e * 4 + 3] = m.AlphaBits > 0 ? bits.Read(m.AlphaBits) : 255;

        int colorBits = m.ColorBits, alphaBits = m.AlphaBits;
        if (m.EndpointPBits || m.SharedPBits)
        {
            Span<int> p = stackalloc int[6];
            if (m.EndpointPBits)
                for (int e = 0; e < endpoints; e++) p[e] = bits.Read(1);
            else
                for (int s = 0; s < m.Subsets; s++) p[s * 2] = p[s * 2 + 1] = bits.Read(1);
            for (int e = 0; e < endpoints; e++)
            {
                for (int c = 0; c < 3; c++) ep[e * 4 + c] = (ep[e * 4 + c] << 1) | p[e];
                if (m.AlphaBits > 0) ep[e * 4 + 3] = (ep[e * 4 + 3] << 1) | p[e];
            }
            colorBits++;
            if (m.AlphaBits > 0) alphaBits++;
        }
        for (int e = 0; e < endpoints; e++)
        {
            for (int c = 0; c < 3; c++) ep[e * 4 + c] = Expand(ep[e * 4 + c], colorBits);
            if (m.AlphaBits > 0) ep[e * 4 + 3] = Expand(ep[e * 4 + 3], alphaBits);
        }

        Span<int> subset = stackalloc int[16];
        for (int i = 0; i < 16; i++)
            subset[i] = m.Subsets switch
            {
                2 => (Partition2[partition] >> i) & 1,
                3 => Partition3[partition][i] - '0',
                _ => 0,
            };
        bool IsAnchor(int i) => i == 0
            || (m.Subsets == 2 && i == Anchor2[partition])
            || (m.Subsets == 3 && (i == Anchor3a[partition] || i == Anchor3b[partition]));

        Span<int> idx = stackalloc int[16];
        Span<int> idx2 = stackalloc int[16];
        for (int i = 0; i < 16; i++) idx[i] = bits.Read(IsAnchor(i) ? m.IndexBits - 1 : m.IndexBits);
        if (m.Index2Bits > 0)
            for (int i = 0; i < 16; i++) idx2[i] = bits.Read(i == 0 ? m.Index2Bits - 1 : m.Index2Bits);

        for (int i = 0; i < 16; i++)
        {
            int s = subset[i];
            int e0 = s * 2 * 4, e1 = (s * 2 + 1) * 4;
            int colorIndex, alphaIndex, colorIndexBits, alphaIndexBits;
            if (m.Index2Bits == 0)
            {
                colorIndex = alphaIndex = idx[i];
                colorIndexBits = alphaIndexBits = m.IndexBits;
            }
            else if (indexSelection == 0)
            {
                (colorIndex, colorIndexBits, alphaIndex, alphaIndexBits) = (idx[i], m.IndexBits, idx2[i], m.Index2Bits);
            }
            else
            {
                (colorIndex, colorIndexBits, alphaIndex, alphaIndexBits) = (idx2[i], m.Index2Bits, idx[i], m.IndexBits);
            }
            int wc = WeightOf(colorIndexBits, colorIndex), wa = WeightOf(alphaIndexBits, alphaIndex);
            int r = Lerp(ep[e0], ep[e1], wc), g = Lerp(ep[e0 + 1], ep[e1 + 1], wc), b = Lerp(ep[e0 + 2], ep[e1 + 2], wc);
            int a = m.AlphaBits > 0 ? Lerp(ep[e0 + 3], ep[e1 + 3], wa) : 255;
            switch (rotation)
            {
                case 1: (r, a) = (a, r); break;
                case 2: (g, a) = (a, g); break;
                case 3: (b, a) = (a, b); break;
            }
            rgba[i * 4] = (byte)r;
            rgba[i * 4 + 1] = (byte)g;
            rgba[i * 4 + 2] = (byte)b;
            rgba[i * 4 + 3] = (byte)a;
        }
    }

    private static int Expand(int v, int bits) => bits >= 8 ? v : (v << (8 - bits)) | (v >> (2 * bits - 8));

    private static int WeightOf(int bits, int index) => bits switch
    {
        2 => Weights2[index],
        3 => Weights3[index],
        _ => Weights4[index],
    };

    private static int Lerp(int e0, int e1, int w) => ((64 - w) * e0 + w * e1 + 32) >> 6;

    /// <summary>Little-endian bit stream over one 128-bit block (bit k = byte k/8, bit k%8).</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public int Read(int count)
        {
            int v = 0;
            for (int i = 0; i < count; i++, _pos++)
                v |= ((_data[_pos >> 3] >> (_pos & 7)) & 1) << i;
            return v;
        }
    }
}
