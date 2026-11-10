using SkiaSharp;

namespace Sts2Overlay.Vision;

/// <summary>Decodes image files (PNG captures, WebP card art) into <see cref="ScreenFrame"/>s with SkiaSharp.</summary>
public static class ImageLoader
{
    public static ScreenFrame Load(string path)
    {
        using SKCodec codec = SKCodec.Create(path) ?? throw new InvalidDataException($"Cannot decode image: {path}");
        return Decode(codec, path);
    }

    public static ScreenFrame Load(Stream stream)
    {
        using SKCodec codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Cannot decode image stream.");
        return Decode(codec, "<stream>");
    }

    /// <summary>
    /// Decodes straight into <paramref name="reuse"/> (replaced by a new array when the image does not fit), so a loop over
    /// many images (the art database build: 1000x760 portraits, 3 MB each) reuses one managed buffer per worker instead
    /// of allocating a native bitmap plus a large-object-heap copy for every file. The frame covers exactly the image's
    /// bytes, which are the ones <see cref="Load(string)"/> returns: same codec call, same BGRA/unpremul format, rows packed.
    /// </summary>
    internal static ScreenFrame Load(string path, [System.Diagnostics.CodeAnalysis.NotNull] ref byte[]? reuse)
    {
        using SKCodec codec = SKCodec.Create(path) ?? throw new InvalidDataException($"Cannot decode image: {path}");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        if (reuse is null || reuse.Length < info.BytesSize) reuse = new byte[info.BytesSize];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(reuse, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            SKCodecResult result = codec.GetPixels(info, pin.AddrOfPinnedObject());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException($"Cannot decode {path}: {result}");
        }
        finally
        {
            pin.Free();
        }
        return new ScreenFrame(reuse.AsMemory(0, info.BytesSize), info.Width, info.Height, info.RowBytes);
    }

    private static ScreenFrame Decode(SKCodec codec, string name)
    {
        // Ask the codec for BGRA directly so no per-pixel conversion is needed afterwards.
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException($"Cannot decode {name}: {result}");
        byte[] bytes = bitmap.GetPixelSpan().ToArray();
        return new ScreenFrame(bytes, info.Width, info.Height, bitmap.RowBytes);
    }

    /// <summary>Writes a frame as PNG (debug crops, synthetic test images).</summary>
    public static void SavePng(in ScreenFrame frame, string path)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        ReadOnlySpan<byte> src = frame.Bgra.Span;
        Span<byte> dst = bitmap.GetPixelSpan();
        for (int y = 0; y < frame.Height; y++)
            src.Slice(y * frame.Stride, frame.Width * 4).CopyTo(dst.Slice(y * bitmap.RowBytes, frame.Width * 4));
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using FileStream fs = File.Create(path);
        data.SaveTo(fs);
    }
}
