//! Port of Bc7Decoder.cs: BC7 (BPTC_RGBA) blocks to straight RGBA8, per the D3D11 BC7 specification.

#[derive(Clone, Copy)]
struct Mode {
    subsets: usize,
    partition_bits: u32,
    rotation_bits: u32,
    index_selection_bits: u32,
    color_bits: u32,
    alpha_bits: u32,
    endpoint_p_bits: bool,
    shared_p_bits: bool,
    index_bits: u32,
    index2_bits: u32,
}

const fn m(subsets: usize, pb: u32, rb: u32, isb: u32, cb: u32, ab: u32, epb: bool, spb: bool, ib: u32, i2b: u32) -> Mode {
    Mode {
        subsets,
        partition_bits: pb,
        rotation_bits: rb,
        index_selection_bits: isb,
        color_bits: cb,
        alpha_bits: ab,
        endpoint_p_bits: epb,
        shared_p_bits: spb,
        index_bits: ib,
        index2_bits: i2b,
    }
}

const MODES: [Mode; 8] = [
    m(3, 4, 0, 0, 4, 0, true, false, 3, 0),
    m(2, 6, 0, 0, 6, 0, false, true, 3, 0),
    m(3, 6, 0, 0, 5, 0, false, false, 2, 0),
    m(2, 6, 0, 0, 7, 0, true, false, 2, 0),
    m(1, 0, 2, 1, 5, 6, false, false, 2, 3),
    m(1, 0, 2, 0, 7, 8, false, false, 2, 2),
    m(1, 0, 0, 0, 7, 7, true, false, 4, 0),
    m(2, 6, 0, 0, 5, 5, true, false, 2, 0),
];

const WEIGHTS2: [i32; 4] = [0, 21, 43, 64];
const WEIGHTS3: [i32; 8] = [0, 9, 18, 27, 37, 46, 55, 64];
const WEIGHTS4: [i32; 16] = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

/// Two-subset partitions: bit i set = pixel i belongs to subset 1.
const PARTITION2: [u16; 64] = [0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000, 0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C, 0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660, 0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,];

/// Three-subset partitions, 16 pixels each, subset 0..2.
const PARTITION3: [&[u8; 16]; 64] = [b"0011001102212222", b"0001001122112221", b"0000200122112211", b"0222002200110111", b"0000000011221122", b"0011001100220022", b"0022002211111111", b"0011001122112211", b"0000000011112222", b"0000111111112222", b"0000111122222222", b"0012001200120012", b"0112011201120112", b"0122012201220122", b"0011011211221222", b"0011200122002220", b"0001001101121122", b"0111001120012200", b"0000112211221122", b"0022002200221111", b"0111011102220222", b"0001000122212221", b"0000001101220122", b"0000110022102210", b"0122012200110000", b"0012001211222222", b"0110122112210110", b"0000011012211221", b"0022110211020022", b"0110011020022222", b"0011012201220011", b"0000200022112221", b"0000000211221222", b"0222002200120011", b"0011001200220222", b"0120012001200120", b"0000111122220000", b"0120120120120120", b"0120201212010120", b"0011220011220011", b"0011112222000011", b"0101010122222222", b"0000000021212121", b"0022112200221122", b"0022001100220011", b"0220122102201221", b"0101222222220101", b"0000212121212121", b"0101010101012222", b"0222011102220111", b"0002111200021112", b"0000211221122112", b"0222011101110222", b"0002111211120002", b"0110011001102222", b"0000000021122112", b"0110011022222222", b"0022001100110022", b"0022112211220022", b"0000000000002112", b"0002000100020001", b"0222122202221222", b"0101222222222222", b"0111201122012220"];

const ANCHOR2: [u8; 64] = [15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2, 15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,];
const ANCHOR3A: [u8; 64] = [3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15, 8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,];
const ANCHOR3B: [u8; 64] = [15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8, 15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,];

/// Decodes a whole BC7 image (blocks row by row); partial edge blocks are clipped.
pub fn decode_image(blocks: &[u8], width: usize, height: usize) -> Result<Vec<u8>, String> {
    let (bw, bh) = (width.div_ceil(4), height.div_ceil(4));
    if blocks.len() < bw * bh * 16 {
        return Err(format!("BC7 data too short: {} bytes for {width}x{height}.", blocks.len()));
    }
    let mut rgba = vec![0u8; width * height * 4];
    let mut block = [0u8; 64];
    for by in 0..bh {
        for bx in 0..bw {
            let at = (by * bw + bx) * 16;
            decode_block(&blocks[at..at + 16], &mut block);
            for py in 0..4 {
                let y = by * 4 + py;
                if y >= height {
                    break;
                }
                for px in 0..4 {
                    let x = bx * 4 + px;
                    if x >= width {
                        break;
                    }
                    let s = (py * 4 + px) * 4;
                    let d = (y * width + x) * 4;
                    rgba[d..d + 4].copy_from_slice(&block[s..s + 4]);
                }
            }
        }
    }
    Ok(rgba)
}

struct Bits<'a> {
    data: &'a [u8],
    pos: usize,
}

impl Bits<'_> {
    fn read(&mut self, count: u32) -> i32 {
        let mut v = 0;
        for i in 0..count {
            v |= i32::from((self.data[self.pos >> 3] >> (self.pos & 7)) & 1) << i;
            self.pos += 1;
        }
        v
    }
}

/// One 16-byte block into 16 RGBA pixels; the reserved mode 8 gives transparent black.
pub fn decode_block(block: &[u8], rgba: &mut [u8; 64]) {
    let mut bits = Bits { data: block, pos: 0 };
    let mut mode_index = 0;
    while mode_index < 8 && bits.read(1) == 0 {
        mode_index += 1;
    }
    if mode_index == 8 {
        rgba.fill(0);
        return;
    }
    let md = MODES[mode_index];
    let partition = bits.read(md.partition_bits) as usize;
    let rotation = bits.read(md.rotation_bits);
    let index_selection = bits.read(md.index_selection_bits);
    let endpoints = md.subsets * 2;
    let mut ep = [0i32; 24];
    for c in 0..3 {
        for e in 0..endpoints {
            ep[e * 4 + c] = bits.read(md.color_bits);
        }
    }
    for e in 0..endpoints {
        ep[e * 4 + 3] = if md.alpha_bits > 0 { bits.read(md.alpha_bits) } else { 255 };
    }
    let (mut color_bits, mut alpha_bits) = (md.color_bits, md.alpha_bits);
    if md.endpoint_p_bits || md.shared_p_bits {
        let mut p = [0i32; 6];
        if md.endpoint_p_bits {
            for v in p.iter_mut().take(endpoints) {
                *v = bits.read(1);
            }
        } else {
            for s in 0..md.subsets {
                let b = bits.read(1);
                p[s * 2] = b;
                p[s * 2 + 1] = b;
            }
        }
        for e in 0..endpoints {
            for c in 0..3 {
                ep[e * 4 + c] = (ep[e * 4 + c] << 1) | p[e];
            }
            if md.alpha_bits > 0 {
                ep[e * 4 + 3] = (ep[e * 4 + 3] << 1) | p[e];
            }
        }
        color_bits += 1;
        if md.alpha_bits > 0 {
            alpha_bits += 1;
        }
    }
    for e in 0..endpoints {
        for c in 0..3 {
            ep[e * 4 + c] = expand(ep[e * 4 + c], color_bits);
        }
        if md.alpha_bits > 0 {
            ep[e * 4 + 3] = expand(ep[e * 4 + 3], alpha_bits);
        }
    }
    let subset = |i: usize| -> usize {
        match md.subsets {
            2 => ((PARTITION2[partition] >> i) & 1) as usize,
            3 => (PARTITION3[partition][i] - b'0') as usize,
            _ => 0,
        }
    };
    let is_anchor = |i: usize| {
        i == 0
            || (md.subsets == 2 && i == ANCHOR2[partition] as usize)
            || (md.subsets == 3 && (i == ANCHOR3A[partition] as usize || i == ANCHOR3B[partition] as usize))
    };
    let mut idx = [0i32; 16];
    let mut idx2 = [0i32; 16];
    for (i, v) in idx.iter_mut().enumerate() {
        *v = bits.read(if is_anchor(i) { md.index_bits - 1 } else { md.index_bits });
    }
    if md.index2_bits > 0 {
        for (i, v) in idx2.iter_mut().enumerate() {
            *v = bits.read(if i == 0 { md.index2_bits - 1 } else { md.index2_bits });
        }
    }
    for i in 0..16 {
        let s = subset(i);
        let (e0, e1) = (s * 2 * 4, (s * 2 + 1) * 4);
        let (ci, cib, ai, aib) = if md.index2_bits == 0 {
            (idx[i], md.index_bits, idx[i], md.index_bits)
        } else if index_selection == 0 {
            (idx[i], md.index_bits, idx2[i], md.index2_bits)
        } else {
            (idx2[i], md.index2_bits, idx[i], md.index_bits)
        };
        let (wc, wa) = (weight(cib, ci), weight(aib, ai));
        let mut r = lerp(ep[e0], ep[e1], wc);
        let mut g = lerp(ep[e0 + 1], ep[e1 + 1], wc);
        let mut b = lerp(ep[e0 + 2], ep[e1 + 2], wc);
        let mut a = if md.alpha_bits > 0 { lerp(ep[e0 + 3], ep[e1 + 3], wa) } else { 255 };
        match rotation {
            1 => std::mem::swap(&mut r, &mut a),
            2 => std::mem::swap(&mut g, &mut a),
            3 => std::mem::swap(&mut b, &mut a),
            _ => {}
        }
        rgba[i * 4] = r as u8;
        rgba[i * 4 + 1] = g as u8;
        rgba[i * 4 + 2] = b as u8;
        rgba[i * 4 + 3] = a as u8;
    }
}

fn expand(v: i32, bits: u32) -> i32 {
    if bits >= 8 { v } else { (v << (8 - bits)) | (v >> (2 * bits - 8)) }
}

fn weight(bits: u32, index: i32) -> i32 {
    match bits {
        2 => WEIGHTS2[index as usize],
        3 => WEIGHTS3[index as usize],
        _ => WEIGHTS4[index as usize],
    }
}

fn lerp(e0: i32, e1: i32, w: i32) -> i32 {
    ((64 - w) * e0 + w * e1 + 32) >> 6
}
