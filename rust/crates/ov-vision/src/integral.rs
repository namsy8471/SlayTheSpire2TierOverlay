//! Port of IntegralImage.cs: a summed-area table of the RGB values of one frame region, at a cell size of
//! `step` x `step` source pixels, so any box mean costs four lookups.

use rayon::prelude::*;

use crate::geometry::{PixelRect, RectF, ScreenFrame};

pub struct IntegralImage {
    sat: Vec<i32>,
    row_len: usize,
    pub width: i32,
    pub height: i32,
    pub step: i32,
    pub origin_x: i32,
    pub origin_y: i32,
}

/// Receives the grid rows of [`IntegralImage::sample_grid_rows`] one at a time (RGB per cell).
pub trait RowSink {
    fn row(&mut self, rgb: &[f32]);
}

struct NoRows;
impl RowSink for NoRows {
    fn row(&mut self, _: &[f32]) {}
}

impl IntegralImage {
    pub fn build(frame: &ScreenFrame<'_>, region: PixelRect, step: i32) -> IntegralImage {
        let step = step.max(1);
        let region = region.intersect(frame.width, frame.height);
        assert!(
            (region.width as i64) * (region.height as i64) * 255 <= i32::MAX as i64,
            "Region {region} is too large for an int summed-area table."
        );
        let w = region.width / step;
        let h = region.height / step;
        let row_len = ((w + 1) * 3) as usize;
        let length = (row_len * (h as usize + 1)).max(1);
        let mut sat = vec![0i32; length];
        let stride = frame.stride as usize;
        let px = frame.bgra;
        let row_pass = |cy: usize, dst: &mut [i32]| {
            dst[0] = 0;
            dst[1] = 0;
            dst[2] = 0;
            let (mut r, mut g, mut b) = (0i32, 0i32, 0i32);
            let sy0 = region.y as usize + cy * step as usize;
            for cx in 0..w as usize {
                let sx0 = region.x as usize + cx * step as usize;
                for dy in 0..step as usize {
                    let mut p = (sy0 + dy) * stride + sx0 * 4;
                    for _ in 0..step {
                        b += px[p] as i32;
                        g += px[p + 1] as i32;
                        r += px[p + 2] as i32;
                        p += 4;
                    }
                }
                let o = (cx + 1) * 3;
                dst[o] = r;
                dst[o + 1] = g;
                dst[o + 2] = b;
            }
        };
        if h > 0 {
            let rows = &mut sat[row_len..row_len * (h as usize + 1)];
            if (w as i64) * (h as i64) >= 100_000 {
                rows.par_chunks_mut(row_len).enumerate().for_each(|(cy, dst)| row_pass(cy, dst));
            } else {
                for (cy, dst) in rows.chunks_mut(row_len).enumerate() {
                    row_pass(cy, dst);
                }
            }
        }
        for y in 2..=h as usize {
            let (above, cur) = sat.split_at_mut(y * row_len);
            let above = &above[(y - 1) * row_len..];
            for (c, a) in cur[..row_len].iter_mut().zip(above.iter()) {
                *c += *a;
            }
        }
        IntegralImage { sat, row_len, width: w, height: h, step, origin_x: region.x, origin_y: region.y }
    }

    pub fn to_cell_x(&self, frame_x: f64) -> f64 {
        (frame_x - self.origin_x as f64) / self.step as f64
    }
    pub fn to_cell_y(&self, frame_y: f64) -> f64 {
        (frame_y - self.origin_y as f64) / self.step as f64
    }

    #[inline]
    fn sample(&self, x: f64, y: f64) -> (f64, f64, f64) {
        let s = &self.sat;
        let x = x.clamp(0.0, self.width as f64);
        let y = y.clamp(0.0, self.height as f64);
        let x0 = (x as i32).min((self.width - 1).max(0));
        let y0 = (y as i32).min((self.height - 1).max(0));
        let fx = x - x0 as f64;
        let fy = y - y0 as f64;
        let i00 = y0 as usize * self.row_len + x0 as usize * 3;
        let i01 = i00 + 3;
        let i10 = i00 + self.row_len;
        let i11 = i10 + 3;
        let w00 = (1.0 - fx) * (1.0 - fy);
        let w01 = fx * (1.0 - fy);
        let w10 = (1.0 - fx) * fy;
        let w11 = fx * fy;
        let v = |k: usize| w00 * s[i00 + k] as f64 + w01 * s[i01 + k] as f64 + w10 * s[i10 + k] as f64 + w11 * s[i11 + k] as f64;
        (v(0), v(1), v(2))
    }

    /// Mean RGB (per source pixel) of every cell of a `gw` x `gh` grid over `frame_box`, row-major, into `dst`.
    pub fn sample_grid(&self, frame_box: RectF, gw: i32, gh: i32, dst: &mut [f32]) {
        self.sample_rows(frame_box, gw, gh, dst, true, &mut NoRows);
    }

    /// Like [`Self::sample_grid`], one grid row at a time into `row` (gw * 3 floats), handed to `sink`.
    pub fn sample_grid_rows<S: RowSink>(&self, frame_box: RectF, gw: i32, gh: i32, row: &mut [f32], sink: &mut S) {
        self.sample_rows(frame_box, gw, gh, row, false, sink);
    }

    fn sample_rows<S: RowSink>(&self, frame_box: RectF, gw: i32, gh: i32, dst: &mut [f32], whole: bool, sink: &mut S) {
        let gx = (gw + 1) as usize;
        let row_len = gx * 3;
        let mut above = vec![0f64; row_len];
        let mut below = vec![0f64; row_len];
        let x0 = self.to_cell_x(frame_box.x as f64);
        let y0 = self.to_cell_y(frame_box.y as f64);
        // C#: frameBox.Width / Step / gw is float arithmetic, widened afterwards.
        let cw = (frame_box.width / self.step as f32 / gw as f32) as f64;
        let ch = (frame_box.height / self.step as f32 / gh as f32) as f64;
        let inv = if cw * ch > 1e-9 { 1.0 / (cw * ch * self.step as f64 * self.step as f64) } else { 0.0 };
        self.sample_grid_row(x0, cw, y0, gx, &mut above);
        let n = gw as usize * 3;
        for j in 0..gh as usize {
            self.sample_grid_row(x0, cw, y0 + (j + 1) as f64 * ch, gx, &mut below);
            let o = if whole { &mut dst[j * n..(j + 1) * n] } else { &mut dst[..n] };
            for i in 0..gw as usize {
                let a = i * 3;
                let b = a + 3;
                for k in 0..3 {
                    o[a + k] = ((below[b + k] - above[b + k] - below[a + k] + above[a + k]) * inv) as f32;
                }
            }
            sink.row(o);
            std::mem::swap(&mut above, &mut below);
        }
    }

    fn sample_grid_row(&self, x0: f64, cw: f64, y: f64, gx: usize, dst: &mut [f64]) {
        for i in 0..gx {
            let (r, g, b) = self.sample(x0 + i as f64 * cw, y);
            dst[i * 3] = r;
            dst[i * 3 + 1] = g;
            dst[i * 3 + 2] = b;
        }
    }

    pub fn mean(&self, frame_box: RectF) -> (f32, f32, f32) {
        let mut c = [0f32; 3];
        self.sample_grid(frame_box, 1, 1, &mut c);
        (c[0], c[1], c[2])
    }
}
