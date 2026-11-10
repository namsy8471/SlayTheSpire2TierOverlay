using System.Diagnostics;
using System.Runtime.InteropServices;
using static Sts2Overlay.App.Interop.D3D11Interop;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Reads a rectangle of a GPU texture into a managed BGRA buffer through CPU-readable STAGING textures, a band of rows at
/// a time (used by <see cref="WgcCapturer"/>).
///
/// Why bands: the CPU can only read a texture after the GPU copied it into a staging texture (system memory), and the
/// pixels still have to end up in the managed frame buffer the recogniser reads. One staging texture of the whole frame
/// keeps a second full copy of every frame alive between polls (32 MB at 4K) only to be read once. Two staging textures
/// of <see cref="BandRows"/> rows each hold an eighth of that, and the work overlaps: while the CPU copies band k out of
/// one texture, the GPU is already copying band k+1 into the other.
///
/// For each band: CopySubresourceRegion (queued) → Flush (send it to the GPU now, so it runs while the CPU works on the
/// previous band) → ... → Map (waits for that band's copy) → copy rows → Unmap → queue the band after next into the same
/// texture. The pixels are the same bytes a whole-frame copy gives; only the memory they pass through is smaller.
///
/// Not thread-safe: the immediate context is used by one thread (the capture worker). The device's CreateTexture2D is
/// free-threaded, so a frame pool recreating its buffers on a WGC thread at the same time is fine.
/// </summary>
internal sealed class StagingReadback : IDisposable
{
    /// <summary>Rows per band: 270 = 2160 / 8, a 4K frame in 8 bands of 4 MB (measured in MemProbe readback).</summary>
    public const int DefaultBandRows = 270;

    /// <summary>Staging textures used in turn: 2 lets the GPU fill one while the CPU reads the other.</summary>
    public const int DefaultTextures = 2;

    private readonly IntPtr _device;  // borrowed: the owner keeps the device and context alive longer than this object
    private readonly IntPtr _context;
    private readonly IntPtr[] _textures;
    private int _width;
    private int _rows;

    /// <param name="bandRows">Rows per staging texture; <c>int.MaxValue</c> = the whole rectangle in one texture (the old way).</param>
    /// <param name="textures">Staging textures used in turn (1 = no overlap).</param>
    public StagingReadback(IntPtr device, IntPtr context, int bandRows = DefaultBandRows, int textures = DefaultTextures)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bandRows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(textures, 1);
        _device = device;
        _context = context;
        BandRows = bandRows;
        _textures = new IntPtr[textures];
    }

    public int BandRows { get; }

    /// <summary>RowPitch of the last mapped band (bytes per row incl. driver padding); for diagnostics.</summary>
    public int LastRowPitch { get; private set; }

    /// <summary>Bytes of staging memory currently held (width x rows x 4 per texture created).</summary>
    public long StagingBytes => (long)_width * _rows * 4 * _textures.Count(t => t != IntPtr.Zero);

    /// <summary>
    /// Copies <paramref name="width"/> x <paramref name="height"/> pixels at (<paramref name="left"/>, <paramref name="top"/>)
    /// of <paramref name="source"/> into <paramref name="dst"/> (rows <paramref name="dstStride"/> bytes apart).
    /// <paramref name="waitMs"/>: time spent queuing copies and waiting in Map (GPU side), <paramref name="copyMs"/>: CPU row copies.
    /// Throws <see cref="COMException"/> on a failed Map or texture creation (device lost included).
    /// </summary>
    public void Read(IntPtr source, int left, int top, int width, int height, byte[] dst, int dstStride, out double waitMs, out double copyMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (dst.Length < (long)(height - 1) * dstStride + width * 4) throw new ArgumentException("destination too small", nameof(dst));
        int rows = Math.Min(BandRows, height);
        EnsureTextures(width, rows);
        int bands = (height + rows - 1) / rows;
        int n = Math.Min(_textures.Length, bands);
        long wait = 0, copy = 0;
        var t = Stopwatch.GetTimestamp();
        for (int b = 0; b < n; b++) Submit(b, source, left, top, width, height, rows, _textures[b]);
        if (bands > 1) Flush(_context);
        wait += Stopwatch.GetTimestamp() - t;
        for (int b = 0; b < bands; b++)
        {
            var texture = _textures[b % n];
            t = Stopwatch.GetTimestamp();
            var hr = Map(_context, texture, out var mapped); // waits until this band's copy is done
            if (hr < 0) throw new COMException($"Map failed (0x{hr:X8})", hr);
            var t1 = Stopwatch.GetTimestamp();
            wait += t1 - t;
            try
            {
                LastRowPitch = (int)mapped.RowPitch;
                int y = b * rows, count = Math.Min(rows, height - y);
                PixelCopy.CopyRows(mapped.Data, (int)mapped.RowPitch, dst.AsSpan(y * dstStride), dstStride, width * 4, count);
            }
            finally
            {
                Unmap(_context, texture);
            }
            var t2 = Stopwatch.GetTimestamp();
            copy += t2 - t1;
            if (b + n < bands)
            {
                Submit(b + n, source, left, top, width, height, rows, texture);
                Flush(_context);
                wait += Stopwatch.GetTimestamp() - t2;
            }
        }
        waitMs = wait * 1000.0 / Stopwatch.Frequency;
        copyMs = copy * 1000.0 / Stopwatch.Frequency;
    }

    private void Submit(int band, IntPtr source, int left, int top, int width, int height, int rows, IntPtr texture)
    {
        int y = band * rows, count = Math.Min(rows, height - y);
        var box = new D3D11_BOX
        {
            Left = (uint)left, Top = (uint)(top + y), Front = 0,
            Right = (uint)(left + width), Bottom = (uint)(top + y + count), Back = 1,
        };
        CopySubresourceRegion(_context, texture, source, box); // into the band texture's (0, 0)
    }

    private void EnsureTextures(int width, int rows)
    {
        if (width == _width && rows == _rows && _textures[0] != IntPtr.Zero) return;
        ReleaseTextures();
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)rows,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            Usage = D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
        };
        _width = width;
        _rows = rows;
        for (int i = 0; i < _textures.Length; i++)
        {
            var hr = CreateTexture2D(_device, desc, out _textures[i]);
            if (hr < 0) throw new COMException($"CreateTexture2D failed (0x{hr:X8})", hr);
        }
    }

    private void ReleaseTextures()
    {
        for (int i = 0; i < _textures.Length; i++) Interop.D3D11Interop.Release(ref _textures[i]); // the COM release, not this.Release()
        _width = _rows = 0;
    }

    /// <summary>
    /// Releases the staging textures and flushes, so D3D11 frees their memory now: it destroys released objects only at
    /// the next flush, and a suspended capturer may not touch the context again for hours (measured: the 32 MB staging
    /// texture of a 4K frame stayed allocated through Suspend, and a second one was added on resume). The next
    /// <see cref="Read"/> creates them again.
    /// </summary>
    public void Release()
    {
        if (!_textures.Any(t => t != IntPtr.Zero)) return;
        ReleaseTextures();
        Flush(_context);
    }

    public void Dispose() => Release();
}
