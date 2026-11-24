//! Port of Bc3Decoder.cs: BC3 (DXT5) blocks to straight RGBA8.

pub fn decode_image(mut blocks: &[u8], width: usize, height: usize) -> Result<Vec<u8>, String> {
    if width == 0 || height == 0 {
        return Err("BC3 image has no size".into());
    }
    let bw = 1 + (width - 1) / 4;
    let bh = 1 + (height - 1) / 4;
    if blocks.len() < bw * bh * 16 {
        return Err(format!("BC3 data too short for {width}x{height}."));
    }
    let mut rgba = vec![0u8; width * height * 4];
    let mut px = [0u8; 64];
    for y in (0..height).step_by(4) {
        for x in (0..width).step_by(4) {
            decode_block(&blocks[..16], &mut px);
            blocks = &blocks[16..];
            let row_bytes = (width - x).min(4) * 4;
            for row in 0..(height - y).min(4) {
                let dst = ((y + row) * width + x) * 4;
                rgba[dst..dst + row_bytes].copy_from_slice(&px[row * 16..row * 16 + row_bytes]);
            }
        }
    }
    Ok(rgba)
}

fn decode_block(block: &[u8], px: &mut [u8; 64]) {
    let mut alpha = [0u8; 8];
    alpha[0] = block[0];
    alpha[1] = block[1];
    let steps: i32 = if alpha[0] > alpha[1] { 7 } else { 5 };
    alpha[6] = 0;
    alpha[7] = 255;
    for i in 1..steps {
        alpha[(i + 1) as usize] = (((steps - i) * i32::from(alpha[0]) + i * i32::from(alpha[1])) / steps) as u8;
    }
    let mut rgb = [0u8; 12];
    expand565(u16::from_le_bytes([block[8], block[9]]), &mut rgb[0..3]);
    expand565(u16::from_le_bytes([block[10], block[11]]), &mut rgb[3..6]);
    for ch in 0..3 {
        let (a, b) = (i32::from(rgb[ch]), i32::from(rgb[3 + ch]));
        rgb[6 + ch] = ((2 * a + b) / 3) as u8;
        rgb[9 + ch] = ((a + 2 * b) / 3) as u8;
    }
    let mut ai = u64::from_le_bytes(block[0..8].try_into().unwrap()) >> 16;
    let mut ci = u32::from_le_bytes(block[12..16].try_into().unwrap());
    for p in 0..16 {
        let c = (ci & 3) as usize * 3;
        px[p * 4..p * 4 + 3].copy_from_slice(&rgb[c..c + 3]);
        px[p * 4 + 3] = alpha[(ai & 7) as usize];
        ci >>= 2;
        ai >>= 3;
    }
}

fn expand565(c: u16, rgb: &mut [u8]) {
    let (r, g, b) = (c >> 11, (c >> 5) & 63, c & 31);
    rgb[0] = ((r << 3) | (r >> 2)) as u8;
    rgb[1] = ((g << 2) | (g >> 4)) as u8;
    rgb[2] = ((b << 3) | (b >> 2)) as u8;
}
