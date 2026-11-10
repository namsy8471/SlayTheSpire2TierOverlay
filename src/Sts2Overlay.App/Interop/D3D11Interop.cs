using System.Runtime.InteropServices;

namespace Sts2Overlay.App.Interop;

/// <summary>
/// The handful of Direct3D 11 / DXGI / WinRT-interop calls the Windows.Graphics.Capture backend needs, without pulling
/// in a DirectX wrapper library.
///
/// COM objects are plain pointers here. A COM interface pointer points at an object whose first field is a pointer to
/// its "vtable", an array of function pointers in the order the interface declares its methods (IUnknown's
/// QueryInterface/AddRef/Release are always slots 0, 1, 2). So calling method N is: read the vtable, take slot N, call it
/// with the object pointer as the first argument. The slot numbers below come from the declaration order in the
/// Windows SDK headers (d3d11.h, windows.graphics.capture.interop.h, windows.graphics.directx.direct3d11.interop.h).
/// </summary>
internal static unsafe class D3D11Interop
{
    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const int D3D_DRIVER_TYPE_WARP = 5;

    /// <summary>Required for BGRA surfaces, which is what Windows.Graphics.Capture produces.</summary>
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    public const uint D3D11_SDK_VERSION = 7;
    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;

    /// <summary>A texture the CPU can read after the GPU copied into it (lives in system memory).</summary>
    public const uint D3D11_USAGE_STAGING = 3;

    public const uint D3D11_CPU_ACCESS_READ = 0x20000;
    public const uint D3D11_MAP_READ = 1;

    public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    public const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);

    /// <summary>DwmGetWindowAttribute: the window's visible bounds (what window capture actually contains).</summary>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    public static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    public static readonly Guid IID_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;   // DXGI_SAMPLE_DESC.Count
        public uint SampleQuality; // DXGI_SAMPLE_DESC.Quality
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_BOX
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    // ---- exported functions -------------------------------------------------------------------------

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    /// <summary>Wraps a DXGI device as the WinRT <c>IDirect3DDevice</c> that Direct3D11CaptureFramePool wants.</summary>
    [DllImport("d3d11.dll")]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr inspectable);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    public static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    public static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    public static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    /// <summary>combaseapi.h line 556.</summary>
    [DllImport("combase.dll")]
    private static extern int CoIncrementMTAUsage(out IntPtr cookie);

    /// <summary>The calling thread has not initialised COM (winerror.h).</summary>
    private const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeMethods.RECT value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);

    // ---- vtable calls -----------------------------------------------------------------------------------

    internal static void* Slot(IntPtr comObject, int index) => (*(void***)comObject)[index];

    /// <summary>ID3D11Device::CreateTexture2D (slot 5).</summary>
    public static int CreateTexture2D(IntPtr device, in D3D11_TEXTURE2D_DESC desc, out IntPtr texture)
    {
        IntPtr t;
        int hr;
        fixed (D3D11_TEXTURE2D_DESC* d = &desc)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)Slot(device, 5))(device, d, IntPtr.Zero, &t);
        }
        texture = hr >= 0 ? t : IntPtr.Zero;
        return hr;
    }

    /// <summary>ID3D11Device::GetDeviceRemovedReason (slot 39): S_OK while the device is healthy.</summary>
    public static int GetDeviceRemovedReason(IntPtr device)
        => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(device, 39))(device);

    /// <summary>ID3D11Texture2D::GetDesc (slot 10; after IUnknown 0-2, ID3D11DeviceChild 3-6, ID3D11Resource 7-9).</summary>
    public static D3D11_TEXTURE2D_DESC GetDesc(IntPtr texture)
    {
        D3D11_TEXTURE2D_DESC desc;
        ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Slot(texture, 10))(texture, &desc);
        return desc;
    }

    /// <summary>ID3D11DeviceContext::Map (slot 14). For a staging texture this waits until the GPU has finished writing it.</summary>
    public static int Map(IntPtr context, IntPtr resource, out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        D3D11_MAPPED_SUBRESOURCE m;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Slot(context, 14))(
            context, resource, 0, D3D11_MAP_READ, 0, &m);
        mapped = m;
        return hr;
    }

    /// <summary>ID3D11DeviceContext::Unmap (slot 15).</summary>
    public static void Unmap(IntPtr context, IntPtr resource)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(context, 15))(context, resource, 0);

    /// <summary>ID3D11DeviceContext::CopySubresourceRegion (slot 46): GPU-side copy of <paramref name="box"/> from src to dst at (0, 0).</summary>
    public static void CopySubresourceRegion(IntPtr context, IntPtr dst, IntPtr src, in D3D11_BOX box)
    {
        fixed (D3D11_BOX* b = &box)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)Slot(context, 46))(
                context, dst, 0, 0, 0, 0, src, 0, b);
        }
    }

    /// <summary>
    /// ID3D11DeviceContext::Flush (slot 111): sends the queued commands to the GPU now instead of at the next Map. Also
    /// the point where D3D11 really destroys objects released since the last flush: a released texture keeps its memory
    /// until then ("deferred destruction").
    /// </summary>
    public static void Flush(IntPtr context)
        => ((delegate* unmanaged[Stdcall]<IntPtr, void>)Slot(context, 111))(context);

    /// <summary>IDirect3DDxgiInterfaceAccess::GetInterface (slot 3): the D3D11 texture behind a WinRT IDirect3DSurface.</summary>
    public static int GetDxgiInterface(IntPtr access, in Guid iid, out IntPtr result)
    {
        IntPtr r;
        int hr;
        fixed (Guid* g = &iid)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(access, 3))(access, g, &r);
        }
        result = hr >= 0 ? r : IntPtr.Zero;
        return hr;
    }

    /// <summary>IGraphicsCaptureItemInterop::CreateForWindow (slot 3) / CreateForMonitor (slot 4).</summary>
    public static int CreateCaptureItem(IntPtr interop, bool monitor, IntPtr handle, out IntPtr item)
    {
        IntPtr r;
        int hr;
        var iid = IID_IGraphicsCaptureItem;
        hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(interop, monitor ? 4 : 3))(interop, handle, &iid, &r);
        item = hr >= 0 ? r : IntPtr.Zero;
        return hr;
    }

    /// <summary>Activation factory of a WinRT class, asked directly for one of its interfaces (statics or interop).</summary>
    public static IntPtr GetActivationFactory(string className, in Guid iid)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
        try
        {
            var hr = RoGetActivationFactory(hstring, iid, out var factory);
            if (hr == CO_E_NOTINITIALIZED)
            {
                // A thread that never initialised COM. The projection handled this the same way: keep the process-wide
                // MTA alive (for the rest of the process) so WinRT can be used from this thread, then retry.
                Marshal.ThrowExceptionForHR(CoIncrementMTAUsage(out _));
                hr = RoGetActivationFactory(hstring, iid, out factory);
            }
            Marshal.ThrowExceptionForHR(hr);
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    public static void Release(ref IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            Marshal.Release(comObject);
            comObject = IntPtr.Zero;
        }
    }
}
