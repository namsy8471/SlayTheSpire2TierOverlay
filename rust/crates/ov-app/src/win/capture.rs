//! A long-lived Windows.Graphics.Capture session on the game window (port of the C# WgcCapturer's main path).
//! Lives on one MTA worker thread. [`CaptureSession::grab`] returns the newest frame cropped to the game's client
//! area, in BGRA, or None when no new frame arrived since the last call.

use windows::Graphics::Capture::{Direct3D11CaptureFramePool, GraphicsCaptureItem, GraphicsCaptureSession};
use windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
use windows::Graphics::DirectX::DirectXPixelFormat;
use windows::Graphics::SizeInt32;
use windows::Win32::Foundation::{HMODULE, HWND, RECT};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_HARDWARE;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BOX, D3D11_CPU_ACCESS_READ, D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_MAP_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_SDK_VERSION,
    D3D11_TEXTURE2D_DESC, D3D11_USAGE_STAGING, D3D11CreateDevice, ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D,
};
use windows::Win32::Graphics::Dwm::{DWMWA_EXTENDED_FRAME_BOUNDS, DwmGetWindowAttribute};
use windows::Win32::Graphics::Dxgi::IDXGIDevice;
use windows::Win32::System::WinRT::Direct3D11::{CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess};
use windows::Win32::System::WinRT::Graphics::Capture::IGraphicsCaptureItemInterop;
use windows::core::{Interface, Result};

use super::PixelRect;

/// One frame of the game's client area.
pub struct BgraFrame {
    pub bgra: Vec<u8>,
    pub width: i32,
    pub height: i32,
    /// Where the frame sits on the screen.
    pub rect: PixelRect,
}

pub struct CaptureSession {
    hwnd: isize,
    d3d: ID3D11Device,
    ctx: ID3D11DeviceContext,
    device: IDirect3DDevice,
    pool: Direct3D11CaptureFramePool,
    session: GraphicsCaptureSession,
    size: SizeInt32,
    staging: Option<(ID3D11Texture2D, u32, u32)>,
}

fn create_device() -> Result<(ID3D11Device, ID3D11DeviceContext)> {
    let mut device = None;
    let mut context = None;
    unsafe {
        D3D11CreateDevice(None, D3D_DRIVER_TYPE_HARDWARE, HMODULE::default(), D3D11_CREATE_DEVICE_BGRA_SUPPORT, None, D3D11_SDK_VERSION, Some(&mut device), None, Some(&mut context))?;
    }
    Ok((device.unwrap(), context.unwrap()))
}

/// The captured surface's screen position (DWM extended frame bounds, else the window rect).
pub fn frame_origin(hwnd: isize) -> PixelRect {
    let mut r = RECT::default();
    let ok = unsafe { DwmGetWindowAttribute(HWND(hwnd as _), DWMWA_EXTENDED_FRAME_BOUNDS, &mut r as *mut _ as *mut _, size_of::<RECT>() as u32) }.is_ok();
    if ok { PixelRect { x: r.left, y: r.top, w: r.right - r.left, h: r.bottom - r.top } } else { super::game::window_rect(hwnd) }
}

impl CaptureSession {
    pub fn start(hwnd: isize) -> Result<CaptureSession> {
        let (d3d, ctx) = create_device()?;
        let dxgi: IDXGIDevice = d3d.cast()?;
        let device: IDirect3DDevice = unsafe { CreateDirect3D11DeviceFromDXGIDevice(&dxgi)? }.cast()?;
        let interop = windows::core::factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>()?;
        let item: GraphicsCaptureItem = unsafe { interop.CreateForWindow(HWND(hwnd as _))? };
        let size = item.Size()?;
        let pool = Direct3D11CaptureFramePool::CreateFreeThreaded(&device, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size)?;
        let session = pool.CreateCaptureSession(&item)?;
        let _ = session.SetIsBorderRequired(false);
        let _ = session.SetIsCursorCaptureEnabled(false);
        session.StartCapture()?;
        Ok(CaptureSession { hwnd, d3d, ctx, device, pool, session, size, staging: None })
    }

    /// The newest frame since the last call, cropped to `client` (screen pixels). None: nothing new.
    pub fn grab(&mut self, client: PixelRect) -> Result<Option<BgraFrame>> {
        let mut latest = None;
        while let Ok(f) = self.pool.TryGetNextFrame() {
            if let Some(old) = latest.replace(f) {
                let _ = old.Close();
            }
        }
        let Some(frame) = latest else { return Ok(None) };
        let content = frame.ContentSize()?;
        if content.Width != self.size.Width || content.Height != self.size.Height {
            // The window was resized: the pool's buffers have the old size. Recreate; the next frame fits.
            self.size = content;
            let _ = frame.Close();
            self.pool.Recreate(&self.device, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, content)?;
            return Ok(None);
        }
        let origin = frame_origin(self.hwnd);
        let src = PixelRect { x: client.x - origin.x, y: client.y - origin.y, w: client.w, h: client.h };
        let access: IDirect3DDxgiInterfaceAccess = frame.Surface()?.cast()?;
        let tex: ID3D11Texture2D = unsafe { access.GetInterface()? };
        let mut desc = D3D11_TEXTURE2D_DESC::default();
        unsafe { tex.GetDesc(&mut desc) };
        if src.x < 0 || src.y < 0 || src.w <= 0 || src.h <= 0 || (src.x + src.w) as u32 > desc.Width || (src.y + src.h) as u32 > desc.Height {
            let _ = frame.Close();
            return Ok(None);
        }
        let (w, h) = (src.w as u32, src.h as u32);
        if self.staging.as_ref().is_none_or(|(_, sw, sh)| *sw != w || *sh != h) {
            let mut d = desc;
            d.Width = w;
            d.Height = h;
            d.Usage = D3D11_USAGE_STAGING;
            d.BindFlags = 0;
            d.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
            d.MiscFlags = 0;
            let mut t = None;
            unsafe { self.d3d.CreateTexture2D(&d, None, Some(&mut t))? };
            self.staging = Some((t.unwrap(), w, h));
        }
        let (staging, _, _) = self.staging.as_ref().unwrap();
        let bx = D3D11_BOX { left: src.x as u32, top: src.y as u32, front: 0, right: (src.x + src.w) as u32, bottom: (src.y + src.h) as u32, back: 1 };
        unsafe { self.ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, &tex, 0, Some(&bx)) };
        let _ = frame.Close();
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        unsafe { self.ctx.Map(staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))? };
        let row = w as usize * 4;
        let mut bgra = vec![0u8; row * h as usize];
        for y in 0..h as usize {
            let s = unsafe { std::slice::from_raw_parts((mapped.pData as *const u8).add(y * mapped.RowPitch as usize), row) };
            bgra[y * row..(y + 1) * row].copy_from_slice(s);
        }
        unsafe { self.ctx.Unmap(staging, 0) };
        Ok(Some(BgraFrame { bgra, width: w as i32, height: h as i32, rect: client }))
    }
}

impl Drop for CaptureSession {
    fn drop(&mut self) {
        let _ = self.session.Close();
        let _ = self.pool.Close();
    }
}
