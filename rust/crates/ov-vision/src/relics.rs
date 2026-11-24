//! Port of Relics/*.cs: the merchant relic and potion icon database and recogniser.

use std::path::Path;

use rayon::prelude::*;

use crate::geometry::{RectF, RefTransform, ScreenFrame};
use crate::image_io;
use crate::integral::IntegralImage;
use crate::ncc;
use crate::price::{PriceReaderOptions, PriceReading, ShopPriceReader};
use crate::results::{RecognizedRelic, RelicSlotState};
use crate::upgrade;

#[derive(Clone, Debug, PartialEq)]
pub struct CatalogRelic {
    pub id: String,
    pub rarity: String,
}

pub const SHOP_RARITIES: [&str; 4] = ["common", "uncommon", "rare", "shop"];

pub fn load_relic_catalog(path: &Path) -> Result<Vec<CatalogRelic>, String> {
    let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let doc: serde_json::Value = serde_json::from_str(&text).map_err(|e| e.to_string())?;
    let mut list = Vec::new();
    for r in doc.get("relics").and_then(|r| r.as_array()).ok_or("catalog has no relics")? {
        if let (Some(id), Some(ra)) = (r.get("id").and_then(|v| v.as_str()), r.get("rarity").and_then(|v| v.as_str())) {
            list.push(CatalogRelic { id: id.to_string(), rarity: ra.to_string() });
        }
    }
    Ok(list)
}

pub fn icon_name(relic_id: &str) -> String {
    relic_id.strip_prefix("RELIC.").unwrap_or(relic_id).to_lowercase()
}

#[derive(Clone, Debug, PartialEq)]
pub struct RelicEntry {
    pub index: usize,
    pub relic_id: String,
    pub rarity: String,
}

/// Straight-alpha RGBA image.
#[derive(Clone)]
pub struct RgbaImage {
    pub rgba: Vec<u8>,
    pub width: i32,
    pub height: i32,
}

impl RgbaImage {
    pub fn load(path: &Path) -> Result<Self, String> {
        let (rgba, width, height) = image_io::load_rgba(path)?;
        Ok(Self { rgba, width, height })
    }
}

pub const MASK_ALPHA: f32 = 0.92;
pub const COARSE_GRID: usize = 12;
pub const FINE_GRID: usize = 24;
pub const OUTLINE_DIR: &str = "outline";
const RUG_COLOUR: (f64, f64, f64) = (75.0, 110.0, 98.0);
const OUTLINE_ALPHA: f64 = 0.5;

pub struct RelicTemplates {
    pub grid: usize,
    pub length: usize,
    t: Vec<f32>,
    m: Vec<f32>,
    n: Vec<i32>,
    full: Vec<f32>,
}

impl RelicTemplates {
    pub fn build(images: &[RgbaImage], outlines: &[Option<RgbaImage>], g: usize) -> Self {
        let len = g * g * 3;
        let mut t = vec![0f32; images.len() * len];
        let mut m = vec![0f32; images.len() * len];
        let mut n = vec![0i32; images.len()];
        let mut full = vec![0f32; images.len() * len];
        for (k, img) in images.iter().enumerate() {
            let tk = &mut t[k * len..(k + 1) * len];
            let mk = &mut m[k * len..(k + 1) * len];
            let fk = &mut full[k * len..(k + 1) * len];
            let outline = outlines.get(k).and_then(|o| o.as_ref()).filter(|o| o.width == img.width && o.height == img.height);
            let side = img.width.max(img.height);
            let ox = (side - img.width) as f32 / 2.0;
            let oy = (side - img.height) as f32 / 2.0;
            let (ox, oy) = (ox as f64, oy as f64);
            for cy in 0..g {
                for cx in 0..g {
                    let x0 = (cx as i32 * side) as f64 / g as f64 - ox;
                    let x1 = ((cx as i32 + 1) * side) as f64 / g as f64 - ox;
                    let y0 = (cy as i32 * side) as f64 / g as f64 - oy;
                    let y1 = ((cy as i32 + 1) * side) as f64 / g as f64 - oy;
                    let (mut area, mut alpha, mut r, mut gr, mut b, mut back) = (0f64, 0f64, 0f64, 0f64, 0f64, 0f64);
                    let mut py = y0.floor() as i32;
                    while (py as f64) < y1.ceil() {
                        let mut px = x0.floor() as i32;
                        while (px as f64) < x1.ceil() {
                            let w = (((px + 1) as f64).min(x1) - (px as f64).max(x0)) * (((py + 1) as f64).min(y1) - (py as f64).max(y0));
                            if w > 0.0 {
                                area += w;
                                if px < 0 || py < 0 || px >= img.width || py >= img.height {
                                    back += w;
                                } else {
                                    let p = ((py * img.width + px) * 4) as usize;
                                    let a = img.rgba[p + 3] as f64 / 255.0;
                                    let shade = match outline {
                                        Some(o) => 1.0 - OUTLINE_ALPHA * o.rgba[p + 3] as f64 / 255.0,
                                        None => 1.0,
                                    };
                                    back += w * (1.0 - a) * shade;
                                    alpha += w * a;
                                    r += w * a * img.rgba[p] as f64;
                                    gr += w * a * img.rgba[p + 1] as f64;
                                    b += w * a * img.rgba[p + 2] as f64;
                                }
                            }
                            px += 1;
                        }
                        py += 1;
                    }
                    let o = (cy * g + cx) * 3;
                    if area > 0.0 {
                        fk[o] = ((r + back * RUG_COLOUR.0) / area) as f32;
                        fk[o + 1] = ((gr + back * RUG_COLOUR.1) / area) as f32;
                        fk[o + 2] = ((b + back * RUG_COLOUR.2) / area) as f32;
                    }
                    if area <= 0.0 || alpha / area < MASK_ALPHA as f64 {
                        continue;
                    }
                    tk[o] = (r / alpha) as f32;
                    tk[o + 1] = (gr / alpha) as f32;
                    tk[o + 2] = (b / alpha) as f32;
                    mk[o] = 1.0;
                    mk[o + 1] = 1.0;
                    mk[o + 2] = 1.0;
                    n[k] += 3;
                }
            }
            let mut sum = 0f64;
            let mut sq = 0f64;
            for i in 0..len {
                sum += tk[i] as f64;
            }
            let mean = if n[k] > 0 { sum / n[k] as f64 } else { 0.0 };
            for i in 0..len {
                if mk[i] > 0.0 {
                    tk[i] = (tk[i] as f64 - mean) as f32;
                    sq += tk[i] as f64 * tk[i] as f64;
                }
            }
            let inv = if sq > 1e-9 { (1.0 / sq.sqrt()) as f32 } else { 0.0 };
            for x in tk.iter_mut() {
                *x *= inv;
            }
            ncc::normalise(fk);
        }
        Self { grid: g, length: len, t, m, n, full }
    }

    pub fn count(&self) -> usize {
        self.n.len()
    }

    fn template(&self, i: usize) -> &[f32] {
        &self.t[i * self.length..(i + 1) * self.length]
    }
    fn mask(&self, i: usize) -> &[f32] {
        &self.m[i * self.length..(i + 1) * self.length]
    }
    pub fn full(&self, i: usize) -> &[f32] {
        &self.full[i * self.length..(i + 1) * self.length]
    }

    pub fn score_full(&self, i: usize, normalised_query: &[f32]) -> f32 {
        ncc::dot(self.full(i), normalised_query)
    }

    /// Masked NCC of relic `i` against a raw query `q` (with its squares `q2`).
    pub fn score(&self, i: usize, q: &[f32], q2: &[f32]) -> f32 {
        let n = self.n[i];
        if n == 0 {
            return 0.0;
        }
        let num = ncc::dot(self.template(i), q);
        let sum = ncc::dot(self.mask(i), q);
        let sum_sq = ncc::dot(self.mask(i), q2);
        let var = sum_sq - sum * sum / n as f32;
        if var <= 9.0 * n as f32 {
            return 0.0;
        }
        num / var.sqrt()
    }
}

pub struct RelicIconDatabase {
    pub entries: Vec<RelicEntry>,
    pub coarse: RelicTemplates,
    pub fine: RelicTemplates,
}

impl RelicIconDatabase {
    pub fn count(&self) -> usize {
        self.entries.len()
    }

    pub fn build(icon_dir: &Path, catalog: &[CatalogRelic], log: &mut dyn FnMut(String)) -> Result<Self, String> {
        let mut list: Vec<&CatalogRelic> = catalog.iter().filter(|r| SHOP_RARITIES.contains(&r.rarity.to_lowercase().as_str())).collect();
        list.sort_by(|a, b| a.id.as_bytes().cmp(b.id.as_bytes()));
        let mut icons = Vec::new();
        let mut outlines = Vec::new();
        for r in list {
            let file = icon_dir.join(format!("{}.png", icon_name(&r.id)));
            if !file.is_file() {
                log(format!("no icon for {}", r.id));
                continue;
            }
            icons.push((r.id.clone(), r.rarity.clone(), RgbaImage::load(&file)?));
            let outline = icon_dir.join(OUTLINE_DIR).join(format!("{}.png", icon_name(&r.id)));
            outlines.push(if outline.is_file() { Some(RgbaImage::load(&outline)?) } else { None });
        }
        Ok(Self::from_images(icons, outlines))
    }

    pub fn from_images(icons: Vec<(String, String, RgbaImage)>, outlines: Vec<Option<RgbaImage>>) -> Self {
        let entries = icons.iter().enumerate().map(|(i, x)| RelicEntry { index: i, relic_id: x.0.clone(), rarity: x.1.clone() }).collect();
        let images: Vec<RgbaImage> = icons.into_iter().map(|x| x.2).collect();
        Self {
            entries,
            coarse: RelicTemplates::build(&images, &outlines, COARSE_GRID),
            fine: RelicTemplates::build(&images, &outlines, FINE_GRID),
        }
    }

    pub fn build_potions(icon_dir: &Path) -> Result<Self, String> {
        let mut files: Vec<std::path::PathBuf> = std::fs::read_dir(icon_dir)
            .map_err(|e| format!("{}: {e}", icon_dir.display()))?
            .flatten()
            .map(|e| e.path())
            .filter(|p| p.is_file() && p.extension().is_some_and(|x| x == "png"))
            .collect();
        files.sort_by(|a, b| a.to_string_lossy().as_bytes().cmp(b.to_string_lossy().as_bytes()));
        let mut icons = Vec::new();
        let mut outlines = Vec::new();
        for file in files {
            let name = file.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
            icons.push((format!("POTION.{}", name.to_uppercase()), String::new(), RgbaImage::load(&file)?));
            let outline = icon_dir.join(OUTLINE_DIR).join(file.file_name().unwrap());
            outlines.push(if outline.is_file() { Some(RgbaImage::load(&outline)?) } else { None });
        }
        Ok(Self::from_images(icons, outlines))
    }
}

#[derive(Clone, Debug)]
pub struct RelicRecognizerOptions {
    pub accept_score: f32,
    pub accept_margin: f32,
    pub shape_score: f32,
    pub shape_margin: f32,
    pub empty_rug_share: f32,
    pub require_price_label: bool,
    pub search_radius: f32,
    pub candidates: usize,
}

impl Default for RelicRecognizerOptions {
    fn default() -> Self {
        Self {
            accept_score: 0.80,
            accept_margin: 0.15,
            shape_score: 0.70,
            shape_margin: 0.10,
            empty_rug_share: 0.9,
            require_price_label: true,
            search_radius: 8.0,
            candidates: 6,
        }
    }
}

pub const SLOT_X0: f32 = 118.0 + 871.0;
pub const SLOT_STEP_X: f32 = 150.0;
pub const SLOT_Y: f32 = 80.0 + 595.0;
pub const SMALL_SCALE: f32 = 0.65;
pub const HOVER_SCALE: f32 = 0.8;
pub const ICON_BOX: RectF = RectF::new(-56.0, -72.0, 120.0, 120.0);
pub const LABEL_BOX: RectF = RectF::new(-148.0, 52.0, 296.0, 54.0);
pub const SLOT_COUNT: usize = 3;
const POTION_PICTURE: RectF = RectF::new(-44.0, -50.0, 90.0, 90.0);

#[derive(Clone, Copy, Debug, PartialEq)]
struct BoxF {
    x: f32,
    y: f32,
    s: f32,
}

pub struct RelicRecognizer {
    pub db: RelicIconDatabase,
    potion: bool,
    first_x: f32,
    row_y: f32,
    prices: ShopPriceReader,
    pub options: RelicRecognizerOptions,
}

pub fn origin(i: usize) -> (f32, f32) {
    (SLOT_X0 + SLOT_STEP_X * i as f32, SLOT_Y)
}

pub fn icon_at(ox: f32, oy: f32, s: f32) -> RectF {
    RectF::new(ox + ICON_BOX.x * s, oy + ICON_BOX.y * s, ICON_BOX.width * s, ICON_BOX.height * s)
}

pub fn is_overlay(r: f32, g: f32, b: f32) -> bool {
    r.max(g.max(b)) < 80.0 || (r >= g && g >= b + 15.0 && r <= 140.0 && !is_rug(r, g, b))
}
pub fn is_rug(r: f32, g: f32, b: f32) -> bool {
    (90.0..=160.0).contains(&g) && g >= r + 8.0 && g >= b + 5.0 && r >= 45.0 && b >= 40.0 && b <= 120.0
}
pub fn is_hand(r: f32, g: f32, b: f32) -> bool {
    b >= 150.0 && b >= r + 25.0 && g >= r && b >= g - 10.0
}

fn desc(a: f32, b: f32) -> std::cmp::Ordering {
    b.partial_cmp(&a).unwrap_or(std::cmp::Ordering::Equal)
}

impl RelicRecognizer {
    pub fn new(db: RelicIconDatabase, options: RelicRecognizerOptions, price_options: PriceReaderOptions) -> Self {
        Self::with(db, false, SLOT_X0, SLOT_Y, options, price_options)
    }

    pub fn for_potions(db: RelicIconDatabase, first_x: f32, row_y: f32) -> Self {
        Self::with(db, true, first_x, row_y, RelicRecognizerOptions::default(), PriceReaderOptions::default())
    }

    pub fn for_relics(db: RelicIconDatabase, first_x: f32, row_y: f32) -> Self {
        Self::with(db, false, first_x, row_y, RelicRecognizerOptions::default(), PriceReaderOptions::default())
    }

    fn with(db: RelicIconDatabase, potion: bool, first_x: f32, row_y: f32, options: RelicRecognizerOptions, price_options: PriceReaderOptions) -> Self {
        Self { db, potion, first_x, row_y, prices: ShopPriceReader::new(price_options), options }
    }

    fn picture(&self) -> RectF {
        if self.potion { POTION_PICTURE } else { ICON_BOX }
    }

    pub fn recognize(&self, frame: &ScreenFrame<'_>) -> Vec<RecognizedRelic> {
        let t = RefTransform::for_frame(frame.width, frame.height);
        let step = ((t.scale + 1e-3).floor() as i32).max(1);
        let r = self.options.search_radius + 2.0;
        let p = self.picture();
        let band = RectF::new(
            self.first_x + p.x * 0.86 - r,
            self.row_y + p.y * 0.86 - r,
            SLOT_STEP_X * (SLOT_COUNT - 1) as f32 + p.width * 0.86 + 2.0 * r,
            p.height * 0.86 + 2.0 * r,
        );
        let img = IntegralImage::build(frame, t.to_pixel_rect(band), step);
        (0..SLOT_COUNT).into_par_iter().map(|i| self.match_slot(&img, &t, frame, i)).collect()
    }

    fn picture_at(&self, b: BoxF) -> RectF {
        let p = self.picture();
        RectF::new(b.x + p.x * b.s, b.y + p.y * b.s, p.width * b.s, p.height * b.s)
    }

    fn sample(&self, img: &IntegralImage, t: &RefTransform, b: BoxF, g: usize, q: &mut [f32], q2: &mut [f32]) {
        img.sample_grid(t.to_pixels(self.picture_at(b)), g as i32, g as i32, q);
        for i in 0..q.len() {
            q2[i] = q[i] * q[i];
        }
    }

    fn read_price(&self, frame: &ScreenFrame<'_>, b: BoxF, rarity: Option<&str>) -> Option<PriceReading> {
        if self.potion { self.prices.read_potion(frame, b.x, b.y, b.s) } else { self.prices.read_relic(frame, b.x, b.y, b.s, rarity) }
    }

    fn match_slot(&self, img: &IntegralImage, t: &RefTransform, frame: &ScreenFrame<'_>, slot: usize) -> RecognizedRelic {
        let ox0 = self.first_x + SLOT_STEP_X * slot as f32;
        let oy0 = self.row_y;
        let (coarse, fine) = (&self.db.coarse, &self.db.fine);
        let n = self.db.count();
        let mut q = vec![0f32; coarse.length];
        let mut q2 = vec![0f32; coarse.length];
        let mut best = vec![f32::NEG_INFINITY; n];
        let mut best_box = vec![BoxF { x: 0.0, y: 0.0, s: 0.0 }; n];
        let rad = self.options.search_radius;
        for s in [0.65f32, 0.70, 0.75, 0.80] {
            let mut dy = -rad;
            while dy <= rad + 1e-3 {
                let mut dx = -rad;
                while dx <= rad + 1e-3 {
                    let b = BoxF { x: ox0 + dx, y: oy0 + dy, s };
                    self.sample(img, t, b, coarse.grid, &mut q, &mut q2);
                    for k in 0..n {
                        let v = coarse.score(k, &q, &q2);
                        if v > best[k] {
                            best[k] = v;
                            best_box[k] = b;
                        }
                    }
                    dx += 4.0;
                }
                dy += 4.0;
            }
        }
        let mut fq = vec![0f32; fine.length];
        let mut fq2 = vec![0f32; fine.length];
        let mut layout_best = vec![f32::NEG_INFINITY; n];
        let mut layout_box = vec![BoxF { x: 0.0, y: 0.0, s: 0.0 }; n];
        for s in [SMALL_SCALE, HOVER_SCALE] {
            let b = BoxF { x: ox0, y: oy0, s };
            self.sample(img, t, b, fine.grid, &mut fq, &mut fq2);
            for k in 0..n {
                let v = fine.score(k, &fq, &fq2);
                if v > layout_best[k] {
                    layout_best[k] = v;
                    layout_box[k] = b;
                }
            }
        }
        let mut by_best: Vec<usize> = (0..n).collect();
        by_best.sort_by(|&a, &b| desc(best[a], best[b]));
        let mut by_layout: Vec<usize> = (0..n).collect();
        by_layout.sort_by(|&a, &b| desc(layout_best[a], layout_best[b]));
        let mut seeds: Vec<(usize, BoxF)> = Vec::new();
        for (k, b) in by_best
            .iter()
            .take(self.options.candidates)
            .map(|&k| (k, best_box[k]))
            .chain(by_layout.iter().take(self.options.candidates).map(|&k| (k, layout_box[k])))
        {
            if !seeds.iter().any(|s| s.0 == k && s.1 == b) {
                seeds.push((k, b));
            }
        }
        let mut win_score = f32::NEG_INFINITY;
        let mut win = BoxF { x: ox0, y: oy0, s: SMALL_SCALE };
        for (k, c) in seeds {
            let mut k_best = f32::NEG_INFINITY;
            let mut k_box = c;
            for pass in 0..2 {
                let centre = k_box;
                let d: f32 = if pass == 0 { 2.0 } else { 1.0 };
                let deltas: [f32; 3] = if pass == 0 { [-0.025, 0.0, 0.025] } else { [-0.01, 0.0, 0.01] };
                for ds in deltas {
                    let mut dy = -d;
                    while dy <= d + 1e-3 {
                        let mut dx = -d;
                        while dx <= d + 1e-3 {
                            let b = BoxF { x: centre.x + dx, y: centre.y + dy, s: (centre.s + ds).clamp(0.6, 0.85) };
                            self.sample(img, t, b, fine.grid, &mut fq, &mut fq2);
                            let v = fine.score(k, &fq, &fq2);
                            if v > k_best {
                                k_best = v;
                                k_box = b;
                            }
                            dx += d;
                        }
                        dy += d;
                    }
                }
            }
            if k_best > win_score {
                win_score = k_best;
                win = k_box;
            }
        }
        self.sample(img, t, win, fine.grid, &mut fq, &mut fq2);
        let mut ranked: Vec<(usize, f32)> = (0..n).map(|k| (k, fine.score(k, &fq, &fq2))).collect();
        ranked.sort_by(|a, b| desc(a.1, b.1));
        ranked.truncate(4);
        let score = ranked.first().map_or(0.0, |x| x.1);
        let margin = if ranked.len() > 1 { score - ranked[1].1 } else { score };
        let mut norm = fq.clone();
        let contrast = ncc::normalise(&mut norm);
        let mut shape: Vec<(usize, f32)> = (0..n).map(|k| (k, if contrast { fine.score_full(k, &norm) } else { 0.0 })).collect();
        shape.sort_by(|a, b| desc(a.1, b.1));
        shape.truncate(2);
        let shape_margin = if shape.len() > 1 {
            shape[0].1 - shape[1].1
        } else if !shape.is_empty() {
            shape[0].1
        } else {
            0.0
        };
        let agree = !ranked.is_empty() && !shape.is_empty() && shape[0].0 == ranked[0].0;
        let looks_right = agree
            && score >= self.options.accept_score
            && shape[0].1 >= self.options.shape_score
            && (margin >= self.options.accept_margin || shape_margin >= self.options.shape_margin);
        let pick = ranked.first().map(|x| &self.db.entries[x.0]);
        let resting = BoxF { x: ox0, y: oy0, s: SMALL_SCALE };
        let mut price_at = if looks_right { win } else { resting };
        let mut reading = self.read_price(frame, price_at, if looks_right { pick.map(|p| p.rarity.as_str()) } else { None });
        let accepted = looks_right && (!self.options.require_price_label || reading.as_ref().is_some_and(|r| r.plausible));
        if !accepted {
            price_at = resting;
        }
        if reading.is_none() && !accepted {
            reading = self.read_price(frame, BoxF { x: ox0, y: oy0, s: HOVER_SCALE }, None);
        }
        let (tip, hand, overlay, rug) = self.cover(img, t, resting);
        let state = if accepted {
            RelicSlotState::Identified
        } else if rug >= self.options.empty_rug_share && !reading.as_ref().is_some_and(|r| r.plausible) {
            RelicSlotState::Empty
        } else {
            RelicSlotState::Unknown
        };
        let price_box = RectF::new(
            price_at.x + LABEL_BOX.x * price_at.s,
            price_at.y + (if self.potion { 47.7 } else { LABEL_BOX.y }) * price_at.s,
            LABEL_BOX.width * price_at.s,
            LABEL_BOX.height * price_at.s,
        );
        RecognizedRelic {
            slot: slot as i32,
            state,
            relic_id: pick.map(|p| p.relic_id.clone()),
            rarity: pick.map(|p| p.rarity.clone()),
            score,
            margin,
            node_scale: win.s,
            icon_bounds: t.to_pixel_rect(self.picture_at(win)),
            price_bounds: t.to_pixel_rect(price_box),
            price: if accepted { reading.as_ref().and_then(|r| r.value) } else { None },
            candidates: ranked.iter().map(|x| (self.db.entries[x.0].relic_id.clone(), x.1)).collect(),
            price_reading: reading,
            tooltip_cover: tip,
            hand_cover: hand,
            overlay_cover: overlay,
            rug_share: rug,
            shape_score: shape.first().map_or(0.0, |x| x.1),
            shape_margin,
            shape_agrees: agree,
        }
    }

    fn cover(&self, img: &IntegralImage, t: &RefTransform, b: BoxF) -> (f32, f32, f32, f32) {
        const G: usize = 12;
        let mut c = vec![0f32; G * G * 3];
        img.sample_grid(t.to_pixels(self.picture_at(b)), G as i32, G as i32, &mut c);
        let (mut tip, mut hand, mut dark, mut rug) = (0, 0, 0, 0);
        for px in c.chunks_exact(3) {
            let (r, g, bl) = (px[0], px[1], px[2]);
            if upgrade::is_tooltip_slate(r, g, bl) {
                tip += 1;
            }
            if is_hand(r, g, bl) {
                hand += 1;
            }
            if is_overlay(r, g, bl) {
                dark += 1;
            }
            if is_rug(r, g, bl) {
                rug += 1;
            }
        }
        let k = (G * G) as f32;
        (tip as f32 / k, hand as f32 / k, dark as f32 / k, rug as f32 / k)
    }
}
