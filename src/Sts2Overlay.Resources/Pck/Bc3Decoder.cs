using System.Buffers.Binary;

namespace Sts2Overlay.Resources.Pck;

/// <summary>Decodes BC3 (DXT5) texture blocks to straight RGBA8.</summary>
internal static class Bc3Decoder
{
    public static byte[] DecodeImage(ReadOnlySpan<byte> blocks, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int blocksWide = 1 + (width - 1) / 4;
        int blocksHigh = 1 + (height - 1) / 4;
        if (blocks.Length < (long)blocksWide * blocksHigh * 16)
            throw new ArgumentException($"BC3 data too short for {width}x{height}.", nameof(blocks));

        var rgba = new byte[checked(width * height * 4)];
        Span<byte> pixels = stackalloc byte[64];
        for (int y = 0; y < height; y += 4)
        {
            for (int x = 0; x < width; x += 4)
            {
                DecodeBlock(blocks[..16], pixels);
                blocks = blocks[16..];
                int rowBytes = Math.Min(width - x, 4) * 4;
                for (int row = 0; row < Math.Min(height - y, 4); row++)
                    pixels.Slice(row * 16, rowBytes).CopyTo(rgba.AsSpan(((y + row) * width + x) * 4));
            }
        }
        return rgba;
    }

    private static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> pixels)
    {
        Span<byte> alpha = stackalloc byte[8];
        alpha[0] = block[0];
        alpha[1] = block[1];
        int steps = alpha[0] > alpha[1] ? 7 : 5;
        alpha[6] = 0;
        alpha[7] = 255;
        for (int i = 1; i < steps; i++)
            alpha[i + 1] = (byte)(((steps - i) * alpha[0] + i * alpha[1]) / steps);

        Span<byte> rgb = stackalloc byte[12];
        ExpandRgb565(BinaryPrimitives.ReadUInt16LittleEndian(block[8..]), rgb);
        ExpandRgb565(BinaryPrimitives.ReadUInt16LittleEndian(block[10..]), rgb[3..]);
        for (int channel = 0; channel < 3; channel++)
        {
            int first = rgb[channel], second = rgb[3 + channel];
            rgb[6 + channel] = (byte)((2 * first + second) / 3);
            rgb[9 + channel] = (byte)((first + 2 * second) / 3);
        }

        ulong alphaIndices = BinaryPrimitives.ReadUInt64LittleEndian(block) >> 16;
        uint colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
        for (int pixel = 0; pixel < 16; pixel++)
        {
            rgb.Slice((int)(colorIndices & 3) * 3, 3).CopyTo(pixels[(pixel * 4)..]);
            pixels[pixel * 4 + 3] = alpha[(int)(alphaIndices & 7)];
            colorIndices >>= 2;
            alphaIndices >>= 3;
        }
    }

    private static void ExpandRgb565(ushort color, Span<byte> rgb)
    {
        int red = color >> 11, green = (color >> 5) & 63, blue = color & 31;
        rgb[0] = (byte)((red << 3) | (red >> 2));
        rgb[1] = (byte)((green << 2) | (green >> 4));
        rgb[2] = (byte)((blue << 3) | (blue >> 2));
    }
}
