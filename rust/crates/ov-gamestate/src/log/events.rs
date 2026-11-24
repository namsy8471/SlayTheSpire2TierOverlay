//! Typed events parsed from godot.log lines (`LogEvents.cs`). Player ids: "Player 1" in single player, the Steam
//! id (net id) in co-op; `None` means the line had no player (older builds) -> the local player.

use ov_core::map::MapCoord;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum CombatKind {
    Unknown,
    Normal,
    Elite,
    Boss,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum RoomKind {
    Other,
    Combat,
    Merchant,
    RestSite,
    Event,
    Treasure,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum RaritySource {
    Unknown,
    Regular,
    Elite,
    Shop,
    Boss,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LobbyPlayer {
    pub net_id: u64,
    pub character: String,
}

/// `Card rarity: Rolled r, need < n for rare (offset = o)`.
#[derive(Clone, Debug, PartialEq)]
pub struct CardRarityRolled {
    pub roll: f64,
    pub need: f64,
    pub offset: f64,
    /// The roll as float32 (NaN when it does not parse).
    pub roll_float: f32,
}

fn near(a: f64, b: f64) -> bool {
    (a - b).abs() < 0.002
}

impl CardRarityRolled {
    pub fn is_rare(&self) -> bool {
        self.roll < self.need
    }

    pub fn base_rare_odds(&self) -> f64 {
        self.need - self.offset
    }

    pub fn source(&self) -> RaritySource {
        let b = self.base_rare_odds();
        if b > 0.5 {
            RaritySource::Boss
        } else if near(b, 0.03) || near(b, 0.0149) {
            RaritySource::Regular
        } else if near(b, 0.10) || near(b, 0.05) {
            RaritySource::Elite
        } else if near(b, 0.09) || near(b, 0.045) {
            RaritySource::Shop
        } else {
            RaritySource::Unknown
        }
    }

    pub fn is_scarcity(&self) -> Option<bool> {
        let b = self.base_rare_odds();
        if near(b, 0.0149) || near(b, 0.05) || near(b, 0.045) {
            Some(true)
        } else if near(b, 0.03) || near(b, 0.10) || near(b, 0.09) {
            Some(false)
        } else {
            None
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub enum LogEvent {
    RunStarted { is_multiplayer: bool, character: Option<String>, ascension: i32, seed: String, players: Vec<LobbyPlayer>, mode: String },
    GameVersion { version: String },
    RunContinued { character: String },
    LocalPlayerId { net_id: u64, from_profile_path: bool, profile: Option<String> },
    MapVote { player_id: u64, generation: i32, coord: MapCoord },
    RoomEntered { room: RoomKind, raw_name: String },
    ActEntered { act_id: String },
    MainMenu,
    CombatStarted { encounter_id: String, kind: CombatKind, visual_only: bool },
    CombatWon { character: String, encounter_id: String, kind: CombatKind },
    CombatLost { character: String, encounter_id: String, kind: CombatKind },
    RunWon { character: String },
    RunAbandoned,
    RunHistorySaved { file_name: String },
    RunSaveWritten { file_name: String, bytes: i64 },
    CardRarityRolled(CardRarityRolled),
    CardObtained { player_id: Option<u64>, card_id: String, special: bool },
    GoldObtained { player_id: Option<u64>, amount: i32 },
    PotionObtained { player_id: Option<u64>, potion_id: String },
    RelicObtained { player_id: Option<u64>, relic_id: String },
    CardRemovalReward { player_id: u64 },
    CardsChosen { player_id: u64, card_ids: Vec<String> },
    CardSelectScreen { screen: String },
    PotionUsed { player_id: u64, potion_id: String, discarded: bool },
    RestSiteOption { player_id: u64, option: String, success: bool },
    EventStarted { player_id: u64, event_id: String, option_keys: Vec<String> },
    EventOptionChosen { player_id: u64, event_id: String, choice_key: String },
    /// `[ActionQueueSet] Enqueueing action PlayCardAction ...` (co-op Debug).
    CardPlayed { owner_id: u64, card_id: String, combat_index: i32 },
    /// `Player N playing card X`.
    CardPlayLogged { player_id: u64, card_id: String },
    RemoteShopPurchase { player_id: u64, item_id: String },
    MonsterMove { monster_id: String, mv: String },
}
