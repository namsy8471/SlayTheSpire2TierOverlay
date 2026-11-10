using System.IO;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace MemProbe;

/// <summary>A saved capture and its pixel size (read from the PNG header, no decode).</summary>
internal sealed record CaptureFile(string Path, int Width, int Height);

internal static class Captures
{
    /// <summary>Every PNG under <paramref name="dir"/> with its size, sorted by name (= capture time).</summary>
    public static List<CaptureFile> List(string dir)
    {
        var list = new List<CaptureFile>();
        Span<byte> head = stackalloc byte[24];
        foreach (var f in Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using (var fs = File.OpenRead(f))
            {
                if (fs.Read(head) < 24) continue;
            }
            // PNG: 8-byte signature, IHDR length + type, then width and height (big endian).
            list.Add(new CaptureFile(f, BinaryPrimitives.ReadInt32BigEndian(head[16..]), BinaryPrimitives.ReadInt32BigEndian(head[20..])));
        }
        return list;
    }

    /// <summary><paramref name="n"/> files evenly spread over <paramref name="files"/> (all when n is larger).</summary>
    public static List<CaptureFile> Spread(IReadOnlyList<CaptureFile> files, int n)
    {
        if (n >= files.Count) return [.. files];
        var pick = new List<CaptureFile>(n);
        for (int i = 0; i < n; i++) pick.Add(files[(int)((long)i * files.Count / n)]);
        return pick;
    }
}

/// <summary>
/// Decodes captures into ONE reused BGRA buffer, the way the WGC capturer reuses <c>_buffer</c>: a new array only when the
/// frame size changes. The decoder's own scratch memory is native (Skia/libpng) and freed per frame.
/// </summary>
internal sealed class ReusedFrameBuffer
{
    public byte[] Buffer { get; private set; } = [];
    public int Reallocations { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    private byte[] _small = [];

    /// <summary>
    /// Decodes, then (k &gt; 1) blows every pixel up to a k x k block, like tools/VisionBench --upscale: almost every
    /// saved capture is 1920x1079, the user plays at 4K, and the recogniser takes another path there (2x2 cells).
    /// </summary>
    public void Decode(CaptureFile file, int k)
    {
        if (k <= 1)
        {
            Decode(file);
            return;
        }
        (byte[] keep, Buffer) = (Buffer, _small);
        Decode(file);
        _small = Buffer;
        Buffer = keep;
        // The saved captures are 1920x1079 (a windowed client area); a real 4K capture is 3840x2160, and 2158 rows would
        // put the recogniser on its 1x1-cell path (scale 1.998 floors to step 1). So the last row is repeated once.
        int srcH = file.Height == 1079 ? 1080 : file.Height;
        int w = file.Width * k, h = srcH * k;
        if (Buffer.Length != w * h * 4)
        {
            Buffer = new byte[w * h * 4];
            Reallocations++;
        }
        byte[] src = _small, dst = Buffer;
        for (int y = 0; y < h; y++)
        {
            int srow = Math.Min(y / k, file.Height - 1) * file.Width * 4, drow = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int s = srow + (x / k) * 4, d = drow + x * 4;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = 255;
            }
        }
        Width = w;
        Height = h;
    }

    public void Decode(CaptureFile file)
    {
        using var codec = SKCodec.Create(file.Path) ?? throw new InvalidDataException($"cannot decode {file.Path}");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        int bytes = info.Width * info.Height * 4;
        if (Buffer.Length != bytes)
        {
            Buffer = new byte[bytes];
            Reallocations++;
        }
        var handle = GCHandle.Alloc(Buffer, GCHandleType.Pinned);
        try
        {
            var r = codec.GetPixels(info, handle.AddrOfPinnedObject()); // rows packed: info.RowBytes = width * 4
            if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) throw new InvalidDataException($"cannot decode {file.Path}: {r}");
        }
        finally
        {
            handle.Free();
        }
        Width = info.Width;
        Height = info.Height;
    }
}
