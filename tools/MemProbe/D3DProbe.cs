using System.Runtime.InteropServices;
using static Sts2Overlay.App.Interop.D3D11Interop;

namespace MemProbe;

/// <summary>
/// The capture side's native objects, created the way WgcCapturer creates them (no capture session, no window): a
/// hardware D3D11 device, two default-usage textures of the game's size (stand-ins for the WGC frame pool's buffers) and
/// the CPU-readable staging texture that every frame is copied into and mapped.
/// </summary>
internal sealed class D3DProbe : IDisposable
{
    private IntPtr _device, _context, _staging;
    private readonly List<IntPtr> _pool = [];

    public void CreateDevice()
    {
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION, out _device, out _, out _context));
    }

    /// <summary>Frame pool stand-ins: B8G8R8A8, default usage, shader resource + render target (what WGC hands back).</summary>
    public void CreatePool(int width, int height, int buffers)
    {
        for (int i = 0; i < buffers; i++)
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1,
                Usage = 0, // D3D11_USAGE_DEFAULT
                BindFlags = 0x8 | 0x20, // SHADER_RESOURCE | RENDER_TARGET
            };
            Marshal.ThrowExceptionForHR(CreateTexture2D(_device, desc, out var t));
            _pool.Add(t);
        }
    }

    /// <summary>The staging texture plus one copy + map, as in WgcCapturer.Capture (drivers often commit on first map).</summary>
    public void CreateStagingAndMap(int width, int height)
    {
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
        Marshal.ThrowExceptionForHR(CreateTexture2D(_device, desc, out _staging));
        if (_pool.Count > 0)
            CopySubresourceRegion(_context, _staging, _pool[0], new D3D11_BOX { Right = (uint)width, Bottom = (uint)height, Back = 1 });
        Marshal.ThrowExceptionForHR(Map(_context, _staging, out _));
        Unmap(_context, _staging);
    }

    public void Dispose()
    {
        Release(ref _staging);
        for (int i = 0; i < _pool.Count; i++)
        {
            var t = _pool[i];
            Release(ref t);
        }
        _pool.Clear();
        Release(ref _context);
        Release(ref _device);
    }
}
