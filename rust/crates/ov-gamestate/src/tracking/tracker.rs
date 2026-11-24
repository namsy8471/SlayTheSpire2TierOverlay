//! Save + log -> one live [`TrackedRun`] (`GameStateTracker`, the pure part).
//!
//! The model is "base state + patches": the latest matching save (or a log-only rebuild for a co-op guest) plus every
//! deck/relic/potion/gold log event that happened after that save was written. The game logs
//! `Wrote N bytes to path=...current_run.save` after each save; N equals the file size, so the log itself says which
//! events are already inside the save. Everything is recomputed on each change.
//!
//! This type has no threads and no file watching: [`super::live::LiveTracker`] feeds it. Diagnostics are queued and
//! the owner drains them with [`Tracker::take_diagnostics`].

use std::collections::{BTreeMap, HashSet};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;

use ov_core::map::{MapCoord, MapGraph, MapNodeType};
use ov_core::model::{CardRef, DeckCard, RunState};

use super::ancients::{self, AncientDeckCard, AncientInputs};
use super::data::{self, GameDataVersion, StartingLoadouts, cards};
use super::deck_effects::{self, DeckEffect, SelectMeaning, UpgradePick};
use super::deck_replay::{CombatObservation, CombatPlay, DeckReplay};
use super::event_pages;
use super::events::{self, EventGuess};
use super::neow;
use super::reward::{self, OfferSlot, RewardCardPool, RewardRollReplay};
use super::rng::{self, DEFAULT_SCHEME, GameRng, RngScheme};
use super::tracked::*;
use crate::log::events::{CardRarityRolled, CombatKind, LobbyPlayer, LogEvent, RaritySource, RoomKind};
use crate::log::parser as log_parser;
use crate::paths::{self, GamePaths};
use crate::save::watcher::SaveSnapshot;
use crate::save::{ParsedSave, PartnerPlayer, PotionSlot, progress};

const PITY_RESET: f64 = -0.05;
const PITY_MAX: f64 = 0.4;
const SCARCITY_ASCENSION: i32 = 7;
const BING_BONG: &str = "RELIC.BING_BONG";
const UPGRADE_ROLL_ARG: &str = "upgrade-roll";
const TRANSFORM_SCREEN: &str = "deck_transform_select_screen";
const UPGRADE_SCREEN: &str = "deck_upgrade_select_screen";
const EGGS: [(&str, &str); 3] = [("RELIC.FROZEN_EGG", "Power"), ("RELIC.MOLTEN_EGG", "Attack"), ("RELIC.TOXIC_EGG", "Skill")];

fn may_roll_upgrade(card_id: &str, v: GameDataVersion) -> bool {
    !matches!(cards::rarity(card_id, v), Some("Rare") | None) && cards::max_upgrade(card_id, v) > 0
}

#[derive(Default)]
pub struct TrackerOptions {
    pub paths: Option<GamePaths>,
    pub local_net_id_override: Option<u64>,
    pub loadouts: Option<StartingLoadouts>,
    /// Unix milliseconds; None = system clock.
    pub clock: Option<Box<dyn Fn() -> i64 + Send>>,
}

#[derive(Clone, Debug)]
struct LogRun {
    seed: Option<String>,
    is_multiplayer: Option<bool>,
    ascension: Option<i32>,
    players: Vec<LobbyPlayer>,
    character: Option<String>,
    continued: bool,
}

#[derive(Clone, Debug)]
struct SaveMark {
    seq: i64,
    file_name: String,
    bytes: i64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum PatchKind {
    AddCard,
    RemoveCard,
    ShopRemoveCard,
    UpgradeCard,
    TransformCard,
    EnchantCard,
    DuplicateCard,
    UnknownSelect,
    RandomUpgrades,
    UnknownAdds,
    RandomRemoves,
    UpgradeBasics,
    TransformAllBasics,
    TransformStrikeAndDefend,
    Transcend,
    CloneEnchanted,
    CombatSeen,
    CardStolen,
    UpgradeAll,
    AddRelic,
    AddGold,
    AddPotion,
    RemovePotion,
    ShuffleUpgrades,
    PickUpgrade,
    EnchantAllDefends,
    StolenReturned,
}

#[derive(Clone, Debug)]
struct Patch {
    kind: PatchKind,
    id: Option<String>,
    amount: i32,
    seq: i64,
    arg: Option<String>,
    combat: Option<CombatObservation>,
    before: Option<Vec<CombatPlay>>,
    steal_seq: i64,
    rng_seed: Option<u64>,
}

impl Patch {
    fn new(kind: PatchKind, id: Option<&str>, amount: i32, seq: i64) -> Self {
        Self { kind, id: id.map(str::to_string), amount, seq, arg: None, combat: None, before: None, steal_seq: 0, rng_seed: None }
    }
}

#[derive(Clone, Debug)]
struct ReplayedOptions {
    keys: Vec<String>,
    possible: Vec<String>,
}

fn kind_name(k: CombatKind) -> &'static str {
    match k {
        CombatKind::Unknown => "Unknown",
        CombatKind::Normal => "Normal",
        CombatKind::Elite => "Elite",
        CombatKind::Boss => "Boss",
    }
}

fn short_key(key: &str) -> &str {
    &key[key.rfind('.').map_or(0, |i| i + 1)..]
}

pub struct Tracker {
    options: TrackerOptions,
    diagnostics: Vec<String>,
    format_problem: bool,
    wanted_saves: Option<PathBuf>,

    seq: i64,
    saves: Vec<(String, SaveSnapshot)>,
    patches: Vec<Patch>,
    save_marks: Vec<SaveMark>,
    roll_batch: Vec<CardRarityRolled>,
    log_run: Option<LogRun>,
    log_local_id: Option<u64>,
    profile_steam_id: Option<u64>,
    log_act: i32,
    log_floor: i32,
    log_act_id: Option<String>,
    move_since_room: bool,
    room: Option<RoomKind>,
    screen: ScreenHint,
    last_kind: Option<CombatKind>,
    last_encounter: Option<String>,
    last_rest_option: Option<String>,
    pending_removal_reward: bool,
    card_stolen: bool,
    steal_at: usize,
    steal_seq: i64,
    steal_plays: Option<(Vec<CombatPlay>, Vec<CombatPlay>)>,
    pending_select: Option<DeckEffect>,
    merchant_visited: bool,
    pending_event_id: Option<String>,
    shop_removal_seen: bool,
    select_screen: Option<String>,
    rest_site_patches: Vec<usize>,
    relics_this_room: HashSet<String>,
    combat_plays: Option<Vec<CombatPlay>>,
    history_deck: Option<Vec<DeckCard>>,
    pity_log: Option<f64>,
    pity_log_seq: i64,
    last_rolls: Vec<RarityRoll>,
    reward_rng: Option<RewardRollReplay>,
    log_game_version: Option<String>,
    offers: Vec<Vec<OfferSlot>>,
    pool: Option<RewardCardPool>,
    pool_version: Option<GameDataVersion>,
    live_epochs: u64,
    seen_offer: Option<Vec<CardRef>>,
    vote_coord: Option<MapCoord>,
    vote_by: u64,
    my_vote: Option<PartnerVote>,
    event_id: Option<String>,
    event_keys: Option<Vec<String>>,
    event_chosen: Vec<String>,
    in_event_room: bool,
    backdrop_encounter: Option<String>,
    event_fight: bool,
    event_guess: Option<EventGuess>,
    event_check: Option<(EventGuess, usize)>,
    vote_seq: i64,
    partner_votes: BTreeMap<u64, PartnerVote>,
    outcome: RunOutcome,
    current: Option<TrackedRun>,
    offered_after_cache: Option<(String, u64, bool, Option<BTreeMap<String, Vec<String>>>)>,
    random_pages_cache: Option<(String, u64, Option<BTreeMap<String, String>>)>,
    neow_cache: Option<(String, i32, i32, RngScheme, bool, Option<ReplayedOptions>)>,
    ancient_cache: Option<(String, Option<ReplayedOptions>)>,
}

impl Tracker {
    pub fn new(options: TrackerOptions) -> Self {
        Self {
            options,
            diagnostics: vec![],
            format_problem: false,
            wanted_saves: None,
            seq: 0,
            saves: vec![],
            patches: vec![],
            save_marks: vec![],
            roll_batch: vec![],
            log_run: None,
            log_local_id: None,
            profile_steam_id: None,
            log_act: 0,
            log_floor: 0,
            log_act_id: None,
            move_since_room: false,
            room: None,
            screen: ScreenHint::Unknown,
            last_kind: None,
            last_encounter: None,
            last_rest_option: None,
            pending_removal_reward: false,
            card_stolen: false,
            steal_at: 0,
            steal_seq: 0,
            steal_plays: None,
            pending_select: None,
            merchant_visited: false,
            pending_event_id: None,
            shop_removal_seen: false,
            select_screen: None,
            rest_site_patches: vec![],
            relics_this_room: HashSet::new(),
            combat_plays: None,
            history_deck: None,
            pity_log: None,
            pity_log_seq: -1,
            last_rolls: vec![],
            reward_rng: None,
            log_game_version: None,
            offers: vec![],
            pool: None,
            pool_version: None,
            live_epochs: u64::MAX,
            seen_offer: None,
            vote_coord: None,
            vote_by: 0,
            my_vote: None,
            event_id: None,
            event_keys: None,
            event_chosen: vec![],
            in_event_room: false,
            backdrop_encounter: None,
            event_fight: false,
            event_guess: None,
            event_check: None,
            vote_seq: -1,
            partner_votes: BTreeMap::new(),
            outcome: RunOutcome::InProgress,
            current: None,
            offered_after_cache: None,
            random_pages_cache: None,
            neow_cache: None,
            ancient_cache: None,
        }
    }

    /// Latest snapshot, or None when no run is known.
    pub fn current(&self) -> Option<&TrackedRun> {
        self.current.as_ref()
    }

    pub fn take_diagnostics(&mut self) -> Vec<String> {
        std::mem::take(&mut self.diagnostics)
    }

    /// True once a save or log line could not be handled at all. Stays true.
    pub fn format_problem(&self) -> bool {
        self.format_problem
    }

    pub fn paths(&self) -> Option<&GamePaths> {
        self.options.paths.as_ref()
    }

    pub fn local_net_id_override(&self) -> Option<u64> {
        self.options.local_net_id_override
    }

    /// The saves folder the log's profile line names (the live driver moves to it when it exists).
    pub fn wanted_saves(&self) -> Option<&PathBuf> {
        self.wanted_saves.as_ref()
    }

    /// The local id a save parse should use (watcher callback in C#).
    pub fn save_local_id(&self, steam_id: Option<u64>) -> Option<u64> {
        self.options.local_net_id_override.or(steam_id).or(self.log_local_id)
    }

    /// The live driver watches `dir` now.
    pub fn set_saves_directory(&mut self, dir: PathBuf) {
        let steam = paths::steam_id_from_path(&dir);
        if let Some(p) = &mut self.options.paths {
            p.saves_directory = Some(dir);
            if steam.is_some() {
                p.steam_id = steam;
            }
        }
    }

    /// The watched profile changed: forget the old folder's saves.
    pub fn forget_saves_in(&mut self, dir: &std::path::Path) -> bool {
        let before = self.saves.len();
        self.saves.retain(|(_, s)| {
            let parent = std::path::Path::new(&s.path).parent().map(|p| p.to_path_buf()).unwrap_or_default();
            !paths::same_path(&parent, dir)
        });
        let changed = before != self.saves.len();
        self.recompute();
        changed
    }

    fn diag(&mut self, s: String) {
        self.diagnostics.push(s);
    }

    /// A note from the live driver.
    pub fn report_diag(&mut self, s: &str) {
        self.diagnostics.push(s.to_string());
    }

    pub fn report_problem(&mut self, what: &str, detail: &str) {
        self.diag(format!("{what}: {detail}"));
        self.format_problem = true;
    }

    fn now(&self) -> i64 {
        match &self.options.clock {
            Some(c) => c(),
            None => std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_millis() as i64),
        }
    }

    // ============================================================================================
    // Inputs
    // ============================================================================================

    pub fn on_save(&mut self, snapshot: SaveSnapshot) {
        let key = snapshot.path.to_lowercase();
        match self.saves.iter_mut().find(|(k, _)| *k == key) {
            Some(slot) => slot.1 = snapshot,
            None => self.saves.push((key, snapshot)),
        }
        self.recompute();
    }

    /// True when the snapshot changed.
    pub fn on_save_removed(&mut self, path: &str) -> bool {
        let key = path.to_lowercase();
        let Some(i) = self.saves.iter().position(|(k, _)| *k == key) else { return false };
        self.saves.remove(i);
        self.recompute();
        true
    }

    pub fn on_save_error(&mut self, path: &str, message: &str) {
        self.diag(format!("save read failed: {path}: {message}"));
    }

    pub fn on_log_rotated(&mut self) {
        self.reset_run(None);
        self.save_marks.clear();
        self.log_local_id = None;
        self.profile_steam_id = None;
        self.log_game_version = None;
        self.screen = ScreenHint::Unknown;
        self.recompute();
        self.diag("godot.log rotated (game restarted)".into());
    }

    /// Feeds raw log lines in order. True when the snapshot may have changed.
    pub fn process_lines<S: AsRef<str>>(&mut self, lines: &[S]) -> bool {
        let mut dirty = false;
        for line in lines {
            let line = line.as_ref();
            self.seq += 1;
            let r = catch_unwind(AssertUnwindSafe(|| {
                let Some(e) = log_parser::parse(line) else { return false };
                if let LogEvent::CardRarityRolled(r) = e {
                    self.roll_batch.push(r);
                    return true;
                }
                self.flush_rolls();
                self.apply(e)
            }));
            match r {
                Ok(d) => dirty |= d,
                Err(_) => {
                    dirty = true;
                    let cut: String = line.chars().take(200).collect();
                    self.report_problem("log line could not be handled", &cut);
                }
            }
        }
        let r = catch_unwind(AssertUnwindSafe(|| {
            self.flush_rolls();
            if dirty {
                self.recompute();
            }
        }));
        if r.is_err() {
            self.report_problem("log update failed", "panic");
        }
        dirty
    }

    // ============================================================================================
    // Log events
    // ============================================================================================

    fn apply(&mut self, e: LogEvent) -> bool {
        match e {
            LogEvent::RunStarted { is_multiplayer, character, ascension, seed, players, .. } => {
                self.reset_run(Some(LogRun {
                    seed: Some(seed),
                    is_multiplayer: Some(is_multiplayer),
                    ascension: Some(ascension),
                    players,
                    character,
                    continued: false,
                }));
                self.move_since_room = true;
                true
            }
            LogEvent::RunContinued { character } => {
                self.reset_run(Some(LogRun {
                    seed: None,
                    is_multiplayer: None,
                    ascension: None,
                    players: vec![],
                    character: Some(character),
                    continued: true,
                }));
                true
            }
            LogEvent::LocalPlayerId { net_id, from_profile_path, profile } => {
                if from_profile_path {
                    self.profile_steam_id = Some(net_id);
                    self.follow_profile(net_id, profile.as_deref());
                } else if net_id > 1 {
                    self.log_local_id = Some(net_id);
                }
                false
            }
            LogEvent::GameVersion { version } => {
                if self.log_game_version.as_deref() == Some(version.as_str()) {
                    return false;
                }
                let scheme = rng::for_game_version(Some(&version));
                self.diag(format!("game {version}: {scheme:?} seeds unless a save says otherwise"));
                self.log_game_version = Some(version);
                true
            }
            LogEvent::ActEntered { act_id } => {
                if self.log_act_id.as_deref() != Some(act_id.as_str()) {
                    self.log_act += 1;
                    self.log_act_id = Some(act_id);
                    self.move_since_room = true;
                    self.partner_votes.clear();
                    self.my_vote = None;
                }
                true
            }
            LogEvent::MapVote { player_id, generation, coord } => {
                self.move_since_room = true;
                if generation > 0 {
                    self.log_act = generation;
                }
                if self.is_me(Some(player_id)) {
                    self.clear_event();
                    self.vote_coord = Some(coord);
                    self.vote_by = player_id;
                    self.vote_seq = self.seq;
                    self.my_vote = Some(PartnerVote { player_id, generation, coord });
                    self.screen = ScreenHint::Map;
                } else {
                    self.partner_votes.insert(player_id, PartnerVote { player_id, generation, coord });
                }
                true
            }
            LogEvent::RoomEntered { room, .. } => {
                if self.move_since_room {
                    self.log_floor += 1;
                    self.move_since_room = false;
                }
                self.flush_combat();
                self.settle_stolen_card();
                self.room = Some(room);
                if room == RoomKind::Event {
                    self.clear_event();
                    self.in_event_room = true;
                } else if room != RoomKind::Combat {
                    self.clear_event();
                }
                if room == RoomKind::Merchant {
                    self.merchant_visited = true;
                }
                self.backdrop_encounter = None;
                self.event_fight = false;
                self.last_rest_option = None;
                self.pending_removal_reward = false;
                self.pending_select = None;
                self.shop_removal_seen = false;
                self.select_screen = None;
                self.rest_site_patches.clear();
                self.relics_this_room.clear();
                self.offers.clear();
                self.seen_offer = None;
                self.partner_votes.clear();
                self.my_vote = None;
                self.screen = match room {
                    RoomKind::Combat => ScreenHint::Combat,
                    RoomKind::Merchant => ScreenHint::ShopLikely,
                    RoomKind::RestSite => ScreenHint::RestSite,
                    RoomKind::Event => ScreenHint::Event,
                    RoomKind::Treasure => ScreenHint::Treasure,
                    RoomKind::Other => ScreenHint::Unknown,
                };
                true
            }
            LogEvent::MainMenu => {
                self.screen = ScreenHint::MainMenu;
                true
            }
            LogEvent::CombatStarted { encounter_id, kind, visual_only: false } => {
                self.flush_combat();
                self.combat_plays = Some(vec![]);
                self.last_encounter = Some(encounter_id);
                self.last_kind = Some(if kind != CombatKind::Unknown { kind } else { self.kind_from_map() });
                self.screen = ScreenHint::Combat;
                true
            }
            LogEvent::CombatStarted { encounter_id, .. } => {
                self.backdrop_encounter = Some(encounter_id);
                false
            }
            LogEvent::CardPlayLogged { .. } if self.backdrop_encounter.is_some() && self.room == Some(RoomKind::Event) && !self.event_fight => {
                self.event_fight = true;
                self.last_encounter = self.backdrop_encounter.clone();
                self.last_kind = Some(self.kind_from_map());
                self.screen = ScreenHint::Combat;
                true
            }
            LogEvent::CombatWon { encounter_id, kind, .. } => {
                self.flush_combat();
                self.backdrop_encounter = None;
                self.event_fight = false;
                self.last_encounter = Some(encounter_id);
                if kind != CombatKind::Unknown {
                    self.last_kind = Some(kind);
                }
                self.screen = ScreenHint::RewardScreenLikely;
                true
            }
            LogEvent::CombatLost { .. } => {
                self.flush_combat();
                self.end(RunOutcome::Lost);
                true
            }
            LogEvent::RunWon { .. } => {
                self.end(RunOutcome::Won);
                true
            }
            LogEvent::RunAbandoned => {
                self.end(RunOutcome::Abandoned);
                true
            }
            LogEvent::RunHistorySaved { file_name } => {
                self.flush_combat();
                self.settle_stolen_card();
                let o = if self.outcome == RunOutcome::InProgress { RunOutcome::Ended } else { self.outcome };
                self.end(o);
                self.history_deck = self.read_history_deck(&file_name);
                true
            }
            LogEvent::RunSaveWritten { file_name, bytes } => {
                self.save_marks.push(SaveMark { seq: self.seq, file_name, bytes });
                if self.save_marks.len() > 64 {
                    self.save_marks.remove(0);
                }
                true
            }
            LogEvent::MonsterMove { mv, .. } if mv == "THIEVERY_MOVE" => {
                self.card_stolen = true;
                self.steal_seq = self.seq;
                self.steal_at = self.combat_plays.as_ref().map_or(0, Vec::len);
                self.steal_plays = None;
                false
            }
            LogEvent::CardObtained { player_id, card_id, special: true } if self.is_me(player_id) && self.card_stolen => {
                self.card_stolen = false;
                let mut p = Patch::new(PatchKind::StolenReturned, Some(&card_id), 0, self.seq);
                p.steal_seq = self.steal_seq;
                self.patches.push(p);
                true
            }
            LogEvent::CardObtained { player_id, card_id, special } if self.is_me(player_id) => {
                let upgrade = if special { Some(0) } else { self.reward_upgrade(&card_id) };
                self.add_patch(PatchKind::AddCard, Some(&card_id), upgrade.unwrap_or(0), if upgrade.is_none() { Some(UPGRADE_ROLL_ARG) } else { None });
                if self.screen == ScreenHint::RewardScreenLikely && !special {
                    self.screen = ScreenHint::Map;
                }
                true
            }
            LogEvent::GoldObtained { player_id, amount } if self.is_me(player_id) => {
                self.add_patch(PatchKind::AddGold, None, amount, None);
                true
            }
            LogEvent::PotionObtained { player_id, potion_id } if self.is_me(player_id) => {
                self.add_patch(PatchKind::AddPotion, Some(&potion_id), 0, None);
                true
            }
            LogEvent::RelicObtained { player_id, relic_id } if self.is_me(player_id) => {
                self.add_patch(PatchKind::AddRelic, Some(&relic_id), 0, None);
                self.apply_relic_effect(&relic_id);
                true
            }
            LogEvent::RemoteShopPurchase { player_id, item_id } if self.is_me(Some(player_id)) => {
                if item_id.starts_with("CARD.") {
                    self.add_patch(PatchKind::AddCard, Some(&item_id), 0, None);
                } else {
                    self.add_patch(PatchKind::AddRelic, Some(&item_id), 0, None);
                    self.apply_relic_effect(&item_id);
                }
                true
            }
            LogEvent::EventStarted { player_id, event_id, option_keys } if self.is_me(Some(player_id)) => {
                self.event_id = Some(event_id);
                self.event_keys = Some(option_keys);
                self.event_chosen.clear();
                self.in_event_room = true;
                true
            }
            LogEvent::EventOptionChosen { player_id, event_id, choice_key } if self.is_me(Some(player_id)) => {
                if self.in_event_room {
                    self.event_chosen.push(choice_key.clone());
                }
                self.apply_choice(&event_id, &choice_key);
                true
            }
            LogEvent::CardPlayed { owner_id, card_id, combat_index } => {
                if let Some(p) = &mut self.combat_plays {
                    p.push(CombatPlay { owner: owner_id, card_id, index: combat_index });
                }
                false
            }
            LogEvent::PotionUsed { player_id, potion_id, .. } if self.is_me(Some(player_id)) => {
                self.add_patch(PatchKind::RemovePotion, Some(&potion_id), 0, None);
                true
            }
            LogEvent::CardRemovalReward { player_id } if self.is_me(Some(player_id)) => {
                self.pending_removal_reward = true;
                false
            }
            LogEvent::RestSiteOption { player_id, option, success: true } if self.is_me(Some(player_id)) => {
                self.last_rest_option = Some(option.clone());
                self.fix_rest_site_patches(&option)
            }
            LogEvent::CardsChosen { player_id, card_ids } if self.is_me(Some(player_id)) && !card_ids.is_empty() => {
                let changed = self.apply_chosen(&card_ids);
                self.select_screen = None;
                changed
            }
            LogEvent::CardSelectScreen { screen } => {
                self.select_screen = Some(screen);
                false
            }
            _ => false,
        }
    }

    fn follow_profile(&mut self, net_id: u64, profile: Option<&str>) {
        let (Some(profile), Some(root)) = (profile, self.options.paths.as_ref().and_then(|p| p.root.clone())) else { return };
        let dir = paths::saves_directory_for(&root, net_id, profile);
        if self.wanted_saves.as_ref().is_some_and(|k| paths::same_path(k, &dir)) {
            return;
        }
        self.wanted_saves = Some(dir);
    }

    fn apply_chosen(&mut self, card_ids: &[String]) -> bool {
        if self.pending_removal_reward {
            self.pending_removal_reward = false;
            for c in card_ids {
                self.add_patch(PatchKind::RemoveCard, Some(c), 0, None);
            }
            return true;
        }
        if let Some(effect) = self.pending_select.clone()
            && effect.select != SelectMeaning::None
        {
            self.pending_select = None;
            let kind = match effect.select {
                SelectMeaning::Remove => PatchKind::RemoveCard,
                SelectMeaning::Transform => PatchKind::TransformCard,
                SelectMeaning::Upgrade => PatchKind::UpgradeCard,
                SelectMeaning::Enchant => PatchKind::EnchantCard,
                SelectMeaning::AddChosen => PatchKind::AddCard,
                _ => PatchKind::DuplicateCard,
            };
            let arg = if effect.select == SelectMeaning::Transform { effect.transform_to.clone() } else { effect.enchantment.clone() };
            if kind == PatchKind::TransformCard
                && arg.is_none()
                && let Some(call) = effect.transform_rng_call
                && card_ids.len() == 1
                && let Some(ev) = self.pending_event_id.clone()
                && let Some(seed) = self.event_rng_seed(&ev)
            {
                let mut p = Patch::new(kind, Some(&card_ids[0]), call, self.seq);
                p.rng_seed = Some(seed);
                self.patches.push(p);
                return true;
            }
            for c in card_ids {
                self.add_patch(kind, Some(c), 0, arg.as_deref());
            }
            return true;
        }
        let room = self.room;
        let kind = match room {
            Some(RoomKind::Merchant) if !self.shop_removal_seen => {
                self.shop_removal_seen = true;
                PatchKind::ShopRemoveCard
            }
            Some(RoomKind::RestSite) if self.last_rest_option.is_none() || self.last_rest_option.as_deref() == Some("SMITH") => {
                for c in card_ids {
                    if self.last_rest_option.is_none() {
                        self.rest_site_patches.push(self.patches.len());
                    }
                    self.add_patch(PatchKind::UpgradeCard, Some(c), 0, None);
                }
                return true;
            }
            Some(RoomKind::Combat) => return false,
            Some(RoomKind::Event) if self.event_fight => return false,
            r if r != Some(RoomKind::Merchant) && self.select_screen.as_deref() == Some(TRANSFORM_SCREEN) => PatchKind::TransformCard,
            r if r != Some(RoomKind::Merchant) && self.select_screen.as_deref() == Some(UPGRADE_SCREEN) => PatchKind::UpgradeCard,
            _ => PatchKind::UnknownSelect,
        };
        for c in card_ids {
            self.add_patch(kind, Some(c), 0, None);
        }
        true
    }

    fn fix_rest_site_patches(&mut self, option: &str) -> bool {
        let mut changed = false;
        if option != "SMITH" {
            for &i in &self.rest_site_patches {
                if i >= self.patches.len() || self.patches[i].kind != PatchKind::UpgradeCard {
                    continue;
                }
                self.patches[i].kind = if option == "COOK" { PatchKind::RemoveCard } else { PatchKind::UnknownSelect };
                changed = true;
            }
        }
        self.rest_site_patches.clear();
        if option == "CLONE" {
            self.add_patch(PatchKind::CloneEnchanted, None, 0, None);
            changed = true;
        }
        changed
    }

    fn apply_choice(&mut self, event_id: &str, choice_key: &str) {
        let relic = choice_key.rfind('.').map(|d| format!("RELIC.{}", &choice_key[d + 1..]));
        if let Some(r) = &relic {
            if deck_effects::is_ancient(event_id) {
                if self.relics_this_room.insert(r.clone()) {
                    self.add_patch(PatchKind::AddRelic, Some(r), 0, None);
                }
            } else if deck_effects::for_relic(r, self.scheme()).is_some() {
                self.relics_this_room.insert(r.clone());
            }
        }
        let effect = deck_effects::for_choice(event_id, choice_key, self.card_version());
        self.apply_effect(effect, Some(event_id));
    }

    fn apply_relic_effect(&mut self, relic_id: &str) {
        if !self.relics_this_room.insert(relic_id.to_string()) {
            return;
        }
        let e = deck_effects::for_relic(relic_id, self.scheme());
        self.apply_effect(e, None);
    }

    fn apply_effect(&mut self, effect: Option<DeckEffect>, event_id: Option<&str>) {
        self.pending_select = effect.clone();
        self.pending_event_id = event_id.map(str::to_string);
        let Some(effect) = effect else { return };
        for c in &effect.adds {
            self.add_patch(PatchKind::AddCard, Some(c), 0, None);
        }
        for c in &effect.removes {
            self.add_patch(PatchKind::RemoveCard, Some(c), 0, None);
        }
        if effect.random_upgrades > 0 {
            let seed = if effect.pick != UpgradePick::Unknown { event_id.and_then(|e| self.event_rng_seed(e)) } else { None };
            match seed {
                Some(seed) => {
                    let shuffle = effect.pick == UpgradePick::EventShuffle;
                    let kind = if shuffle { PatchKind::ShuffleUpgrades } else { PatchKind::PickUpgrade };
                    let mut p = Patch::new(kind, None, if shuffle { effect.random_upgrades } else { effect.event_rng_call }, self.seq);
                    p.arg = effect.random_upgrade_type.clone();
                    p.rng_seed = Some(seed);
                    self.patches.push(p);
                }
                None => self.add_patch(PatchKind::RandomUpgrades, None, effect.random_upgrades, effect.random_upgrade_type.as_deref()),
            }
        }
        if effect.unknown_adds > 0 {
            self.add_patch(PatchKind::UnknownAdds, None, effect.unknown_adds, None);
        }
        if effect.random_removes > 0 {
            self.add_patch(PatchKind::RandomRemoves, None, effect.random_removes, None);
        }
        if effect.upgrade_basic_strike_and_defend {
            self.add_patch(PatchKind::UpgradeBasics, None, 0, None);
        }
        if effect.transform_all_basics {
            self.add_patch(PatchKind::TransformAllBasics, None, 0, None);
        }
        if effect.transform_one_strike_and_defend {
            self.add_patch(PatchKind::TransformStrikeAndDefend, None, 0, None);
        }
        if effect.transcend {
            self.add_patch(PatchKind::Transcend, None, 0, None);
        }
        if effect.upgrade_all {
            self.add_patch(PatchKind::UpgradeAll, None, 0, None);
        }
        if effect.add_basic_strike_and_defend
            && let Some(character) = self.my_character()
            && let Some(lo) = StartingLoadouts::embedded_for(self.card_version()).for_character(&character)
        {
            let starter = lo.deck.clone();
            for card in [starter.iter().find(|c| deck_effects::is_basic_strike(c)), starter.iter().find(|c| deck_effects::is_basic_defend(c))]
                .into_iter()
                .flatten()
            {
                self.add_patch(PatchKind::AddCard, Some(card), 0, None);
            }
        }
        if let Some(e) = &effect.enchant_all_defends {
            self.add_patch(PatchKind::EnchantAllDefends, None, 0, Some(e));
        }
    }

    fn settle_stolen_card(&mut self) {
        if !self.card_stolen {
            return;
        }
        self.card_stolen = false;
        let (before, after) = self.steal_plays.take().unwrap_or_default();
        let mut p = Patch::new(PatchKind::CardStolen, None, 0, self.seq);
        p.steal_seq = self.steal_seq;
        p.before = Some(before);
        p.combat = Some(CombatObservation { plays: after, after_merchant: false });
        self.patches.push(p);
    }

    fn flush_combat(&mut self) {
        if self.card_stolen
            && self.steal_plays.is_none()
            && let Some(fight) = &self.combat_plays
        {
            let at = self.steal_at.min(fight.len());
            self.steal_plays = Some((fight[..at].to_vec(), fight[at..].to_vec()));
        }
        if let Some(plays) = self.combat_plays.take() {
            let mut p = Patch::new(PatchKind::CombatSeen, None, 0, self.seq);
            p.combat = Some(CombatObservation { plays, after_merchant: self.merchant_visited });
            p.arg = Some(kind_name(self.last_kind.unwrap_or(CombatKind::Unknown)).into());
            self.patches.push(p);
        }
    }

    fn read_history_deck(&mut self, file_name: &str) -> Option<Vec<DeckCard>> {
        let saves = self.options.paths.as_ref()?.saves_directory.clone()?;
        let path = saves.join("history").join(file_name);
        let json = crate::io::read_all_text(&path)?;
        match ov_core::history::parse(&json) {
            Ok(h) => {
                let me = self.my_id();
                let player = h.players.iter().find(|p| p.id as u64 == me).or(if h.players.len() == 1 { h.players.first() } else { None });
                player.map(|p| p.deck.clone())
            }
            Err(e) => {
                self.diag(format!("run history read failed: {}: {e:?}", path.display()));
                None
            }
        }
    }

    fn add_patch(&mut self, kind: PatchKind, id: Option<&str>, amount: i32, arg: Option<&str>) {
        let mut p = Patch::new(kind, id, amount, self.seq);
        p.arg = arg.map(str::to_string);
        self.patches.push(p);
    }

    fn end(&mut self, o: RunOutcome) {
        self.outcome = o;
        self.screen = ScreenHint::RunEnded;
    }

    fn reset_run(&mut self, run: Option<LogRun>) {
        self.log_run = run;
        self.patches.clear();
        self.roll_batch.clear();
        self.log_act = 0;
        self.log_floor = 0;
        self.log_act_id = None;
        self.move_since_room = false;
        self.room = None;
        self.backdrop_encounter = None;
        self.event_fight = false;
        self.last_kind = None;
        self.last_encounter = None;
        self.last_rest_option = None;
        self.pending_removal_reward = false;
        self.card_stolen = false;
        self.steal_plays = None;
        self.pending_select = None;
        self.shop_removal_seen = false;
        self.select_screen = None;
        self.rest_site_patches.clear();
        self.relics_this_room.clear();
        self.combat_plays = None;
        self.history_deck = None;
        self.pity_log = None;
        self.pity_log_seq = -1;
        self.last_rolls = vec![];
        self.reward_rng = None;
        self.offers.clear();
        self.seen_offer = None;
        self.pool = None;
        self.pool_version = None;
        self.live_epochs = u64::MAX;
        self.merchant_visited = false;
        self.vote_coord = None;
        self.vote_seq = -1;
        self.partner_votes.clear();
        self.my_vote = None;
        self.clear_event();
        self.event_check = None;
        self.outcome = RunOutcome::InProgress;
        self.screen = ScreenHint::Unknown;
    }

    fn clear_event(&mut self) {
        self.event_id = None;
        self.event_keys = None;
        self.event_chosen.clear();
        self.in_event_room = false;
        self.event_guess = None;
    }

    // ============================================================================================
    // Events
    // ============================================================================================

    fn current_event_of(&mut self, snapshot: Option<&SaveSnapshot>) -> Option<ActiveEvent> {
        self.check_event_prediction(snapshot.map(|s| &*s.save));
        if !self.in_event_room {
            return None;
        }
        if let Some(event_id) = self.event_id.clone() {
            if !self.event_keys.as_ref().is_some_and(|k| !k.is_empty())
                && let Some(r) = self.replay_options(&event_id)
            {
                let mut a = ActiveEvent::new(&event_id, Some(r.keys), self.event_chosen.clone(), EventSource::Log);
                a.options_replayed = true;
                a.possible_keys = Some(r.possible);
                return Some(a);
            }
            let mut a = ActiveEvent::new(&event_id, self.event_keys.clone(), self.event_chosen.clone(), EventSource::Log);
            a.random_pages = self.random_pages_of(&event_id);
            a.offered_after = self.offered_after_of(&event_id);
            return Some(a);
        }
        if self.event_guess.is_none()
            && let Some(snap) = snapshot
            && !self.is_behind_log(snap)
            && let Some(guess) = events::predict(&snap.save, Some(self.card_version()))
        {
            let seen = snap.save.events.as_ref().map_or(0, |e| e.events_seen.len());
            self.event_check = Some((guess.clone(), seen));
            let alt = if guess.certain { String::new() } else { format!(" (or {})", guess.alternatives.join(", ")) };
            self.diag(format!("event predicted: {}{alt}", guess.event_id));
            self.event_guess = Some(guess);
        }
        let g = self.event_guess.clone()?;
        let draw = if g.certain { self.replay_options(&g.event_id) } else { None };
        let mut a = ActiveEvent::new(&g.event_id, draw.as_ref().map(|d| d.keys.clone()), self.event_chosen.clone(), EventSource::SavePrediction);
        a.certain = g.certain;
        a.alternatives = g.alternatives.clone();
        a.options_replayed = draw.is_some();
        a.possible_keys = draw.map(|d| d.possible);
        a.random_pages = self.random_pages_of(&g.event_id);
        a.offered_after = self.offered_after_of(&g.event_id);
        Some(a)
    }

    fn offered_after_of(&mut self, event_id: &str) -> Option<BTreeMap<String, Vec<String>>> {
        if !event_pages::offered_handles(event_id) {
            return None;
        }
        let seed = self.event_rng_seed(event_id)?;
        let save = self.choose_save()?.save.clone();
        let child = self.uses_v113_rules(&save);
        if let Some((e, s, c, m)) = &self.offered_after_cache
            && e == event_id
            && *s == seed
            && *c == child
        {
            return m.clone();
        }
        let map = event_pages::offered_options(event_id, seed, child)?;
        let text: Vec<String> =
            map.iter().map(|(k, v)| format!("{} -> {}", short_key(k), v.iter().map(|x| short_key(x)).collect::<Vec<_>>().join(", "))).collect();
        self.diag(format!("{event_id} options (seed replay): {}", text.join("; ")));
        self.offered_after_cache = Some((event_id.into(), seed, child, Some(map.clone())));
        Some(map)
    }

    fn random_pages_of(&mut self, event_id: &str) -> Option<BTreeMap<String, String>> {
        if !event_pages::random_pages_handles(event_id) {
            return None;
        }
        let seed = self.event_rng_seed(event_id)?;
        if let Some((e, s, m)) = &self.random_pages_cache
            && e == event_id
            && *s == seed
        {
            return m.clone();
        }
        let map = event_pages::random_pages(event_id, seed)?;
        let text: Vec<String> = map.iter().map(|(k, v)| format!("{k} = {v}")).collect();
        self.diag(format!("{event_id} random page (seed replay): {}", text.join(", ")));
        self.random_pages_cache = Some((event_id.into(), seed, Some(map.clone())));
        Some(map)
    }

    fn replay_options(&mut self, event_id: &str) -> Option<ReplayedOptions> {
        if event_id == neow::EVENT_ID { self.neow_draw() } else { self.ancient_draw(event_id) }
    }

    fn ancient_draw(&mut self, event_id: &str) -> Option<ReplayedOptions> {
        if !ancients::handles(event_id) {
            return None;
        }
        let save = self.choose_save()?.save.clone();
        if save.modifier_count != 0 {
            return None;
        }
        let ev = save.events.as_ref()?;
        let seed = self.run_seed()?;
        if ev.ancient_id.as_deref() != Some(event_id) {
            return None;
        }
        let map = save.map.as_ref()?;
        let cur = map.current?;
        if map.nodes.get(&cur).is_none_or(|n| n.kind != MapNodeType::Ancient) {
            return None;
        }
        let slot = self.my_slot();
        if slot < 0 {
            return None;
        }
        let scheme = self.scheme();
        let v113 = self.uses_v113_rules(&save);
        let key = format!("{seed}|{slot}|{event_id}|{}|{scheme:?}|{v113}", ev.act_index);
        if let Some((k, d)) = &self.ancient_cache
            && *k == key
        {
            return d.clone();
        }
        let mut inputs = AncientInputs::new(seed);
        inputs.slot = slot;
        inputs.act_index = ev.act_index;
        inputs.character = save.state.character.clone();
        inputs.deck = save.state.deck.iter().map(|d| AncientDeckCard { id: d.id().to_string(), enchantment_id: d.enchantment_id.clone() }).collect();
        inputs.relics = save.state.relics.clone();
        inputs.uses_v113_rules = v113;
        inputs.unlocked_epochs = Some(save.unlocked_epochs.clone());
        inputs.scheme = scheme;
        let mut keys = ancients::keys(event_id, &inputs);
        if save.unlocked_epochs.is_empty() {
            let mut all = inputs.clone();
            all.unlocked_epochs = None;
            if keys != ancients::keys(event_id, &all) {
                keys = None;
            }
        }
        let draw = keys.map(|k| ReplayedOptions { keys: k, possible: ancients::possible_keys(event_id, &inputs).unwrap_or_default() });
        self.ancient_cache = Some((key, draw.clone()));
        self.diag(match &draw {
            None => format!("{event_id} options: unlocks unknown and they decide the draw; showing every option"),
            Some(d) => format!("{event_id} options (seed replay): {}", d.keys.iter().map(|k| short_key(k)).collect::<Vec<_>>().join(", ")),
        });
        draw
    }

    fn neow_draw(&mut self) -> Option<ReplayedOptions> {
        let save = self.choose_save()?.save.clone();
        if save.modifier_count != 0 {
            return None;
        }
        let seed = self.run_seed()?;
        let slot = self.my_slot();
        if slot < 0 {
            return None;
        }
        let players = if self.is_multiplayer() { self.player_order().len() as i32 } else { 1 };
        let scheme = self.scheme();
        let v113_rules = self.uses_v113_rules(&save);
        if v113_rules && !save.state.is_standard_run {
            return None;
        }
        let v113 = v113_rules && !rng::is_migrated(&seed);
        if let Some((s, sl, p, sc, v, d)) = &self.neow_cache
            && *s == seed
            && *sl == slot
            && *p == players
            && *sc == scheme
            && *v == v113
        {
            return d.clone();
        }
        let unlocked = if !save.unlocked_epochs.is_empty() {
            Some(progress::all_characters_unlocked(&save.unlocked_epochs, v113))
        } else {
            self.options.paths.as_ref().and_then(|p| p.saves_directory.as_deref()).and_then(|d| progress::all_characters_unlocked_in(d, v113))
        };
        let pools = neow::effective_scheme(&seed, scheme);
        let draw = match unlocked {
            Some(u) => Some(ReplayedOptions { keys: neow::keys(&seed, slot, players, u, scheme, v113), possible: neow::possible_keys(players, u, pools) }),
            None => {
                let a = neow::keys(&seed, slot, players, true, scheme, v113);
                (a == neow::keys(&seed, slot, players, false, scheme, v113)).then(|| ReplayedOptions { keys: a, possible: neow::possible_keys(players, true, pools) })
            }
        };
        self.neow_cache = Some((seed, slot, players, scheme, v113, draw.clone()));
        self.diag(match &draw {
            None => "neow options: progress.save unreadable and the unlocks decide the draw; showing every option".into(),
            Some(d) => format!("neow options (seed replay): {}", d.keys.iter().map(|k| short_key(k)).collect::<Vec<_>>().join(", ")),
        });
        draw
    }

    fn check_event_prediction(&mut self, save: Option<&ParsedSave>) {
        let (Some((guess, seen)), Some(ev)) = (self.event_check.clone(), save.and_then(|s| s.events.as_ref())) else { return };
        if Some(guess.event_id.as_str()) == ev.ancient_id.as_deref() {
            self.event_check = None;
            return;
        }
        if ev.events_seen.len() <= seen {
            return;
        }
        self.event_check = None;
        let actual = ev.events_seen[seen].clone();
        self.diag(if actual == guess.event_id {
            format!("event prediction ok: {actual}")
        } else {
            format!("event prediction MISS: guessed {}, game pulled {actual}", guess.event_id)
        });
    }

    fn kind_from_map(&self) -> CombatKind {
        if let Some(s) = self.choose_save()
            && let Some(map) = &s.save.map
            && let Some(c) = map.current
            && let Some(node) = map.nodes.get(&c)
        {
            return match node.kind {
                MapNodeType::Elite => CombatKind::Elite,
                MapNodeType::Boss => CombatKind::Boss,
                _ => CombatKind::Normal,
            };
        }
        CombatKind::Normal
    }

    // ============================================================================================
    // Who am I?
    // ============================================================================================

    fn is_multiplayer(&self) -> bool {
        match self.log_run.as_ref().and_then(|r| r.is_multiplayer) {
            Some(m) => m,
            None => self.choose_save().is_some_and(|s| s.save.player_net_ids.len() > 1),
        }
    }

    fn my_id(&self) -> u64 {
        if !self.is_multiplayer() {
            return 1;
        }
        self.options
            .local_net_id_override
            .or(self.log_local_id)
            .or(self.profile_steam_id)
            .or(self.options.paths.as_ref().and_then(|p| p.steam_id))
            .or(self.choose_save().map(|s| s.save.my_net_id))
            .unwrap_or(0)
    }

    fn is_me(&self, player_id: Option<u64>) -> bool {
        player_id.is_none_or(|p| p == self.my_id())
    }

    fn partners_of(&self, save: Option<&ParsedSave>, me: u64) -> Vec<PartnerPlayer> {
        if let Some(s) = save
            && !s.partners.is_empty()
            && s.my_player_matched
        {
            return s.partners.clone();
        }
        match &self.log_run {
            Some(r) if !r.players.is_empty() => r
                .players
                .iter()
                .filter(|p| p.net_id != me)
                .map(|p| PartnerPlayer { net_id: p.net_id, character: p.character.clone(), deck: vec![], relics: vec![] })
                .collect(),
            _ => vec![],
        }
    }

    fn player_order(&self) -> Vec<u64> {
        match &self.log_run {
            Some(r) if !r.players.is_empty() => r.players.iter().map(|p| p.net_id).collect(),
            _ => self.choose_save().map(|s| s.save.player_net_ids.clone()).unwrap_or_default(),
        }
    }

    // ============================================================================================
    // Rarity rolls
    // ============================================================================================

    fn flush_rolls(&mut self) {
        if self.roll_batch.is_empty() {
            return;
        }
        let rolls: Vec<CardRarityRolled> = self.roll_batch.drain(..).filter(|r| r.source() != RaritySource::Shop).collect();
        if rolls.is_empty() {
            return;
        }
        let mine = self.select_mine(&rolls);
        self.last_rolls =
            mine.iter().map(|r| RarityRoll { roll: r.roll, need: r.need, offset_before: r.offset, is_rare: r.is_rare(), source: r.source() }).collect();
        self.locate_offer(&rolls);
        self.pity_log = Some(self.after(mine.last().unwrap()));
        self.pity_log_seq = self.seq;
    }

    fn after(&self, r: &CardRarityRolled) -> f64 {
        if r.is_rare() || r.source() == RaritySource::Boss {
            return PITY_RESET;
        }
        let scarcity = r.is_scarcity().unwrap_or_else(|| self.ascension() >= SCARCITY_ASCENSION);
        (r.offset + if scarcity { 0.005 } else { 0.01 }).min(PITY_MAX)
    }

    fn ascension(&self) -> i32 {
        self.choose_save().map(|s| s.save.state.ascension).or(self.log_run.as_ref().and_then(|r| r.ascension)).unwrap_or(0)
    }

    fn run_seed(&self) -> Option<String> {
        self.log_run.as_ref().and_then(|r| r.seed.clone()).or_else(|| self.choose_save().and_then(|s| s.save.seed.clone()))
    }

    fn my_slot(&self) -> i32 {
        if self.is_multiplayer() {
            let me = self.my_id();
            self.player_order().iter().position(|x| *x == me).map_or(-1, |i| i as i32)
        } else {
            0
        }
    }

    fn current_act(&self) -> i32 {
        if self.log_act > 0 { self.log_act } else { self.choose_save().map_or(1, |s| s.save.state.act) }
    }

    fn reward_rng(&mut self) -> Option<&mut RewardRollReplay> {
        let slot = self.my_slot();
        if slot < 0 {
            return None;
        }
        let seed = self.run_seed()?;
        let scheme = self.scheme();
        if self.reward_rng.as_ref().is_none_or(|r| r.slot != slot || r.scheme != scheme) {
            self.reward_rng = Some(RewardRollReplay::new(&seed, slot, scheme));
        }
        self.reward_rng.as_mut()
    }

    fn scheme(&self) -> RngScheme {
        self.choose_save()
            .and_then(|s| rng::for_save_schema(s.save.schema_version))
            .unwrap_or_else(|| self.log_game_version.as_deref().map_or(DEFAULT_SCHEME, |v| rng::for_game_version(Some(v))))
    }

    fn event_rng_seed(&self, event_id: &str) -> Option<u64> {
        let slot = self.my_slot();
        if slot < 0 {
            return None;
        }
        let seed = self.run_seed()?;
        Some(event_rng_seed(self.scheme(), &seed, slot, event_id))
    }

    fn locate_offer(&mut self, rolls: &[CardRarityRolled]) {
        let Some(rng) = self.reward_rng() else { return };
        let found = rng.locate(rolls);
        let mut groups: Vec<Vec<OfferSlot>> = Vec::new();
        for s in found.into_iter().flatten() {
            match groups.last_mut() {
                Some(g) if g.last().unwrap().counter + 3 == s.counter => g.push(s),
                _ => groups.push(vec![s]),
            }
        }
        for g in groups {
            if g.len() > 3 && g.len() % 3 == 0 {
                self.offers.extend(g.chunks(3).map(<[OfferSlot]>::to_vec));
            } else {
                self.offers.push(g);
            }
        }
    }

    fn my_character(&self) -> Option<String> {
        let me = self.my_id();
        self.log_run
            .as_ref()
            .and_then(|r| r.players.iter().find(|p| p.net_id == me).map(|p| p.character.clone()))
            .or_else(|| self.log_run.as_ref().and_then(|r| r.character.clone()))
            .or_else(|| self.choose_save().map(|s| s.save.state.character.clone()))
    }

    fn uses_v113_rules(&self, save: &ParsedSave) -> bool {
        GameDataVersion::detect(save.schema_version, self.log_game_version.as_deref()) == Some(GameDataVersion::Latest)
    }

    fn card_version(&self) -> GameDataVersion {
        GameDataVersion::for_run(self.choose_save().map_or(0, |s| s.save.schema_version), self.log_game_version.as_deref())
    }

    fn ensure_pool(&mut self) -> bool {
        let v = self.card_version();
        if self.pool.is_none() || self.pool_version != Some(v) {
            self.pool = RewardCardPool::for_character(self.my_character().as_deref(), self.is_multiplayer(), v);
            self.pool_version = self.pool.as_ref().map(|p| p.version);
            self.live_epochs = u64::MAX;
        }
        self.pool.is_some()
    }

    fn picked_slots(&mut self, card_id: &str) -> Option<Vec<OfferSlot>> {
        if self.offers.is_empty() || !self.ensure_pool() {
            return None;
        }
        let pool = self.pool.as_ref().unwrap();
        let mut live = 0u64;
        let mut slots: Vec<OfferSlot> = Vec::new();
        for h in 0..pool.hypotheses().min(64) {
            if (self.live_epochs >> h) & 1 == 0 {
                continue;
            }
            for offer in &self.offers {
                let Some(shown) = pool.predict(offer, h) else { continue };
                for (i, c) in shown.iter().enumerate() {
                    if *c != card_id {
                        continue;
                    }
                    live |= 1u64 << h;
                    if !slots.contains(&offer[i]) {
                        slots.push(offer[i].clone());
                    }
                }
            }
        }
        if live == 0 {
            return None;
        }
        self.live_epochs = live;
        Some(slots)
    }

    fn reward_upgrade(&mut self, card_id: &str) -> Option<i32> {
        let picked = self.picked_slots(card_id);
        let act_index = self.current_act() - 1;
        let v = self.card_version();
        if !may_roll_upgrade(card_id, v) || act_index <= 0 {
            return Some(0);
        }
        let rarity = cards::rarity(card_id, v).unwrap();
        let asc = self.ascension();
        let outcomes_of = |slots: &mut dyn Iterator<Item = &OfferSlot>| {
            let mut o: Vec<bool> = Vec::new();
            for s in slots {
                let u = reward::upgrades(s.upgrade_roll, rarity, act_index, asc);
                if !o.contains(&u) {
                    o.push(u);
                }
            }
            o
        };
        let mut outcomes = match &picked {
            Some(p) if !p.is_empty() => outcomes_of(&mut p.iter()),
            _ => vec![],
        };
        if outcomes.len() != 1 {
            outcomes = outcomes_of(&mut self.offers.iter().flatten().filter(|s| s.rarity == rarity));
        }
        if outcomes.len() == 1 {
            return Some(outcomes[0] as i32);
        }
        if let Some(seen) = &self.seen_offer {
            let mut s: Vec<bool> = Vec::new();
            for c in seen.iter().filter(|c| c.id == card_id) {
                let u = c.upgrade > 0;
                if !s.contains(&u) {
                    s.push(u);
                }
            }
            if s.len() == 1 && (outcomes.is_empty() || outcomes.contains(&s[0])) {
                return Some(s[0] as i32);
            }
        }
        None
    }

    fn replay_transform(&mut self, p: &Patch) -> Option<String> {
        let seed = p.rng_seed?;
        if !self.ensure_pool() {
            return None;
        }
        let mut g = GameRng::new(seed);
        for _ in 0..p.amount {
            g.next_u64();
        }
        let raw = g.next_u64();
        self.pool.as_ref().unwrap().transform_target(p.id.as_deref()?, raw, self.live_epochs).map(str::to_string)
    }

    /// What the screen recognizer saw on the reward screen.
    pub fn on_card_offer_seen(&mut self, offered: Vec<CardRef>) {
        self.seen_offer = Some(offered);
    }

    /// Replayed upgrade levels of the shown reward cards; None when unknown or the hypotheses disagree.
    pub fn replayed_reward_upgrades(&mut self, shown: &[String]) -> Option<BTreeMap<String, i32>> {
        let distinct: HashSet<&String> = shown.iter().collect();
        if shown.is_empty() || distinct.len() != shown.len() || self.offers.is_empty() || !self.ensure_pool() {
            return None;
        }
        let act_index = self.current_act() - 1;
        let asc = self.ascension();
        let v = self.card_version();
        let egg_types: HashSet<&str> = EGGS
            .iter()
            .filter(|(r, _)| self.current.as_ref().is_some_and(|c| c.state.relics.iter().any(|x| x == r)))
            .map(|(_, t)| *t)
            .collect();
        let pool = self.pool.as_ref().unwrap();
        let mut result: Option<BTreeMap<String, i32>> = None;
        for h in 0..pool.hypotheses().min(64) {
            if (self.live_epochs >> h) & 1 == 0 {
                continue;
            }
            for offer in screens(&self.offers) {
                let Some(cards_) = pool.predict(&offer, h) else { continue };
                if !shown.iter().all(|s| cards_.contains(&s.as_str())) {
                    continue;
                }
                let mut levels = BTreeMap::new();
                for (i, c) in cards_.iter().enumerate() {
                    if !shown.iter().any(|s| s == c) {
                        continue;
                    }
                    if let Some(t) = cards::type_of(c, v)
                        && egg_types.contains(t)
                    {
                        return None;
                    }
                    let up = act_index > 0
                        && may_roll_upgrade(c, v)
                        && reward::upgrades(offer[i].upgrade_roll, cards::rarity(c, v).unwrap(), act_index, asc);
                    levels.insert(c.to_string(), up as i32);
                }
                match &result {
                    None => result = Some(levels),
                    Some(r) if *r != levels => return None,
                    _ => {}
                }
            }
        }
        result
    }

    /// The card rewards of this room with `count` cards, replayed from the run seed, each in screen order.
    pub fn replayed_reward_cards(&mut self, count: usize) -> Vec<Vec<String>> {
        let mut out = Vec::new();
        if count == 0 || self.offers.is_empty() || !self.ensure_pool() {
            return out;
        }
        let pool = self.pool.as_ref().unwrap();
        for offer in screens(&self.offers).into_iter().filter(|o| o.len() == count) {
            let mut result: Option<Vec<&str>> = None;
            for h in 0..pool.hypotheses().min(64) {
                if (self.live_epochs >> h) & 1 == 0 {
                    continue;
                }
                match pool.predict(&offer, h) {
                    Some(c) if result.as_ref().is_none_or(|r| *r == c) => result = Some(c),
                    _ => {
                        result = None;
                        break;
                    }
                }
            }
            if let Some(r) = result {
                out.push(r.into_iter().map(str::to_string).collect());
            }
        }
        out
    }

    fn select_mine(&self, rolls: &[CardRarityRolled]) -> Vec<CardRarityRolled> {
        let order = self.player_order();
        let n = order.len();
        if n <= 1 {
            return rolls.to_vec();
        }
        let me = self.my_id();
        let my_index = order.iter().position(|x| *x == me);
        let segments: Vec<Vec<CardRarityRolled>> = if rolls.iter().all(|r| r.source() == RaritySource::Boss) {
            if rolls.len() % n == 0 { rolls.chunks(rolls.len() / n).map(<[_]>::to_vec).collect() } else { vec![rolls.to_vec()] }
        } else {
            let mut s = vec![vec![rolls[0].clone()]];
            for i in 1..rolls.len() {
                if (rolls[i].offset - self.after(&rolls[i - 1])).abs() > 0.001 {
                    s.push(vec![]);
                }
                s.last_mut().unwrap().push(rolls[i].clone());
            }
            s
        };
        if let Some(known) = self.known_pity() {
            let m: Vec<&Vec<CardRarityRolled>> = segments.iter().filter(|s| (s[0].offset - known).abs() < 0.001).collect();
            if m.len() == 1 {
                return m[0].clone();
            }
        }
        if let Some(i) = my_index {
            if segments.len() == n {
                return segments[i].clone();
            }
            if rolls.len() % n == 0 {
                return rolls.chunks(rolls.len() / n).nth(i).unwrap().to_vec();
            }
        }
        rolls.to_vec()
    }

    fn known_pity(&self) -> Option<f64> {
        match self.choose_save() {
            None => Some(self.pity_log.unwrap_or(PITY_RESET)),
            Some(s) => {
                if self.pity_log_seq > self.cutoff_seq(s) {
                    self.pity_log
                } else {
                    s.save.card_rarity_offset
                }
            }
        }
    }

    // ============================================================================================
    // Composition
    // ============================================================================================

    fn choose_save(&self) -> Option<&SaveSnapshot> {
        if self.saves.is_empty() {
            return None;
        }
        if let Some(seed) = self.log_run.as_ref().and_then(|r| r.seed.as_ref()) {
            return self.saves.iter().map(|(_, s)| s).find(|s| s.save.seed.as_ref() == Some(seed));
        }
        // MaxBy: the first of the newest.
        let mut best: Option<&SaveSnapshot> = None;
        for (_, s) in &self.saves {
            if best.is_none_or(|b| s.file_write_ms > b.file_write_ms) {
                best = Some(s);
            }
        }
        best
    }

    fn cutoff_seq(&self, save: &SaveSnapshot) -> i64 {
        let name = save_file_name(save);
        for m in self.save_marks.iter().rev() {
            if m.bytes == save.file_bytes && m.file_name.eq_ignore_ascii_case(name) {
                return m.seq;
            }
        }
        self.save_marks.last().map_or(-1, |m| m.seq)
    }

    fn is_behind_log(&self, save: &SaveSnapshot) -> bool {
        let name = save_file_name(save);
        for m in self.save_marks.iter().rev() {
            if m.file_name.eq_ignore_ascii_case(name) {
                return m.bytes != save.file_bytes;
            }
        }
        false
    }

    fn recompute(&mut self) {
        self.current = self.compose();
    }

    fn compose(&mut self) -> Option<TrackedRun> {
        let me = self.my_id();
        let save = self.choose_save().cloned();
        let mut potions: Vec<PotionSlot>;
        let mut max_potions = 3;
        let cutoff: i64;
        let pity: Option<f64>;
        let from_save = save.is_some();
        let mut state: RunState;
        let mut map: Option<MapGraph> = None;

        if let Some(s) = &save {
            state = s.save.state.clone();
            map = s.save.map.clone();
            potions = s.save.potions.clone();
            max_potions = s.save.max_potion_slots;
            cutoff = self.cutoff_seq(s);
            pity = if self.pity_log_seq > cutoff { self.pity_log } else { s.save.card_rarity_offset };
            if self.save_marks.iter().any(|m| m.seq == cutoff && m.bytes == s.file_bytes) {
                self.patches.retain(|p| p.seq > cutoff);
            }
        } else if let Some(run) = self.log_run.clone().filter(|r| !r.continued)
            && let Some(rebuilt) = self.build_from_log(&run, me)
        {
            state = rebuilt;
            potions = vec![];
            cutoff = -1;
            pity = Some(self.pity_log.unwrap_or(PITY_RESET));
        } else {
            return None;
        }

        let mut deck = DeckReplay::new(state.deck.clone(), self.card_version());
        let mut relics = state.relics.clone();
        let (mut gold, mut removals, mut applied) = (state.gold, state.card_removals_bought, 0);
        let mut rod_fights = 0;
        let order = self.player_order();
        let patches = self.patches.clone();
        for p in &patches {
            if p.seq <= cutoff {
                continue;
            }
            if from_save && p.kind == PatchKind::UnknownSelect {
                deck.named(p.id.as_deref().unwrap(), state.floor);
                applied += 1;
                continue;
            }
            if from_save && matches!(p.kind, PatchKind::RandomUpgrades | PatchKind::UnknownAdds | PatchKind::RandomRemoves) {
                continue;
            }
            applied += 1;
            let id = p.id.as_deref().unwrap_or("");
            match p.kind {
                PatchKind::AddCard => {
                    deck.duplicate_adds = relics.iter().any(|r| r == BING_BONG);
                    deck.upgrade_added_types = EGGS.iter().filter(|(r, _)| relics.iter().any(|x| x == r)).map(|(_, t)| t.to_string()).collect();
                    let reason = (p.arg.as_deref() == Some(UPGRADE_ROLL_ARG) && !from_save).then_some(UncertainReason::UpgradeRoll);
                    deck.add(id, state.floor, reason, p.amount);
                }
                PatchKind::ShuffleUpgrades => deck.shuffle_upgrades(&mut GameRng::new(p.rng_seed.unwrap()), p.amount, p.arg.as_deref()),
                PatchKind::PickUpgrade => {
                    let mut g = GameRng::new(p.rng_seed.unwrap());
                    for _ in 0..p.amount {
                        g.next_u64();
                    }
                    deck.pick_upgrade(&mut g);
                }
                PatchKind::UpgradeAll => deck.upgrade_all(),
                PatchKind::ShopRemoveCard => {
                    removals += 1;
                    deck.remove(id);
                }
                PatchKind::RemoveCard => {
                    deck.remove(id);
                }
                PatchKind::UpgradeCard => deck.upgrade(id, state.floor),
                PatchKind::TransformCard => {
                    let target = match &p.arg {
                        Some(a) => Some(a.clone()),
                        None => self.replay_transform(p),
                    };
                    deck.transform(id, target.as_deref(), state.floor);
                }
                PatchKind::EnchantCard => deck.enchant(id, p.arg.as_deref(), state.floor),
                PatchKind::DuplicateCard => deck.duplicate(id, state.floor),
                PatchKind::UnknownSelect => deck.mark_unknown(id, state.floor),
                PatchKind::RandomUpgrades => deck.random_upgrades(p.amount, p.arg.as_deref()),
                PatchKind::UnknownAdds => deck.unknown_adds(p.amount),
                PatchKind::RandomRemoves => deck.random_removes(p.amount),
                PatchKind::StolenReturned => {
                    if p.steal_seq <= cutoff {
                        deck.add(id, state.floor, None, 0);
                    }
                }
                PatchKind::CardStolen => {
                    if p.steal_seq > cutoff {
                        let before: Vec<CombatPlay> = p.before.iter().flatten().filter(|x| x.owner == me).cloned().collect();
                        let after: Vec<CombatPlay> = p.combat.iter().flat_map(|c| c.plays.iter()).filter(|x| x.owner == me).cloned().collect();
                        deck.stolen(&before, &after);
                    }
                }
                PatchKind::UpgradeBasics => deck.upgrade_basics(),
                PatchKind::TransformAllBasics => deck.transform_all_basics(),
                PatchKind::TransformStrikeAndDefend => deck.transform_strike_and_defend(),
                PatchKind::Transcend => deck.transcend(),
                PatchKind::CloneEnchanted => deck.clone_enchanted(),
                PatchKind::EnchantAllDefends => deck.enchant_all(deck_effects::has_defend_tag, p.arg.as_deref().unwrap()),
                PatchKind::CombatSeen => {
                    let c = p.combat.as_ref().unwrap();
                    if !from_save && !c.plays.is_empty() {
                        deck.observe(c, &order, me, state.floor);
                    }
                    deck.combat_ended();
                    if p.arg.as_deref() == Some("Normal") && relics.iter().any(|r| r == "RELIC.FISHING_ROD") {
                        rod_fights += 1;
                        if rod_fights % 3 == 0 {
                            deck.random_upgrades(1, None);
                        }
                    }
                    if p.arg.as_deref() == Some("Elite") && relics.iter().any(|r| r == "RELIC.WAR_HAMMER") {
                        deck.random_upgrades(4, None);
                    }
                }
                PatchKind::AddRelic => {
                    relics.push(id.to_string());
                    max_potions += match id {
                        "RELIC.POTION_BELT" => 2,
                        "RELIC.PHIAL_HOLSTER" => 1,
                        "RELIC.ALCHEMICAL_COFFER" => 4,
                        _ => 0,
                    };
                }
                PatchKind::AddGold => gold += p.amount,
                PatchKind::AddPotion => {
                    let limit = max_potions.max(potions.len() as i32 + 1);
                    let slot = (0..limit).find(|s| potions.iter().all(|x| x.slot != *s)).unwrap();
                    potions.push(PotionSlot { id: id.to_string(), slot });
                }
                PatchKind::RemovePotion => {
                    if let Some(i) = potions.iter().position(|x| x.id == id) {
                        potions.remove(i);
                    }
                }
            }
        }

        let from_history = !from_save && self.history_deck.is_some();
        let final_deck = if from_history { self.history_deck.clone().unwrap() } else { deck.cards() };
        state.empty_potion_slots = if from_save && state.empty_potion_slots.is_some() { Some((max_potions - potions.len() as i32).max(0)) } else { None };
        state.deck = final_deck;
        state.relics = relics;
        state.gold = gold;
        state.card_removals_bought = removals;
        state.vitals_known = from_save;

        if let Some(m) = &mut map
            && let Some(vote) = self.vote_coord
            && self.vote_is_move(self.vote_by)
            && self.vote_seq > cutoff
            && m.nodes.contains_key(&vote)
        {
            m.current = Some(vote);
        }
        let my_vote = self.my_vote.clone().or_else(|| self.partner_votes.get(&me).cloned());
        let multiplayer = self.is_multiplayer();
        let current_event = self.current_event_of(save.as_ref());

        let mut run = TrackedRun::new(state);
        run.deck_matches_save = save.as_ref().is_some_and(|s| run.state.deck == s.save.state.deck);
        run.map = map;
        run.screen = self.screen;
        run.last_combat_kind = self.last_kind;
        run.last_encounter_id = self.last_encounter.clone();
        run.rarity_pity_offset = pity;
        run.last_reward_rolls = self.last_rolls.clone();
        potions.sort_by_key(|x| x.slot);
        run.potions = potions;
        run.current_event = current_event;
        run.from_save = from_save;
        run.hp_gold_reliable = from_save;
        run.deck_approximate = !from_history && (!from_save || applied > 0);
        if !from_history {
            run.uncertain_cards = deck.uncertain();
            run.unidentified_cards = deck.unidentified();
            run.unknown_upgrades = deck.unknown_upgrades();
            run.unknown_removals = deck.unknown_removals();
        }
        run.deck_from_run_history = from_history;
        run.applied_log_patches = applied;
        run.local_player_id = me;
        run.player_count = if multiplayer { (self.player_order().len() as i32).max(1) } else { 1 };
        run.partners = if multiplayer { self.partners_of(save.as_ref().map(|s| &*s.save), me) } else { vec![] };
        run.my_vote = my_vote.filter(|v| !self.vote_is_move(v.player_id) && on_this_map(v, run.map.as_ref())).map(|v| v.coord);
        run.partner_votes = self.partner_votes.values().filter(|v| v.player_id != me && on_this_map(v, run.map.as_ref())).cloned().collect();
        run.seed = save.as_ref().and_then(|s| s.save.seed.clone()).or_else(|| self.log_run.as_ref().and_then(|r| r.seed.clone()));
        run.rng_scheme = self.scheme();
        run.outcome = self.outcome;
        run.save = save.map(|s| s.save);
        run.updated_at = self.now();
        Some(run)
    }

    fn vote_is_move(&self, voter: u64) -> bool {
        voter == 1 && !self.is_multiplayer()
    }

    fn build_from_log(&self, run: &LogRun, me: u64) -> Option<RunState> {
        let character = run
            .character
            .clone()
            .or_else(|| run.players.iter().find(|p| p.net_id == me).map(|p| p.character.clone()))
            .or_else(|| run.players.first().map(|p| p.character.clone()))?;
        let embedded;
        let loadouts = match &self.options.loadouts {
            Some(l) => l,
            None => {
                embedded = StartingLoadouts::embedded_for(self.card_version());
                embedded
            }
        };
        let lo = loadouts.for_character(&character);
        let ascension = run.ascension.unwrap_or(0);
        let mut deck: Vec<DeckCard> = lo
            .map(|l| l.deck.clone())
            .unwrap_or_default()
            .into_iter()
            .map(|id| {
                let mut c = DeckCard::new(CardRef::new(id, 0));
                c.floor_added = 1;
                c
            })
            .collect();
        if ascension >= data::ASCENDERS_BANE_LEVEL {
            let mut c = DeckCard::new(CardRef::new(data::ASCENDERS_BANE, 0));
            c.floor_added = 1;
            deck.push(c);
        }
        let mut s = RunState::new(character);
        s.ascension = ascension;
        s.act = self.log_act.max(1);
        s.floor = self.log_floor.max(1);
        s.hp = lo.and_then(|l| l.starting_hp).unwrap_or(0);
        s.max_hp = s.hp;
        s.gold = lo.and_then(|l| l.starting_gold).unwrap_or(0);
        s.deck = deck;
        s.relics = lo.map(|l| l.relics.clone()).unwrap_or_default();
        s.is_multiplayer = run.is_multiplayer.unwrap_or(false);
        Some(s)
    }
}

/// `EventModel.BeginEvent`'s stream; a shared event gives every player the slot-0 stream.
pub fn event_rng_seed(scheme: RngScheme, seed: &str, slot: i32, event_id: &str) -> u64 {
    rng::event_stream(scheme, seed, slot, event_id, data::is_shared_event(scheme, event_id))
}

fn on_this_map(v: &PartnerVote, map: Option<&MapGraph>) -> bool {
    map.is_none_or(|m| (v.generation <= 0 || v.generation == m.act) && m.nodes.contains_key(&v.coord))
}

fn save_file_name(save: &SaveSnapshot) -> &str {
    let p = save.path.as_str();
    &p[p.rfind(['/', '\\']).map_or(0, |i| i + 1)..]
}

/// The located slots regrouped into reward screens (one screen's cards sit 3 values apart).
fn screens(offers: &[Vec<OfferSlot>]) -> Vec<Vec<OfferSlot>> {
    let mut all: Vec<&OfferSlot> = Vec::new();
    let mut seen = HashSet::new();
    for s in offers.iter().flatten() {
        if seen.insert(s.counter) {
            all.push(s);
        }
    }
    all.sort_by_key(|s| s.counter);
    let mut chains: Vec<Vec<OfferSlot>> = Vec::new();
    for s in all {
        match chains.last_mut() {
            Some(c) if c.last().unwrap().counter + 3 == s.counter => c.push(s.clone()),
            _ => chains.push(vec![s.clone()]),
        }
    }
    chains.into_iter().flat_map(|c| if c.len() > 3 && c.len() % 3 == 0 { c.chunks(3).map(<[_]>::to_vec).collect() } else { vec![c] }).collect()
}

