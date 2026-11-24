//! Ctrl+F12 "오버레이 포함 스크린샷": the game's area of the screen as the user sees it, overlay windows included.
//!
//! Steam's F12 and the Xbox Game Bar save only the game's own frames, so no window drawn above the game (this
//! overlay, or any other) can ever be in them. This reads the composed desktop instead (GDI `BitBlt` from the
//! screen DC, the same pixels Win+Shift+S and Print Screen get) and writes a PNG to `Pictures\STS2 Overlay`.

use super::PixelRect;
use std::path::PathBuf;
use windows::Win32::Graphics::Gdi::{
    BITMAPINFO, BITMAPINFOHEADER, BI_RGB, BitBlt, CAPTUREBLT, CreateCompatibleBitmap, CreateCompatibleDC, DIB_RGB_COLORS, DeleteDC,
    DeleteObject, GetDC, GetDIBits, ReleaseDC, SRCCOPY, SelectObject,
};
use windows::Win32::System::SystemInformation::GetLocalTime;

/// The folder the screenshots go to: `%USERPROFILE%\Pictures\STS2 Overlay` (the data folder when there is no profile).
pub fn folder() -> PathBuf {
    match std::env::var_os("USERPROFILE") {
        Some(p) => PathBuf::from(p).join("Pictures").join("STS2 Overlay"),
        None => crate::paths::data_root().join("screenshots"),
    }
}

/// `sts2-20261124-193512.png` in local time.
fn file_name() -> String {
    let t = unsafe { GetLocalTime() };
    format!("sts2-{:04}{:02}{:02}-{:02}{:02}{:02}.png", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond)
}

/// The screen pixels of `r` as RGBA rows, top row first.
pub fn grab(r: PixelRect) -> Result<Vec<u8>, String> {
    if r.w <= 0 || r.h <= 0 {
        return Err("empty area".into());
    }
    unsafe {
        let screen = GetDC(None);
        if screen.is_invalid() {
            return Err("GetDC failed".into());
        }
        let mem = CreateCompatibleDC(Some(screen));
        let bmp = CreateCompatibleBitmap(screen, r.w, r.h);
        let old = SelectObject(mem, bmp.into());
        // CAPTUREBLT: layered windows (the overlay's marks layer) are copied too.
        let copied = BitBlt(mem, 0, 0, r.w, r.h, Some(screen), r.x, r.y, SRCCOPY | CAPTUREBLT);
        let mut info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: r.w,
                biHeight: -r.h, // negative: top-down rows
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut px = vec![0u8; r.w as usize * r.h as usize * 4];
        SelectObject(mem, old);
        let rows = GetDIBits(mem, bmp, 0, r.h as u32, Some(px.as_mut_ptr().cast()), &mut info, DIB_RGB_COLORS);
        let _ = DeleteObject(bmp.into());
        let _ = DeleteDC(mem);
        ReleaseDC(None, screen);
        if copied.is_err() {
            return Err("BitBlt failed".into());
        }
        if rows != r.h {
            return Err("GetDIBits failed".into());
        }
        // BGRA -> RGBA, opaque (the desktop has no alpha)
        for p in px.chunks_exact_mut(4) {
            p.swap(0, 2);
            p[3] = 255;
        }
        Ok(px)
    }
}

/// Saves the screen area `r` as a PNG in [`folder`]. Returns the line to show.
pub fn save(r: PixelRect) -> String {
    let px = match grab(r) {
        Ok(p) => p,
        Err(e) => {
            crate::applog::warn(format!("screenshot: failed ({e})"));
            return ov_core::loc::tr(format!("스크린샷 실패: {e}"), format!("Screenshot failed: {e}"));
        }
    };
    let dir = folder();
    let path = dir.join(file_name());
    let saved = std::fs::create_dir_all(&dir).map_err(|e| e.to_string()).and_then(|_| {
        image::save_buffer(&path, &px, r.w as u32, r.h as u32, image::ColorType::Rgba8).map_err(|e| e.to_string())
    });
    match saved {
        Ok(()) => {
            crate::applog::info(format!("screenshot: {}x{} saved", r.w, r.h));
            ov_core::loc::tr(format!("스크린샷 저장: {}", path.display()), format!("Screenshot saved: {}", path.display()))
        }
        Err(e) => {
            crate::applog::warn(format!("screenshot: could not save ({e})"));
            ov_core::loc::tr(format!("스크린샷 저장 실패: {e}"), format!("Could not save the screenshot: {e}"))
        }
    }
}
