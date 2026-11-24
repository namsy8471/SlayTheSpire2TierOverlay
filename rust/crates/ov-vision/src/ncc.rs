//! Port of Ncc.cs: normalised cross-correlation of fixed-size RGB thumbnails.

use crate::geometry::RectF;
use crate::integral::IntegralImage;

pub const FULL_W: i32 = 48;
pub const FULL_H: i32 = 36;
pub const FULL_LENGTH: usize = (FULL_W * FULL_H * 3) as usize;
pub const COARSE_W: i32 = 16;
pub const COARSE_H: i32 = 12;
pub const COARSE_LENGTH: usize = (COARSE_W * COARSE_H * 3) as usize;
/// Part of the art box that is sampled (the frame's border and the type banner are left out).
pub const INNER: RectF = RectF::new(0.08, 0.16, 0.84, 0.74);
/// RMS contrast (grey levels) below which a patch counts as flat.
pub const MIN_CONTRAST: f32 = 3.0;

/// Subtracts the mean and scales to unit length, so NCC becomes a dot product. False (and zeroed) for a flat patch.
pub fn normalise(v: &mut [f32]) -> bool {
    let mut sum = 0f64;
    for &x in v.iter() {
        sum += x as f64;
    }
    let mean = (sum / v.len() as f64) as f32;
    let mut len2 = 0f64;
    for x in v.iter_mut() {
        *x -= mean;
        len2 += (*x * *x) as f64;
    }
    if len2 < (v.len() as f32 * MIN_CONTRAST * MIN_CONTRAST) as f64 {
        v.fill(0.0);
        return false;
    }
    let inv = (1.0 / len2.sqrt()) as f32;
    for x in v.iter_mut() {
        *x *= inv;
    }
    true
}

/// Dot product in the summation order of the C# `Vector<float>` code on an AVX2 machine (8 lanes, four
/// accumulators, pairwise horizontal sum), so scores agree with the C# app to the last bit there.
pub fn dot(a: &[f32], b: &[f32]) -> f32 {
    const L: usize = 8;
    let n = a.len().min(b.len());
    let vecs = n / L;
    let mut s = [[0f32; L]; 4];
    let mut i = 0;
    while i + 4 <= vecs {
        for (k, acc) in s.iter_mut().enumerate() {
            let o = (i + k) * L;
            for l in 0..L {
                acc[l] += a[o + l] * b[o + l];
            }
        }
        i += 4;
    }
    while i < vecs {
        let o = i * L;
        for l in 0..L {
            s[0][l] += a[o + l] * b[o + l];
        }
        i += 1;
    }
    let mut t = [0f32; L];
    for l in 0..L {
        t[l] = s[0][l] + s[1][l] + s[2][l] + s[3][l];
    }
    let mut sum = hsum8(&t);
    for j in vecs * L..n {
        sum += a[j] * b[j];
    }
    sum
}

/// Horizontal sum of 8 lanes as `Vector256.Sum` does it on x86 (two horizontal adds, then low + high half).
#[inline]
pub fn hsum8(t: &[f32; 8]) -> f32 {
    ((t[0] + t[1]) + (t[2] + t[3])) + ((t[4] + t[5]) + (t[6] + t[7]))
}

/// Samples the inner part of an art box (frame pixels) into a `w` x `h` thumbnail and normalises it.
pub fn extract(img: &IntegralImage, art_box_pixels: RectF, w: i32, h: i32, dst: &mut [f32]) -> bool {
    let inner = art_box_pixels.fraction(INNER.x, INNER.y, INNER.width, INNER.height);
    img.sample_grid(inner, w, h, dst);
    normalise(dst)
}
