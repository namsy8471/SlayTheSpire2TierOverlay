//! Port of Events/EventButtonDetector.cs and Events/TextInk.cs: the event option column and its text ink.

use std::sync::OnceLock;

use crate::geometry::{PixelRect, RectF, RefTransform, ScreenFrame};

// ------------------------------------------------------------------ text ink (TextInk.cs)

pub const LIGHT: u8 = 150;

#[derive(Clone, Debug)]
pub struct TextInk {
    ink: Vec<bool>,
    xs: Vec<i32>,
    ys: Vec<i32>,
    grown: OnceLock<Vec<bool>>,
    pub width: i32,
    pub height: i32,
    pub pivot_x: f32,
    pub pivot_y: f32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct InkMatch {
    pub distance: f32,
    pub lost: f32,
    pub gained: f32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TextVerdict {
    Same,
    Unsure,
    Changed,
}

pub const SAME_MAX: f32 = 0.02;
pub const CHANGED_MIN: f32 = 0.04;
pub const INSIDE_MAX: f32 = 0.01;
pub const INSIDE_MIN_SHARE: f32 = 0.25;
const MAX_SHIFT_X: i32 = 2;
const MAX_SHIFT_Y: i32 = 4;

fn tries() -> &'static [(f32, i32, i32)] {
    static T: OnceLock<Vec<(f32, i32, i32)>> = OnceLock::new();
    T.get_or_init(|| {
        let mut v = Vec::new();
        for i in -8..=8 {
            let s = 1.0 + i as f32 * 0.0025f32;
            for dy in -MAX_SHIFT_Y..=MAX_SHIFT_Y {
                for dx in -MAX_SHIFT_X..=MAX_SHIFT_X {
                    v.push((s, dx, dy));
                }
            }
        }
        let key = |t: &(f32, i32, i32)| (t.0 - 1.0).abs() / 0.0025 + t.1.abs() as f32 + t.2.abs() as f32;
        v.sort_by(|a, b| key(a).partial_cmp(&key(b)).unwrap()); // stable, like LINQ orderby
        v
    })
}

impl TextInk {
    pub fn new(ink: Vec<bool>, width: i32, height: i32, pivot_x: f32, pivot_y: f32) -> Self {
        assert_eq!(ink.len(), (width * height) as usize, "ink size");
        let (mut xs, mut ys) = (Vec::new(), Vec::new());
        for y in 0..height {
            for x in 0..width {
                if ink[(y * width + x) as usize] {
                    xs.push(x);
                    ys.push(y);
                }
            }
        }
        Self { ink, xs, ys, grown: OnceLock::new(), width, height, pivot_x, pivot_y }
    }

    pub fn count(&self) -> usize {
        self.xs.len()
    }

    pub fn read(f: &ScreenFrame<'_>, t: &RefTransform, area: RectF, ref_pivot_x: f32, ref_pivot_y: f32) -> TextInk {
        let w = area.width as i32;
        let h = area.height as i32;
        let mut ink = vec![false; (w * h).max(0) as usize];
        let px = f.bgra;
        for y in 0..h {
            let py0 = t.to_pixel_y(area.y + y as f32) as i32;
            let py1 = (py0 + 1).max(t.to_pixel_y(area.y + y as f32 + 1.0) as i32);
            if py0 < 0 || py1 > f.height {
                continue;
            }
            for x in 0..w {
                let px0 = t.to_pixel_x(area.x + x as f32) as i32;
                let px1 = (px0 + 1).max(t.to_pixel_x(area.x + x as f32 + 1.0) as i32);
                if px0 < 0 || px1 > f.width {
                    continue;
                }
                let mut any = false;
                'rows: for yy in py0..py1 {
                    let row = (yy * f.stride) as usize;
                    for xx in px0..px1 {
                        let p = row + xx as usize * 4;
                        if px[p].max(px[p + 1].max(px[p + 2])) > LIGHT {
                            any = true;
                            break 'rows;
                        }
                    }
                }
                ink[(y * w + x) as usize] = any;
            }
        }
        TextInk::new(ink, w, h, ref_pivot_x - area.x, ref_pivot_y - area.y)
    }

    pub fn distance(a: &TextInk, b: &TextInk) -> f32 {
        Self::match_inks(a, b).distance
    }

    pub fn judge(reference: &TextInk, current: &TextInk) -> TextVerdict {
        if provably_changed(reference, current) { TextVerdict::Changed } else { Self::judge_exact(reference, current) }
    }

    pub fn judge_exact(reference: &TextInk, current: &TextInk) -> TextVerdict {
        let m = Self::match_inks(reference, current);
        if m.distance <= SAME_MAX {
            return TextVerdict::Same;
        }
        if m.lost <= INSIDE_MAX && reference.count() <= current.count() {
            return TextVerdict::Same;
        }
        if m.gained <= INSIDE_MAX {
            return if current.count() > 0 && current.count() as f32 >= INSIDE_MIN_SHARE * reference.count() as f32 {
                TextVerdict::Same
            } else {
                TextVerdict::Unsure
            };
        }
        if m.distance >= CHANGED_MIN { TextVerdict::Changed } else { TextVerdict::Unsure }
    }

    pub fn match_inks(a: &TextInk, b: &TextInk) -> InkMatch {
        if a.width != b.width || a.height != b.height {
            return InkMatch { distance: 1.0, lost: 1.0, gained: 1.0 };
        }
        if a.count() + b.count() == 0 {
            return InkMatch { distance: 0.0, lost: 0.0, gained: 0.0 };
        }
        if a.count() == 0 {
            return InkMatch { distance: 1.0, lost: 0.0, gained: 1.0 };
        }
        if b.count() == 0 {
            return InkMatch { distance: 1.0, lost: 1.0, gained: 0.0 };
        }
        let (ga, gb) = (a.grown(), b.grown());
        let (mut best, mut lost, mut gained) = (i32::MAX, 0, 0);
        for &(s, dx, dy) in tries() {
            let ma = misses(a, gb, b, s, dx, dy, best);
            if ma >= best {
                continue;
            }
            let mb = misses(b, ga, a, 1.0 / s, -dx, -dy, best - ma);
            if ma + mb >= best {
                continue;
            }
            best = ma + mb;
            lost = ma;
            gained = mb;
        }
        InkMatch {
            distance: best as f32 / (a.count() + b.count()) as f32,
            lost: lost as f32 / a.count() as f32,
            gained: gained as f32 / b.count() as f32,
        }
    }

    fn grown(&self) -> &[bool] {
        self.grown.get_or_init(|| {
            let mut g = vec![false; self.ink.len()];
            for i in 0..self.xs.len() {
                for dy in -1..=1 {
                    let y = self.ys[i] + dy;
                    if y < 0 || y >= self.height {
                        continue;
                    }
                    for dx in -1..=1 {
                        let x = self.xs[i] + dx;
                        if x >= 0 && x < self.width {
                            g[(y * self.width + x) as usize] = true;
                        }
                    }
                }
            }
            g
        })
    }
}

fn first_count(max: i32, over: impl Fn(i32) -> bool) -> i32 {
    (0..=max).find(|&k| over(k)).unwrap_or(max + 1)
}

fn provably_changed(a: &TextInk, b: &TextInk) -> bool {
    if a.width != b.width || a.height != b.height || a.count() == 0 || b.count() == 0 {
        return false;
    }
    let (ac, bc) = (a.count() as i32, b.count() as i32);
    let n = ac + bc;
    let lost_over = first_count(ac, |k| k as f32 / ac as f32 > INSIDE_MAX);
    let gained_over = first_count(bc, |k| k as f32 / bc as f32 > INSIDE_MAX);
    let changed_at = first_count(n, |k| k as f32 / n as f32 >= CHANGED_MIN);
    let (ga, gb) = (a.grown(), b.grown());
    for &(s, dx, dy) in tries() {
        let ma = misses(a, gb, b, s, dx, dy, lost_over.max(changed_at));
        if ma < lost_over {
            return false;
        }
        let mb = misses(b, ga, a, 1.0 / s, -dx, -dy, gained_over.max(changed_at - ma));
        if mb < gained_over || ma + mb < changed_at {
            return false;
        }
    }
    true
}

fn misses(from: &TextInk, grown_to: &[bool], to: &TextInk, s: f32, dx: i32, dy: i32, limit: i32) -> i32 {
    let mut miss = 0;
    let mut i = 0;
    while i < from.xs.len() && miss < limit {
        let x = (to.pivot_x + (from.xs[i] as f32 - from.pivot_x) * s).round_ties_even() as i32 + dx;
        let y = (to.pivot_y + (from.ys[i] as f32 - from.pivot_y) * s).round_ties_even() as i32 + dy;
        if x < 0 || y < 0 || x >= to.width || y >= to.height || !grown_to[(y * to.width + x) as usize] {
            miss += 1;
        }
        i += 1;
    }
    miss
}

// ------------------------------------------------------------------ option buttons (EventButtonDetector.cs)

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EventButtonState {
    Idle,
    Hovered,
    Locked,
}

#[derive(Clone, Debug)]
pub struct EventButton {
    pub index: i32,
    pub bounds: PixelRect,
    pub state: EventButtonState,
    pub text_signature: Vec<f32>,
    pub ink: Option<TextInk>,
}

#[derive(Clone, Debug)]
pub struct EventScreen {
    pub buttons: Vec<EventButton>,
}

impl EventScreen {
    pub fn hovered_index(&self) -> i32 {
        self.buttons.iter().find(|b| b.state == EventButtonState::Hovered).map_or(-1, |b| b.index)
    }
}

pub const REF_TOP: f32 = 604.0;
pub const REF_STEP: f32 = 104.0;
pub const REF_LEFT: f32 = 930.0;
pub const REF_WIDTH: f32 = 784.0;
pub const REF_HEIGHT: f32 = 84.0;
pub const MAX_BUTTONS: i32 = 6;
const STRIPE_X0: f32 = 1000.0;
const STRIPE_X1: f32 = 1650.0;
const TOP_STRIPE0: f32 = 5.0;
const TOP_STRIPE1: f32 = 11.0;
const BOTTOM_STRIPE0: f32 = 72.0;
const BOTTOM_STRIPE1: f32 = 80.0;
const MIN_FILL: f32 = 0.6;
const HOVER_GREEN: i32 = 80;
const HOVER_GLOW: i32 = 60;
const GAP0: f32 = 92.0;
const GAP1: f32 = 100.0;
const INK_X0: f32 = 954.0;
const INK_X1: f32 = 1692.0;
const INK_Y0: f32 = 6.0;
const INK_Y1: f32 = 80.0;
const PIVOT_X: f32 = 1322.0;
const PIVOT_DY: f32 = 42.0;
const TEXT_X0: f32 = 950.0;
const TEXT_X1: f32 = 1690.0;
const TEXT_Y0: f32 = 14.0;
const TEXT_Y1: f32 = 70.0;
const SIG_COLS: i32 = 24;
const SIG_ROWS: i32 = 4;
const TEXT_LIGHT: u8 = 150;
const SEARCH_Y0: f32 = REF_TOP - 14.0;
const SEARCH_Y1: f32 = 1080.0 - REF_HEIGHT;
const RUN_KEEP: f32 = 0.2;
const RUN_GAP: i32 = 2;
const RUN_MIN: i32 = 70;
const RUN_MAX: i32 = 104;

pub fn detect(frame: &ScreenFrame<'_>) -> Option<EventScreen> {
    let t = RefTransform::for_frame(frame.width, frame.height);
    let d = column_offset(frame, &t);
    let screen = detect_at(frame, &t, d);
    if screen.is_none() && d != 0 { detect_at(frame, &t, 0) } else { screen }
}

pub fn column_offset(f: &ScreenFrame<'_>, t: &RefTransform) -> i32 {
    let (mut start, mut last) = (-1, -1);
    let mut y = SEARCH_Y0 as i32;
    while y < SEARCH_Y1 as i32 + RUN_MAX {
        let fill = row_fill(f, t, y);
        if start < 0 {
            if fill >= MIN_FILL {
                start = y;
                last = y;
            } else if y as f32 >= SEARCH_Y1 {
                break;
            }
            y += 1;
            continue;
        }
        if fill >= RUN_KEEP {
            last = y;
            y += 1;
            continue;
        }
        if y - last <= RUN_GAP {
            y += 1;
            continue;
        }
        let length = last + 1 - start;
        if (RUN_MIN..=RUN_MAX).contains(&length) {
            let p0 = t.to_pixel_y((start - 1) as f32) as i32;
            let p1 = t.to_pixel_y((last + 2) as f32) as i32;
            let mut rise = p0;
            let mut fall = p1;
            while rise < p1 && row_fill_at(f, t, rise) < MIN_FILL {
                rise += 1;
            }
            while fall > rise && row_fill_at(f, t, fall - 1) < RUN_KEEP {
                fall -= 1;
            }
            let centre = ((rise + fall) as f32 / 2.0 - t.offset_y) / t.scale;
            let d = (centre - REF_HEIGHT / 2.0 - REF_TOP).round_ties_even() as i32;
            return if d <= 4 { 0 } else { d };
        }
        return 0;
    }
    0
}

fn row_fill(f: &ScreenFrame<'_>, t: &RefTransform, ref_y: i32) -> f32 {
    row_fill_at(f, t, t.to_pixel_y(ref_y as f32 + 0.5) as i32)
}

fn band_fill(f: &ScreenFrame<'_>, t: &RefTransform, y0: f32, y1: f32) -> f32 {
    let (mut sum, mut n) = (0f32, 0);
    for y in y0 as i32..y1 as i32 {
        sum += row_fill(f, t, y);
        n += 1;
    }
    if n > 0 { sum / n as f32 } else { 0.0 }
}

fn row_fill_at(f: &ScreenFrame<'_>, t: &RefTransform, y: i32) -> f32 {
    if y < 0 || y >= f.height {
        return 0.0;
    }
    let x0 = 0.max(t.to_pixel_x(STRIPE_X0) as i32);
    let x1 = f.width.min(t.to_pixel_x(STRIPE_X1) as i32);
    let step = 1.max(t.scale as i32);
    let row = (y * f.stride) as usize;
    let (mut n, mut fill) = (0, 0);
    let mut x = x0;
    while x < x1 {
        let p = row + x as usize * 4;
        let (bl, g, rd) = (f.bgra[p] as i32, f.bgra[p + 1] as i32, f.bgra[p + 2] as i32);
        if is_teal(rd, g, bl) || is_locked(rd, g, bl) {
            fill += 1;
        }
        x += step;
        n += 1;
    }
    if n > 0 { fill as f32 / n as f32 } else { 0.0 }
}

fn detect_at(frame: &ScreenFrame<'_>, t: &RefTransform, offset: i32) -> Option<EventScreen> {
    let mut buttons = Vec::new();
    for i in 0..MAX_BUTTONS {
        let top = REF_TOP + offset as f32 + REF_STEP * i as f32;
        if t.to_pixel_y(top + REF_HEIGHT) > frame.height as f32 {
            break;
        }
        let a = stripe(frame, t, top + TOP_STRIPE0, top + TOP_STRIPE1);
        let b = stripe(frame, t, top + BOTTOM_STRIPE0, top + BOTTOM_STRIPE1);
        let state = if a.0 >= MIN_FILL && b.0 >= MIN_FILL {
            if b.2 >= HOVER_GREEN && mean_green(frame, t, top - 7.0, top - 2.0) >= HOVER_GLOW {
                EventButtonState::Hovered
            } else {
                EventButtonState::Idle
            }
        } else if a.1 >= MIN_FILL && b.1 >= MIN_FILL {
            EventButtonState::Locked
        } else {
            break;
        };
        if band_fill(frame, t, top + GAP0, top + GAP1) >= MIN_FILL {
            return None;
        }
        let bounds = t.to_pixel_rect(RectF::new(REF_LEFT, top, REF_WIDTH, REF_HEIGHT)).intersect(frame.width, frame.height);
        buttons.push(EventButton {
            index: i,
            bounds,
            state,
            text_signature: signature(frame, t, RectF::new(TEXT_X0, top + TEXT_Y0, TEXT_X1 - TEXT_X0, TEXT_Y1 - TEXT_Y0)),
            ink: Some(TextInk::read(frame, t, RectF::new(INK_X0, top + INK_Y0, INK_X1 - INK_X0, INK_Y1 - INK_Y0), PIVOT_X, top + PIVOT_DY)),
        });
    }
    if buttons.is_empty() { None } else { Some(EventScreen { buttons }) }
}

pub fn signature_distance(a: &[f32], b: &[f32]) -> f32 {
    if a.len() != b.len() {
        return 1.0;
    }
    let mut sum = 0f32;
    for i in 0..a.len() {
        sum += (a[i] - b[i]).abs();
    }
    sum / a.len() as f32
}

/// (teal share, locked share, mean green of the teal pixels).
fn stripe(f: &ScreenFrame<'_>, t: &RefTransform, y0: f32, y1: f32) -> (f32, f32, i32) {
    let r = t.to_pixel_rect(RectF::new(STRIPE_X0, y0, STRIPE_X1 - STRIPE_X0, y1 - y0)).intersect(f.width, f.height);
    let step = 1.max(t.scale as i32);
    let (mut n, mut teal, mut locked) = (0, 0, 0);
    let mut green = 0i64;
    let mut y = r.y;
    while y < r.bottom() {
        let row = (y * f.stride) as usize;
        let mut x = r.x;
        while x < r.right() {
            let p = row + x as usize * 4;
            let (bl, g, rd) = (f.bgra[p] as i32, f.bgra[p + 1] as i32, f.bgra[p + 2] as i32);
            n += 1;
            if is_teal(rd, g, bl) {
                teal += 1;
                green += g as i64;
            } else if is_locked(rd, g, bl) {
                locked += 1;
            }
            x += step;
        }
        y += step;
    }
    if n == 0 {
        return (0.0, 0.0, 0);
    }
    (teal as f32 / n as f32, locked as f32 / n as f32, if teal > 0 { (green / teal as i64) as i32 } else { 0 })
}

fn mean_green(f: &ScreenFrame<'_>, t: &RefTransform, y0: f32, y1: f32) -> i32 {
    let r = t.to_pixel_rect(RectF::new(STRIPE_X0, y0, STRIPE_X1 - STRIPE_X0, y1 - y0)).intersect(f.width, f.height);
    let step = 1.max(t.scale as i32);
    let mut n = 0i64;
    let mut sum = 0i64;
    let mut y = r.y;
    while y < r.bottom() {
        let mut x = r.x;
        while x < r.right() {
            sum += f.bgra[(y * f.stride + x * 4 + 1) as usize] as i64;
            n += 1;
            x += step;
        }
        y += step;
    }
    if n > 0 { (sum / n) as i32 } else { 0 }
}

pub fn is_teal(r: i32, g: i32, b: i32) -> bool {
    (22..=64).contains(&r) && (18..=58).contains(&(g - r)) && (1..=22).contains(&(b - g))
}

pub fn is_locked(r: i32, g: i32, b: i32) -> bool {
    (34..=62).contains(&r) && (2..=10).contains(&(r - g)) && (g - b).abs() <= 4
}

pub(crate) fn signature(f: &ScreenFrame<'_>, t: &RefTransform, area: RectF) -> Vec<f32> {
    let r = t.to_pixel_rect(area).intersect(f.width, f.height);
    let mut sig = vec![0f32; (SIG_COLS * SIG_ROWS) as usize];
    if r.width < SIG_COLS || r.height < SIG_ROWS {
        return sig;
    }
    let step = 1.max(t.scale as i32);
    for cy in 0..SIG_ROWS {
        let y0 = r.y + cy * r.height / SIG_ROWS;
        let y1 = r.y + (cy + 1) * r.height / SIG_ROWS;
        for cx in 0..SIG_COLS {
            let x0 = r.x + cx * r.width / SIG_COLS;
            let x1 = r.x + (cx + 1) * r.width / SIG_COLS;
            let (mut n, mut light) = (0, 0);
            let mut y = y0;
            while y < y1 {
                let row = (y * f.stride) as usize;
                let mut x = x0;
                while x < x1 {
                    let p = row + x as usize * 4;
                    n += 1;
                    if f.bgra[p].max(f.bgra[p + 1].max(f.bgra[p + 2])) > TEXT_LIGHT {
                        light += 1;
                    }
                    x += step;
                }
                y += step;
            }
            sig[(cy * SIG_COLS + cx) as usize] = if n > 0 { light as f32 / n as f32 } else { 0.0 };
        }
    }
    sig
}

// ------------------------------------------------------------------ ancient options (AncientButtonDetector.cs)

pub mod ancient {
    use super::{signature, EventButton, EventButtonState, EventScreen, TextInk};
    use crate::geometry::{RectF, RefTransform, ScreenFrame};

    pub const REF_LEFT: f32 = 460.0;
    pub const REF_WIDTH: f32 = 1000.0;
    pub const REF_HEIGHT: f32 = 100.0;
    pub const REF_STEP: f32 = 96.0;
    pub const REF_LOWEST_TOP: f32 = 942.0;
    pub const MAX_BUTTONS: i32 = 4;
    const TITLE_X0: f32 = 544.0;
    const TITLE_X1: f32 = 1440.0;
    const TITLE_Y0: f32 = 18.0;
    const TITLE_Y1: f32 = 46.0;
    const TITLE_START0: f32 = 546.0;
    const TITLE_START1: f32 = 557.0;
    const MIN_TITLE_WIDTH: f32 = 20.0;
    const BAND_X0: f32 = 552.0;
    const BAND_X1: f32 = 1420.0;
    const BAND_Y0: f32 = 10.0;
    const BAND_Y1: f32 = 90.0;
    const BRIGHT: i32 = 150;
    const SATURATED: i32 = 90;
    const MAX_BRIGHT: f32 = 0.12;
    const MAX_SATURATED: f32 = 0.07;
    const TEXT_X0: f32 = 552.0;
    const TEXT_X1: f32 = 1420.0;
    const TEXT_Y0: f32 = 13.0;
    const TEXT_Y1: f32 = 87.0;
    const RING_Y0: f32 = 3.0;
    const RING_Y1: f32 = 10.0;
    const RING_FILL_Y: f32 = 17.0;
    const RING_X0: f32 = 560.0;
    const RING_X1: f32 = 1360.0;
    const RING_CYAN: i32 = 40;
    const RING_SHARE: f32 = 0.8;

    pub fn slot_top(count: i32, index: i32) -> f32 {
        REF_LOWEST_TOP - REF_STEP * (count - 1 - index) as f32
    }

    pub fn detect(frame: &ScreenFrame<'_>) -> Option<EventScreen> {
        let t = RefTransform::for_frame(frame.width, frame.height);
        let mut count = 0;
        while count < MAX_BUTTONS && is_option(frame, &t, REF_LOWEST_TOP - REF_STEP * count as f32) {
            count += 1;
        }
        if count == 0 {
            return None;
        }
        let mut hovered = -1;
        let mut best = f32::MIN;
        for i in 0..count {
            if let Some(strength) = ring(frame, &t, slot_top(count, i)) {
                if strength > best {
                    best = strength;
                    hovered = i;
                }
            }
        }
        let mut buttons = Vec::new();
        for i in 0..count {
            let top = slot_top(count, i);
            let bounds = t.to_pixel_rect(RectF::new(REF_LEFT, top, REF_WIDTH, REF_HEIGHT)).intersect(frame.width, frame.height);
            let area = RectF::new(TEXT_X0, top + TEXT_Y0, TEXT_X1 - TEXT_X0, TEXT_Y1 - TEXT_Y0);
            buttons.push(EventButton {
                index: i,
                bounds,
                state: if i == hovered { EventButtonState::Hovered } else { EventButtonState::Idle },
                text_signature: signature(frame, &t, area),
                ink: Some(TextInk::read(frame, &t, area, REF_LEFT + REF_WIDTH / 2.0, top + REF_HEIGHT / 2.0)),
            });
        }
        Some(EventScreen { buttons })
    }

    fn is_option(f: &ScreenFrame<'_>, t: &RefTransform, top: f32) -> bool {
        if top < 0.0 || t.to_pixel_y(top + REF_HEIGHT) > (f.height + 1) as f32 {
            return false;
        }
        let step = 1.max(t.scale as i32);
        let title = t.to_pixel_rect(RectF::new(TITLE_X0, top + TITLE_Y0, TITLE_X1 - TITLE_X0, TITLE_Y1 - TITLE_Y0)).intersect(f.width, f.height);
        let (mut first, mut gold_cols) = (-1, 0);
        let mut x = title.x;
        while x < title.right() {
            let mut y = title.y;
            while y < title.bottom() {
                let p = (y * f.stride + x * 4) as usize;
                if is_gold(f.bgra[p + 2] as i32, f.bgra[p + 1] as i32, f.bgra[p] as i32) {
                    if first < 0 {
                        first = x;
                    }
                    gold_cols += 1;
                    break;
                }
                y += step;
            }
            x += step;
        }
        if first < 0 {
            return false;
        }
        let first_ref = (first as f32 - t.offset_x) / t.scale;
        if !(TITLE_START0..=TITLE_START1).contains(&first_ref) || ((gold_cols * step) as f32 / t.scale) < MIN_TITLE_WIDTH {
            return false;
        }
        let band = t.to_pixel_rect(RectF::new(BAND_X0, top + BAND_Y0, BAND_X1 - BAND_X0, BAND_Y1 - BAND_Y0)).intersect(f.width, f.height);
        let (mut n, mut bright, mut saturated) = (0, 0, 0);
        let mut y = band.y;
        while y < band.bottom() {
            let row = (y * f.stride) as usize;
            let mut x = band.x;
            while x < band.right() {
                let p = row + x as usize * 4;
                let (b, g, r) = (f.bgra[p] as i32, f.bgra[p + 1] as i32, f.bgra[p + 2] as i32);
                let max = r.max(g.max(b));
                let min = r.min(g.min(b));
                if max >= BRIGHT {
                    bright += 1;
                }
                if max - min >= SATURATED {
                    saturated += 1;
                }
                x += step;
                n += 1;
            }
            y += step;
        }
        n > 0 && bright as f32 <= MAX_BRIGHT * n as f32 && saturated as f32 <= MAX_SATURATED * n as f32
    }

    pub fn is_gold(r: i32, g: i32, b: i32) -> bool {
        r > 190 && g > 140 && g < 225 && b < 150 && r - b > 80
    }

    fn ring(f: &ScreenFrame<'_>, t: &RefTransform, top: f32) -> Option<f32> {
        let ring = t.to_pixel_rect(RectF::new(RING_X0, top + RING_Y0, RING_X1 - RING_X0, RING_Y1 - RING_Y0)).intersect(f.width, f.height);
        let fill_y = (f.height - 1).min(t.to_pixel_y(top + RING_FILL_Y) as i32);
        let step = 1.max(t.scale as i32);
        let (mut cols, mut cyan) = (0, 0);
        let mut sum = 0i64;
        let c = |p: usize| f.bgra[p].min(f.bgra[p + 1]) as i32 - f.bgra[p + 2] as i32;
        let mut x = ring.x;
        while x < ring.right() {
            cols += 1;
            let mut best = i32::MIN;
            for y in ring.y..ring.bottom() {
                best = best.max(c((y * f.stride + x * 4) as usize));
            }
            let d = best - c((fill_y * f.stride + x * 4) as usize);
            sum += d as i64;
            if d >= RING_CYAN {
                cyan += 1;
            }
            x += step;
        }
        if cols > 0 && cyan as f32 >= RING_SHARE * cols as f32 { Some(sum as f32 / cols as f32) } else { None }
    }
}
