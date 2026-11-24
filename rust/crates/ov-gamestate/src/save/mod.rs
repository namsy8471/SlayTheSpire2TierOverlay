//! `current_run.save` / `current_run_mp.save` (`Sts2Overlay.GameState.Save`).

pub mod history;
pub mod parser;

pub use parser::{RUN_SAVE_FILE_NAMES, parse, parse_file};

use std::collections::{BTreeMap, BTreeSet};

use ov_core::map::MapGraph;
use ov_core::model::{DeckCard, RunState};

/// A relic as saved: id plus the names of its true `props.bools`.
#[derive(Clone, Debug, PartialEq)]
pub struct SavedRelic {
    pub id: String,
    pub true_bools: BTreeSet<String>,
}

/// Another co-op player as the host's save shows them.
#[derive(Clone, Debug, PartialEq)]
pub struct PartnerPlayer {
    pub net_id: u64,
    pub character: String,
    pub deck: Vec<DeckCard>,
    pub relics: Vec<String>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PotionSlot {
    pub id: String,
    pub slot: i32,
}

/// Everything read from one run save (`ParsedSave`).
#[derive(Clone, Debug, PartialEq)]
pub struct ParsedSave {
    pub state: RunState,
    pub map: Option<MapGraph>,
    pub seed: Option<String>,
    /// 16 = v0.107.1, 20 = v0.111.0, 21 = v0.113; 0 when absent.
    pub schema_version: i32,
    pub my_net_id: u64,
    pub my_player_matched: bool,
    pub player_net_ids: Vec<u64>,
    pub partners: Vec<PartnerPlayer>,
    pub potions: Vec<PotionSlot>,
    pub unlocked_epochs: Vec<String>,
    /// Rarity key -> relic ids still in the bag, in save order of keys.
    pub relic_grab_bag: Option<Vec<(String, Vec<String>)>>,
    pub relic_details: Vec<SavedRelic>,
    pub max_potion_slots: i32,
    pub card_rarity_offset: Option<f64>,
    pub potion_reward_odds: Option<f64>,
    pub unknown_map_point_odds: BTreeMap<String, f64>,
    /// `save_time`, unix seconds (> 0).
    pub save_time: Option<i64>,
    pub act_id: Option<String>,
    pub game_mode: Option<String>,
    /// Run modifiers count; -1 when the save has no list.
    pub modifier_count: i32,
    pub events: Option<EventRoomState>,
}

/// The current act's event bookkeeping (`EventRoomState`).
#[derive(Clone, Debug, PartialEq, Default)]
pub struct EventRoomState {
    pub act_index: i32,
    pub event_ids: Vec<String>,
    pub events_visited: i32,
    pub ancient_id: Option<String>,
    pub events_seen: Vec<String>,
    pub visited_in_act: i32,
    pub history_in_act: i32,
    pub total_history: i32,
    pub pre_finished_room: bool,
    pub unknown_map_point_counter: i32,
    pub previous_was_shop: bool,
    pub number_of_runs: i32,
}

impl EventRoomState {
    pub fn is_room_entry_save(&self) -> bool {
        !self.pre_finished_room && self.visited_in_act == self.history_in_act + 1
    }
}
