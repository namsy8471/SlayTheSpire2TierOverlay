//! Port of Results.cs and the result records of the other recognisers.

use std::fmt;

use crate::geometry::PixelRect;
use crate::price::{PriceReading, PriceTint};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ScreenKind {
    Other,
    RewardCardSelect,
    Shop,
    CardChoice,
    CardGrid,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum CardSlotKind {
    Reward,
    ShopCharacter,
    ShopColorless,
    Choice,
    Grid,
}

impl CardSlotKind {
    pub fn is_shop(self) -> bool {
        matches!(self, CardSlotKind::ShopCharacter | CardSlotKind::ShopColorless)
    }
}

#[derive(Clone, Debug, Default)]
pub struct RecognitionHints {
    pub character_id: Option<String>,
}

/// A database candidate with its NCC score.
#[derive(Clone, Debug, PartialEq)]
pub struct ArtMatch {
    pub entry_index: usize,
    pub card_id: String,
    pub score: f32,
}

/// Outcome of the occlusion-robust (partial) match of one slot.
#[derive(Clone, Debug, PartialEq)]
pub struct PartialMatch {
    pub card_id: String,
    pub score: f32,
    pub margin: f32,
    pub visible_tiles: i32,
    pub total_tiles: i32,
    pub accepted: bool,
    pub stage: &'static str,
}

impl PartialMatch {
    pub fn visible_fraction(&self) -> f32 {
        self.visible_tiles as f32 / self.total_tiles as f32
    }
}

impl fmt::Display for PartialMatch {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "{} {} p={:.3} m={:.3} vis={}/{}{}",
            self.card_id,
            self.stage,
            self.score,
            self.margin,
            self.visible_tiles,
            self.total_tiles,
            if self.accepted { "" } else { " (rejected)" }
        )
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct RecognizedCard {
    pub slot: i32,
    pub slot_kind: CardSlotKind,
    pub card_id: Option<String>,
    pub upgraded: bool,
    pub score: f32,
    pub margin: f32,
    pub accepted: bool,
    pub ambiguous_with: Option<String>,
    pub upgrade_evidence: f32,
    pub upgrade_known: bool,
    pub node_scale: f32,
    pub art_bounds: PixelRect,
    pub card_bounds: PixelRect,
    pub price_bounds: Option<PixelRect>,
    pub price: Option<i32>,
    pub candidates: Vec<ArtMatch>,
    pub partial: Option<PartialMatch>,
    pub missing_portrait: bool,
    pub price_reading: Option<PriceReading>,
    pub rug_share: f32,
    pub slot_empty: bool,
}

impl RecognizedCard {
    pub fn unaffordable(&self) -> Option<bool> {
        self.price_reading.as_ref().filter(|r| r.tint != PriceTint::Unknown).map(|r| r.unaffordable())
    }
}

impl fmt::Display for RecognizedCard {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "#{} {}{} s={:.3} m={:.3}{}",
            self.slot,
            self.card_id.as_deref().unwrap_or("-"),
            if self.upgraded { "+" } else if self.upgrade_known { "" } else { "?" },
            self.score,
            self.margin,
            if self.accepted { "" } else { " (rejected)" }
        )?;
        if let Some(a) = &self.ambiguous_with {
            write!(f, " ~{a}")?;
        }
        if let Some(p) = &self.partial {
            write!(f, " [partial {p}]")?;
        }
        if let Some(r) = &self.price_reading {
            write!(f, " ${r}")?;
        }
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct ScreenGate {
    pub reward_likely: bool,
    pub shop_likely: bool,
    pub detail: String,
    pub inspect_likely: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RelicSlotState {
    Identified,
    Empty,
    Unknown,
}

#[derive(Clone, Debug, PartialEq)]
pub struct RecognizedRelic {
    pub slot: i32,
    pub state: RelicSlotState,
    pub relic_id: Option<String>,
    pub rarity: Option<String>,
    pub score: f32,
    pub margin: f32,
    pub node_scale: f32,
    pub icon_bounds: PixelRect,
    pub price_bounds: PixelRect,
    pub price: Option<i32>,
    pub candidates: Vec<(String, f32)>,
    pub price_reading: Option<PriceReading>,
    pub tooltip_cover: f32,
    pub hand_cover: f32,
    pub overlay_cover: f32,
    pub rug_share: f32,
    pub shape_score: f32,
    pub shape_margin: f32,
    pub shape_agrees: bool,
}

impl RecognizedRelic {
    pub fn accepted(&self) -> bool {
        self.state == RelicSlotState::Identified
    }
    pub fn unaffordable(&self) -> Option<bool> {
        self.price_reading.as_ref().filter(|r| r.tint != PriceTint::Unknown).map(|r| r.unaffordable())
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct ScreenRecognition {
    pub kind: ScreenKind,
    pub cards: Vec<RecognizedCard>,
    pub slots: Vec<RecognizedCard>,
    pub gate: ScreenGate,
    pub elapsed_ms: f64,
    pub detail: String,
    pub relics: Vec<RecognizedRelic>,
    pub potions: Vec<RecognizedRelic>,
}
