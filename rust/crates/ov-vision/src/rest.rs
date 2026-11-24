//! Port of Rest/*.cs: the rest site option buttons and the card grid probe.

use std::fmt::Write;
use std::sync::OnceLock;

use crate::classifier;
use crate::geometry::{PixelRect, RectF, RefTransform, ScreenFrame};
use crate::rest_signatures;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum RestOption {
    Unknown,
    Heal,
    Smith,
    Mend,
    Lift,
    Dig,
    Cook,
    Clone,
    Hatch,
    Kindle,
    Toke,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RestButtonState {
    Normal,
    Hovered,
    Pressed,
}

#[derive(Clone, Debug, PartialEq)]
pub struct RestButton {
    pub option: RestOption,
    pub index: i32,
    pub rect: RectF,
    pub slot: RectF,
    pub state: RestButtonState,
    pub enabled: bool,
    pub residual: f32,
    pub margin: f32,
}

#[derive(Clone, Debug, PartialEq)]
pub struct RestSiteResult {
    pub choice_screen: bool,
    pub buttons: Vec<RestButton>,
    pub card_grid_open: bool,
    pub detail: String,
}

impl RestSiteResult {
    pub fn find(&self, option: RestOption) -> Option<&RestButton> {
        self.buttons.iter().find(|b| b.option == option)
    }
}

// ------------------------------------------------------------------ layout (RestSiteLayout.cs)

pub const SLOT_WIDTH: f32 = 246.786;
pub const SLOT_HEIGHT: f32 = 162.879;
pub const SEPARATION: f32 = 100.0;
pub const ROW_TOP: f32 = 255.0;
pub const ROW_CENTER_X: f32 = 960.5;
pub const PITCH: f32 = SLOT_WIDTH + SEPARATION;
pub const ICON_TEXTURE_WIDTH: f32 = 256.0;
pub const ICON_TEXTURE_HEIGHT: f32 = 169.0;
pub const PIVOT_X: f32 = 125.0;
pub const PIVOT_Y: f32 = 82.0;
pub const MAX_OPTIONS: i32 = 6;
pub const CELL_COLUMNS: usize = 4;
pub const CELL_ROWS: usize = 3;
pub const CELL_INSET: f32 = 0.10;
pub const SIGNATURE_LENGTH: usize = CELL_COLUMNS * CELL_ROWS * 3;

pub fn slot(index: i32, count: i32) -> RectF {
    assert!(count >= 1 && index >= 0 && index < count);
    let total = count as f32 * SLOT_WIDTH + (count - 1) as f32 * SEPARATION;
    let x = ROW_CENTER_X - total / 2.0 + index as f32 * PITCH;
    RectF::new(x, ROW_TOP, SLOT_WIDTH, SLOT_HEIGHT)
}

pub fn icon(slot: RectF, scale: f32) -> RectF {
    let box_h = slot.height - 8.0 - 3.0;
    let h = box_h;
    let w = box_h * ICON_TEXTURE_WIDTH / ICON_TEXTURE_HEIGHT;
    let x = slot.x + (slot.width - w) / 2.0;
    let y = slot.y + 8.0;
    let px = slot.x + PIVOT_X;
    let py = slot.y + PIVOT_Y;
    RectF::new(px + (x - px) * scale, py + (y - py) * scale, w * scale, h * scale)
}

pub fn cell(icon: RectF, col: usize, row: usize) -> RectF {
    let x0 = icon.x + icon.width * CELL_INSET;
    let y0 = icon.y + icon.height * CELL_INSET;
    let w = icon.width * (1.0 - 2.0 * CELL_INSET) / CELL_COLUMNS as f32;
    let h = icon.height * (1.0 - 2.0 * CELL_INSET) / CELL_ROWS as f32;
    RectF::new(x0 + col as f32 * w, y0 + row as f32 * h, w, h)
}

// ------------------------------------------------------------------ signatures (RestIconSignature.cs)

fn round_rect(r: RectF) -> PixelRect {
    let x0 = r.x.round_ties_even() as i32;
    let y0 = r.y.round_ties_even() as i32;
    let x1 = r.right().round_ties_even() as i32;
    let y1 = r.bottom().round_ties_even() as i32;
    PixelRect::new(x0, y0, x1 - x0, y1 - y0)
}

/// Signature of a whole icon image (the generator side).
pub fn signature_from_image(icon_img: &ScreenFrame<'_>, brightness: f32) -> Vec<f32> {
    let mut sig = vec![0f32; SIGNATURE_LENGTH];
    let whole = RectF::new(0.0, 0.0, icon_img.width as f32, icon_img.height as f32);
    let px = icon_img.bgra;
    for row in 0..CELL_ROWS {
        for col in 0..CELL_COLUMNS {
            let r = round_rect(cell(whole, col, row)).intersect(icon_img.width, icon_img.height);
            let (mut sr, mut sg, mut sb) = (0f64, 0f64, 0f64);
            let mut n = 0i64;
            for y in r.y..r.bottom() {
                for x in r.x..r.right() {
                    let p = (y * icon_img.stride + x * 4) as usize;
                    sb += 255f32.min(px[p] as f32 * brightness) as f64;
                    sg += 255f32.min(px[p + 1] as f32 * brightness) as f64;
                    sr += 255f32.min(px[p + 2] as f32 * brightness) as f64;
                    n += 1;
                }
            }
            let o = (row * CELL_COLUMNS + col) * 3;
            sig[o] = (sr / n as f64) as f32;
            sig[o + 1] = (sg / n as f64) as f32;
            sig[o + 2] = (sb / n as f64) as f32;
        }
    }
    sig
}

fn signature_from_frame(frame: &ScreenFrame<'_>, icon_pixels: RectF, scale: f32, sig: &mut [f32]) {
    let step = ((2.0 * scale).round_ties_even() as i32).max(1);
    let px = frame.bgra;
    for row in 0..CELL_ROWS {
        for col in 0..CELL_COLUMNS {
            let r = round_rect(cell(icon_pixels, col, row)).intersect(frame.width, frame.height);
            let (mut sr, mut sg, mut sb, mut n) = (0i64, 0i64, 0i64, 0i64);
            let mut y = r.y;
            while y < r.bottom() {
                let mut p = (y * frame.stride + r.x * 4) as usize;
                let mut x = r.x;
                while x < r.right() {
                    sb += px[p] as i64;
                    sg += px[p + 1] as i64;
                    sr += px[p + 2] as i64;
                    n += 1;
                    x += step;
                    p += 4 * step as usize;
                }
                y += step;
            }
            let o = (row * CELL_COLUMNS + col) * 3;
            if n == 0 {
                sig[o] = -1000.0;
                sig[o + 1] = -1000.0;
                sig[o + 2] = -1000.0;
                continue;
            }
            sig[o] = sr as f32 / n as f32;
            sig[o + 1] = sg as f32 / n as f32;
            sig[o + 2] = sb as f32 / n as f32;
        }
    }
}

pub fn disabled(normal: &[f32]) -> Vec<f32> {
    let mut sig = vec![0f32; normal.len()];
    for i in (0..normal.len()).step_by(3) {
        let y = 0.6 * (0.2989 * normal[i] + 0.587 * normal[i + 1] + 0.114 * normal[i + 2]);
        sig[i] = y;
        sig[i + 1] = y;
        sig[i + 2] = y;
    }
    sig
}

// ------------------------------------------------------------------ detector (RestSiteDetector.cs)

pub const MAX_RESIDUAL: f32 = 8.0;
pub const MIN_GAIN: f32 = 0.75;
pub const MAX_GAIN: f32 = 1.3;
const DISABLED_MIN_GAIN: f32 = 0.75;
const DISABLED_MAX_GAIN: f32 = 1.3;
const SCALES: [f32; 3] = [1.0, 1.1, 0.9];

#[derive(Clone, Debug)]
struct SlotMatch {
    option: RestOption,
    residual: f32,
    margin: f32,
    scale: f32,
    gain: f32,
    enabled: bool,
}

struct Signature {
    option: RestOption,
    normal: Vec<f32>,
    bright: Vec<f32>,
    grey: Vec<f32>,
}

fn signatures() -> &'static [Signature] {
    static S: OnceLock<Vec<Signature>> = OnceLock::new();
    S.get_or_init(|| {
        rest_signatures::ALL
            .iter()
            .map(|(o, n, b)| {
                let normal: Vec<f32> = n.iter().map(|&x| x as f32).collect();
                Signature { option: *o, grey: disabled(&normal), normal, bright: b.iter().map(|&x| x as f32).collect() }
            })
            .collect()
    })
}

pub fn detect(frame: &ScreenFrame<'_>) -> RestSiteResult {
    let t = RefTransform::for_frame(frame.width, frame.height);
    let (grid, grid_detail) = card_grid_open(frame);
    let mut cache: std::collections::HashMap<i32, (SlotMatch, SlotMatch)> = std::collections::HashMap::new();
    let mut order: Vec<i32> = Vec::new();
    let mut detail = grid_detail;
    for n in (1..=MAX_OPTIONS).rev() {
        let mut matches: Vec<Option<SlotMatch>> = vec![None; n as usize];
        let (mut ok, mut any_colour) = (true, false);
        let mut idx: Vec<i32> = (0..n).collect();
        idx.sort_by_key(|&i| std::cmp::Reverse((2 * i - (n - 1)).abs())); // stable, like OrderByDescending
        for i in idx {
            let k = 2 * i - (n - 1);
            if !cache.contains_key(&k) {
                cache.insert(k, match_slot(frame, &t, slot(i, n)));
                order.push(k);
            }
            let (enabled, grey) = &cache[&k];
            if enabled.residual <= MAX_RESIDUAL {
                matches[i as usize] = Some(enabled.clone());
                any_colour = true;
            } else if grey.residual <= MAX_RESIDUAL {
                matches[i as usize] = Some(grey.clone());
            } else {
                ok = false;
                break;
            }
        }
        if !ok || !any_colour {
            continue;
        }
        let mut buttons = Vec::new();
        for i in 0..n {
            let m = matches[i as usize].as_ref().unwrap();
            let s = slot(i, n);
            let state = if m.scale > 1.05 {
                RestButtonState::Hovered
            } else if m.scale < 0.95 {
                RestButtonState::Pressed
            } else {
                RestButtonState::Normal
            };
            buttons.push(RestButton {
                option: m.option,
                index: i,
                rect: icon(s, m.scale),
                slot: s,
                state,
                enabled: m.enabled,
                residual: m.residual,
                margin: m.margin,
            });
            let _ = write!(detail, " {:?}:r{:.1}/m{:.0}/g{:.2}/s{:.1}", m.option, m.residual, m.margin, m.gain, m.scale);
        }
        return RestSiteResult { choice_screen: true, buttons, card_grid_open: grid, detail: format!("n={n}{detail}") };
    }
    let closest = order.iter().map(|k| cache[k].0.residual).fold(f32::INFINITY, f32::min);
    RestSiteResult { choice_screen: false, buttons: vec![], card_grid_open: grid, detail: format!("none closest={closest:.1} {detail}") }
}

fn match_slot(frame: &ScreenFrame<'_>, t: &RefTransform, s: RectF) -> (SlotMatch, SlotMatch) {
    let mut obs = [0f32; SIGNATURE_LENGTH];
    let mut best_enabled: Option<SlotMatch> = None;
    let mut best_grey: Option<SlotMatch> = None;
    for scale in SCALES {
        signature_from_frame(frame, t.to_pixels(icon(s, scale)), t.scale, &mut obs);
        consider(&obs, scale, true, &mut best_enabled);
        consider(&obs, scale, false, &mut best_grey);
    }
    (best_enabled.unwrap(), best_grey.unwrap())
}

fn consider(obs: &[f32], scale: f32, enabled: bool, best: &mut Option<SlotMatch>) {
    let (mut best_r, mut second_r, mut best_gain) = (f32::INFINITY, f32::INFINITY, 0f32);
    let mut best_option = RestOption::Unknown;
    for s in signatures() {
        let (r, g) = if enabled {
            let (r1, g1) = fit(obs, &s.normal, MIN_GAIN, MAX_GAIN);
            let (r2, g2) = fit(obs, &s.bright, MIN_GAIN, MAX_GAIN);
            if r1 <= r2 { (r1, g1) } else { (r2, g2 * 1.2) }
        } else {
            fit(obs, &s.grey, DISABLED_MIN_GAIN, DISABLED_MAX_GAIN)
        };
        if r < best_r {
            second_r = best_r;
            best_r = r;
            best_option = s.option;
            best_gain = g;
        } else if r < second_r {
            second_r = r;
        }
    }
    if best.as_ref().is_none_or(|b| best_r < b.residual) {
        *best = Some(SlotMatch { option: best_option, residual: best_r, margin: second_r - best_r, scale, gain: best_gain, enabled });
    }
}

pub fn fit(obs: &[f32], sig: &[f32], min: f32, max: f32) -> (f32, f32) {
    let (mut dot, mut norm) = (0f32, 0f32);
    for i in 0..obs.len() {
        dot += obs[i] * sig[i];
        norm += sig[i] * sig[i];
    }
    let g = if norm > 0.0 { (dot / norm).clamp(min, max) } else { min };
    let mut sum = 0f32;
    for i in 0..obs.len() {
        let d = obs[i] - g * sig[i];
        sum += d * d;
    }
    ((sum / obs.len() as f32).sqrt(), g)
}

// ------------------------------------------------------------------ card grid probe (CardGridProbe.cs)

const RING: [RectF; 4] = [
    RectF::new(33.0, 1020.0, 24.0, 4.0),
    RectF::new(33.0, 1048.0, 24.0, 4.0),
    RectF::new(29.0, 1026.0, 4.0, 20.0),
    RectF::new(57.0, 1026.0, 4.0, 20.0),
];
const GRID_INNER: RectF = RectF::new(38.0, 1029.0, 14.0, 14.0);
const LEFT_OF_BOX: RectF = RectF::new(4.0, 1022.0, 16.0, 26.0);
const LEFT_MARGIN: RectF = RectF::new(165.0, 150.0, 70.0, 550.0);
const RIGHT_MARGIN: RectF = RectF::new(1700.0, 150.0, 90.0, 800.0);
pub const RING_MIN_BRIGHTNESS: f32 = 120.0;
pub const RING_MAX_SPREAD: f32 = 45.0;
pub const MIN_RING_CONTRAST: f32 = 50.0;
pub const MAX_LEFT_OF_BOX: f32 = 40.0;
pub const MAX_LEFT_MARGIN: f32 = 25.0;
pub const MAX_RIGHT_MARGIN: f32 = 35.0;

/// Is the card grid (with its tickbox at the bottom left) open? Returns the answer and a detail string.
pub fn card_grid_open(frame: &ScreenFrame<'_>) -> (bool, String) {
    let t = RefTransform::for_frame(frame.width, frame.height);
    let mut sb = String::from("grid[");
    let mut ok = true;
    let mut ring_min = f32::MAX;
    for r in RING {
        let (cr, cg, cb) = classifier::mean_rgb(frame, t.to_pixel_rect(r));
        let v = (cr + cg + cb) / 3.0;
        let spread = cr.max(cg.max(cb)) - cr.min(cg.min(cb));
        ring_min = ring_min.min(v);
        ok &= v >= RING_MIN_BRIGHTNESS && spread <= RING_MAX_SPREAD;
    }
    let inner = brightness(frame, &t, GRID_INNER);
    ok &= ring_min - inner >= MIN_RING_CONTRAST;
    let _ = write!(sb, "ring={ring_min:.0} in={inner:.0}");
    if ok {
        let lb = brightness(frame, &t, LEFT_OF_BOX);
        let lm = brightness(frame, &t, LEFT_MARGIN);
        let rm = brightness(frame, &t, RIGHT_MARGIN);
        ok &= lb <= MAX_LEFT_OF_BOX && lm <= MAX_LEFT_MARGIN && rm <= MAX_RIGHT_MARGIN;
        let _ = write!(sb, " lb={lb:.0} lm={lm:.0} rm={rm:.0}");
    }
    sb.push_str(if ok { " -> open]" } else { " -> no]" });
    (ok, sb)
}

fn brightness(frame: &ScreenFrame<'_>, t: &RefTransform, b: RectF) -> f32 {
    let (r, g, bl) = classifier::mean_rgb(frame, t.to_pixel_rect(b));
    (r + g + bl) / 3.0
}
