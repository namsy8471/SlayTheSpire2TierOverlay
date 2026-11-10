using System.Runtime.InteropServices;
using static Sts2Overlay.App.Interop.D3D11Interop;

namespace Sts2Overlay.App.Interop;

/// <summary>
/// The Windows.Graphics.Capture calls <c>WgcCapturer</c> makes, through the WinRT ABI (raw COM vtables) instead of the
/// C#/WinRT projection, so the app needs no Windows SDK projection assembly (Microsoft.Windows.SDK.NET.dll, ~25 MB).
///
/// WinRT interfaces are COM interfaces that start with IInspectable: slots 0-2 IUnknown (QueryInterface, AddRef, Release),
/// 3-5 IInspectable (GetIids, GetRuntimeClassName, GetTrustLevel; inspectable.h lines 109-116), so an interface's own
/// methods begin at slot 6 in the order the header declares them. A property getter is <c>get_X(out value)</c>, a setter
/// <c>put_X(value)</c>, an event is <c>add_X(handler, out token)</c> + <c>remove_X(token)</c>. WinRT <c>boolean</c> is
/// one byte. Every IID and slot below is taken from the 10.0.22621.0 Windows SDK headers, file and line noted.
///
/// Pointers handed out here carry one reference the caller must release (<see cref="CloseAndRelease"/> for closable
/// objects). Calls throw on a failed HRESULT (<see cref="Marshal.ThrowExceptionForHR(int)"/>), except the teardown
/// calls, which return it so the caller can finish releasing first.
/// </summary>
internal static unsafe class WgcInterop
{
    /// <summary>DirectXPixelFormat.B8G8R8A8UIntNormalized (winrt\windows.graphics.directx.h line 285).</summary>
    public const int PixelFormatB8G8R8A8UIntNormalized = 87;

    /// <summary>Windows.Graphics.SizeInt32 (winrt\windows.graphics.h line 222).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SizeInt32
    {
        public int Width;
        public int Height;

        public override readonly string ToString() => $"{Width}x{Height}";
    }

    // ---- IIDs (winrt\windows.graphics.capture.h unless noted) ------------------------------------------------

    /// <summary>IGraphicsCaptureSessionStatics, line 1205: IsSupported = slot 6 (line 1209).</summary>
    public static readonly Guid IID_IGraphicsCaptureSessionStatics = new("2224a540-5974-49aa-b232-0882536f4cb5");

    /// <summary>IDirect3D11CaptureFramePoolStatics2, line 856: CreateFreeThreaded = slot 6 (line 860).</summary>
    public static readonly Guid IID_IDirect3D11CaptureFramePoolStatics2 = new("589b103f-6bbc-5df5-a991-02e28b3b66d5");

    /// <summary>IGraphicsCaptureSession2, line 1127: get_IsCursorCaptureEnabled 6 (1131), put_IsCursorCaptureEnabled 7 (1134).</summary>
    public static readonly Guid IID_IGraphicsCaptureSession2 = new("2c39ae40-7d2e-5044-804e-8b6799d4cf9e");

    /// <summary>IGraphicsCaptureSession3, line 1166: get_IsBorderRequired 6 (1170), put_IsBorderRequired 7 (1173).</summary>
    public static readonly Guid IID_IGraphicsCaptureSession3 = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");

    /// <summary>Windows.Foundation.IClosable, winrt\windows.foundation.h line 1291: Close = slot 6 (line 1295).</summary>
    public static readonly Guid IID_IClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");

    /// <summary>
    /// IDirect3DDevice, winrt\windows.graphics.directx.direct3d11.h line 328. CreateDirect3D11DeviceFromDXGIDevice returns
    /// an IInspectable; the frame pool methods take this interface, so the pointer we pass must be QueryInterface'd to it.
    /// </summary>
    public static readonly Guid IID_IDirect3DDevice = new("a37624ab-8d5f-4650-9d3e-9eae3d9bc670");

    /// <summary>
    /// <c>TypedEventHandler&lt;Direct3D11CaptureFramePool, IInspectable&gt;</c> (FrameArrived), line 466. A parameterized
    /// IID: UUID v5 of the signature <c>pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};rc(Windows.Graphics.Capture.Direct3D11CaptureFramePool;{24eb6d22-1975-422e-82e7-780dbd8ddf24});cinterface(IInspectable))</c>
    /// (checked in WinRTIidTests).
    /// </summary>
    public static readonly Guid IID_FrameArrivedHandler = new("51a947f7-79cf-5a3e-a3a5-1289cfa6dfe8");

    /// <summary>
    /// <c>TypedEventHandler&lt;GraphicsCaptureItem, IInspectable&gt;</c> (Closed), line 493. Signature
    /// <c>pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};rc(Windows.Graphics.Capture.GraphicsCaptureItem;{79c3f95b-31f7-4ec2-a464-632ef5d30760});cinterface(IInspectable))</c>.
    /// </summary>
    public static readonly Guid IID_ItemClosedHandler = new("e9c610c0-a68c-5bd9-8021-8589346eeee2");

    // ---- GraphicsCaptureSession statics -----------------------------------------------------------------------

    /// <summary>GraphicsCaptureSession.IsSupported(): IGraphicsCaptureSessionStatics::IsSupported (slot 6).</summary>
    public static bool IsSupported()
    {
        var statics = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureSession", IID_IGraphicsCaptureSessionStatics);
        try
        {
            byte supported;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Slot(statics, 6))(statics, &supported));
            return supported != 0;
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    // ---- Direct3D11CaptureFramePool (IDirect3D11CaptureFramePool, line 760) -----------------------------------

    /// <summary>Direct3D11CaptureFramePool.CreateFreeThreaded: IDirect3D11CaptureFramePoolStatics2::CreateFreeThreaded (slot 6).</summary>
    public static IntPtr CreateFreeThreadedFramePool(IntPtr device, int pixelFormat, int buffers, SizeInt32 size)
    {
        var statics = GetActivationFactory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", IID_IDirect3D11CaptureFramePoolStatics2);
        try
        {
            IntPtr pool;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, SizeInt32, IntPtr*, int>)Slot(statics, 6))(
                statics, device, pixelFormat, buffers, size, &pool));
            return pool;
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    /// <summary>IDirect3D11CaptureFramePool::Recreate (slot 6, line 764): new buffers of a new size.</summary>
    public static void Recreate(IntPtr pool, IntPtr device, int pixelFormat, int buffers, SizeInt32 size)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, SizeInt32, int>)Slot(pool, 6))(
            pool, device, pixelFormat, buffers, size));

    /// <summary>IDirect3D11CaptureFramePool::TryGetNextFrame (slot 7, line 770): a frame, or zero when none is waiting.</summary>
    public static IntPtr TryGetNextFrame(IntPtr pool)
    {
        IntPtr frame;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(pool, 7))(pool, &frame));
        return frame;
    }

    /// <summary>IDirect3D11CaptureFramePool::add_FrameArrived (slot 8, line 773). <paramref name="callback"/> gets the pool pointer.</summary>
    public static long AddFrameArrived(IntPtr pool, Action<IntPtr> callback) => AddHandler(pool, 8, IID_FrameArrivedHandler, callback);

    /// <summary>IDirect3D11CaptureFramePool::remove_FrameArrived (slot 9, line 777). Returns the HRESULT.</summary>
    public static int RemoveFrameArrived(IntPtr pool, long token)
        => ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(pool, 9))(pool, token);

    /// <summary>IDirect3D11CaptureFramePool::CreateCaptureSession (slot 10, line 780).</summary>
    public static IntPtr CreateCaptureSession(IntPtr pool, IntPtr item)
    {
        IntPtr session;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(pool, 10))(pool, item, &session));
        return session;
    }

    // ---- GraphicsCaptureItem (IGraphicsCaptureItem, line 933) -------------------------------------------------

    /// <summary>IGraphicsCaptureItem::get_Size (slot 7, line 940).</summary>
    public static SizeInt32 GetItemSize(IntPtr item)
    {
        SizeInt32 size;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)Slot(item, 7))(item, &size));
        return size;
    }

    /// <summary>IGraphicsCaptureItem::add_Closed (slot 8, line 943). <paramref name="callback"/> gets the item pointer.</summary>
    public static long AddItemClosed(IntPtr item, Action<IntPtr> callback) => AddHandler(item, 8, IID_ItemClosedHandler, callback);

    /// <summary>IGraphicsCaptureItem::remove_Closed (slot 9, line 947). Returns the HRESULT.</summary>
    public static int RemoveItemClosed(IntPtr item, long token)
        => ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(item, 9))(item, token);

    // ---- GraphicsCaptureSession (IGraphicsCaptureSession, line 1093) ------------------------------------------

    /// <summary>IGraphicsCaptureSession::StartCapture (slot 6, line 1097).</summary>
    public static void StartCapture(IntPtr session)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(session, 6))(session));

    /// <summary>GraphicsCaptureSession.IsCursorCaptureEnabled = value: IGraphicsCaptureSession2::put_IsCursorCaptureEnabled (slot 7). Windows 10 2004+.</summary>
    public static void SetCursorCaptureEnabled(IntPtr session, bool enabled) => PutBoolean(session, IID_IGraphicsCaptureSession2, 7, enabled);

    /// <summary>
    /// GraphicsCaptureSession.IsBorderRequired = value: IGraphicsCaptureSession3::put_IsBorderRequired (slot 7). Build 20348+.
    /// Throws InvalidCastException when the session lacks the interface, UnauthorizedAccessException when the system refuses.
    /// </summary>
    public static void SetBorderRequired(IntPtr session, bool required) => PutBoolean(session, IID_IGraphicsCaptureSession3, 7, required);

    // ---- Direct3D11CaptureFrame (IDirect3D11CaptureFrame, line 718) -------------------------------------------

    /// <summary>IDirect3D11CaptureFrame::get_Surface (slot 6, line 722): an IDirect3DSurface.</summary>
    public static IntPtr GetFrameSurface(IntPtr frame)
    {
        IntPtr surface;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(frame, 6))(frame, &surface));
        return surface;
    }

    /// <summary>IDirect3D11CaptureFrame::get_ContentSize (slot 8, line 728).</summary>
    public static SizeInt32 GetFrameContentSize(IntPtr frame)
    {
        SizeInt32 size;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)Slot(frame, 8))(frame, &size));
        return size;
    }

    // ---- shared -----------------------------------------------------------------------------------------------

    /// <summary>
    /// IClosable::Close (what the projection's Dispose did), then drops our reference, whatever Close returned. Throws
    /// Close's failure only after the reference is gone, so an error never leaks the object. Zero is a no-op.
    /// </summary>
    public static void CloseAndRelease(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        var hr = Close(obj);
        Release(ref obj);
        Marshal.ThrowExceptionForHR(hr);
    }

    private static int Close(IntPtr obj)
    {
        var hr = Marshal.QueryInterface(obj, in IID_IClosable, out var closable);
        if (hr < 0) return hr;
        try
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(closable, 6))(closable);
        }
        finally
        {
            Marshal.Release(closable);
        }
    }

    private static void PutBoolean(IntPtr obj, in Guid iid, int slot, bool value)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(obj, in iid, out var itf));
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Slot(itf, slot))(itf, value ? (byte)1 : (byte)0));
        }
        finally
        {
            Marshal.Release(itf);
        }
    }

    /// <summary>
    /// add_X(handler, out EventRegistrationToken) on <paramref name="source"/>. The source keeps its own reference to
    /// the handler (and so the callback) until remove_X or until it is destroyed; ours is dropped right away.
    /// EventRegistrationToken is one INT64 (winrt\eventtoken.h line 57).
    /// </summary>
    private static long AddHandler(IntPtr source, int slot, in Guid handlerIid, Action<IntPtr> callback)
    {
        var handler = ComEventHandler.Create(handlerIid, callback);
        try
        {
            long token;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(source, slot))(source, handler, &token));
            return token;
        }
        finally
        {
            Marshal.Release(handler);
        }
    }
}
