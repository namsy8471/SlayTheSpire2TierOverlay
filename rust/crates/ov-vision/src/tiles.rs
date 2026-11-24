//! Port of TileMatcher.cs: per-tile statistics of the 48x36 thumbnail and the occlusion-robust partial match.

use crate::artdb::CardArtDatabase;
use crate::ncc::{self, FULL_H, FULL_LENGTH, FULL_W};
use crate::results::ArtMatch;

pub struct TileGrid {
    pub tiles_x: usize,
    pub tiles_y: usize,
    pub count: usize,
    pub cells_x: usize,
    pub cells_y: usize,
    pub segment_length: usize,
    pub tile_length: usize,
    tile_of_segment: Vec<usize>,
    pub is_rows: bool,
}

impl TileGrid {
    pub fn new(tiles_x: usize, tiles_y: usize) -> Self {
        let count = tiles_x * tiles_y;
        let cells_x = FULL_W as usize / tiles_x;
        let cells_y = FULL_H as usize / tiles_y;
        let segment_length = cells_x * 3;
        assert!(count <= 64 && segment_length.is_multiple_of(8), "Grid must have <= 64 tiles and segments of whole 8-float vectors.");
        let tile_of_segment = (0..FULL_H as usize * tiles_x).map(|seg| (seg / tiles_x) / cells_y * tiles_x + seg % tiles_x).collect();
        TileGrid {
            tiles_x,
            tiles_y,
            count,
            cells_x,
            cells_y,
            segment_length,
            tile_length: cells_x * cells_y * 3,
            tile_of_segment,
            is_rows: tiles_x == 1 && tiles_y == FULL_H as usize,
        }
    }

    pub fn blocks() -> Self {
        Self::new(6, 4)
    }

    pub fn rows() -> Self {
        Self::new(1, FULL_H as usize)
    }

    pub fn sums_of(&self, v: &[f32], sum: &mut [f32], sq: &mut [f32]) {
        sum[..self.count].fill(0.0);
        sq[..self.count].fill(0.0);
        let mut i = 0;
        let mut seg = 0;
        while i < FULL_LENGTH {
            let tile = self.tile_of_segment[seg];
            let (mut s, mut q) = (0f32, 0f32);
            for k in 0..self.segment_length {
                let x = v[i + k];
                s += x;
                q += x * x;
            }
            sum[tile] += s;
            sq[tile] += q;
            seg += 1;
            i += self.segment_length;
        }
    }

    pub fn texture_energy(&self, v: &[f32], energy: &mut [f32]) {
        let mut s = vec![0f32; self.count * 3];
        let mut q = vec![0f32; self.count * 3];
        let mut i = 0;
        let mut seg = 0;
        while i < FULL_LENGTH {
            let tile = self.tile_of_segment[seg] * 3;
            let mut k = 0;
            while k < self.segment_length {
                for c in 0..3 {
                    let x = v[i + k + c];
                    s[tile + c] += x;
                    q[tile + c] += x * x;
                }
                k += 3;
            }
            seg += 1;
            i += self.segment_length;
        }
        let n = (self.cells_x * self.cells_y) as i32;
        for t in 0..self.count {
            energy[t] = var(s[3 * t], q[3 * t], n) + var(s[3 * t + 1], q[3 * t + 1], n) + var(s[3 * t + 2], q[3 * t + 2], n);
        }
    }

    /// Σxy per tile, in the lane order of the C# Vector256 code.
    pub fn dots(&self, a: &[f32], b: &[f32], dots: &mut [f32]) {
        let mut acc = vec![[0f32; 8]; self.count];
        let mut i = 0;
        let mut seg = 0;
        while i < FULL_LENGTH {
            let mut p = [0f32; 8];
            let mut k = 0;
            while k < self.segment_length {
                for l in 0..8 {
                    p[l] += a[i + k + l] * b[i + k + l];
                }
                k += 8;
            }
            let t = &mut acc[self.tile_of_segment[seg]];
            for l in 0..8 {
                t[l] += p[l];
            }
            seg += 1;
            i += self.segment_length;
        }
        for t in 0..self.count {
            dots[t] = ncc::hsum8(&acc[t]);
        }
    }
}

/// Variance term Σx² - (Σx)²/n.
pub fn var(sum: f32, sq: f32, n: i32) -> f32 {
    0f32.max(sq - sum * sum / n as f32)
}

#[derive(Clone, Debug, PartialEq)]
pub struct PartialStage {
    pub agree: f32,
    pub flat_energy: f32,
    pub min_tiles: i32,
    pub accept_score: f32,
    pub accept_margin: f32,
    pub whole_top_k: i32,
    pub hinted_pool_only: bool,
    pub free_margin: f32,
}

pub struct TileMatcher {
    sum: Vec<f32>,
    sq: Vec<f32>,
    pub grid: TileGrid,
}

pub struct BestTiles {
    pub entry: usize,
    pub mask: u64,
    pub visible: i32,
}

impl TileMatcher {
    pub fn new(db: &CardArtDatabase, grid: TileGrid) -> Self {
        let t = grid.count;
        let mut sum = vec![0f32; db.count() * t];
        let mut sq = vec![0f32; db.count() * t];
        for e in 0..db.count() {
            grid.sums_of(db.full_vector(e), &mut sum[e * t..(e + 1) * t], &mut sq[e * t..(e + 1) * t]);
        }
        TileMatcher { sum, sq, grid }
    }

    pub fn best(
        &self,
        db: &CardArtDatabase,
        query: &[f32],
        allowed: &dyn Fn(usize) -> bool,
        o: &PartialStage,
        ranking: &mut Vec<ArtMatch>,
    ) -> Option<BestTiles> {
        let tn = self.grid.count;
        let n = self.grid.tile_length as i32;
        let mut qs = vec![0f32; tn];
        let mut qq = vec![0f32; tn];
        let mut q_var = vec![0f32; tn];
        let mut tex = vec![0f32; tn];
        self.grid.sums_of(query, &mut qs, &mut qq);
        self.grid.texture_energy(query, &mut tex);
        let flat = o.flat_energy / tn as f32;
        for t in 0..tn {
            q_var[t] = if tex[t] < flat { 0.0 } else { var(qs[t], qq[t], n) };
        }
        let count = db.count();
        let mut dots = vec![0f32; count * tn];
        let mut best_entry: Option<usize> = None;
        let mut best_visible = 0;
        let mut best_mask = 0u64;
        let mut best_score = f32::NEG_INFINITY;
        for e in 0..count {
            if !allowed(e) {
                continue;
            }
            let d = &mut dots[e * tn..(e + 1) * tn];
            self.grid.dots(query, db.full_vector(e), d);
            let es = &self.sum[e * tn..(e + 1) * tn];
            let eq = &self.sq[e * tn..(e + 1) * tn];
            let mut mask = 0u64;
            let mut visible = 0;
            for t in 0..tn {
                let ev = var(es[t], eq[t], n);
                if q_var[t] < flat || ev < flat {
                    continue;
                }
                let ncc = (d[t] - qs[t] * es[t] / n as f32) / (q_var[t] * ev).sqrt();
                if ncc < o.agree {
                    continue;
                }
                mask |= 1u64 << t;
                visible += 1;
            }
            if visible < o.min_tiles {
                continue;
            }
            let score = self.masked_ncc(e, mask, d, &qs, &qq);
            if score > best_score {
                best_score = score;
                best_entry = Some(e);
                best_mask = mask;
                best_visible = visible;
            }
        }
        ranking.clear();
        let entry = best_entry?;
        let mut all: Vec<ArtMatch> = Vec::new();
        for e in 0..count {
            if !allowed(e) {
                continue;
            }
            all.push(ArtMatch {
                entry_index: e,
                card_id: db.entries[e].card_id.clone(),
                score: self.masked_ncc(e, best_mask, &dots[e * tn..(e + 1) * tn], &qs, &qq),
            });
        }
        all.sort_by(|a, b| b.score.partial_cmp(&a.score).unwrap_or(std::cmp::Ordering::Equal));
        let mut seen = std::collections::HashSet::new();
        for m in all {
            if ranking.len() >= 8 {
                break;
            }
            if seen.insert(m.card_id.clone()) {
                ranking.push(m);
            }
        }
        Some(BestTiles { entry, mask: best_mask, visible: best_visible })
    }

    pub fn agreement(&self, db: &CardArtDatabase, query: &[f32], entries: &[usize], o: &PartialStage) -> f32 {
        let tn = self.grid.count;
        let n = self.grid.tile_length as i32;
        let mut qs = vec![0f32; tn];
        let mut qq = vec![0f32; tn];
        let mut d = vec![0f32; tn];
        let mut tex = vec![0f32; tn];
        self.grid.sums_of(query, &mut qs, &mut qq);
        self.grid.texture_energy(query, &mut tex);
        let flat = o.flat_energy / tn as f32;
        let mut best = 0f32;
        for &e in entries {
            self.grid.dots(query, db.full_vector(e), &mut d);
            let es = &self.sum[e * tn..(e + 1) * tn];
            let eq = &self.sq[e * tn..(e + 1) * tn];
            let mut sum = 0f32;
            for t in 0..tn {
                let qv = var(qs[t], qq[t], n);
                let ev = var(es[t], eq[t], n);
                if qv < flat || ev < flat || tex[t] < flat {
                    continue;
                }
                let ncc = (d[t] - qs[t] * es[t] / n as f32) / (qv * ev).sqrt();
                if ncc >= o.agree {
                    sum += ncc;
                }
            }
            best = best.max(sum);
        }
        best
    }

    pub fn fit(&self, db: &CardArtDatabase, query: &[f32], entry: usize, o: &PartialStage) -> (i32, f32) {
        let tn = self.grid.count;
        let n = self.grid.tile_length as i32;
        let mut qs = vec![0f32; tn];
        let mut qq = vec![0f32; tn];
        let mut d = vec![0f32; tn];
        let mut tex = vec![0f32; tn];
        self.grid.sums_of(query, &mut qs, &mut qq);
        self.grid.texture_energy(query, &mut tex);
        self.grid.dots(query, db.full_vector(entry), &mut d);
        let es = &self.sum[entry * tn..(entry + 1) * tn];
        let eq = &self.sq[entry * tn..(entry + 1) * tn];
        let flat = o.flat_energy / tn as f32;
        let mut mask = 0u64;
        let mut visible = 0;
        for t in 0..tn {
            let qv = var(qs[t], qq[t], n);
            let ev = var(es[t], eq[t], n);
            if qv < flat || ev < flat || tex[t] < flat {
                continue;
            }
            if (d[t] - qs[t] * es[t] / n as f32) / (qv * ev).sqrt() < o.agree {
                continue;
            }
            mask |= 1u64 << t;
            visible += 1;
        }
        (visible, if visible == 0 { 0.0 } else { self.masked_ncc(entry, mask, &d, &qs, &qq) })
    }

    fn masked_ncc(&self, e: usize, mask: u64, dots: &[f32], qs: &[f32], qq: &[f32]) -> f32 {
        let tn = self.grid.count;
        let es = &self.sum[e * tn..(e + 1) * tn];
        let eq = &self.sq[e * tn..(e + 1) * tn];
        let (mut sxy, mut sx, mut sxx, mut sy, mut syy) = (0f32, 0f32, 0f32, 0f32, 0f32);
        let mut tiles = 0;
        for t in 0..tn {
            if mask & (1u64 << t) == 0 {
                continue;
            }
            tiles += 1;
            sxy += dots[t];
            sx += qs[t];
            sxx += qq[t];
            sy += es[t];
            syy += eq[t];
        }
        let n = tiles * self.grid.tile_length as i32;
        if n == 0 {
            return 0.0;
        }
        let vx = var(sx, sxx, n);
        let vy = var(sy, syy, n);
        if vx <= 0.0 || vy <= 0.0 { 0.0 } else { (sxy - sx * sy / n as f32) / (vx * vy).sqrt() }
    }
}
