//! Port of Price/*.cs: the shop price rules, the digit templates and the price label reader.

use std::sync::OnceLock;

use crate::geometry::{PixelRect, RectF, RefTransform, ScreenFrame};
use crate::glyph_data;
use crate::layout::card_geometry;
use crate::ncc;
use crate::results::CardSlotKind;

// ------------------------------------------------------------------ rules (ShopPriceRules.cs)

/// Relic price factors accepted when nothing narrows them down (none, Courier, Membership Card, both).
pub const ALL_FACTORS: [f64; 4] = [1.0, 0.8, 0.5, 0.4];

fn round_even(x: f64) -> i32 {
    x.round_ties_even() as i32
}

pub fn base_price(rarity: Option<&str>, colorless: bool) -> Option<i32> {
    let b = match rarity.map(|r| r.to_lowercase()).as_deref() {
        Some("common") => Some(50),
        Some("uncommon") => Some(75),
        Some("rare") => Some(150),
        _ => None,
    };
    match b {
        Some(v) if colorless => Some((v as f32 * 1.15f32).round_ties_even() as i32),
        other => other,
    }
}

pub fn range(base: i32, on_sale: bool, factor: f64) -> (i32, i32) {
    let mut lo = round_even(base as f64 * 0.95);
    let mut hi = round_even(base as f64 * 1.05);
    if on_sale {
        lo /= 2;
        hi /= 2;
    }
    ((lo as f64 * factor) as i32, (hi as f64 * factor) as i32)
}

pub fn is_plausible(price: i32, kind: CardSlotKind, rarity: Option<&str>, factors: Option<&[f64]>) -> bool {
    if !kind.is_shop() || price <= 0 {
        return false;
    }
    let colorless = kind == CardSlotKind::ShopColorless;
    let rarities: Vec<&str> = if base_price(rarity, colorless).is_some() {
        vec![rarity.unwrap()]
    } else if colorless {
        vec!["uncommon", "rare"]
    } else {
        vec!["common", "uncommon", "rare"]
    };
    let factors = factors.unwrap_or(&ALL_FACTORS);
    for r in rarities {
        let b = base_price(Some(r), colorless).unwrap();
        let sales: &[bool] = if colorless { &[false] } else { &[false, true] };
        for &sale in sales {
            for &f in factors {
                let (lo, hi) = range(b, sale, f);
                if price >= lo && price <= hi {
                    return true;
                }
            }
        }
    }
    false
}

pub fn relic_base_price(rarity: Option<&str>) -> Option<i32> {
    match rarity.map(|r| r.to_lowercase()).as_deref() {
        Some("common") => Some(175),
        Some("uncommon") => Some(225),
        Some("rare") => Some(275),
        Some("shop") => Some(200),
        _ => None,
    }
}

pub fn relic_range(base: i32, factor: f64) -> (i32, i32) {
    let lo = (base as f64 * 0.85).floor() as i32;
    let hi = (base as f64 * 1.15).ceil() as i32;
    ((lo as f64 * factor) as i32, (hi as f64 * factor) as i32)
}

pub fn is_plausible_relic(price: i32, rarity: Option<&str>, factors: Option<&[f64]>) -> bool {
    if price <= 0 {
        return false;
    }
    let rarities: Vec<&str> =
        if relic_base_price(rarity).is_some() { vec![rarity.unwrap()] } else { vec!["common", "uncommon", "rare", "shop"] };
    for r in rarities {
        for &f in factors.unwrap_or(&ALL_FACTORS) {
            let (lo, hi) = relic_range(relic_base_price(Some(r)).unwrap(), f);
            if price >= lo && price <= hi {
                return true;
            }
        }
    }
    false
}

// ------------------------------------------------------------------ glyphs (PriceGlyphs.cs)

pub const GLYPH_W: usize = 12;
pub const GLYPH_H: usize = 16;
pub const GLYPH_LEN: usize = GLYPH_W * GLYPH_H;
pub const GLYPH_ASPECT: f32 = 0.8;

pub const GYEONGGI_WIDTHS: [(f32, f32); 10] = [
    (0.667, 0.706), (0.444, 0.529), (0.556, 0.647), (0.591, 0.706), (0.588, 0.882),
    (0.588, 0.647), (0.647, 0.706), (0.556, 0.647), (0.611, 0.714), (0.611, 0.706),
];
pub const KREON_WIDTHS: [(f32, f32); 10] = [
    (0.700, 0.789), (0.444, 0.526), (0.650, 0.737), (0.600, 0.700), (0.667, 0.789),
    (0.611, 0.737), (0.650, 0.750), (0.556, 0.684), (0.636, 0.727), (0.611, 0.737),
];

pub struct PriceGlyphs {
    pub raw: Vec<Vec<f32>>,
    normalised: Vec<Vec<f32>>,
    pub widths: [(f32, f32); 10],
    pub name: String,
}

impl PriceGlyphs {
    pub fn new(raw: Vec<Vec<f32>>, widths: [(f32, f32); 10], name: &str) -> Self {
        assert!(raw.len() == 10 && raw.iter().all(|g| g.len() == GLYPH_LEN), "Need 10 glyphs of {GLYPH_LEN} cells.");
        let normalised = raw
            .iter()
            .map(|g| {
                let mut n = g.clone();
                glyph_normalise(&mut n);
                n
            })
            .collect();
        Self { raw, normalised, widths, name: name.to_string() }
    }

    pub fn parse(lines: &[&str], widths: [(f32, f32); 10], name: &str) -> Self {
        let raw = lines
            .iter()
            .map(|l| l.trim().chars().map(|c| c.to_digit(16).expect("hex") as f32 / 15.0).collect())
            .collect();
        Self::new(raw, widths, name)
    }

    pub fn gyeonggi() -> &'static PriceGlyphs {
        static G: OnceLock<PriceGlyphs> = OnceLock::new();
        G.get_or_init(|| PriceGlyphs::parse(&glyph_data::GYEONGGI, GYEONGGI_WIDTHS, "gyeonggi"))
    }

    pub fn kreon() -> &'static PriceGlyphs {
        static K: OnceLock<PriceGlyphs> = OnceLock::new();
        K.get_or_init(|| PriceGlyphs::parse(&glyph_data::KREON, KREON_WIDTHS, "kreon"))
    }

    pub fn all() -> [&'static PriceGlyphs; 2] {
        [Self::gyeonggi(), Self::kreon()]
    }

    /// Best digit for a raw coverage grid: (digit, NCC, gap to the second best); digit -1 for a flat grid.
    pub fn classify(&self, raw: &[f32]) -> (i32, f32, f32) {
        let mut q = raw.to_vec();
        if !glyph_normalise(&mut q) {
            return (-1, 0.0, 0.0);
        }
        let mut best = -1;
        let (mut s1, mut s2) = (f32::NEG_INFINITY, f32::NEG_INFINITY);
        for d in 0..10 {
            let s = ncc::dot(&q, &self.normalised[d]);
            if s > s1 {
                s2 = s1;
                s1 = s;
                best = d as i32;
            } else if s > s2 {
                s2 = s;
            }
        }
        (best, s1, s1 - s2)
    }

    pub fn score(&self, raw: &[f32], digit: usize) -> f32 {
        let mut q = raw.to_vec();
        if glyph_normalise(&mut q) { ncc::dot(&q, &self.normalised[digit]) } else { 0.0 }
    }
}

fn glyph_normalise(v: &mut [f32]) -> bool {
    let mut mean = 0f32;
    for &x in v.iter() {
        mean += x;
    }
    mean /= v.len() as f32;
    let mut len2 = 0f32;
    for x in v.iter_mut() {
        *x -= mean;
        len2 += *x * *x;
    }
    if len2 < 1e-6 {
        v.fill(0.0);
        return false;
    }
    let inv = 1.0 / len2.sqrt();
    for x in v.iter_mut() {
        *x *= inv;
    }
    true
}

// ------------------------------------------------------------------ reader (ShopPriceReader.cs)

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriceTint {
    Unknown,
    Cream,
    Red,
    Green,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PriceReading {
    /// The price, when the label was read clearly and fits the price rules.
    pub value: Option<i32>,
    pub text: String,
    pub tint: PriceTint,
    pub min_score: f32,
    pub min_margin: f32,
    pub plausible: bool,
    pub bounds: PixelRect,
    pub font: String,
    pub font_gap: f32,
}

impl PriceReading {
    pub fn unaffordable(&self) -> bool {
        self.tint == PriceTint::Red
    }
    pub fn on_sale(&self) -> bool {
        self.tint == PriceTint::Green
    }
}

impl std::fmt::Display for PriceReading {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{}{} {:?} s={:.2} m={:.2}",
            self.text,
            if self.value.is_none() { "(rejected)" } else { "" },
            self.tint,
            self.min_score,
            self.min_margin
        )
    }
}

#[derive(Clone, Debug)]
pub struct PriceReaderOptions {
    pub min_digit_score: f32,
    pub min_digit_margin: f32,
    pub min_font_gap: f32,
    pub price_factors: Option<Vec<f64>>,
}

impl Default for PriceReaderOptions {
    fn default() -> Self {
        Self { min_digit_score: 0.60, min_digit_margin: 0.04, min_font_gap: 0.10, price_factors: None }
    }
}

pub const LABEL_BOX: RectF = RectF::new(-150.0, 212.0, 300.0, 76.0);
pub const DIGIT_HEIGHT: f32 = 26.6;
pub const DIGIT_CENTER_Y: f32 = 249.0;
pub const DIGIT_BLOCK_CENTER_X: f32 = 31.5;
pub const DIGIT_BLOCK_TOLERANCE: f32 = 6.0;
pub const MIN_COIN_FRACTION: f32 = 0.10;
pub const MAX_OCCLUDER_FRACTION: f32 = 0.05;
pub const WIDTH_TOLERANCE: f32 = 0.06;
pub const RELIC_LABEL_SHIFT: f32 = 249.0 - 79.0;
const POTION_BASE_PRICES: [i32; 3] = [50, 75, 100];

pub struct ShopPriceReader {
    sets: Vec<&'static PriceGlyphs>,
    pub options: PriceReaderOptions,
}

struct Digits {
    set: usize,
    text: String,
    min_score: f32,
    min_margin: f32,
    widths_ok: bool,
}

pub(crate) struct DigitRow {
    pub glyphs: Vec<Vec<f32>>,
    pub tint: PriceTint,
    pub bounds: PixelRect,
    pub center_x: f32,
    #[allow(dead_code)]
    pub center_y: f32,
    pub coin_fraction: f32,
    pub occluder_fraction: f32,
    pub widths: Vec<f32>,
}

impl ShopPriceReader {
    pub fn new(options: PriceReaderOptions) -> Self {
        Self { sets: PriceGlyphs::all().to_vec(), options }
    }

    pub fn with_glyphs(glyphs: &'static PriceGlyphs, options: PriceReaderOptions) -> Self {
        Self { sets: vec![glyphs], options }
    }

    fn classify(set: usize, glyphs: &PriceGlyphs, row: &DigitRow) -> Option<Digits> {
        let mut text = String::new();
        let (mut min_score, mut min_margin) = (1f32, 1f32);
        let mut widths_ok = true;
        for (i, g) in row.glyphs.iter().enumerate() {
            let (d, s, m) = glyphs.classify(g);
            if d < 0 {
                return None;
            }
            text.push((b'0' + d as u8) as char);
            min_score = min_score.min(s);
            min_margin = min_margin.min(m);
            let (lo, hi) = glyphs.widths[d as usize];
            if i < row.widths.len() && (row.widths[i] < lo - WIDTH_TOLERANCE || row.widths[i] > hi + WIDTH_TOLERANCE) {
                widths_ok = false;
            }
        }
        Some(Digits { set, text, min_score, min_margin, widths_ok })
    }

    pub fn read(&self, frame: &ScreenFrame<'_>, cx: f32, cy: f32, scale: f32, kind: CardSlotKind, rarity: Option<&str>) -> Option<PriceReading> {
        let factors = self.options.price_factors.as_deref();
        self.read_at(frame, cx, cy, scale, &|v| is_plausible(v, kind, rarity, factors))
    }

    pub fn read_relic(&self, frame: &ScreenFrame<'_>, ox: f32, oy: f32, scale: f32, rarity: Option<&str>) -> Option<PriceReading> {
        let factors = self.options.price_factors.as_deref();
        self.read_at(frame, ox, oy - RELIC_LABEL_SHIFT * scale, scale, &|v| is_plausible_relic(v, rarity, factors))
    }

    pub fn read_potion(&self, frame: &ScreenFrame<'_>, ox: f32, oy: f32, scale: f32) -> Option<PriceReading> {
        let factors: Vec<f64> = self.options.price_factors.clone().unwrap_or_else(|| ALL_FACTORS.to_vec());
        self.read_at(frame, ox, oy - (249.0f32 - 74.7f32) * scale, scale, &|value| {
            factors.iter().any(|&f| {
                POTION_BASE_PRICES.iter().any(|&p| {
                    let (lo, hi) = range(p, false, f);
                    value >= lo && value <= hi
                })
            })
        })
    }

    fn read_at(&self, frame: &ScreenFrame<'_>, cx: f32, cy: f32, scale: f32, plausible_fn: &dyn Fn(i32) -> bool) -> Option<PriceReading> {
        let row = segment(frame, cx, cy, scale)?;
        let readings: Vec<Digits> = self.sets.iter().enumerate().filter_map(|(i, s)| Self::classify(i, s, &row)).collect();
        if readings.is_empty() {
            return None;
        }
        // OrderByDescending(WidthsOk).ThenByDescending(MinScore).First(): first of the best on ties.
        let mut bi = 0;
        for (i, r) in readings.iter().enumerate().skip(1) {
            let b = &readings[bi];
            if (r.widths_ok && !b.widths_ok) || (r.widths_ok == b.widths_ok && r.min_score > b.min_score) {
                bi = i;
            }
        }
        let best = &readings[bi];
        let mut rival: Option<&Digits> = None;
        for (i, r) in readings.iter().enumerate() {
            if i == bi || r.text == best.text || r.widths_ok != best.widths_ok {
                continue;
            }
            if rival.is_none_or(|x| r.min_score > x.min_score) {
                rival = Some(r);
            }
        }
        let font_gap = rival.map_or(1.0, |r| best.min_score - r.min_score);
        let font_clear = font_gap >= self.options.min_font_gap;
        let value: i32 = best.text.parse().unwrap_or(0);
        let plausible = !best.text.starts_with('0') && plausible_fn(value);
        let complete = row.coin_fraction >= MIN_COIN_FRACTION
            && (row.center_x - DIGIT_BLOCK_CENTER_X).abs() <= DIGIT_BLOCK_TOLERANCE
            && row.occluder_fraction <= MAX_OCCLUDER_FRACTION;
        let clear = best.min_score >= self.options.min_digit_score
            && best.min_margin >= self.options.min_digit_margin
            && complete
            && best.widths_ok
            && font_clear;
        Some(PriceReading {
            value: if plausible && clear { Some(value) } else { None },
            text: best.text.clone(),
            tint: row.tint,
            min_score: best.min_score,
            min_margin: best.min_margin,
            plausible,
            bounds: row.bounds,
            font: self.sets[best.set].name.clone(),
            font_gap,
        })
    }
}

const CREAM: usize = 0;
const RED: usize = 1;
const GREEN: usize = 2;
const GOLD: usize = 3;
const BRIGHT: usize = 4;
const CHANNELS: usize = 5;

pub(crate) fn class_of(r: i32, g: i32, b: i32) -> i32 {
    let (rf, gf, bf) = (r as f32, g as f32, b as f32);
    if b >= 150 && gf >= 0.85 * rf && bf >= 0.75 * rf && r >= 160 {
        return CREAM as i32;
    }
    if r >= 150 && gf <= 0.6 * rf && bf <= 0.6 * rf && ((g - b).abs() as f32) <= 0.12 * rf {
        return RED as i32;
    }
    if g >= 150 && rf <= 0.7 * gf && bf <= 0.4 * gf {
        return GREEN as i32;
    }
    if r >= 170 && gf >= 0.55 * rf && gf <= 0.88 * rf && bf <= 0.45 * rf {
        return GOLD as i32;
    }
    if r.max(g.max(b)) >= 150 && b >= r + 20 && b >= g - 10 {
        return BRIGHT as i32;
    }
    -1
}

#[derive(Clone)]
struct Comp {
    root: i32,
    min_x: i32,
    min_y: i32,
    max_x: i32,
    max_y: i32,
    area: i32,
    cover: [f32; 3],
}

impl Comp {
    fn new(root: i32) -> Self {
        Comp { root, min_x: i32::MAX, min_y: i32::MAX, max_x: i32::MIN, max_y: i32::MIN, area: 0, cover: [0.0; 3] }
    }
    fn w(&self) -> i32 {
        self.max_x - self.min_x + 1
    }
    fn h(&self) -> i32 {
        self.max_y - self.min_y + 1
    }
    fn center_y(&self) -> f32 {
        (self.min_y + self.max_y) as f32 / 2.0
    }
    fn add(&mut self, x: i32, y: i32, cover: &[f32]) {
        self.min_x = self.min_x.min(x);
        self.max_x = self.max_x.max(x);
        self.min_y = self.min_y.min(y);
        self.max_y = self.max_y.max(y);
        self.area += 1;
        for k in 0..3 {
            self.cover[k] += cover[k];
        }
    }
    fn absorb(&mut self, o: &Comp) {
        self.min_x = self.min_x.min(o.min_x);
        self.max_x = self.max_x.max(o.max_x);
        self.min_y = self.min_y.min(o.min_y);
        self.max_y = self.max_y.max(o.max_y);
        self.area += o.area;
        for k in 0..3 {
            self.cover[k] += o.cover[k];
        }
    }
}

fn find(root: &mut [i32], a: i32) -> i32 {
    if root[a as usize] == a {
        return a;
    }
    let r = find(root, root[a as usize]);
    root[a as usize] = r;
    r
}

fn row_dist(row: &[Comp], y_exp: f32) -> f32 {
    let mut sum = 0f64;
    for c in row {
        sum += c.center_y() as f64;
    }
    ((sum / row.len() as f64) as f32 - y_exp).abs()
}

fn better_row(a: Option<Vec<Comp>>, b: Vec<Comp>, y_exp: f32) -> Option<Vec<Comp>> {
    if b.is_empty() {
        return a;
    }
    match a {
        None => Some(b),
        Some(a) => {
            if b.len() > a.len() || (b.len() == a.len() && row_dist(&b, y_exp) < row_dist(&a, y_exp)) {
                Some(b)
            } else {
                Some(a)
            }
        }
    }
}

pub(crate) fn segment(frame: &ScreenFrame<'_>, cx: f32, cy: f32, scale: f32) -> Option<DigitRow> {
    let t = RefTransform::for_frame(frame.width, frame.height);
    let pr = t.to_pixel_rect(card_geometry::place(LABEL_BOX, cx, cy, scale)).intersect(frame.width, frame.height);
    let step = ((t.scale + 1e-3).floor() as i32).max(1);
    let gw = pr.width / step;
    let gh = pr.height / step;
    if gw < 8 || gh < 8 {
        return None;
    }
    let (gwu, ghu) = (gw as usize, gh as usize);
    let n = gwu * ghu;
    let mut cover = vec![0f32; n * CHANNELS];
    let mut text = vec![0f32; n];
    let mut label = vec![0i32; n];
    let px = frame.bgra;
    let w1 = 1.0f32 / (step * step) as f32;
    for gy in 0..gh {
        for sy in 0..step {
            let row_start = ((pr.y + gy * step + sy) * frame.stride + pr.x * 4) as usize;
            for gx in 0..gw {
                for sx in 0..step {
                    let p = row_start + ((gx * step + sx) * 4) as usize;
                    let c = class_of(px[p + 2] as i32, px[p + 1] as i32, px[p] as i32);
                    if c >= 0 {
                        cover[(gy * gw + gx) as usize * CHANNELS + c as usize] += w1;
                    }
                }
            }
        }
    }
    for i in 0..n {
        text[i] = cover[i * CHANNELS + CREAM] + cover[i * CHANNELS + RED] + cover[i * CHANNELS + GREEN];
    }
    // (2) 8-connected components of text >= 0.5
    let mut comps: Vec<Comp> = Vec::new();
    let mut stack: Vec<usize> = Vec::new();
    for i in 0..n {
        if label[i] != 0 || text[i] < 0.5 {
            continue;
        }
        let mut comp = Comp::new(comps.len() as i32 + 1);
        label[i] = comp.root;
        stack.push(i);
        while let Some(j) = stack.pop() {
            let x = (j % gwu) as i32;
            let y = (j / gwu) as i32;
            comp.add(x, y, &cover[j * CHANNELS..j * CHANNELS + 3]);
            for dy in -1..=1 {
                for dx in -1..=1 {
                    let nx = x + dx;
                    let ny = y + dy;
                    if nx < 0 || ny < 0 || nx >= gw || ny >= gh {
                        continue;
                    }
                    let k = (ny * gw + nx) as usize;
                    if label[k] != 0 || text[k] < 0.5 {
                        continue;
                    }
                    label[k] = comp.root;
                    stack.push(k);
                }
            }
        }
        comps.push(comp);
    }
    if comps.is_empty() {
        return None;
    }
    let h_exp = DIGIT_HEIGHT * scale * t.scale / step as f32;
    let mut root: Vec<i32> = (0..=comps.len() as i32).collect();
    let mut live = comps.clone();
    let mut merged = true;
    while merged {
        merged = false;
        'outer: for a in 0..live.len() {
            for b in a + 1..live.len() {
                let (p, q) = (&live[a], &live[b]);
                let overlap = p.max_x.min(q.max_x) - p.min_x.max(q.min_x) + 1;
                let union = p.max_y.max(q.max_y) - p.min_y.min(q.min_y) + 1;
                if (overlap as f32) < 0.5 * p.w().min(q.w()) as f32 || union as f32 > 1.3 * h_exp {
                    continue;
                }
                let q = live[b].clone();
                live[a].absorb(&q);
                root[q.root as usize] = live[a].root;
                live.remove(b);
                merged = true;
                break 'outer;
            }
        }
    }
    let mut pieces: Vec<Comp> = Vec::new();
    for c in &live {
        let k = (c.w() as f32 / (0.65 * h_exp)).round_ties_even() as i32;
        if (c.h() as f32) < 0.7 * h_exp || c.h() as f32 > 1.3 * h_exp || c.w() as f32 <= 1.0 * h_exp || !(2..=3).contains(&k) {
            pieces.push(c.clone());
            continue;
        }
        let mut cuts = vec![c.min_x];
        for j in 1..k {
            let expected = c.min_x + ((c.w() * j) as f32 / k as f32).round_ties_even() as i32;
            let radius = 1.max((c.w() as f32 / (4.0 * k as f32)) as i32);
            let mut best_x = expected;
            let mut best_sum = f32::MAX;
            for x in expected - radius..=expected + radius {
                let mut col_sum = 0f32;
                for y in c.min_y..=c.max_y {
                    let idx = (y * gw + x) as usize;
                    if label[idx] != 0 && find(&mut root, label[idx]) == c.root {
                        col_sum += text[idx];
                    }
                }
                if col_sum < best_sum {
                    best_sum = col_sum;
                    best_x = x;
                }
            }
            cuts.push(best_x);
        }
        cuts.push(c.max_x + 1);
        for j in 0..k as usize {
            let mut part = Comp::new(c.root);
            for y in c.min_y..=c.max_y {
                for x in cuts[j]..cuts[j + 1] {
                    let idx = (y * gw + x) as usize;
                    if label[idx] != 0 && find(&mut root, label[idx]) == c.root {
                        part.add(x, y, &cover[idx * CHANNELS..idx * CHANNELS + 3]);
                    }
                }
            }
            if part.area > 0 {
                pieces.push(part);
            }
        }
    }
    // (3) digit-sized components, grouped into rows
    let y_exp = (DIGIT_CENTER_Y - LABEL_BOX.y) * scale * t.scale / step as f32;
    let mut digits: Vec<Comp> = pieces
        .into_iter()
        .filter(|c| {
            c.h() as f32 >= 0.7 * h_exp
                && c.h() as f32 <= 1.3 * h_exp
                && c.w() as f32 >= 0.12 * h_exp
                && c.w() as f32 <= 1.0 * h_exp
                && (c.center_y() - y_exp).abs() <= 0.9 * h_exp
        })
        .collect();
    digits.sort_by_key(|c| c.min_x); // stable, like OrderBy
    let mut best_row: Option<Vec<Comp>> = None;
    let mut current: Vec<Comp> = Vec::new();
    for c in digits {
        if let Some(prev) = current.last() {
            let gap = c.min_x - prev.max_x - 1;
            let same_height = c.h().max(prev.h()) as f32 <= 1.2 * c.h().min(prev.h()) as f32;
            if gap as f32 > 0.6 * h_exp || (gap as f32) < -0.2 * h_exp || (c.center_y() - prev.center_y()).abs() > 0.2 * h_exp || !same_height {
                best_row = better_row(best_row, std::mem::take(&mut current), y_exp);
            }
        }
        current.push(c);
    }
    best_row = better_row(best_row, current, y_exp);
    let best_row = best_row?;
    if best_row.is_empty() || best_row.len() > 4 {
        return None;
    }
    // (4) one coverage grid per digit
    let top = best_row.iter().map(|c| c.min_y).min().unwrap();
    let bottom = best_row.iter().map(|c| c.max_y).max().unwrap() + 1;
    let hh = (bottom - top) as f32;
    let ww = hh * GLYPH_ASPECT;
    const SUB: i32 = 3;
    let mut glyphs = Vec::new();
    let mut widths = Vec::new();
    for c in &best_row {
        let left = (c.min_x + c.max_x + 1) as f32 / 2.0 - ww / 2.0;
        let mut g = vec![0f32; GLYPH_LEN];
        for oy in 0..GLYPH_H as i32 {
            for ox in 0..GLYPH_W as i32 {
                let mut sum = 0f32;
                for sy in 0..SUB {
                    for sx in 0..SUB {
                        let x = (left + (ox as f32 + (sx as f32 + 0.5) / SUB as f32) / GLYPH_W as f32 * ww).floor() as i32;
                        let y = (top as f32 + (oy as f32 + (sy as f32 + 0.5) / SUB as f32) / GLYPH_H as f32 * hh).floor() as i32;
                        if x < 0 || y < 0 || x >= gw || y >= gh {
                            continue;
                        }
                        let k = (y * gw + x) as usize;
                        if label[k] != 0 && x >= c.min_x && x <= c.max_x && find(&mut root, label[k]) == c.root {
                            sum += text[k];
                        }
                    }
                }
                g[(oy * GLYPH_W as i32 + ox) as usize] = sum / (SUB * SUB) as f32;
            }
        }
        glyphs.push(g);
        widths.push(c.w() as f32 / hh);
    }
    let mut tint = [0f32; 3];
    for c in &best_row {
        for k in 0..3 {
            tint[k] += c.cover[k];
        }
    }
    let major = if tint[RED] > tint[CREAM] && tint[RED] > tint[GREEN] {
        RED
    } else if tint[GREEN] > tint[CREAM] {
        GREEN
    } else {
        CREAM
    };
    let x0 = best_row[0].min_x;
    let x1 = best_row[best_row.len() - 1].max_x + 1;
    let bounds = PixelRect::new(pr.x + x0 * step, pr.y + top * step, (x1 - x0) * step, (bottom - top) * step);
    let cx0 = 0.max((x0 as f32 - 0.9 * h_exp) as i32);
    let cx1 = 0.max((x0 as f32 - 0.15 * h_exp) as i32);
    let mut gold = 0f32;
    let mut gold_cells = 0;
    for y in top..bottom {
        for x in cx0..cx1 {
            gold += cover[(y * gw + x) as usize * CHANNELS + GOLD];
            gold_cells += 1;
        }
    }
    let coin = if gold_cells == 0 { 0.0 } else { gold / gold_cells as f32 };
    let mut bright = 0f32;
    let mut ring_cells = 0;
    for y in top - 4..bottom + 4 {
        for x in x0 - 4..x1 + 4 {
            if x < 0 || y < 0 || x >= gw || y >= gh {
                continue;
            }
            if y >= top - 1 && y < bottom + 1 && x >= x0 - 1 && x < x1 + 1 {
                continue;
            }
            bright += cover[(y * gw + x) as usize * CHANNELS + BRIGHT];
            ring_cells += 1;
        }
    }
    let occluder = if ring_cells == 0 { 0.0 } else { bright / ring_cells as f32 };
    let block_x = ((pr.x as f32 + ((x0 + x1) * step) as f32 / 2.0 - t.offset_x) / t.scale - cx) / scale;
    let block_y = ((pr.y as f32 + ((top + bottom) * step) as f32 / 2.0 - t.offset_y) / t.scale - cy) / scale;
    Some(DigitRow {
        glyphs,
        tint: match major {
            RED => PriceTint::Red,
            GREEN => PriceTint::Green,
            _ => PriceTint::Cream,
        },
        bounds,
        center_x: block_x,
        center_y: block_y,
        coin_fraction: coin,
        occluder_fraction: occluder,
        widths,
    })
}
