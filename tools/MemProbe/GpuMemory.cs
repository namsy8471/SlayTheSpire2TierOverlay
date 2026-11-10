using System.Globalization;
using System.Runtime.InteropServices;
using static Sts2Overlay.App.Interop.D3D11Interop;

namespace MemProbe;

/// <summary>
/// This process's video memory use on every hardware adapter (IDXGIAdapter3::QueryVideoMemoryInfo), "local" (the GPU's
/// own segment; on an integrated GPU that is system RAM) and "non-local" (system RAM the GPU reaches over the bus).
/// D3D resources on an integrated GPU are also in the process's private bytes, on a discrete GPU they are not.
/// </summary>
internal static unsafe class GpuMemory
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIAdapter3 = new("645967a4-1392-4310-a798-8053ce3e93fd");
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
    }

    public sealed record Adapter(int Index, string Name, long Luid, bool Software);

    /// <summary>Every adapter DXGI enumerates (index = the EnumAdapters1 order; 0 is what a null adapter picks).</summary>
    public static List<Adapter> Adapters()
    {
        var list = new List<Adapter>();
        ForEach((i, adapter, desc) => list.Add(new Adapter(i, desc.Description.Trim(), Luid(desc), (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)));
        return list;
    }

    /// <summary>IDXGIAdapter1 of EnumAdapters1(index) (caller releases).</summary>
    public static IntPtr AdapterPointer(int index)
    {
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(IID_IDXGIFactory1, out var factory));
        try
        {
            IntPtr adapter;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, 12))(factory, (uint)index, &adapter));
            return adapter;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    /// <summary>LUID of the adapter a D3D11 device was created on (IDXGIDevice::GetAdapter, IDXGIAdapter::GetDesc).</summary>
    public static long DeviceAdapterLuid(IntPtr d3dDevice)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in IID_IDXGIDevice, out var dxgi));
        try
        {
            IntPtr adapter;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(dxgi, 7))(dxgi, &adapter));
            try
            {
                // DXGI_ADAPTER_DESC is DXGI_ADAPTER_DESC1 without the trailing Flags: same layout up to the LUID.
                var raw = stackalloc byte[Marshal.SizeOf<DXGI_ADAPTER_DESC1>()];
                new Span<byte>(raw, Marshal.SizeOf<DXGI_ADAPTER_DESC1>()).Clear();
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Slot(adapter, 8))(adapter, raw));
                return Luid(Marshal.PtrToStructure<DXGI_ADAPTER_DESC1>((IntPtr)raw));
            }
            finally
            {
                Marshal.Release(adapter);
            }
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    /// <summary>"gpu Intel(R) Graphics local 12.0 nonlocal 0.0 | ..." in MB, hardware adapters only.</summary>
    public static string Line()
    {
        var parts = new List<string>();
        ForEach((i, adapter, desc) =>
        {
            if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) return;
            if (Marshal.QueryInterface(adapter, in IID_IDXGIAdapter3, out var a3) < 0) return;
            try
            {
                var local = Query(a3, 0);
                var nonLocal = Query(a3, 1);
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"[{i}] {Short(desc.Description)} local {local / 1048576.0:F1} nonlocal {nonLocal / 1048576.0:F1}"));
            }
            finally
            {
                Marshal.Release(a3);
            }
        });
        return "gpu MB: " + string.Join(" | ", parts);
    }

    /// <summary>This process's local + non-local use on every hardware adapter, bytes.</summary>
    public static long Total()
    {
        long sum = 0;
        ForEach((i, adapter, desc) =>
        {
            if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) return;
            if (Marshal.QueryInterface(adapter, in IID_IDXGIAdapter3, out var a3) < 0) return;
            try
            {
                sum += (long)(Query(a3, 0) + Query(a3, 1));
            }
            finally
            {
                Marshal.Release(a3);
            }
        });
        return sum;
    }

    private static string Short(string name) => name.Length > 22 ? name[..22] : name;

    private static long Luid(DXGI_ADAPTER_DESC1 d) => ((long)d.LuidHigh << 32) | d.LuidLow;

    private static ulong Query(IntPtr adapter3, int segmentGroup)
    {
        DXGI_QUERY_VIDEO_MEMORY_INFO info;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, DXGI_QUERY_VIDEO_MEMORY_INFO*, int>)Slot(adapter3, 14))(adapter3, 0, segmentGroup, &info);
        return hr < 0 ? 0 : info.CurrentUsage;
    }

    private static void ForEach(Action<int, IntPtr, DXGI_ADAPTER_DESC1> visit)
    {
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(IID_IDXGIFactory1, out var factory));
        var raw = stackalloc byte[Marshal.SizeOf<DXGI_ADAPTER_DESC1>()];
        try
        {
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, 12))(factory, i, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Slot(adapter, 10))(adapter, raw));
                    visit((int)i, adapter, Marshal.PtrToStructure<DXGI_ADAPTER_DESC1>((IntPtr)raw));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }
}
