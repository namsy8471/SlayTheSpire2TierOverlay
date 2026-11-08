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
