using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services.Perf;
using static Sts2Overlay.App.Interop.D3D11Interop;
using static Sts2Overlay.App.Interop.NativeMethods;
using static Sts2Overlay.App.Interop.WgcInterop;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Windows.Graphics.Capture (WGC) backend: asks DWM for the game window's own surface instead of reading the composed
/// desktop through GDI.
///
/// Data flow for one <see cref="Capture"/>:
/// 1. A capture session (one per target window, kept alive between captures) makes DWM copy every new frame of the
///    window into a small pool of GPU textures. <see cref="OnFrameArrived"/> keeps only the newest one.
/// 2. Capture takes that frame and asks the GPU to copy the game rectangle into a STAGING texture. A staging texture
///    lives in memory the CPU can read; normal textures live in VRAM, which the CPU cannot address.
/// 3. <c>Map</c> waits for that GPU copy to finish and returns a pointer + <c>RowPitch</c> (bytes per row, maybe padded).
/// 4. <see cref="PixelCopy"/> copies row by row into the reused managed buffer of <see cref="CapturedFrame"/>.
///
/// The window does not have to be in front: WGC sees the window's content even when something covers it.
/// WGC only delivers frames when the window's content changes. If no new frame arrived since the last capture the
/// previous buffer is still exactly right, so it is returned again (<see cref="LastFrameReused"/>).
///
/// Threading: Capture/Suspend/Dispose run on the capture worker; FrameArrived runs on a WGC thread
/// ("free-threaded" frame pool, no dispatcher needed). They share only <see cref="_latest"/> and <see cref="_pool"/>
/// (under a lock) and the perf frame counter (interlocked, see <see cref="FrameRateCounter"/>).
///
/// WinRT objects are raw COM pointers (Interop/WgcInterop.cs), each holding one reference that the teardown paths
/// (StopSession, Reset, the frame's try/finally) close and release deterministically: this runs for hours.
/// </summary>
internal sealed class WgcCapturer : IFrameCapturer
{
    private const int PixelFormat = PixelFormatB8G8R8A8UIntNormalized;
    private const int PoolBuffers = 2;
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(1);

    /// <summary>After a resize, how long to wait for a frame that matches the new window size.</summary>
    private static readonly TimeSpan ResizeTimeout = TimeSpan.FromMilliseconds(100);

    // Device (kept until a failure or Dispose).
    private IntPtr _d3dDevice;
    private IntPtr _d3dContext;
    private IntPtr _device; // WinRT IDirect3DDevice wrapping _d3dDevice; FrameArrived reads it only under _frameLock

    // Session (kept while periodic capture runs; recreated for a new window or after the window closed).
    private IntPtr _targetHandle;
    private IntPtr _item;           // IGraphicsCaptureItem
    private long _itemClosedToken;  // its Closed registration (0 = none)
    private IntPtr _pool;           // IDirect3D11CaptureFramePool; written under _frameLock
    private long _frameArrivedToken;
    private IntPtr _session;        // IGraphicsCaptureSession
    private SizeInt32 _poolSize; // touched by FrameArrived only once the session runs
    private volatile bool _itemClosed;

    private readonly Lock _frameLock = new();
    private readonly AutoResetEvent _frameArrived = new(false);
    private IntPtr _latest; // IDirect3D11CaptureFrame, owned; zero = none

    // CPU side.
    private IntPtr _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private byte[] _buffer = [];
    private PixelRect _bufferRect; // what _buffer currently holds (empty = nothing valid)

    private readonly FrameRateCounter? _gameFrames;

    /// <param name="gameFrames">Told about every frame that arrives (the game's frame rate in the perf log); null = don't.</param>
    public WgcCapturer(FrameRateCounter? gameFrames = null) => _gameFrames = gameFrames;

    public string Name => "wgc";

    public CaptureTimings LastTimings { get; private set; }

    /// <summary>The last Capture returned the previous pixels because the window had not produced a new frame.</summary>
    public bool LastFrameReused { get; private set; }

    /// <summary>RowPitch of the last mapped staging texture (bytes per row incl. driver padding); for diagnostics.</summary>
    public int LastRowPitch { get; private set; }

    /// <summary>WGC with the features we use (cursor exclusion needs Windows 10 2004).</summary>
    public static bool IsSupported()
    {
        try
        {
            return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && Interop.WgcInterop.IsSupported();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Warn($"wgc: support check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Window capture gets the game window's own surface: windows drawn over it (our overlay) are never in it. Only
    /// monitor capture (no game window, demo mode) reads the composed desktop.
    /// </summary>
    public bool ReadsDesktop(GameWindowInfo target) => target.Hwnd == IntPtr.Zero;

    public CapturedFrame Capture(GameWindowInfo target)
    {
        var rect = target.CaptureRect;
        if (rect.IsEmpty) throw new ArgumentException("empty capture rect", nameof(target));
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            EnsureSession(target);
            var canReuse = !_bufferRect.IsEmpty && _bufferRect == rect;
            var frame = TakeLatestFrame(canReuse ? TimeSpan.Zero : FirstFrameTimeout);
            if (frame == IntPtr.Zero)
            {
                if (!canReuse) throw new TimeoutException($"no frame from Windows.Graphics.Capture within {FirstFrameTimeout.TotalMilliseconds:0} ms");
                LastFrameReused = true;
                LastTimings = new CaptureTimings(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 0);
                return new CapturedFrame(_buffer, rect.Width, rect.Height, rect.Width * 4, rect, DateTime.Now);
            }

            try
            {
                try
                {
                    return CopyFrame(frame, target, rect, t0);
                }
                finally
                {
                    CloseAndRelease(ref frame); // gives the buffer back to the pool (was: using (frame))
                }
            }
            catch (FrameSizeMismatchException ex)
            {
                // The window was just resized: the frame (or its pool buffer) still has the old size. The FrameArrived
                // handler has already recreated the pool, so the next frame fits - but WGC only sends one when the
                // content changes. A game redraws constantly; a static window may never, so restart the session
                // (a new session's first frame is always the current content).
                var next = TakeLatestFrame(ResizeTimeout);
                if (next == IntPtr.Zero)
                {
                    AppLog.Info($"wgc: {ex.Message}; restarting the session for the new size");
                    StopSession();
                    EnsureSession(target);
                    next = TakeLatestFrame(FirstFrameTimeout);
                    if (next == IntPtr.Zero) throw new FrameSizeMismatchException("no frame of the new window size");
                }
                try
                {
                    return CopyFrame(next, target, rect, t0);
                }
                finally
                {
                    CloseAndRelease(ref next);
                }
            }
        }
        catch (FrameSizeMismatchException)
        {
            throw; // transient: keep device and session
        }
        catch
        {
            // Whatever went wrong (device removed, window gone, driver hiccup), start from scratch next time.
            Reset();
            throw;
        }
    }

    private CapturedFrame CopyFrame(IntPtr frame, GameWindowInfo target, PixelRect rect, long t0)
    {
        // Where the frame's (0, 0) is on screen: window capture covers the window's visible (DWM) bounds.
        var origin = FrameOrigin(target);
        var content = GetFrameContentSize(frame);
        if (content.Width != origin.Width || content.Height != origin.Height)
        {
            throw new FrameSizeMismatchException($"frame content {content.Width}x{content.Height} != window bounds {origin}");
        }
        var src = rect.Offset(-origin.X, -origin.Y);
        if (src.X < 0 || src.Y < 0 || src.Right > content.Width || src.Bottom > content.Height)
        {
            throw new FrameSizeMismatchException($"capture rect {rect} outside captured window {origin}");
        }

        var texture = SurfaceTexture(frame);
        try
        {
            var desc = GetDesc(texture);
            if (src.Right > desc.Width || src.Bottom > desc.Height)
            {
                // Pool buffers are still the old size right after a resize; the next frame will fit.
                throw new FrameSizeMismatchException($"frame texture {desc.Width}x{desc.Height} smaller than capture region {src}");
            }
            EnsureStaging(rect.Width, rect.Height);
            var box = new D3D11_BOX
            {
                Left = (uint)src.X, Top = (uint)src.Y, Front = 0,
                Right = (uint)src.Right, Bottom = (uint)src.Bottom, Back = 1,
            };
            CopySubresourceRegion(_d3dContext, _staging, texture, box); // queued on the GPU, returns at once
        }
        finally
        {
            Marshal.Release(texture);
        }

        var hr = Map(_d3dContext, _staging, out var mapped); // blocks until the GPU copy is done
        if (hr < 0) ThrowDeviceError(hr, "Map");
        var acquireMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var t1 = Stopwatch.GetTimestamp();
        var stride = rect.Width * 4;
        try
        {
            if (_buffer.Length != stride * rect.Height) _buffer = new byte[stride * rect.Height];
            LastRowPitch = (int)mapped.RowPitch;
            PixelCopy.CopyRows(mapped.Data, (int)mapped.RowPitch, _buffer, stride, stride, rect.Height);
        }
        finally
        {
            Unmap(_d3dContext, _staging);
        }
        _bufferRect = rect;
        LastFrameReused = false;
        LastTimings = new CaptureTimings(acquireMs, Stopwatch.GetElapsedTime(t1).TotalMilliseconds);
        return new CapturedFrame(_buffer, rect.Width, rect.Height, stride, rect, DateTime.Now);
    }

    private static PixelRect FrameOrigin(GameWindowInfo target)
    {
        if (target.Hwnd == IntPtr.Zero) return MonitorBounds(target.CaptureRect);
        if (DwmGetWindowAttribute(target.Hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
        {
            GetWindowRect(target.Hwnd, out r);
        }
        return PixelRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static PixelRect MonitorBounds(PixelRect rect)
    {
        var mon = MonitorForRect(rect);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(mon, ref mi) ? PixelRect.FromLtrb(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom) : rect;
    }

    private static IntPtr MonitorForRect(PixelRect rect)
        => MonitorFromPoint(new POINT { X = rect.X + rect.Width / 2, Y = rect.Y + rect.Height / 2 }, MONITOR_DEFAULTTONEAREST);

    // ---- session ----------------------------------------------------------------------------------------

    private void EnsureSession(GameWindowInfo target)
    {
        // Demo mode has no game window: capture the monitor instead.
        var isMonitor = target.Hwnd == IntPtr.Zero;
        var handle = isMonitor ? MonitorForRect(target.CaptureRect) : target.Hwnd;
        if (_session != IntPtr.Zero && !_itemClosed && handle == _targetHandle) return;

        if (_itemClosed) AppLog.Info("wgc: capture item closed (window gone); restarting session");
        StopSession();
        EnsureDevice();

        // Each object goes into its field as soon as it exists, so a failure further down leaves nothing that
        // StopSession (via Reset) would not release.
        var item = CreateItem(isMonitor, handle);
        _item = item;
        _itemClosedToken = AddItemClosed(item, sender =>
        {
            // The callback is bound to this item; the sender is the same object.
            if (_item == item) _itemClosed = true;
        });
        _itemClosed = false;
        _targetHandle = handle;
        _poolSize = GetItemSize(item);
        var pool = CreateFreeThreadedFramePool(_device, PixelFormat, PoolBuffers, _poolSize);
        try
        {
            _frameArrivedToken = AddFrameArrived(pool, sender => OnFrameArrived(sender, pool));
        }
        catch
        {
            ReleaseQuietly(ref pool);
            throw;
        }
        lock (_frameLock) _pool = pool;
        _session = CreateCaptureSession(pool, item);
        SetCursorCaptureEnabled(_session, false);
        var border = "required";
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            try
            {
                SetBorderRequired(_session, false); // no yellow frame around the game
                border = "off";
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or NotSupportedException or InvalidCastException)
            {
                // InvalidCastException: the session has no IGraphicsCaptureSession3 (QueryInterface failed).
                border = "required (" + ex.Message + ")";
            }
        }
        StartCapture(_session);
        AppLog.Info($"wgc: session started for {(isMonitor ? "monitor" : "window")} 0x{handle:X} ({_poolSize.Width}x{_poolSize.Height}), cursor off, border {border}");
    }

    /// <summary>A GraphicsCaptureItem (IGraphicsCaptureItem, caller releases) for a window or a monitor.</summary>
    private static IntPtr CreateItem(bool monitor, IntPtr handle)
    {
        var interop = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", IID_IGraphicsCaptureItemInterop);
        try
        {
            Marshal.ThrowExceptionForHR(CreateCaptureItem(interop, monitor, handle, out var item));
            return item;
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    /// <param name="sender">The pool raising the event (valid only during this call).</param>
    /// <param name="pool">The pool this handler was registered on: frames from any pool but the current one are dropped.</param>
    private void OnFrameArrived(IntPtr sender, IntPtr pool)
    {
        try
        {
            var frame = TryGetNextFrame(sender);
            if (frame == IntPtr.Zero) return;
            try
            {
                var content = GetFrameContentSize(frame);
                if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                {
                    // Window resized: new buffers of the new size for the following frames.
                    _poolSize = content;
                    RecreatePool(sender, pool);
                }
                IntPtr old;
                lock (_frameLock)
                {
                    if (_pool != pool) return; // a pool we are shutting down: the finally drops the frame
                    old = _latest;
                    _latest = frame;
                    frame = IntPtr.Zero; // _latest owns it now
                }
                _gameFrames?.Arrived(Stopwatch.GetTimestamp()); // a few interlocked operations; this runs at the game's frame rate
                // Return the older frame's buffer to the pool right away, so DWM always has one free to write into.
                CloseAndRelease(ref old);
                _frameArrived.Set();
            }
            finally
            {
                CloseAndRelease(ref frame);
            }
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
        {
            // The pool is being torn down on the worker thread.
        }
    }

    /// <summary>
    /// Pool buffers of the new <see cref="_poolSize"/>. The device is borrowed under the lock: once StopSession has taken
    /// the pool away, Reset may release the device on the worker at any moment.
    /// </summary>
    private void RecreatePool(IntPtr sender, IntPtr pool)
    {
        IntPtr device;
        lock (_frameLock)
        {
            if (_pool != pool) return;
            device = _device;
            Marshal.AddRef(device);
        }
        try
        {
            Recreate(sender, device, PixelFormat, PoolBuffers, _poolSize);
        }
        finally
        {
            Marshal.Release(device);
        }
    }

    /// <summary>The newest frame (caller closes and releases it), or zero when none arrived within <paramref name="wait"/>.</summary>
    private IntPtr TakeLatestFrame(TimeSpan wait)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            lock (_frameLock)
            {
                if (_latest != IntPtr.Zero)
                {
                    var f = _latest;
                    _latest = IntPtr.Zero;
                    return f;
                }
            }
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0 || _itemClosed) return IntPtr.Zero;
            _frameArrived.WaitOne(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
        }
    }

    private void StopSession()
    {
        IntPtr latest;
        IntPtr pool;
        lock (_frameLock)
        {
            latest = _latest;
            _latest = IntPtr.Zero;
            pool = _pool;
            _pool = IntPtr.Zero;
        }
        var session = _session;
        var item = _item;
        var frameArrivedToken = _frameArrivedToken;
        var itemClosedToken = _itemClosedToken;
        _session = IntPtr.Zero;
        _item = IntPtr.Zero;
        _frameArrivedToken = _itemClosedToken = 0;
        _targetHandle = IntPtr.Zero;
        _bufferRect = default;

        // Everything is released even when a step fails; the first failure is rethrown at the end (as the projection's
        // Dispose would have thrown it), so the caller still sees it.
        Exception? error = null;
        void Step(Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                error ??= ex; // deferred, not swallowed: rethrown below once everything is released
            }
        }
        Step(() => CloseAndRelease(ref latest));
        Step(() => CloseAndRelease(ref session)); // stops the capture
        if (pool != IntPtr.Zero)
        {
            if (frameArrivedToken != 0) Step(() => Marshal.ThrowExceptionForHR(RemoveFrameArrived(pool, frameArrivedToken)));
            Step(() => CloseAndRelease(ref pool));
        }
        if (item != IntPtr.Zero)
        {
            // The projection never unsubscribed Closed (it dropped the item to the GC); a failure here changes nothing.
            if (itemClosedToken != 0) RemoveItemClosed(item, itemClosedToken);
            Release(ref item);
        }
        _gameFrames?.Break(); // the time without a session is no gap between game frames
        if (error is not null) ExceptionDispatchInfo.Throw(error);
    }

    /// <summary>Close + release on an error path that is already throwing something more useful.</summary>
    private static void ReleaseQuietly(ref IntPtr obj)
    {
        try
        {
            CloseAndRelease(ref obj);
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
        {
            // The reference is gone either way.
        }
    }

    // ---- device and staging texture ---------------------------------------------------------------------

    private void EnsureDevice()
    {
        if (_device != IntPtr.Zero) return;
        var hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION, out _d3dDevice, out _, out _d3dContext);
        if (hr < 0)
        {
            AppLog.Warn($"wgc: hardware D3D11 device failed (0x{hr:X8}), trying WARP");
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out _d3dDevice, out _, out _d3dContext));
        }

        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_d3dDevice, in IID_IDXGIDevice, out var dxgi));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
            try
            {
                // The frame pool takes an IDirect3DDevice*, not the IInspectable* we got.
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in IID_IDirect3DDevice, out _device));
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    private void EnsureStaging(int width, int height)
    {
        if (_staging != IntPtr.Zero && width == _stagingWidth && height == _stagingHeight) return;
        Release(ref _staging);
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            Usage = D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
        };
        var hr = CreateTexture2D(_d3dDevice, desc, out _staging);
        if (hr < 0) ThrowDeviceError(hr, "CreateTexture2D");
        _stagingWidth = width;
        _stagingHeight = height;
    }

    /// <summary>The ID3D11Texture2D behind a WGC frame's surface (caller releases it).</summary>
    private static IntPtr SurfaceTexture(IntPtr frame)
    {
        var surface = GetFrameSurface(frame);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in IID_IDirect3DDxgiInterfaceAccess, out var access));
            try
            {
                Marshal.ThrowExceptionForHR(GetDxgiInterface(access, IID_ID3D11Texture2D, out var texture));
                return texture;
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(surface);
        }
    }

    private void ThrowDeviceError(int hr, string call)
    {
        if (hr is DXGI_ERROR_DEVICE_REMOVED or DXGI_ERROR_DEVICE_RESET && _d3dDevice != IntPtr.Zero)
        {
            throw new COMException($"{call}: D3D11 device lost (reason 0x{GetDeviceRemovedReason(_d3dDevice):X8})", hr);
        }
        throw new COMException($"{call} failed (0x{hr:X8})", hr);
    }

    // ---- teardown ---------------------------------------------------------------------------------------

    public void Suspend()
    {
        // _item also counts: a session that failed half-way has an item (and maybe a pool) but no _session yet.
        if (_session == IntPtr.Zero && _item == IntPtr.Zero && _staging == IntPtr.Zero) return;
        StopSession();
        Release(ref _staging);
        _stagingWidth = _stagingHeight = 0;
    }

    /// <summary>Drops session AND device (device lost, unknown failure).</summary>
    private void Reset()
    {
        try
        {
            Suspend();
        }
        catch (Exception ex)
        {
            // StopSession has released everything before rethrowing, so the device below can still go.
            AppLog.Warn($"wgc: teardown failed: {ex.Message}");
        }
        Release(ref _device);
        Release(ref _staging);
        Release(ref _d3dContext);
        Release(ref _d3dDevice);
    }

    public void Dispose()
    {
        Reset();
        _frameArrived.Dispose();
    }
}

/// <summary>The captured frame does not match the window's current size (mid-resize). Transient; no reset needed.</summary>
internal sealed class FrameSizeMismatchException(string message) : InvalidOperationException(message);
