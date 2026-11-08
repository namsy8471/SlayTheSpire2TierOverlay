namespace Sts2Overlay.Vision;

/// <summary>
/// One captured frame of the game window, independent of how it was captured
/// (GDI CopyFromScreen / PrintWindow in the app, a PNG file in the bench and tests).
/// Pixels are 32-bit BGRA (the native layout of Windows DIBs and of SkiaSharp's N32 on Windows), top-down rows.
/// </summary>
/// <param name="Bgra">Pixel bytes. Row <c>y</c> starts at <c>y * Stride</c>.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Stride">Bytes per row (at least <c>Width * 4</c>; GDI rows may be padded).</param>
public readonly record struct ScreenFrame(ReadOnlyMemory<byte> Bgra, int Width, int Height, int Stride)
{
    /// <summary>Wraps a tightly packed BGRA buffer.</summary>
    public static ScreenFrame FromPacked(byte[] bgra, int width, int height) => new(bgra, width, height, width * 4);

    /// <summary>Throws when the buffer is too small for the declared size (cheap sanity check before reading pixels).</summary>
    public void Validate()
    {
        if (Width <= 0 || Height <= 0) throw new ArgumentException($"Empty frame {Width}x{Height}.");
        if (Stride < Width * 4) throw new ArgumentException($"Stride {Stride} < width*4 ({Width * 4}).");
        long needed = (long)Stride * (Height - 1) + Width * 4L;
        if (Bgra.Length < needed) throw new ArgumentException($"Buffer has {Bgra.Length} bytes, frame needs {needed}.");
    }
}
