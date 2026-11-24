//! Port of RecognitionTracker.cs: merges per-frame results while the same screen stays open.

use std::collections::{BTreeMap, HashMap};
use std::time::{Duration, Instant};

use crate::price::PriceTint;
use crate::results::*;

#[derive(Clone, Debug, PartialEq)]
pub struct TrackedScreen {
    pub kind: ScreenKind,
    pub cards: Vec<RecognizedCard>,
    pub relics: Vec<RecognizedRelic>,
    pub potions: Vec<RecognizedRelic>,
    pub slot_count: usize,
    pub generation: i32,
    pub held: bool,
    pub unsettled: bool,
}

impl TrackedScreen {
    pub fn none() -> Self {
        TrackedScreen {
            kind: ScreenKind::Other,
            cards: vec![],
            relics: vec![],
            potions: vec![],
            slot_count: 0,
            generation: 0,
            held: false,
            unsettled: false,
        }
    }
}

pub struct RecognitionTracker {
    kind: ScreenKind,
    slot_count: usize,
    best: BTreeMap<i32, RecognizedCard>,
    relics: BTreeMap<i32, RecognizedRelic>,
    potions: BTreeMap<i32, RecognizedRelic>,
    empty_seen: HashMap<i32, i32>,
    misses: i32,
    first_miss: Option<Instant>,
    pub hold_frames: i32,
    pub hold_time: Duration,
    pub empty_frames: i32,
    pub generation: i32,
}

impl Default for RecognitionTracker {
    fn default() -> Self {
        Self {
            kind: ScreenKind::Other,
            slot_count: 0,
            best: BTreeMap::new(),
            relics: BTreeMap::new(),
            potions: BTreeMap::new(),
            empty_seen: HashMap::new(),
            misses: 0,
            first_miss: None,
            hold_frames: 2,
            hold_time: Duration::from_millis(1500),
            empty_frames: 2,
            generation: 0,
        }
    }
}

impl RecognitionTracker {
    pub fn current(&self) -> TrackedScreen {
        if self.kind == ScreenKind::Other {
            return TrackedScreen::none();
        }
        TrackedScreen {
            kind: self.kind,
            cards: self.best.values().cloned().collect(),
            relics: self.relics.values().cloned().collect(),
            potions: self.potions.values().cloned().collect(),
            slot_count: self.slot_count,
            generation: self.generation,
            held: self.misses > 0,
            unsettled: self.misses > 0 || self.empty_seen.values().any(|&n| n < self.empty_frames),
        }
    }

    pub fn update(&mut self, frame: &ScreenRecognition, now: Instant) -> TrackedScreen {
        if frame.gate.inspect_likely {
            if self.kind != ScreenKind::Other {
                self.reset_to(ScreenKind::Other, 0);
            }
            return self.current();
        }
        if frame.kind == ScreenKind::Other {
            if self.kind == ScreenKind::Other {
                return self.current();
            }
            if self.misses == 0 {
                self.first_miss = Some(now);
            }
            self.misses += 1;
            let since = self.first_miss.map_or(Duration::ZERO, |f| now.saturating_duration_since(f));
            if self.misses > self.hold_frames || since > self.hold_time {
                self.reset_to(ScreenKind::Other, 0);
            }
            return self.current();
        }
        self.misses = 0;
        let contradicts = frame.cards.iter().any(|c| self.best.get(&c.slot).is_some_and(|old| old.card_id != c.card_id));
        if frame.kind != self.kind || frame.slots.len() != self.slot_count || contradicts {
            self.reset_to(frame.kind, frame.slots.len());
        }
        self.drop_empty_slots(&frame.slots);
        for c in &frame.cards {
            let Some(old) = self.best.get(&c.slot) else {
                self.best.insert(c.slot, c.clone());
                continue;
            };
            let upgraded = if old.upgrade_known && !c.upgrade_known {
                old.upgraded
            } else if c.upgrade_known && !old.upgrade_known {
                c.upgraded
            } else {
                old.upgraded || c.upgraded
            };
            let mut n = c.clone();
            n.upgraded = upgraded;
            n.upgrade_known = old.upgrade_known || c.upgrade_known;
            n.score = old.score.max(c.score);
            n.price = c.price.or(old.price);
            n.price_reading = if c.price.is_some() { c.price_reading.clone() } else { old.price_reading.clone().or_else(|| c.price_reading.clone()) };
            self.best.insert(c.slot, n);
        }
        merge_items(&frame.relics, &mut self.relics);
        merge_items(&frame.potions, &mut self.potions);
        self.current()
    }

    fn drop_empty_slots(&mut self, slots: &[RecognizedCard]) {
        for s in slots {
            if !s.slot_empty {
                self.empty_seen.remove(&s.slot);
                continue;
            }
            let n = self.empty_seen.get(&s.slot).copied().unwrap_or(0) + 1;
            self.empty_seen.insert(s.slot, n);
            if n >= self.empty_frames {
                self.best.remove(&s.slot);
            }
        }
    }

    pub fn reset(&mut self) {
        self.reset_to(ScreenKind::Other, 0);
    }

    fn reset_to(&mut self, kind: ScreenKind, slots: usize) {
        self.generation += 1;
        self.kind = kind;
        self.slot_count = slots;
        self.misses = 0;
        self.best.clear();
        self.relics.clear();
        self.potions.clear();
        self.empty_seen.clear();
    }
}

fn merge_items(items: &[RecognizedRelic], known: &mut BTreeMap<i32, RecognizedRelic>) {
    for r in items {
        match r.state {
            RelicSlotState::Identified => {
                let merged = match known.get(&r.slot) {
                    Some(old) if old.relic_id == r.relic_id && r.price.is_none() => {
                        let mut n = r.clone();
                        n.price = old.price;
                        n.price_reading = if r.price_reading.as_ref().is_some_and(|p| p.tint != PriceTint::Unknown) {
                            r.price_reading.clone()
                        } else {
                            old.price_reading.clone().or_else(|| r.price_reading.clone())
                        };
                        n
                    }
                    _ => r.clone(),
                };
                known.insert(r.slot, merged);
            }
            RelicSlotState::Empty => {
                known.remove(&r.slot);
            }
            RelicSlotState::Unknown => {}
        }
    }
}
