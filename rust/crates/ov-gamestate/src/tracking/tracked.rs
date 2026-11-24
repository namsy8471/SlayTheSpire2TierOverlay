//! The tracker's snapshot types (`TrackedRun.cs`).

use std::collections::BTreeMap;
use std::sync::Arc;

use ov_core::map::{MapCoord, MapGraph};
use ov_core::model::{OfferContext, RunState};

use super::rng::{DEFAULT_SCHEME, RngScheme};
use crate::log::events::{CombatKind, RaritySource};
use crate::save::{ParsedSave, PartnerPlayer, PotionSlot};

/// Best guess of what the game is showing, from log events.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum ScreenHint {
    #[default]
    Unknown,
    MainMenu,
    Map,
    Combat,
    RewardScreenLikely,
    ShopLikely,
    RestSite,
    Event,
    Treasure,
    RunEnded,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum RunOutcome {
    #[default]
    InProgress,
    Won,
    Lost,
    Abandoned,
    Ended,
}

#[derive(Clone, Debug, PartialEq)]
pub struct RarityRoll {
    pub roll: f64,
    pub need: f64,
    pub offset_before: f64,
    pub is_rare: bool,
    pub source: RaritySource,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum UncertainReason {
    UnknownCardSelect,
    Inferred,
    UpgradeRoll,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PartnerVote {
    pub player_id: u64,
    pub generation: i32,
    pub coord: MapCoord,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EventSource {
    Log,
    SavePrediction,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ActiveEvent {
    pub event_id: String,
    pub option_keys: Option<Vec<String>>,
    pub chosen_keys: Vec<String>,
    pub source: EventSource,
    pub certain: bool,
    pub alternatives: Vec<String>,
    pub options_replayed: bool,
    pub possible_keys: Option<Vec<String>>,
    pub random_pages: Option<BTreeMap<String, String>>,
    pub offered_after: Option<BTreeMap<String, Vec<String>>>,
}

impl ActiveEvent {
    pub fn new(event_id: impl Into<String>, option_keys: Option<Vec<String>>, chosen_keys: Vec<String>, source: EventSource) -> Self {
        Self {
            event_id: event_id.into(),
            option_keys,
            chosen_keys,
            source,
            certain: true,
            alternatives: vec![],
            options_replayed: false,
            possible_keys: None,
            random_pages: None,
            offered_after: None,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct UncertainCard {
    pub deck_index: i32,
    pub card_id: String,
    pub reason: UncertainReason,
}

/// The tracker's current snapshot. A new value is published on every change.
#[derive(Clone, Debug, PartialEq)]
pub struct TrackedRun {
    pub state: RunState,
    pub map: Option<MapGraph>,
    pub screen: ScreenHint,
    pub last_combat_kind: Option<CombatKind>,
    pub last_encounter_id: Option<String>,
    pub rarity_pity_offset: Option<f64>,
    pub last_reward_rolls: Vec<RarityRoll>,
    pub potions: Vec<PotionSlot>,
    pub current_event: Option<ActiveEvent>,
    pub from_save: bool,
    pub deck_matches_save: bool,
    pub hp_gold_reliable: bool,
    pub deck_approximate: bool,
    pub uncertain_cards: Vec<UncertainCard>,
    pub unidentified_cards: i32,
    pub unknown_upgrades: i32,
    pub unknown_removals: i32,
    pub deck_from_run_history: bool,
    pub applied_log_patches: i32,
    pub local_player_id: u64,
    pub player_count: i32,
    pub partners: Vec<PartnerPlayer>,
    pub partner_votes: Vec<PartnerVote>,
    pub my_vote: Option<MapCoord>,
    pub seed: Option<String>,
    pub rng_scheme: RngScheme,
    pub outcome: RunOutcome,
    pub save: Option<Arc<ParsedSave>>,
    /// Unix milliseconds.
    pub updated_at: i64,
}

impl TrackedRun {
    pub fn new(state: RunState) -> Self {
        Self {
            state,
            map: None,
            screen: ScreenHint::Unknown,
            last_combat_kind: None,
            last_encounter_id: None,
            rarity_pity_offset: None,
            last_reward_rolls: vec![],
            potions: vec![],
            current_event: None,
            from_save: false,
            deck_matches_save: false,
            hp_gold_reliable: true,
            deck_approximate: false,
            uncertain_cards: vec![],
            unidentified_cards: 0,
            unknown_upgrades: 0,
            unknown_removals: 0,
            deck_from_run_history: false,
            applied_log_patches: 0,
            local_player_id: 0,
            player_count: 1,
            partners: vec![],
            partner_votes: vec![],
            my_vote: None,
            seed: None,
            rng_scheme: DEFAULT_SCHEME,
            outcome: RunOutcome::InProgress,
            save: None,
            updated_at: 0,
        }
    }

    pub fn offer_context(&self) -> Option<OfferContext> {
        match self.screen {
            ScreenHint::RewardScreenLikely => Some(match self.last_combat_kind {
                Some(CombatKind::Elite) => OfferContext::EliteReward,
                Some(CombatKind::Boss) => OfferContext::BossReward,
                _ => OfferContext::CardReward,
            }),
            ScreenHint::ShopLikely => Some(OfferContext::Shop),
            _ => None,
        }
    }

    pub fn is_uncertain(&self, deck_index: i32) -> bool {
        self.uncertain_cards.iter().any(|u| u.deck_index == deck_index)
    }
}
