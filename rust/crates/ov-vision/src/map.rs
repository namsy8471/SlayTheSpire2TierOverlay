//! Port of Map/MapScrollEstimator.cs: how far the map screen is scrolled, from where the node icons are.

use std::fmt;

use crate::geometry::{RectF, RefTransform, ScreenFrame};

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MapMark {
    pub x: f32,
    pub y0: f32,
}

#[derive(Clone, Debug, PartialEq)]
pub struct MapFit {
    pub is_map: bool,
    pub scroll: f32,
    pub score: f32,
    pub visible: i32,
    pub matched: i32,
    pub runner: f32,
    pub paper: f32,
    pub detail: String,
}

impl MapFit {
    pub fn none(why: impl Into<String>) -> Self {
        MapFit { is_map: false, scroll: 0.0, score: 0.0, visible: 0, matched: 0, runner: 0.0, paper: 0.0, detail: why.into() }
    }
}

impl fmt::Display for MapFit {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "map={} scroll={:.1} score={:.1} runner={:.1} matched={}/{} paper={:.0} {}",
            if self.is_map { "True" } else { "False" },
            self.scroll,
            self.score,
            self.runner,
            self.matched,
            self.visible,
            self.paper,
            self.detail
        )
    }
}

const CELL: i32 = 2;
const GRID_W: usize = 1920 / CELL as usize;
const GRID_H: usize = 1080 / CELL as usize;
const SPOT_HALF: f32 = 14.0;
pub const MATCH_INK: f32 = 16.0;
pub const MIN_SCORE: f32 = 14.0;
pub const MIN_VISIBLE: i32 = 8;
pub const MIN_BOXES: i32 = 4;
pub const MIN_MATCHED_SHARE: f32 = 0.8;
const MAX_PEAKS: usize = 6;
const PAPER_INK: f32 = 22.0;
const ICON_INK: i32 = 30;
const MIN_SEED_INK: i32 = 20;
const UI_MASKS: [RectF; 5] = [
    RectF::new(0.0, 0.0, 1920.0, 90.0),
    RectF::new(0.0, 90.0, 360.0, 70.0),
    RectF::new(1555.0, 270.0, 365.0, 470.0),
    RectF::new(0.0, 715.0, 165.0, 130.0),
    RectF::new(40.0, 950.0, 240.0, 110.0),
];

#[derive(Clone, Debug)]
struct Candidate {
    scroll: f32,
    score: f32,
    visible: i32,
    matched: i32,
    boxes: i32,
    spread: f32,
    med_dx: f32,
    valid: bool,
}

impl Candidate {
    fn beats(&self, o: &Candidate) -> bool {
        if self.valid != o.valid {
            self.valid
        } else if self.boxes != o.boxes {
            self.boxes > o.boxes
        } else {
            self.score > o.score
        }
    }
}

pub struct MapScrollEstimator {
    lum_integral: Vec<i32>,
    ink_integral: Vec<i32>,
    ink: Vec<u8>,
    r: Vec<u8>,
    g: Vec<u8>,
    b: Vec<u8>,
}

impl Default for MapScrollEstimator {
    fn default() -> Self {
        let n = (GRID_W + 1) * (GRID_H + 1);
        let m = GRID_W * GRID_H;
        Self { lum_integral: vec![0; n], ink_integral: vec![0; n], ink: vec![0; m], r: vec![0; m], g: vec![0; m], b: vec![0; m] }
    }
}

pub fn is_masked(x: f32, y: f32) -> bool {
    UI_MASKS
        .iter()
        .any(|r| x >= r.x - SPOT_HALF && x <= r.right() + SPOT_HALF && y >= r.y - SPOT_HALF && y <= r.bottom() + SPOT_HALF)
}

fn box_mean(integral: &[i32], x0: f32, y0: f32, x1: f32, y1: f32) -> f32 {
    let c = CELL as f32;
    let gx0 = ((x0 / c).round_ties_even() as i32).clamp(0, GRID_W as i32);
    let gx1 = ((x1 / c).round_ties_even() as i32).clamp(0, GRID_W as i32);
    let gy0 = ((y0 / c).round_ties_even() as i32).clamp(0, GRID_H as i32);
    let gy1 = ((y1 / c).round_ties_even() as i32).clamp(0, GRID_H as i32);
    let n = (gx1 - gx0) * (gy1 - gy0);
    if n <= 0 {
        return 0.0;
    }
    let w = GRID_W as i32 + 1;
    let s = integral[(gy1 * w + gx1) as usize] - integral[(gy0 * w + gx1) as usize] - integral[(gy1 * w + gx0) as usize]
        + integral[(gy0 * w + gx0) as usize];
    s as f32 / n as f32
}

impl MapScrollEstimator {
    pub fn estimate(&mut self, frame: &ScreenFrame<'_>, marks: &[MapMark], min_scroll: f32, max_scroll: f32) -> MapFit {
        if marks.len() < 6 {
            return MapFit::none("too few nodes");
        }
        let (pr, pg, pb) = self.build_grid(frame);
        let paper = (pr as i32 * 77 + pg as i32 * 150 + pb as i32 * 29) as f32 / 256.0;
        let margin = box_mean(&self.lum_integral, 150.0, 200.0, 300.0, 700.0);
        if paper < 90.0 {
            return MapFit { paper, ..MapFit::none(format!("paper {paper:.0} too dark")) };
        }
        if paper - margin < 40.0 {
            return MapFit { paper, ..MapFit::none(format!("no dark margin (paper {paper:.0}, margin {margin:.0})")) };
        }
        let steps = ((max_scroll - min_scroll) / CELL as f32) as i32 + 1;
        let mut scores = vec![0f32; steps as usize];
        for i in 0..steps as usize {
            let (s, visible, _) = self.score(marks, min_scroll + (i as i32 * CELL) as f32);
            scores[i] = if visible < 5 { 0.0 } else { s };
        }
        let mut order: Vec<usize> = (0..steps as usize).filter(|&i| scores[i] > 0.0).collect();
        order.sort_by(|&a, &b| scores[b].partial_cmp(&scores[a]).unwrap_or(std::cmp::Ordering::Equal));
        let mut peaks: Vec<usize> = Vec::new();
        for i in order {
            if peaks.len() >= MAX_PEAKS {
                break;
            }
            if peaks.iter().all(|&p| (p as i32 - i as i32).abs() * CELL >= 60) {
                peaks.push(i);
            }
        }
        if peaks.is_empty() {
            return MapFit { paper, ..MapFit::none("no fit") };
        }
        let candidates: Vec<Candidate> = peaks.iter().map(|&i| self.verify(marks, &scores, i, min_scroll)).collect();
        let mut wi = 0;
        for (k, c) in candidates.iter().enumerate().skip(1) {
            if c.beats(&candidates[wi]) {
                wi = k;
            }
        }
        let win = &candidates[wi];
        let ambiguous = candidates.iter().enumerate().any(|(k, c)| k != wi && c.valid && c.boxes as f64 >= 0.6 * win.boxes as f64);
        let runner = candidates.iter().enumerate().filter(|(k, _)| *k != wi).map(|(_, c)| c.score).fold(None, |a: Option<f32>, s| {
            Some(a.map_or(s, |a| a.max(s)))
        });
        MapFit {
            is_map: win.valid && !ambiguous,
            scroll: win.scroll,
            score: win.score,
            visible: win.visible,
            matched: win.matched,
            runner: runner.unwrap_or(0.0),
            paper,
            detail: format!(
                "margin={margin:.0} paperRGB={pr},{pg},{pb} boxes={} spread={:.1} medDx={:.1} peaks={}{}",
                win.boxes,
                win.spread,
                win.med_dx,
                peaks.len(),
                if ambiguous { " AMBIGUOUS" } else { "" }
            ),
        }
    }

    fn verify(&self, marks: &[MapMark], scores: &[f32], i: usize, min_scroll: f32) -> Candidate {
        let mut scroll = min_scroll + (i as i32 * CELL) as f32;
        if i > 0 && i < scores.len() - 1 {
            let (a, b, c) = (scores[i - 1], scores[i], scores[i + 1]);
            let den = a - 2.0 * b + c;
            if den < 0.0 {
                scroll += (0.5 * (a - c) / den).clamp(-0.5, 0.5) * CELL as f32;
            }
        }
        let mut dys: Vec<f32> = Vec::new();
        let mut dxs: Vec<f32> = Vec::new();
        for m in marks {
            let (x, y) = (m.x, m.y0 + scroll);
            if !(50.0..=1030.0).contains(&y) || is_masked(x, y) {
                continue;
            }
            if let Some((cx, cy, _, _)) = self.icon_box(x, y) {
                dxs.push(cx - x);
                dys.push(cy - y);
            }
        }
        let (mut med_dx, mut spread) = (0f32, -1f32);
        if dys.len() >= 3 {
            dys.sort_by(|a, b| a.partial_cmp(b).unwrap());
            dxs.sort_by(|a, b| a.partial_cmp(b).unwrap());
            let med_dy = dys[dys.len() / 2];
            scroll += med_dy;
            med_dx = dxs[dxs.len() / 2];
            let mut dev: Vec<f32> = dys.iter().map(|d| (d - med_dy).abs()).collect();
            dev.sort_by(|a, b| a.partial_cmp(b).unwrap());
            spread = dev[dys.len() / 2];
        }
        let (score, vis, matched) = self.score(marks, scroll);
        let boxes = dys.len() as i32;
        let boxes_agree = boxes >= MIN_BOXES.max((vis + 1) / 2) && (0.0..=2.5).contains(&spread) && med_dx.abs() <= 3.0;
        let valid = boxes_agree && score >= MIN_SCORE && vis >= MIN_VISIBLE && matched as f32 >= MIN_MATCHED_SHARE * vis as f32;
        Candidate { scroll, score, visible: vis, matched, boxes, spread, med_dx, valid }
    }

    fn score(&self, marks: &[MapMark], scroll: f32) -> (f32, i32, i32) {
        let (mut visible, mut matched) = (0, 0);
        let mut sum = 0f32;
        for m in marks {
            let (x, y) = (m.x, m.y0 + scroll);
            if !(20.0..=1060.0).contains(&y) || is_masked(x, y) {
                continue;
            }
            visible += 1;
            if !self.on_paper(x, y) {
                continue;
            }
            let ink = box_mean(&self.ink_integral, x - SPOT_HALF, y - SPOT_HALF, x + SPOT_HALF, y + SPOT_HALF);
            if ink >= MATCH_INK {
                matched += 1;
            }
            sum += ink.min(60.0);
        }
        (if visible == 0 { 0.0 } else { sum / visible as f32 }, visible, matched)
    }

    pub fn local_peak(&self, m: MapMark, scroll: f32, radius: f32) -> (f32, f32, f32) {
        let (mut bx, mut by, mut bi) = (0f32, 0f32, -1f32);
        let mut dy = -radius;
        while dy <= radius {
            let mut dx = -radius;
            while dx <= radius {
                let (x, y) = (m.x + dx, m.y0 + scroll + dy);
                let ink = box_mean(&self.ink_integral, x - SPOT_HALF, y - SPOT_HALF, x + SPOT_HALF, y + SPOT_HALF);
                if ink > bi {
                    bx = dx;
                    by = dy;
                    bi = ink;
                }
                dx += CELL as f32;
            }
            dy += CELL as f32;
        }
        (bx, by, bi)
    }

    fn on_paper(&self, x: f32, y: f32) -> bool {
        const D: f32 = 40.0;
        const H: f32 = 5.0;
        let ii = &self.ink_integral;
        let mut paper = 0;
        for (sx, sy) in [(-D, -D), (D, -D), (-D, D), (D, D)] {
            if box_mean(ii, x + sx - H, y + sy - H, x + sx + H, y + sy + H) < PAPER_INK {
                paper += 1;
            }
        }
        paper >= 2
    }

    /// The node icon blob around a predicted centre: (centre x, centre y, width, height) in design px.
    pub fn icon_box(&self, x: f32, y: f32) -> Option<(f32, f32, f32, f32)> {
        const R: i32 = 24;
        let (gw, gh) = (GRID_W as i32, GRID_H as i32);
        let cx = (x / CELL as f32) as i32;
        let cy = (y / CELL as f32) as i32;
        let (x0, x1, y0, y1) = (0.max(cx - R), (gw - 1).min(cx + R), 0.max(cy - R), (gh - 1).min(cy + R));
        let (mut seed, mut seed_ink) = (-1i32, MIN_SEED_INK - 1);
        for gy in y0.max(cy - 6)..=y1.min(cy + 6) {
            for gx in x0.max(cx - 6)..=x1.min(cx + 6) {
                let v = self.ink[(gy * gw + gx) as usize] as i32;
                if v > seed_ink {
                    seed = gy * gw + gx;
                    seed_ink = v;
                }
            }
        }
        if seed < 0 {
            return None;
        }
        let thr = ((seed_ink as f32 * 0.4) as i32).clamp(16, ICON_INK);
        let (mut bx0, mut bx1, mut by0, mut by1, mut count) = (i32::MAX, -1, i32::MAX, -1, 0);
        let mut seen = std::collections::HashSet::new();
        seen.insert(seed);
        let mut stack = vec![seed];
        while let Some(i) = stack.pop() {
            let (gx, gy) = (i % gw, i / gw);
            count += 1;
            bx0 = bx0.min(gx);
            bx1 = bx1.max(gx);
            by0 = by0.min(gy);
            by1 = by1.max(gy);
            for (nx, ny) in [(gx - 1, gy), (gx + 1, gy), (gx, gy - 1), (gx, gy + 1)] {
                if nx < x0 || nx > x1 || ny < y0 || ny > y1 {
                    continue;
                }
                let n = ny * gw + nx;
                if self.ink[n as usize] as i32 >= thr && seen.insert(n) {
                    stack.push(n);
                }
            }
        }
        if count < 60 || bx0 == x0 || bx1 == x1 || by0 == y0 || by1 == y1 {
            return None;
        }
        Some((
            ((bx0 + bx1 + 1) * CELL) as f32 / 2.0,
            ((by0 + by1 + 1) * CELL) as f32 / 2.0,
            ((bx1 - bx0 + 1) * CELL) as f32,
            ((by1 - by0 + 1) * CELL) as f32,
        ))
    }

    fn build_grid(&mut self, frame: &ScreenFrame<'_>) -> (u8, u8, u8) {
        let t = RefTransform::for_frame(frame.width, frame.height);
        let px = frame.bgra;
        let xs: Vec<usize> =
            (0..GRID_W).map(|gx| ((t.to_pixel_x((gx as f32 + 0.5) * CELL as f32) as i32).clamp(0, frame.width - 1) * 4) as usize).collect();
        for gy in 0..GRID_H {
            let row = ((t.to_pixel_y((gy as f32 + 0.5) * CELL as f32) as i32).clamp(0, frame.height - 1) * frame.stride) as usize;
            let o = gy * GRID_W;
            for gx in 0..GRID_W {
                let p = row + xs[gx];
                self.b[o + gx] = px[p];
                self.g[o + gx] = px[p + 1];
                self.r[o + gx] = px[p + 2];
            }
        }
        let (pr, pg, pb) = self.paper_colour();
        let w = GRID_W + 1;
        for gy in 0..GRID_H {
            let (mut lum_acc, mut ink_acc) = (0i32, 0i32);
            let o = gy * GRID_W;
            let irow = (gy + 1) * w;
            let iprev = gy * w;
            for gx in 0..GRID_W {
                let (r, g, b) = (self.r[o + gx] as i32, self.g[o + gx] as i32, self.b[o + gx] as i32);
                lum_acc += (r * 77 + g * 150 + b * 29) >> 8;
                let ink = ((r - pr as i32).abs() + (g - pg as i32).abs() + (b - pb as i32).abs()) / 3;
                self.ink[o + gx] = ink as u8;
                ink_acc += ink;
                self.lum_integral[irow + gx + 1] = self.lum_integral[iprev + gx + 1] + lum_acc;
                self.ink_integral[irow + gx + 1] = self.ink_integral[iprev + gx + 1] + ink_acc;
            }
        }
        (pr, pg, pb)
    }

    fn paper_colour(&self) -> (u8, u8, u8) {
        let (mut hr, mut hg, mut hb) = ([0i32; 256], [0i32; 256], [0i32; 256]);
        let mut n = 0;
        let mut gy = 150 / CELL as usize;
        while gy < 1000 / CELL as usize {
            let mut gx = 420 / CELL as usize;
            while gx < 1500 / CELL as usize {
                let i = gy * GRID_W + gx;
                hr[self.r[i] as usize] += 1;
                hg[self.g[i] as usize] += 1;
                hb[self.b[i] as usize] += 1;
                n += 1;
                gx += 2;
            }
            gy += 2;
        }
        (median(&hr, n), median(&hg, n), median(&hb, n))
    }
}

fn median(hist: &[i32; 256], n: i32) -> u8 {
    let mut cum = 0;
    for (v, &h) in hist.iter().enumerate() {
        cum += h;
        if cum * 2 >= n {
            return v as u8;
        }
    }
    255
}
