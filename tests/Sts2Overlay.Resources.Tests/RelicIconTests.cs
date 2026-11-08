using System.IO.Compression;
using Sts2Overlay.Resources.Pck;

namespace Sts2Overlay.Resources.Tests;

/// <summary>
/// The pieces that turn the game's relic atlas into icon files: the BC7 block decoder, the PNG writer and the
/// AtlasTexture cutting. Every input here is synthetic (hand-built blocks and tiny pixel arrays), no game data.
/// </summary>
public class RelicIconTests
{
    /// <summary>Writes <paramref name="count"/> bits of <paramref name="value"/> at <paramref name="pos"/>, LSB first (BC7 bit order).</summary>
    private static void Put(byte[] block, ref int pos, int count, int value)
    {
        for (int i = 0; i < count; i++, pos++)
            if (((value >> i) & 1) != 0) block[pos >> 3] |= (byte)(1 << (pos & 7));
    }

    /// <summary>A mode-6 block (one subset, RGBA 7.7.7.7 endpoints + one p-bit each, 4-bit indices).</summary>
    private static byte[] Mode6(int r0, int r1, int g, int b, int a, int p0, int p1, Func<int, int> index)
    {
        var block = new byte[16];
        int pos = 0;
        Put(block, ref pos, 7, 1 << 6);   // mode 6: six zero bits, then a one
        Put(block, ref pos, 7, r0);
        Put(block, ref pos, 7, r1);
        Put(block, ref pos, 7, g);
        Put(block, ref pos, 7, g);
        Put(block, ref pos, 7, b);
        Put(block, ref pos, 7, b);
        Put(block, ref pos, 7, a);
        Put(block, ref pos, 7, a);
        Put(block, ref pos, 1, p0);
        Put(block, ref pos, 1, p1);
        for (int px = 0; px < 16; px++) Put(block, ref pos, px == 0 ? 3 : 4, index(px)); // pixel 0 is the anchor: 3 bits
        Assert.Equal(128, pos);
        return block;
    }

    [Fact]
    public void Bc7_mode6_endpoints_pbits_and_indices()
    {
        // R from (0<<1|1) = 1 to (127<<1|1) = 255; G, B, A constant 255. Pixel 0 uses index 0, the rest index 15.
        byte[] block = Mode6(0, 127, 127, 127, 127, 1, 1, px => px == 0 ? 0 : 15);
        var rgba = new byte[64];
        Bc7Decoder.DecodeBlock(block, rgba);
        Assert.Equal(new byte[] { 1, 255, 255, 255 }, rgba[..4]);
        for (int px = 1; px < 16; px++) Assert.Equal(new byte[] { 255, 255, 255, 255 }, rgba[(px * 4)..(px * 4 + 4)]);
    }

    [Fact]
    public void Bc7_mode6_interpolates_with_the_4bit_weights()
    {
        // e0 = 1, e1 = 255; weight table [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64] / 64.
        byte[] block = Mode6(0, 127, 0, 0, 127, 1, 1, px => px);
        var rgba = new byte[64];
        Bc7Decoder.DecodeBlock(block, rgba);
        int[] w = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
        for (int px = 0; px < 16; px++)
            Assert.Equal((1 * (64 - w[px]) + 255 * w[px] + 32) >> 6, rgba[px * 4]);
    }

    [Fact]
    public void Bc7_image_places_blocks_left_to_right_top_to_bottom()
    {
        byte[] white = Mode6(127, 127, 127, 127, 127, 1, 1, _ => 0);
        byte[] black = Mode6(0, 0, 0, 0, 127, 0, 0, _ => 0); // alpha (127<<1|0) = 254
        byte[] blocks = [.. white, .. black, .. black, .. white]; // 8x8 image, 2x2 blocks
        byte[] img = Bc7Decoder.DecodeImage(blocks, 8, 8);
        byte R(int x, int y) => img[(y * 8 + x) * 4];
        Assert.Equal(255, R(0, 0));
        Assert.Equal(0, R(4, 0));
        Assert.Equal(0, R(0, 4));
        Assert.Equal(255, R(7, 7));
        Assert.Equal(254, img[(0 * 8 + 4) * 4 + 3]);
    }

    [Fact]
    public void Png_writer_round_trips_pixels()
    {
        byte[] rgba = new byte[3 * 2 * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 11);
        byte[] png = PngWriter.Encode(rgba, 3, 2);

        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        // IHDR: width 3, height 2, 8 bits, colour type 6 (RGBA)
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(3, png[19]);
        Assert.Equal(2, png[23]);
        Assert.Equal(8, png[24]);
        Assert.Equal(6, png[25]);

        // IDAT: one zlib stream of (filter byte 0 + row) per row
        int idat = 8 + 25; // after the signature and the IHDR chunk (4 + 4 + 13 + 4)
        int len = (png[idat] << 24) | (png[idat + 1] << 16) | (png[idat + 2] << 8) | png[idat + 3];
        Assert.Equal("IDAT", System.Text.Encoding.ASCII.GetString(png, idat + 4, 4));
        using var z = new ZLibStream(new MemoryStream(png, idat + 8, len), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        byte[] rows = raw.ToArray();
        Assert.Equal(2 * (1 + 12), rows.Length);
        Assert.Equal(0, rows[0]);
        Assert.Equal(rgba[..12], rows[1..13]);
        Assert.Equal(0, rows[13]);
        Assert.Equal(rgba[12..], rows[14..]);
    }

    [Fact]
    public void Atlas_texture_rects_are_parsed()
    {
        const string tres = """
            [gd_resource type="AtlasTexture" load_steps=2 format=3]
            [ext_resource type="Texture2D" path="res://images/atlases/relic_atlas.png" id="1"]
            [resource]
            atlas = ExtResource("1")
            region = Rect2(170, 85, 80, 79)
            margin = Rect2(2, 3, 5, 6)
            """;
        Assert.Equal((170, 85, 80, 79), RelicIconExtractor.ParseRect(tres, "region"));
        Assert.Equal((2, 3, 5, 6), RelicIconExtractor.ParseRect(tres, "margin"));
        Assert.Null(RelicIconExtractor.ParseRect(tres, "filter_clip"));
    }

    [Fact]
    public void Cut_restores_the_trimmed_margin()
    {
        // 4x4 atlas, pixel value = its index; region (1,1,2,2) placed at margin offset (1,0) in a 2+1 x 2+2 canvas.
        byte[] atlas = new byte[4 * 4 * 4];
        for (int p = 0; p < 16; p++) atlas[p * 4] = atlas[p * 4 + 3] = (byte)(p + 1);
        byte[]? icon = RelicIconExtractor.Cut(atlas, 4, 4, (1, 1, 2, 2), (1, 0, 1, 2), out int w, out int h);
        Assert.NotNull(icon);
        Assert.Equal((3, 4), (w, h));
        byte At(int x, int y) => icon![(y * w + x) * 4];
        Assert.Equal(0, At(0, 0));        // margin stays transparent
        Assert.Equal(1 * 4 + 1 + 1, At(1, 0)); // atlas (1,1)
        Assert.Equal(1 * 4 + 2 + 1, At(2, 0)); // atlas (2,1)
        Assert.Equal(2 * 4 + 2 + 1, At(2, 1)); // atlas (2,2)
        Assert.Equal(0, At(1, 2));
        Assert.Null(RelicIconExtractor.Cut(atlas, 4, 4, (3, 3, 2, 2), (0, 0, 0, 0), out _, out _)); // outside the atlas
    }
}
