//! Windows.Graphics.Capture (C# `WgcCapturer`): asks DWM for the game window's own surface, so the overlay's own
//! windows (excluded from capture) and anything covering the game never show up in the frame.
//!
//! M0 has the one-frame path: device, capture item for the window, a free-threaded frame pool, wait for the first
//! frame, copy it to a staging texture and read it back as RGBA. The kept-alive session of the C# app (one session
//! per window, newest frame only, band readback) comes with recognition (M5).

use std::sync::mpsc;
use std::time::Duration;
use windows::Foundation::TypedEventHandler;
use windows::Graphics::Capture::{Direct3D11CaptureFramePool, GraphicsCaptureItem, GraphicsCaptureSession};
use windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
use windows::Graphics::DirectX::DirectXPixelFormat;
use windows::Win32::Foundation::{HMODULE, HWND};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_HARDWARE;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CPU_ACCESS_READ, D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_MAP_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_SDK_VERSION,
    D3D11_TEXTURE2D_DESC, D3D11_USAGE_STAGING, D3D11CreateDevice, ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D,
};
use windows::Win32::Graphics::Dxgi::IDXGIDevice;
use windows::Win32::System::WinRT::Direct3D11::{CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess};
use windows::Win32::System::WinRT::Graphics::Capture::IGraphicsCaptureItemInterop;
use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize};
use windows::core::{Interface, Result};

/// One captured frame, RGBA, tightly packed.
pub struct Frame {
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
}

/// Captures one frame of `hwnd`. Runs on its own MTA thread (WinRT needs an initialised apartment).
pub fn capture_once(hwnd: isize, timeout: Duration) -> std::result::Result<Frame, String> {
    std::thread::spawn(move || {
        unsafe {
            let _ = RoInitialize(RO_INIT_MULTITHREADED);
        }
        capture_inner(hwnd, timeout).map_err(|e| format!("{e} (0x{:08X})", e.code().0))
    })
    .join()
    .map_err(|_| "capture thread panicked".to_string())?
}

fn create_device() -> Result<(ID3D11Device, ID3D11DeviceContext)> {
    let mut device = None;
    let mut context = None;
    unsafe {
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )?;
    }
    Ok((device.unwrap(), context.unwrap()))
}

fn capture_inner(hwnd: isize, timeout: Duration) -> Result<Frame> {
    let (d3d, ctx) = create_device()?;
    let dxgi: IDXGIDevice = d3d.cast()?;
    let device: IDirect3DDevice = unsafe { CreateDirect3D11DeviceFromDXGIDevice(&dxgi)? }.cast()?;

    let interop = windows::core::factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>()?;
    let item: GraphicsCaptureItem = unsafe { interop.CreateForWindow(HWND(hwnd as _))? };
    let size = item.Size()?;
    let pool = Direct3D11CaptureFramePool::CreateFreeThreaded(&device, DirectXPixelFormat::B8G8R8A8UIntNormalized, 1, size)?;
    let session: GraphicsCaptureSession = pool.CreateCaptureSession(&item)?;
    // Both are newer than WGC itself (Windows 10 2104 / 11): an older system just keeps the yellow border / cursor.
    let _ = session.SetIsBorderRequired(false);
    let _ = session.SetIsCursorCaptureEnabled(false);

    let (tx, rx) = mpsc::channel();
    let token = pool.FrameArrived(&TypedEventHandler::<Direct3D11CaptureFramePool, windows::core::IInspectable>::new(move |p, _| {
        if let Some(p) = p.as_ref() {
            if let Ok(f) = p.TryGetNextFrame() {
                let _ = tx.send(f);
            }
        }
        Ok(())
    }))?;
    session.StartCapture()?;
    let frame = rx.recv_timeout(timeout);
    let _ = pool.RemoveFrameArrived(token);
    let frame = frame.map_err(|_| windows::core::Error::new(windows::core::HRESULT(0x8000_000Au32 as i32), "no frame in time"))?;

    let access: IDirect3DDxgiInterfaceAccess = frame.Surface()?.cast()?;
    let tex: ID3D11Texture2D = unsafe { access.GetInterface()? };
    let out = read_texture(&d3d, &ctx, &tex);
    let _ = frame.Close();
    let _ = session.Close();
    let _ = pool.Close();
    out
}

fn read_texture(d3d: &ID3D11Device, ctx: &ID3D11DeviceContext, tex: &ID3D11Texture2D) -> Result<Frame> {
    let mut desc = D3D11_TEXTURE2D_DESC::default();
    unsafe { tex.GetDesc(&mut desc) };
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    desc.MiscFlags = 0;
    let mut staging = None;
    unsafe { d3d.CreateTexture2D(&desc, None, Some(&mut staging))? };
    let staging = staging.unwrap();
    unsafe { ctx.CopyResource(&staging, tex) };
    let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
    unsafe { ctx.Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))? };
    let (w, h) = (desc.Width as usize, desc.Height as usize);
    let mut rgba = vec![0u8; w * h * 4];
    for y in 0..h {
        let src = unsafe { std::slice::from_raw_parts((mapped.pData as *const u8).add(y * mapped.RowPitch as usize), w * 4) };
        let dst = &mut rgba[y * w * 4..(y + 1) * w * 4];
        for (d, s) in dst.chunks_exact_mut(4).zip(src.chunks_exact(4)) {
            d.copy_from_slice(&[s[2], s[1], s[0], 255]);
        }
    }
    unsafe { ctx.Unmap(&staging, 0) };
    Ok(Frame { width: w as u32, height: h as u32, rgba })
}
