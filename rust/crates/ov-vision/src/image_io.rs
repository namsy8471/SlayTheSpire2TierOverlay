//! Port of ImageLoader.cs: decodes PNG captures and WebP card art into BGRA frames.

use std::path::Path;

use crate::geometry::{OwnedFrame, ScreenFrame};

pub fn load(path: &Path) -> Result<OwnedFrame, String> {
    let img = image::open(path).map_err(|e| format!("Cannot decode image: {}: {e}", path.display()))?;
    Ok(from_rgba(img.to_rgba8()))
}

pub fn load_bytes(bytes: &[u8]) -> Result<OwnedFrame, String> {
    let img = image::load_from_memory(bytes).map_err(|e| format!("Cannot decode image stream: {e}"))?;
    Ok(from_rgba(img.to_rgba8()))
}

/// RGBA8 (straight alpha) of an image file, as the relic icon database reads it.
pub fn load_rgba(path: &Path) -> Result<(Vec<u8>, i32, i32), String> {
    let img = image::open(path).map_err(|e| format!("cannot decode {}: {e}", path.display()))?.to_rgba8();
    let (w, h) = img.dimensions();
    Ok((img.into_raw(), w as i32, h as i32))
}

fn from_rgba(img: image::RgbaImage) -> OwnedFrame {
    let (w, h) = img.dimensions();
    let mut bgra = img.into_raw();
    for px in bgra.chunks_exact_mut(4) {
        px.swap(0, 2);
    }
    OwnedFrame { bgra, width: w as i32, height: h as i32, stride: w as i32 * 4 }
}

pub fn save_png(frame: &ScreenFrame<'_>, path: &Path) -> Result<(), String> {
    let (w, h) = (frame.width as usize, frame.height as usize);
    let mut rgba = Vec::with_capacity(w * h * 4);
    for y in 0..h {
        let row = &frame.bgra[y * frame.stride as usize..y * frame.stride as usize + w * 4];
        for px in row.chunks_exact(4) {
            rgba.extend_from_slice(&[px[2], px[1], px[0], px[3]]);
        }
    }
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    image::save_buffer(path, &rgba, w as u32, h as u32, image::ExtendedColorType::Rgba8).map_err(|e| e.to_string())
}
