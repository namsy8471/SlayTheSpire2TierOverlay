using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Sts2Overlay.App.Integration;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Services;

/// <summary>
/// GDI screen capture into a reused BGRA buffer. Same idea as the OverlaySpike's <c>CopyFromScreen</c> (verified not
/// black over the fullscreen game, because Godot's fullscreen is a DWM-composed borderless window), minus the
/// per-frame Bitmap allocations.
///
/// How it works:
/// 1. <c>GetDC(NULL)</c> is a device context for the whole (composed) desktop.
/// 2. A DIB section is a bitmap whose pixels live in memory we can read directly. We ask for 32 bpp, top-down
///    (negative height), so the memory layout is exactly BGRA rows starting at the top.
/// 3. <c>BitBlt</c> copies the game rectangle from the desktop DC into the DIB, then one <c>Marshal.Copy</c> moves the
///    pixels into a managed array that is reused while the size stays the same.
/// Everything drawn over the game is in the frame, our overlay included, so before a GDI frame
/// <see cref="OverlayCaptureVisibility"/> hides our windows from capture (WDA_EXCLUDEFROMCAPTURE).
///
/// Why this is slow at 4K (~50 ms): the game is drawn by the GPU and composed by DWM in VRAM. A desktop-DC BitBlt makes
/// Windows read the composed desktop back from the GPU into system memory through GDI, synchronously, every call.
/// <see cref="WgcCapturer"/> asks DWM for the window's own GPU texture instead; this class is the fallback.
///
/// Not thread-safe: used only by the capture worker thread.
/// </summary>
internal sealed class ScreenCapturer : IFrameCapturer
{
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private int _width;
    private int _height;
    private byte[] _buffer = [];

    public string Name => "gdi";

    public CaptureTimings LastTimings { get; private set; }

    public CapturedFrame Capture(GameWindowInfo target) => Capture(target.CaptureRect);

    /// <summary>Always: BitBlt copies the composed desktop.</summary>
    public bool ReadsDesktop(GameWindowInfo target) => true;

    public CapturedFrame Capture(PixelRect rect)
    {
        if (rect.IsEmpty) throw new ArgumentException("empty capture rect", nameof(rect));
        var t0 = Stopwatch.GetTimestamp();
        EnsureSurface(rect.Width, rect.Height);

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new Win32Exception("GetDC failed");
        try
        {
            if (!BitBlt(_memDc, 0, 0, rect.Width, rect.Height, screenDc, rect.X, rect.Y, SRCCOPY))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt failed");
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        var acquireMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var t1 = Stopwatch.GetTimestamp();
        var stride = rect.Width * 4;
        Marshal.Copy(_bits, _buffer, 0, stride * rect.Height);
        LastTimings = new CaptureTimings(acquireMs, Stopwatch.GetElapsedTime(t1).TotalMilliseconds);
        return new CapturedFrame(_buffer, rect.Width, rect.Height, stride, rect, DateTime.Now);
    }

    private void EnsureSurface(int width, int height)
    {
        if (_dib != IntPtr.Zero && width == _width && height == _height) return;
        ReleaseSurface();

        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            _memDc = CreateCompatibleDC(screenDc);
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // negative = top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };
            _dib = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero) throw new Win32Exception("CreateDIBSection failed");
            _oldBitmap = SelectObject(_memDc, _dib);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        _width = width;
        _height = height;
        _buffer = new byte[width * height * 4];
    }

    private void ReleaseSurface()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) SelectObject(_memDc, _oldBitmap);
            DeleteDC(_memDc);
        }
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        _memDc = _dib = _oldBitmap = _bits = IntPtr.Zero;
        _width = _height = 0;
    }

    /// <summary>Nothing to stop: GDI keeps no session. The DIB is kept for the next capture.</summary>
    public void Suspend()
    {
    }

    /// <summary>DIB section and managed buffer released (2 x 33 MB at 4K); the next capture makes new ones.</summary>
    public void Trim()
    {
        ReleaseSurface();
        _buffer = [];
    }

    public void Dispose() => ReleaseSurface();
}
