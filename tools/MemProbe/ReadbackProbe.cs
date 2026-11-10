using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Sts2Overlay.App.Services;
using static Sts2Overlay.App.Interop.D3D11Interop;

namespace MemProbe;

/// <summary>
///   MemProbe readback [--rounds 40] [--width 3840] [--height 2160]
///
/// The app's <see cref="StagingReadback"/> in several band layouts, interleaved round by round, reading the same GPU
/// texture (default usage, like a WGC frame pool buffer) into a managed buffer: time per read and staging memory held.
/// Every read is compared with the texture's pixels. "whole" (one staging texture of the full frame) is the readback
/// WgcCapturer used before bands. An odd-width texture checks padded row pitches.
/// </summary>
internal static unsafe class ReadbackProbe
{
    public static int Run(Dictionary<string, string> opts)
    {
        int rounds = int.Parse(opts.GetValueOrDefault("rounds") ?? "40", CultureInfo.InvariantCulture);
        int width = int.Parse(opts.GetValueOrDefault("width") ?? "3840", CultureInfo.InvariantCulture);
        int height = int.Parse(opts.GetValueOrDefault("height") ?? "2160", CultureInfo.InvariantCulture);
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION, out var device, out _, out var context));
        try
        {
            int bad = Odd(device, context);
            var src = CaptureProbe.Pattern(width, height, 0).Bgra.ToArray();
            var texture = SourceTexture(device, width, height, src);
            var configs = new (string Name, int Rows, int Textures)[]
            {
                ("whole", int.MaxValue, 1), ("540x2", 540, 2), ("270x2", 270, 2), ("135x2", 135, 2), ("270x1", 270, 1), ("270x3", 270, 3),
            };
            var readers = configs.Select(c => new StagingReadback(device, context, c.Rows, c.Textures)).ToArray();
            var total = configs.Select(_ => new List<double>()).ToArray();
            var wait = configs.Select(_ => new List<double>()).ToArray();
            var copy = configs.Select(_ => new List<double>()).ToArray();
            var dst = new byte[width * height * 4];
            var progress = new Progress("readback rounds", rounds);
            for (int r = -1; r < rounds; r++) // round -1: warm-up (texture creation, first map), not counted
            {
                for (int k = 0; k < configs.Length; k++)
                {
                    int i = (k + Math.Max(0, r)) % configs.Length; // rotate the order every round
                    Array.Clear(dst);
                    var t0 = Stopwatch.GetTimestamp();
                    readers[i].Read(texture, 0, 0, width, height, dst, width * 4, out var w, out var c);
                    var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    if (!dst.AsSpan().SequenceEqual(src))
                    {
                        bad++;
                        Console.WriteLine($"   {configs[i].Name}: PIXELS DIFFER");
                    }
                    if (r < 0) continue;
                    total[i].Add(ms);
                    wait[i].Add(w);
                    copy[i].Add(c);
                }
                if (r >= 0) progress.Step(r + 1);
            }
            Console.WriteLine($"readback of {width}x{height} into a managed buffer, {rounds} interleaved rounds [measured under full CPU load]:");
            for (int i = 0; i < configs.Length; i++)
            {
                Console.WriteLine(CaptureProbe.Inv($"   {configs[i].Name,-6} staging {readers[i].StagingBytes / 1048576.0,5:F1} MB: median {CaptureProbe.Median(total[i]),6:F2} ms (GPU wait {CaptureProbe.Median(wait[i]):F2}, CPU copy {CaptureProbe.Median(copy[i]):F2}), p90 {CaptureProbe.P90(total[i]),6:F2} ms"));
            }
            foreach (var reader in readers) reader.Dispose();
            Release(ref texture);
            Console.WriteLine(bad == 0 ? "pixels: every read identical to the texture" : $"PIXELS DIFFER in {bad} reads");
            return bad == 0 ? 0 : 1;
        }
        finally
        {
            Release(ref context);
            Release(ref device);
        }
    }

    /// <summary>1001x601 (4004 bytes per row: padded pitch) at an offset inside a larger texture, every layout.</summary>
    private static int Odd(IntPtr device, IntPtr context)
    {
        const int w = 1280, h = 720, cw = 1001, ch = 601, cx = 123, cy = 57;
        var src = CaptureProbe.Pattern(w, h, 1).Bgra.ToArray();
        var texture = SourceTexture(device, w, h, src);
        int bad = 0;
        foreach (var (rows, n) in new[] { (int.MaxValue, 1), (270, 2), (64, 3), (600, 2), (601, 2), (1, 2) })
        {
            using var reader = new StagingReadback(device, context, rows, n);
            var dst = new byte[cw * ch * 4];
            reader.Read(texture, cx, cy, cw, ch, dst, cw * 4, out _, out _);
            int diff = 0;
            for (int y = 0; y < ch; y++)
                if (!dst.AsSpan(y * cw * 4, cw * 4).SequenceEqual(src.AsSpan(((cy + y) * w + cx) * 4, cw * 4))) diff++;
            Console.WriteLine($"   odd crop {cw}x{ch} at ({cx},{cy}), bands {(rows == int.MaxValue ? "whole" : rows.ToString(CultureInfo.InvariantCulture))} x{n}: row pitch {reader.LastRowPitch}, {diff} rows differ");
            if (diff != 0) bad++;
        }
        Release(ref texture);
        return bad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_SUBRESOURCE_DATA
    {
        public IntPtr SysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    /// <summary>A default-usage BGRA texture (shader resource + render target, like WGC's) holding <paramref name="pixels"/>.</summary>
    private static IntPtr SourceTexture(IntPtr device, int width, int height, byte[] pixels)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
            Usage = 0, BindFlags = 0x8 | 0x20,
        };
        fixed (byte* p = pixels)
        {
            var init = new D3D11_SUBRESOURCE_DATA { SysMem = (IntPtr)p, SysMemPitch = (uint)(width * 4) };
            IntPtr t;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Slot(device, 5))(device, &desc, &init, &t);
            Marshal.ThrowExceptionForHR(hr);
            return t;
        }
    }
}
