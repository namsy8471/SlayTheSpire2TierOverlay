//! Port of CardRecognizer.cs: classify the frame, find the offered cards, identify them.

use std::collections::HashSet;
use std::time::Instant;

use rayon::prelude::*;

use crate::artdb::CardArtDatabase;
use crate::catalog::{self, VisionCatalog, COLORLESS_POOL, OFFERABLE_POOLS};
use crate::classifier;
use crate::geometry::{RectF, RefTransform, ScreenFrame};
use crate::integral::IntegralImage;
use crate::layout::{self, card_geometry, SlotLayout};
use crate::ncc::{self, COARSE_H, COARSE_LENGTH, COARSE_W, FULL_H, FULL_LENGTH, FULL_W};
use crate::price::{PriceReaderOptions, PriceReading, ShopPriceReader};
use crate::relics::{self, RelicRecognizer};
use crate::results::*;
use crate::tiles::{PartialStage, TileGrid, TileMatcher};
use crate::upgrade;

#[derive(Clone, Debug)]
pub struct RecognizerOptions {
    pub accept_score: f32,
    pub accept_margin: f32,
    pub covered_score: f32,
    pub covered_margin: f32,
    pub tile_fit_min_tiles: i32,
    pub tile_fit_score: f32,
    pub slot_present_score: f32,
    pub slot_strong_score: f32,
    pub upgrade_fraction: f32,
    pub unoccluded_score: f32,
    pub title_covered_fraction: f32,
    pub hint_tolerance: f32,
    pub search_radius: f32,
    pub search_candidates: usize,
    /// Pools that can be offered; None = every database entry.
    pub candidate_pools: Option<Vec<String>>,
    pub grid_screens: bool,
    pub choice_screens: bool,
    pub price: Option<PriceReaderOptions>,
    pub empty_slot_rug_share: f32,
    pub partial_matching: bool,
    pub block_stage: PartialStage,
    pub row_stage: PartialStage,
    pub wide_search_below: f32,
    pub wide_search_up: f32,
    pub wide_search_shop: f32,
}

impl Default for RecognizerOptions {
    fn default() -> Self {
        Self {
            accept_score: 0.70,
            accept_margin: 0.10,
            covered_score: 0.55,
            covered_margin: 0.25,
            tile_fit_min_tiles: 8,
            tile_fit_score: 0.82,
            slot_present_score: 0.60,
            slot_strong_score: 0.75,
            upgrade_fraction: 0.02,
            unoccluded_score: 0.85,
            title_covered_fraction: 0.15,
            hint_tolerance: 0.03,
            search_radius: 12.0,
            search_candidates: 16,
            candidate_pools: Some(OFFERABLE_POOLS.iter().map(|s| s.to_string()).collect()),
            grid_screens: true,
            choice_screens: true,
            price: Some(PriceReaderOptions::default()),
            empty_slot_rug_share: 0.9,
            partial_matching: true,
            block_stage: PartialStage {
                agree: 0.7,
                flat_energy: 0.1,
                min_tiles: 5,
                accept_score: 0.85,
                accept_margin: 0.30,
                whole_top_k: 8,
                hinted_pool_only: false,
                free_margin: 0.40,
            },
            row_stage: PartialStage {
                agree: 0.85,
                flat_energy: 0.1,
                min_tiles: 2,
                accept_score: 0.90,
                accept_margin: 0.30,
                whole_top_k: 0,
                hinted_pool_only: false,
                free_margin: f32::INFINITY,
            },
            wide_search_below: 0.70,
            wide_search_up: 44.0,
            wide_search_shop: 24.0,
        }
    }
}

#[derive(Clone, Copy, Default)]
struct Presence {
    present: i32,
    strong: i32,
    empty: i32,
    sum: f32,
}

fn better(a: Presence, b: Presence) -> bool {
    if a.strong != b.strong {
        a.strong > b.strong
    } else if a.empty != b.empty {
        a.empty < b.empty
    } else {
        a.sum > b.sum
    }
}

#[derive(Clone, Default)]
struct SlotBox {
    full: Option<Vec<f32>>,
    x: f32,
    y: f32,
    scale: f32,
}

/// Inserts into an insertion-ordered set (C# HashSet<int> without removals enumerates in insertion order).
fn add_unique(v: &mut Vec<usize>, seen: &mut HashSet<usize>, x: usize) {
    if seen.insert(x) {
        v.push(x);
    }
}

pub struct CardRecognizer {
    pub db: CardArtDatabase,
    offerable: Vec<bool>,
    blocks: TileMatcher,
    rows: TileMatcher,
    price_reader: Option<ShopPriceReader>,
    pub options: RecognizerOptions,
    pub catalog: Option<VisionCatalog>,
    pub relics: Option<RelicRecognizer>,
    pub potions: Option<RelicRecognizer>,
}

impl CardRecognizer {
    pub fn new(db: CardArtDatabase, options: RecognizerOptions) -> Self {
        let offerable = db
            .entries
            .iter()
            .map(|e| options.candidate_pools.as_ref().is_none_or(|p| p.contains(&e.pool)))
            .collect();
        let blocks = TileMatcher::new(&db, TileGrid::blocks());
        let rows = TileMatcher::new(&db, TileGrid::rows());
        let price_reader = options.price.clone().map(ShopPriceReader::new);
        Self { db, offerable, blocks, rows, price_reader, options, catalog: None, relics: None, potions: None }
    }

    fn allowed(&self) -> impl Fn(usize) -> bool + '_ {
        move |i| self.offerable[i]
    }

    pub fn recognize(&self, frame: &ScreenFrame<'_>, hints: &RecognitionHints) -> Result<ScreenRecognition, String> {
        let sw = Instant::now();
        frame.validate()?;
        let gate = classifier::evaluate(frame);
        let t = RefTransform::for_frame(frame.width, frame.height);
        let step = ((t.scale + 1e-3).floor() as i32).max(1);
        let mut notes: Vec<String> = Vec::new();
        let mut examined: Vec<RecognizedCard> = Vec::new();
        let ms = |sw: &Instant| sw.elapsed().as_secs_f64() * 1000.0;
        let make = |kind, cards, slots, gate: &ScreenGate, detail: String, sw: &Instant| ScreenRecognition {
            kind,
            cards,
            slots,
            gate: gate.clone(),
            elapsed_ms: ms(sw),
            detail,
            relics: Vec::new(),
            potions: Vec::new(),
        };
        if gate.inspect_likely {
            return Ok(make(ScreenKind::Other, vec![], vec![], &gate, "inspect view".into(), &sw));
        }
        if gate.reward_likely {
            let (slots, detail, kind) = self.recognize_reward(frame, &t, step, hints);
            let accepted: Vec<RecognizedCard> = slots.iter().filter(|s| s.accepted).cloned().collect();
            if accepted.iter().filter(|c| c.partial.is_none()).count() >= 2 {
                return Ok(make(kind, accepted, slots, &gate, detail, &sw));
            }
            notes.push(format!("reward banner but {} card(s): {}", accepted.len(), detail));
            examined = slots;
        }
        if gate.shop_likely {
            let slots = self.recognize_shop(frame, &t, step, hints);
            let accepted: Vec<RecognizedCard> = slots.iter().filter(|s| s.accepted).cloned().collect();
            let relics = self.relics.as_ref().map(|r| r.recognize(frame)).unwrap_or_default();
            let potions = self.potions.as_ref().map(|r| r.recognize(frame)).unwrap_or_default();
            if accepted.iter().any(|c| c.partial.is_none()) || relics.iter().any(|r| r.accepted()) || potions.iter().any(|r| r.accepted()) {
                let mut r = make(ScreenKind::Shop, accepted, slots, &gate, "shop".into(), &sw);
                r.relics = relics;
                r.potions = potions;
                return Ok(r);
            }
            notes.push("shop rug but no cards".into());
            examined = slots;
        }
        if self.options.grid_screens {
            let (slots, detail) = self.recognize_grid(frame, &t, step, hints);
            let accepted: Vec<RecognizedCard> = slots.iter().filter(|s| s.accepted).cloned().collect();
            if accepted.iter().filter(|c| c.partial.is_none()).count() >= 2 {
                return Ok(make(ScreenKind::CardGrid, accepted, slots, &gate, detail, &sw));
            }
            if !slots.is_empty() {
                notes.push(format!("grid-like but {} card(s): {}", accepted.len(), detail));
                if examined.is_empty() {
                    examined = slots;
                }
            }
        }
        Ok(make(ScreenKind::Other, vec![], examined, &gate, notes.join("; "), &sw))
    }

    // ------------------------------------------------------------------ reward screen

    fn recognize_reward(&self, frame: &ScreenFrame<'_>, t: &RefTransform, step: i32, hints: &RecognitionHints) -> (Vec<RecognizedCard>, String, ScreenKind) {
        let img = IntegralImage::build(frame, t.to_pixel_rect(RectF::new(80.0, 380.0, 1760.0, 285.0)), step);
        let mut chosen: Option<Vec<SlotLayout>> = None;
        let mut detail = String::new();
        let mut best = Presence::default();
        let choices: &[bool] = if self.options.choice_screens { &[false, true] } else { &[false] };
        for n in [3, 4, 2, 5] {
            for &choice in choices {
                let lay = if choice { layout::choice(n, 1.0) } else { layout::reward(n, 1.0) };
                let p = self.quick_presence(&img, t, &lay, 0.76, 1.04);
                if p.present < (if n >= 3 { n - 1 } else { n }) {
                    continue;
                }
                if chosen.is_none() || better(p, best) {
                    best = p;
                    chosen = Some(lay);
                    detail = format!("{n} cards{}", if choice { ", choose-a-card" } else { "" });
                }
            }
        }
        if chosen.is_none() {
            let mut best_sum = 0f32;
            let mut spread = 0.2f32;
            while spread < 0.99 {
                for &choice in choices {
                    let lay = if choice { layout::choice(3, spread) } else { layout::reward(3, spread) };
                    let p = self.quick_presence(&img, t, &lay, 0.76, 0.84);
                    if p.present >= 2 && p.sum > best_sum {
                        best_sum = p.sum;
                        chosen = Some(lay);
                        detail = format!("3 cards{}, opening (spread {spread:.2})", if choice { ", choose-a-card" } else { "" });
                    }
                }
                spread += 0.05;
            }
        }
        let Some(chosen) = chosen else { return (vec![], "no card row found".into(), ScreenKind::Other) };
        let first_kind = chosen[0].kind;
        let n_chosen = chosen.len();
        let cards = self.match_all(&img, t, chosen, hints, true);
        if cards.len() < n_chosen {
            detail += &format!(" → {} (outer slots empty)", cards.len());
        }
        let kind = row_kind(&cards, t).unwrap_or(if first_kind == CardSlotKind::Choice { ScreenKind::CardChoice } else { ScreenKind::RewardCardSelect });
        (cards, detail, kind)
    }

    fn match_all(&self, img: &IntegralImage, t: &RefTransform, chosen: Vec<SlotLayout>, hints: &RecognitionHints, trim_row: bool) -> Vec<RecognizedCard> {
        let results: Vec<(RecognizedCard, SlotBox)> =
            chosen.par_iter().map(|s| self.match_slot(img, t, s, 0.76, 1.04, hints, None)).collect();
        let (mut cards, mut boxes): (Vec<RecognizedCard>, Vec<SlotBox>) = results.into_iter().unzip();
        let mut chosen = chosen;
        while trim_row && cards.len() >= 4 && !cards[0].accepted && !cards[cards.len() - 1].accepted {
            let k = chosen.len();
            chosen = chosen[1..k - 1].iter().map(|s| SlotLayout { index: s.index - 1, ..*s }).collect();
            let k = cards.len();
            cards = cards[1..k - 1]
                .iter()
                .map(|c| {
                    let mut c = c.clone();
                    c.slot -= 1;
                    c
                })
                .collect();
            let k = boxes.len();
            boxes = boxes[1..k - 1].to_vec();
        }
        if cards.iter().filter(|c| c.accepted).count() >= 2 {
            cards = (0..chosen.len())
                .into_par_iter()
                .map(|i| self.rescue_covered(img, t, &chosen[i], 0.76, 1.04, &cards[i], &boxes[i], hints, None))
                .collect();
        }
        cards
    }

    // ------------------------------------------------------------------ card grid

    fn recognize_grid(&self, frame: &ScreenFrame<'_>, t: &RefTransform, step: i32, hints: &RecognitionHints) -> (Vec<RecognizedCard>, String) {
        let img = IntegralImage::build(frame, t.to_pixel_rect(RectF::new(215.0, 70.0, 1440.0, 940.0)), step);
        let layouts = [
            (layout::grid(false), "grid, 1 row"),
            (layout::grid(true), "grid, 2+ rows"),
            (layout::grid_scrolled(layout::GRID_FOCUS_SCROLL), "grid, pad-scrolled"),
            (layout::grid_scrolled(layout::GRID_END_SCROLL), "grid, pad-scrolled to the end"),
        ];
        let mut chosen: Option<Vec<SlotLayout>> = None;
        let mut best = Presence::default();
        let mut detail = "";
        for (lay, name) in layouts {
            let p = self.quick_presence(&img, t, &lay, 0.76, 1.04);
            if p.present < 2 {
                continue;
            }
            if chosen.is_none() || better(p, best) {
                best = p;
                chosen = Some(lay);
                detail = name;
            }
        }
        match chosen {
            None => (vec![], "no grid".into()),
            Some(c) => (self.match_all(&img, t, c, hints, false), detail.to_string()),
        }
    }

    // ------------------------------------------------------------------ shop

    fn recognize_shop(&self, frame: &ScreenFrame<'_>, t: &RefTransform, step: i32, hints: &RecognitionHints) -> Vec<RecognizedCard> {
        let top = IntegralImage::build(frame, t.to_pixel_rect(RectF::new(285.0, 190.0, 1355.0, 235.0)), step);
        let bottom = IntegralImage::build(frame, t.to_pixel_rect(RectF::new(375.0, 563.0, 535.0, 235.0)), step);
        let slots = layout::shop();
        let image_of = |i: usize| if slots[i].kind == CardSlotKind::ShopColorless { &bottom } else { &top };
        let results: Vec<(RecognizedCard, SlotBox)> =
            (0..slots.len()).into_par_iter().map(|i| self.match_slot(image_of(i), t, &slots[i], 0.61, 0.84, hints, Some(frame))).collect();
        let (mut cards, boxes): (Vec<RecognizedCard>, Vec<SlotBox>) = results.into_iter().unzip();
        if cards.iter().any(|c| c.accepted) {
            cards = (0..slots.len())
                .into_par_iter()
                .map(|i| self.rescue_covered(image_of(i), t, &slots[i], 0.61, 0.84, &cards[i], &boxes[i], hints, Some(frame)))
                .collect();
        }
        for i in 0..slots.len() {
            if cards[i].accepted {
                continue;
            }
            let rug = rug_share(image_of(i), t, &slots[i]);
            cards[i].rug_share = rug;
            cards[i].slot_empty = rug >= self.options.empty_slot_rug_share && cards[i].price_reading.is_none();
        }
        cards
    }

    // ------------------------------------------------------------------ per-slot matching

    fn quick_presence(&self, img: &IntegralImage, t: &RefTransform, lay: &[SlotLayout], min_scale: f32, max_scale: f32) -> Presence {
        let mut v = vec![0f32; COARSE_LENGTH];
        let (mut present, mut strong) = (0, 0);
        let mut sum = 0f32;
        let allowed = self.allowed();
        for slot in lay {
            let mut best = 0f32;
            for s in [slot.small_scale, slot.hover_scale] {
                if s < min_scale || s > max_scale {
                    continue;
                }
                if !extract_at(img, t, slot.center_x, slot.center_y, s, &mut v) {
                    continue;
                }
                let top = self.db.top_k(&v, 1, Some(&allowed));
                if let Some(m) = top.first() {
                    best = best.max(m.score);
                }
            }
            sum += best;
            if best >= self.options.slot_present_score {
                present += 1;
            }
            if best >= self.options.slot_strong_score {
                strong += 1;
            }
        }
        Presence { present, strong, empty: lay.len() as i32 - present, sum }
    }

    #[allow(clippy::too_many_arguments)]
    fn match_slot(
        &self,
        img: &IntegralImage,
        t: &RefTransform,
        slot: &SlotLayout,
        min_scale: f32,
        max_scale: f32,
        hints: &RecognitionHints,
        price_frame: Option<&ScreenFrame<'_>>,
    ) -> (RecognizedCard, SlotBox) {
        let o = &self.options;
        let allowed = self.allowed();
        let mut coarse = vec![0f32; COARSE_LENGTH];
        let mut cand: Vec<usize> = Vec::new();
        let mut seen: HashSet<usize> = HashSet::new();
        for s in [slot.small_scale, slot.hover_scale] {
            if !extract_at(img, t, slot.center_x, slot.center_y, s, &mut coarse) {
                continue;
            }
            for m in self.db.top_k(&coarse, o.search_candidates, Some(&allowed)) {
                add_unique(&mut cand, &mut seen, m.entry_index);
            }
        }
        if cand.is_empty() {
            return (empty(t, slot), SlotBox::default());
        }
        let mut best_score = f32::NEG_INFINITY;
        let (mut bx, mut by, mut bs) = (slot.center_x, slot.center_y, slot.small_scale);
        let try_at = |cx: f32, cy: f32, s: f32, buf: &mut [f32], cand: &[usize], best_score: &mut f32, bx: &mut f32, by: &mut f32, bs: &mut f32| {
            if !extract_at(img, t, cx, cy, s, buf) {
                return;
            }
            let (score, _) = self.db.best_of(buf, cand);
            if score > *best_score {
                *best_score = score;
                *bx = cx;
                *by = cy;
                *bs = s;
            }
        };
        // (2) scale sweep
        let mut s = min_scale;
        while s <= max_scale + 1e-4 {
            try_at(slot.center_x, slot.center_y, s, &mut coarse, &cand, &mut best_score, &mut bx, &mut by, &mut bs);
            s += 0.02;
        }
        // (3) coarse offsets
        let r = o.search_radius;
        let (cx0, cy0, s0) = (bx, by, bs);
        let mut dy = -r;
        while dy <= r + 1e-3 {
            let mut dx = -r;
            while dx <= r + 1e-3 {
                try_at(cx0 + dx, cy0 + dy, s0, &mut coarse, &cand, &mut best_score, &mut bx, &mut by, &mut bs);
                dx += 4.0;
            }
            dy += 4.0;
        }
        if !slot.kind.is_shop() && best_score < o.wide_search_below {
            for s in [slot.small_scale, slot.hover_scale] {
                let mut dy = -o.wide_search_up * 0.75;
                while dy < -1.0 {
                    if extract_at(img, t, slot.center_x, slot.center_y + dy, s, &mut coarse) {
                        for m in self.db.top_k(&coarse, o.search_candidates / 2, Some(&allowed)) {
                            add_unique(&mut cand, &mut seen, m.entry_index);
                        }
                    }
                    dy += o.wide_search_up / 4.0;
                }
            }
            for s in [slot.small_scale, slot.hover_scale] {
                let mut dy = -o.wide_search_up;
                while dy <= r + 1e-3 {
                    let mut dx = -r;
                    while dx <= r + 1e-3 {
                        try_at(slot.center_x + dx, slot.center_y + dy, s, &mut coarse, &cand, &mut best_score, &mut bx, &mut by, &mut bs);
                        dx += 4.0;
                    }
                    dy += 4.0;
                }
            }
        } else if slot.kind.is_shop() && best_score < o.wide_search_below {
            let w = o.wide_search_shop;
            for s in [slot.small_scale, slot.hover_scale] {
                let mut dy = -w / 2.0;
                while dy <= w / 2.0 + 1e-3 {
                    let mut dx = -w / 2.0;
                    while dx <= w / 2.0 + 1e-3 {
                        if (dx != 0.0 || dy != 0.0) && extract_at(img, t, slot.center_x + dx, slot.center_y + dy, s, &mut coarse) {
                            for m in self.db.top_k(&coarse, o.search_candidates / 2, Some(&allowed)) {
                                add_unique(&mut cand, &mut seen, m.entry_index);
                            }
                        }
                        dx += w / 2.0;
                    }
                    dy += w / 2.0;
                }
            }
            for s in [slot.small_scale, slot.hover_scale] {
                let mut dy = -w;
                while dy <= w + 1e-3 {
                    let mut dx = -w;
                    while dx <= w + 1e-3 {
                        try_at(slot.center_x + dx, slot.center_y + dy, s, &mut coarse, &cand, &mut best_score, &mut bx, &mut by, &mut bs);
                        dx += 4.0;
                    }
                    dy += 4.0;
                }
            }
        }
        // (4) fine offsets, then a final scale nudge
        let (cx0, cy0) = (bx, by);
        for dy in -2..=2 {
            for dx in -2..=2 {
                try_at(cx0 + dx as f32, cy0 + dy as f32, s0, &mut coarse, &cand, &mut best_score, &mut bx, &mut by, &mut bs);
            }
        }
        let (x1, y1, s1) = (bx, by, bs);
        try_at(x1, y1, s1 - 0.01, &mut coarse, &cand, &mut best_score, &mut bx, &mut by, &mut bs);
        let (x1, y1, s1b) = (bx, by, bs);
        try_at(x1, y1, s1b + 0.01, &mut coarse, &cand, &mut best_score, &mut bx, &mut by, &mut bs);
        // (5) full-resolution identification
        let mut full = vec![0f32; FULL_LENGTH];
        if !extract_at(img, t, bx, by, bs, &mut full) {
            return (empty(t, slot), SlotBox::default());
        }
        let top = self.db.top_k(&full, 8, Some(&allowed));
        let sb = SlotBox { full: Some(full.clone()), x: bx, y: by, scale: bs };
        if top.is_empty() {
            return (empty(t, slot), sb);
        }
        let (pick, ambiguous) = self.decide(&top, hints);
        let twins = self.db.twins_of(&pick.card_id);
        let runner_up = top
            .iter()
            .filter(|m| m.card_id != pick.card_id && !twins.contains(&m.card_id))
            .map(|m| m.score)
            .fold(None, |a: Option<f32>, s| Some(a.map_or(s, |a| a.max(s))))
            .unwrap_or(-1.0);
        let margin = pick.score - runner_up;
        let mut accepted =
            (pick.score >= o.accept_score && margin >= o.accept_margin) || (pick.score >= o.covered_score && margin >= o.covered_margin);
        if accepted && o.partial_matching {
            let (fit_tiles, fit_score) = self.blocks.fit(&self.db, &full, pick.entry_index, &o.block_stage);
            accepted = fit_tiles >= o.tile_fit_min_tiles && fit_score >= o.tile_fit_score;
        }
        let (green, covered) = upgrade::title_evidence(img, t, bx, by, bs);
        let upgraded = green >= o.upgrade_fraction;
        let upgrade_known = upgraded || (pick.score >= o.unoccluded_score && covered < o.title_covered_fraction);
        let price = if slot.kind.is_shop() { Some(t.to_pixel_rect(layout::shop_price(bx, by, bs))) } else { None };
        let reading = self.read_price(price_frame, slot, &pick.card_id, bx, by, bs);
        let card = RecognizedCard {
            slot: slot.index,
            slot_kind: slot.kind,
            card_id: Some(pick.card_id.clone()),
            upgraded,
            score: pick.score,
            margin,
            accepted,
            ambiguous_with: ambiguous,
            upgrade_evidence: green,
            upgrade_known,
            node_scale: bs,
            art_bounds: t.to_pixel_rect(card_geometry::place(card_geometry::ART, bx, by, bs)),
            card_bounds: t.to_pixel_rect(card_geometry::place(card_geometry::CARD, bx, by, bs)),
            price_bounds: price,
            price: reading.as_ref().and_then(|r| r.value),
            candidates: top.iter().take(3).cloned().collect(),
            partial: None,
            missing_portrait: self.db.entries[pick.entry_index].is_missing_portrait(),
            price_reading: reading,
            rug_share: 0.0,
            slot_empty: false,
        };
        (card, sb)
    }

    fn read_price(&self, price_frame: Option<&ScreenFrame<'_>>, slot: &SlotLayout, card_id: &str, bx: f32, by: f32, bs: f32) -> Option<PriceReading> {
        let pf = price_frame?;
        let reader = self.price_reader.as_ref()?;
        if !slot.kind.is_shop() {
            return None;
        }
        let rarity = self.catalog.as_ref().and_then(|c| c.find(card_id)).and_then(|c| c.rarity.clone());
        let mut reading = reader.read(pf, bx, by, bs, slot.kind, rarity.as_deref());
        for s in [slot.small_scale, slot.hover_scale] {
            if reading.as_ref().is_some_and(|r| r.value.is_some()) {
                break;
            }
            let retry = reader.read(pf, slot.center_x, slot.center_y, s, slot.kind, rarity.as_deref());
            if retry.as_ref().is_some_and(|r| r.value.is_some()) || reading.is_none() {
                reading = retry;
            }
        }
        reading
    }

    /// Picks the card from ranked candidates: the hinted pool wins near-ties; identical-art twins by pool.
    pub fn decide(&self, top: &[ArtMatch], hints: &RecognitionHints) -> (ArtMatch, Option<String>) {
        let char_pool = hints.character_id.as_deref().map(catalog::pool_of_character);
        let preferred = |m: &ArtMatch| {
            char_pool.as_ref().is_some_and(|cp| {
                let p = &self.db.entries[m.entry_index].pool;
                p == cp || p == COLORLESS_POOL
            })
        };
        let mut pick = top[0].clone();
        if char_pool.is_some() && !preferred(&pick) {
            for m in top {
                if m.score < pick.score - self.options.hint_tolerance {
                    break;
                }
                if preferred(m) {
                    pick = m.clone();
                    break;
                }
            }
        }
        let mut ambiguous = None;
        let twins = self.db.twins_of(&pick.card_id).clone();
        for m in top {
            if m.card_id == pick.card_id || !twins.contains(&m.card_id) || m.score < pick.score - 0.03 {
                continue;
            }
            if char_pool.is_some() && preferred(&pick) && !preferred(m) {
                continue;
            }
            if char_pool.is_some() && preferred(m) && !preferred(&pick) {
                pick = m.clone();
                continue;
            }
            ambiguous = Some(m.card_id.clone());
        }
        (pick, ambiguous)
    }

    #[allow(clippy::too_many_arguments)]
    fn rescue_covered(
        &self,
        img: &IntegralImage,
        t: &RefTransform,
        slot: &SlotLayout,
        min_scale: f32,
        max_scale: f32,
        c: &RecognizedCard,
        b: &SlotBox,
        hints: &RecognitionHints,
        price_frame: Option<&ScreenFrame<'_>>,
    ) -> RecognizedCard {
        let o = &self.options;
        let Some(box_full) = b.full.as_ref() else { return c.clone() };
        if !o.partial_matching || c.accepted {
            return c.clone();
        }
        let allowed = self.allowed();
        let first_top: Vec<String> = c.candidates.iter().map(|m| m.card_id.clone()).collect();
        let mut r1 = Vec::new();
        let mut r2 = Vec::new();
        let first_blocks = self.evaluate_partial(box_full, &first_top, &self.blocks, &o.block_stage, &allowed, hints, &mut r1).0;
        let first_rows = self.evaluate_partial(box_full, &first_top, &self.rows, &o.row_stage, &allowed, hints, &mut r2).0;
        let first_best = first_blocks.or(first_rows);
        let with_partial = |p: Option<PartialMatch>| {
            let mut x = c.clone();
            if p.is_some() {
                x.partial = p;
            }
            x
        };
        let mut cand: Vec<usize> = Vec::new();
        let mut seen = HashSet::new();
        for m in c.candidates.iter().take(3).chain(r1.iter().take(3)).chain(r2.iter().take(3)) {
            add_unique(&mut cand, &mut seen, m.entry_index);
        }
        if cand.is_empty() {
            return with_partial(first_best);
        }
        let mut buf = vec![0f32; FULL_LENGTH];
        let mut best_obj = f32::NEG_INFINITY;
        let (mut bx, mut by, mut bs) = (b.x, b.y, b.scale);
        let mut try_at = |x: f32, y: f32, s: f32, best_obj: &mut f32, bx: &mut f32, by: &mut f32, bs: &mut f32| {
            if s < min_scale - 1e-4 || s > max_scale + 1e-4 || !extract_at(img, t, x, y, s, &mut buf) {
                return;
            }
            let obj = self.blocks.agreement(&self.db, &buf, &cand, &o.block_stage) + self.rows.agreement(&self.db, &buf, &cand, &o.row_stage);
            if obj > *best_obj {
                *best_obj = obj;
                *bx = x;
                *by = y;
                *bs = s;
            }
        };
        try_at(b.x, b.y, b.scale, &mut best_obj, &mut bx, &mut by, &mut bs);
        let mut s = min_scale;
        while s <= max_scale + 1e-4 {
            try_at(b.x, b.y, s, &mut best_obj, &mut bx, &mut by, &mut bs);
            s += 0.02;
        }
        let (cx0, cy0, bs0) = (bx, by, bs);
        let mut dy = -6.0f32;
        while dy <= 6.0 {
            let mut dx = -6.0f32;
            while dx <= 6.0 {
                try_at(cx0 + dx, cy0 + dy, bs0, &mut best_obj, &mut bx, &mut by, &mut bs);
                dx += 2.0;
            }
            dy += 2.0;
        }
        let (cx0, cy0, bs1) = (bx, by, bs);
        for dy in -1..=1 {
            for dx in -1..=1 {
                try_at(cx0 + dx as f32, cy0 + dy as f32, bs1, &mut best_obj, &mut bx, &mut by, &mut bs);
            }
        }
        let (x1, y1, s1) = (bx, by, bs);
        try_at(x1, y1, s1 - 0.01, &mut best_obj, &mut bx, &mut by, &mut bs);
        let (x1, y1, s1b) = (bx, by, bs);
        try_at(x1, y1, s1b + 0.01, &mut best_obj, &mut bx, &mut by, &mut bs);
        let mut boxes = vec![(bx, by, bs)];
        for dy in [0.0f32, -1.0, 1.0] {
            if (slot.center_x - bx).abs() > 0.5 || (slot.center_y + dy - by).abs() > 0.5 || (slot.small_scale - bs).abs() > 0.005 {
                boxes.push((slot.center_x, slot.center_y + dy, slot.small_scale));
            }
        }
        let mut full = vec![0f32; FULL_LENGTH];
        let mut partial: Option<PartialMatch> = None;
        let mut ranking: Vec<ArtMatch> = Vec::new();
        let mut ambiguous: Option<String> = None;
        for (x, y, sc) in boxes {
            if !extract_at(img, t, x, y, sc, &mut full) {
                continue;
            }
            let (p, rk, amb) = self.decide_covered(&full, hints);
            let Some(p) = p else { continue };
            let p_accepted = p.accepted;
            if partial.as_ref().is_none_or(|q| p.accepted || (!q.accepted && p.margin > q.margin)) {
                partial = Some(p);
                ranking = rk;
                ambiguous = amb;
                bx = x;
                by = y;
                bs = sc;
            }
            if p_accepted {
                break;
            }
        }
        let Some(partial) = partial else { return with_partial(first_best) };
        if !partial.accepted {
            let mut x = c.clone();
            x.partial = Some(partial);
            return x;
        }
        extract_at(img, t, bx, by, bs, &mut full);
        let pick = ranking.iter().find(|m| m.card_id == partial.card_id).cloned().expect("pick in ranking");
        let whole = ncc::dot(&full, self.db.full_vector(pick.entry_index));
        let (green, covered) = upgrade::title_evidence(img, t, bx, by, bs);
        let upgraded = green >= o.upgrade_fraction;
        let reading = self.read_price(price_frame, slot, &partial.card_id, bx, by, bs);
        let mut x = c.clone();
        x.card_id = Some(partial.card_id.clone());
        x.upgraded = upgraded;
        x.score = whole;
        x.margin = partial.margin;
        x.accepted = true;
        x.ambiguous_with = ambiguous;
        x.upgrade_evidence = green;
        x.upgrade_known = upgraded || (whole >= o.unoccluded_score && covered < o.title_covered_fraction);
        x.node_scale = bs;
        x.art_bounds = t.to_pixel_rect(card_geometry::place(card_geometry::ART, bx, by, bs));
        x.card_bounds = t.to_pixel_rect(card_geometry::place(card_geometry::CARD, bx, by, bs));
        x.price_bounds = if slot.kind.is_shop() { Some(t.to_pixel_rect(layout::shop_price(bx, by, bs))) } else { None };
        x.candidates = ranking.iter().take(3).cloned().collect();
        x.partial = Some(partial);
        x.price = reading.as_ref().and_then(|r| r.value);
        x.price_reading = reading;
        x
    }

    fn stage_filter<'a>(&'a self, stage: &PartialStage, hints: &RecognitionHints) -> Box<dyn Fn(usize) -> bool + 'a> {
        let pool = if stage.hinted_pool_only { hints.character_id.as_deref().map(catalog::pool_of_character) } else { None };
        match pool {
            None => Box::new(move |i| self.offerable[i]),
            Some(p) => Box::new(move |i| {
                let e = &self.db.entries[i];
                self.offerable[i] && (e.pool == p || e.pool == COLORLESS_POOL)
            }),
        }
    }

    fn decide_covered(&self, full: &[f32], hints: &RecognitionHints) -> (Option<PartialMatch>, Vec<ArtMatch>, Option<String>) {
        let o = &self.options;
        let allowed = self.allowed();
        let whole_top: Vec<String> = self.db.top_k(full, 8, Some(&allowed)).into_iter().map(|m| m.card_id).collect();
        let mut ranking = Vec::new();
        let bf = self.stage_filter(&o.block_stage, hints);
        let (blocks, ambiguous) = self.evaluate_partial(full, &whole_top, &self.blocks, &o.block_stage, &*bf, hints, &mut ranking);
        if blocks.as_ref().is_some_and(|b| b.accepted) {
            return (blocks, ranking, ambiguous);
        }
        let mut row_ranking = Vec::new();
        let rf = self.stage_filter(&o.row_stage, hints);
        let (rows, row_ambiguous) = self.evaluate_partial(full, &whole_top, &self.rows, &o.row_stage, &*rf, hints, &mut row_ranking);
        match rows {
            Some(r) if r.accepted || blocks.is_none() || r.margin > blocks.as_ref().unwrap().margin => (Some(r), row_ranking, row_ambiguous),
            _ => (blocks, ranking, ambiguous),
        }
    }

    /// The partial-match decision for one thumbnail on one grid.
    #[allow(clippy::too_many_arguments)]
    pub fn evaluate_partial(
        &self,
        full: &[f32],
        whole_top: &[String],
        matcher: &TileMatcher,
        stage: &PartialStage,
        allowed: &dyn Fn(usize) -> bool,
        hints: &RecognitionHints,
        ranking: &mut Vec<ArtMatch>,
    ) -> (Option<PartialMatch>, Option<String>) {
        let Some(best) = matcher.best(&self.db, full, allowed, stage, ranking) else { return (None, None) };
        if ranking.is_empty() {
            return (None, None);
        }
        let (pick, ambiguous) = self.decide(ranking, hints);
        let twins = self.db.twins_of(&pick.card_id);
        let runner_up = ranking
            .iter()
            .filter(|m| m.card_id != pick.card_id && !twins.contains(&m.card_id))
            .map(|m| m.score)
            .fold(None, |a: Option<f32>, s| Some(a.map_or(s, |a| a.max(s))))
            .unwrap_or(-1.0);
        let margin = pick.score - runner_up;
        let agrees = stage.whole_top_k <= 0
            || margin >= stage.free_margin
            || whole_top.iter().take(stage.whole_top_k as usize).any(|id| *id == pick.card_id || twins.contains(id));
        let accepted = agrees && pick.score >= stage.accept_score && margin >= stage.accept_margin;
        (
            Some(PartialMatch {
                card_id: pick.card_id.clone(),
                score: pick.score,
                margin,
                visible_tiles: best.visible,
                total_tiles: matcher.grid.count as i32,
                accepted,
                stage: if matcher.grid.is_rows { "rows" } else { "blocks" },
            }),
            ambiguous,
        )
    }
}

fn row_kind(cards: &[RecognizedCard], t: &RefTransform) -> Option<ScreenKind> {
    let mut ys: Vec<f32> = cards
        .iter()
        .filter(|c| c.accepted && c.partial.is_none())
        .map(|c| (c.card_bounds.y as f32 + c.card_bounds.height as f32 / 2.0 - t.offset_y) / t.scale)
        .collect();
    if ys.is_empty() {
        return None;
    }
    ys.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    let y = ys[ys.len() / 2];
    let mid = (layout::REWARD_ROW_Y + layout::CHOICE_ROW_Y) / 2.0;
    Some(if y >= mid { ScreenKind::RewardCardSelect } else { ScreenKind::CardChoice })
}

fn rug_share(img: &IntegralImage, t: &RefTransform, slot: &SlotLayout) -> f32 {
    const GW: i32 = 16;
    const GH: i32 = 12;
    let mut rgb = vec![0f32; (GW * GH * 3) as usize];
    img.sample_grid(t.to_pixels(card_geometry::place(card_geometry::ART, slot.center_x, slot.center_y, slot.small_scale)), GW, GH, &mut rgb);
    let rug = rgb.as_chunks::<3>().0.iter().filter(|p| relics::is_rug(p[0], p[1], p[2])).count();
    rug as f32 / (GW * GH) as f32
}

fn extract_at(img: &IntegralImage, t: &RefTransform, cx: f32, cy: f32, scale: f32, dst: &mut [f32]) -> bool {
    let art = t.to_pixels(card_geometry::place(card_geometry::ART, cx, cy, scale));
    let (w, h) = if dst.len() == FULL_LENGTH { (FULL_W, FULL_H) } else { (COARSE_W, COARSE_H) };
    ncc::extract(img, art, w, h, dst)
}

fn empty(t: &RefTransform, slot: &SlotLayout) -> RecognizedCard {
    RecognizedCard {
        slot: slot.index,
        slot_kind: slot.kind,
        card_id: None,
        upgraded: false,
        score: 0.0,
        margin: 0.0,
        accepted: false,
        ambiguous_with: None,
        upgrade_evidence: 0.0,
        upgrade_known: false,
        node_scale: slot.small_scale,
        art_bounds: t.to_pixel_rect(card_geometry::place(card_geometry::ART, slot.center_x, slot.center_y, slot.small_scale)),
        card_bounds: t.to_pixel_rect(card_geometry::place(card_geometry::CARD, slot.center_x, slot.center_y, slot.small_scale)),
        price_bounds: None,
        price: None,
        candidates: vec![],
        partial: None,
        missing_portrait: false,
        price_reading: None,
        rug_share: 0.0,
        slot_empty: false,
    }
}
