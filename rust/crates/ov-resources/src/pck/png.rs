//! Port of PngWriter.cs: 8-bit RGBA PNG, no filtering, one zlib IDAT chunk.

use std::io::Write;

fn crc_table() -> &'static [u32; 256] {
    static T: std::sync::OnceLock<[u32; 256]> = std::sync::OnceLock::new();
    T.get_or_init(|| {
        let mut t = [0u32; 256];
        for (n, e) in t.iter_mut().enumerate() {
            let mut c = n as u32;
            for _ in 0..8 {
                c = if c & 1 != 0 { 0xEDB8_8320 ^ (c >> 1) } else { c >> 1 };
            }
            *e = c;
        }
        t
    })
}

fn crc(data: &[u8], mut c: u32) -> u32 {
    let t = crc_table();
    for &b in data {
        c = t[((c ^ u32::from(b)) & 0xFF) as usize] ^ (c >> 8);
    }
    c
}

fn chunk(out: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    out.extend_from_slice(&(data.len() as u32).to_be_bytes());
    out.extend_from_slice(kind);
    out.extend_from_slice(data);
    let c = crc(data, crc(kind, 0xFFFF_FFFF)) ^ 0xFFFF_FFFF;
    out.extend_from_slice(&c.to_be_bytes());
}

/// Encodes straight RGBA8 pixels, row-major.
pub fn encode(rgba: &[u8], width: u32, height: u32) -> Result<Vec<u8>, String> {
    let (w, h) = (width as usize, height as usize);
    if rgba.len() < w * h * 4 {
        return Err("pixel buffer too small".into());
    }
    let mut out = vec![0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A];
    let mut ihdr = [0u8; 13];
    ihdr[0..4].copy_from_slice(&width.to_be_bytes());
    ihdr[4..8].copy_from_slice(&height.to_be_bytes());
    ihdr[8] = 8;
    ihdr[9] = 6;
    chunk(&mut out, b"IHDR", &ihdr);
    let mut z = flate2::write::ZlibEncoder::new(Vec::new(), flate2::Compression::best());
    for y in 0..h {
        z.write_all(&[0]).map_err(|e| e.to_string())?;
        z.write_all(&rgba[y * w * 4..(y + 1) * w * 4]).map_err(|e| e.to_string())?;
    }
    let idat = z.finish().map_err(|e| e.to_string())?;
    chunk(&mut out, b"IDAT", &idat);
    chunk(&mut out, b"IEND", &[]);
    Ok(out)
}
