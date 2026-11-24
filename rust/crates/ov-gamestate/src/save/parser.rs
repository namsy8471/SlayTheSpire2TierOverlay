//! `RunSaveParser`: a run save -> [`ParsedSave`]. Missing or extra fields never fail; only invalid JSON
//! (e.g. read mid-write) or a save without players does.

use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::path::Path;

use ov_core::history::parse_card;
use ov_core::json::{array, double, int, long, obj, prop, str_, strings, uint64};
use ov_core::map::{MapCoord, MapGraph, MapNode, MapNodeType};
use ov_core::model::{DeckCard, RunState};
use serde_json::Value;

use super::history as mph;
use super::{EventRoomState, ParsedSave, PartnerPlayer, PotionSlot, SavedRelic};

/// File names of an in-progress run (single player / co-op host).
pub const RUN_SAVE_FILE_NAMES: [&str; 2] = ["current_run.save", "current_run_mp.save"];

#[derive(Debug)]
pub struct SaveError(pub String);

impl std::fmt::Display for SaveError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for SaveError {}

/// Reads (sharing-safe) and parses a save; Ok(None) when the file does not exist.
pub fn parse_file(path: &Path, local_net_id: Option<u64>) -> Result<Option<ParsedSave>, SaveError> {
    match crate::io::read_all_text(path) {
        None => Ok(None),
        Some(text) => parse(&text, local_net_id).map(Some),
    }
}

/// `local_net_id`: our co-op net id (Steam id); ignored for a single-player save.
pub fn parse(text: &str, local_net_id: Option<u64>) -> Result<ParsedSave, SaveError> {
    let root_v: Value = serde_json::from_str(text).map_err(|e| SaveError(e.to_string()))?;
    let root = Some(&root_v);

    // --- pick "my" player
    let players = array(root, "players");
    if players.is_empty() {
        return Err(SaveError("save has no players".into()));
    }
    let net_ids: Vec<u64> = players.iter().map(|p| uint64(Some(p), "net_id").unwrap_or(0)).collect();
    let found = if players.len() == 1 { Some(0) } else { local_net_id.and_then(|want| net_ids.iter().position(|&n| n == want)) };
    let matched = found.is_some();
    let my_index = found.unwrap_or(0);
    let me = Some(&players[my_index]);
    let my_net_id = net_ids[my_index];

    // --- act, map, floor
    let act_index = int(root, "current_act_index").unwrap_or(0);
    let act = prop(root, "acts").and_then(Value::as_array).and_then(|a| usize::try_from(act_index).ok().and_then(|i| a.get(i)));
    let act_id = str_(act, "id").map(str::to_string);

    let visited = read_coords(prop(root, "visited_map_coords"));
    let schema = int(root, "schema_version").unwrap_or(0);
    let history = prop(root, "map_point_history");
    let map = obj(act, "saved_map").map(|saved| {
        let mut m = parse_map(saved, act_id.as_deref().unwrap_or(""), act_index + 1, visited.last().copied());
        let (done, easy) = read_hallway_fights(prop(act, "rooms"), &m, &visited, mph::floors_in_act(history, act_index));
        m.visited = visited.clone();
        m.normal_fights_done = done;
        m.easy_fights = easy;
        m.is_blitz = str_(root, "game_mode") == Some("blitz");
        // Schema 21 is game v0.113, which lays the map rows out differently.
        m.centered_rows = schema >= 21;
        m
    });
    let floors_before_act: i32 = (0..act_index.max(0)).map(|i| mph::floors_in_act(history, i)).sum();
    // The room-entry save is written before that room's history entry is appended, while visited_map_coords
    // already holds the new room: take whichever is larger.
    let floor = (floors_before_act + mph::floors_in_act(history, act_index)).max(floors_before_act + visited.len() as i32);

    // --- deck, relics, potions
    let deck: Vec<DeckCard> = array(me, "deck").iter().map(read_deck_card).collect();
    let relic_details: Vec<SavedRelic> = array(me, "relics")
        .iter()
        .filter_map(|r| {
            let id = str_(Some(r), "id")?;
            let true_bools: BTreeSet<String> = array(prop(Some(r), "props"), "bools")
                .iter()
                .filter(|b| matches!(prop(Some(b), "value"), Some(Value::Bool(true))))
                .filter_map(|b| str_(Some(b), "name").map(str::to_string))
                .collect();
            Some(SavedRelic { id: id.to_string(), true_bools })
        })
        .collect();
    let relics: Vec<String> = relic_details.iter().map(|r| r.id.clone()).collect();
    let potions: Vec<PotionSlot> = array(me, "potions")
        .iter()
        .filter_map(|p| Some(PotionSlot { id: str_(Some(p), "id")?.to_string(), slot: int(Some(p), "slot_index").unwrap_or(0) }))
        .collect();
    let potion_capacity = int(me, "max_potion_slot_count");

    // Removal price = 75 + 25 x removals; older saves lack the count, so count shop removals in the history.
    let removals = int(prop(me, "extra_fields"), "card_shop_removals_used").unwrap_or_else(|| mph::count_shop_removals(history, my_net_id));

    let mut state = RunState::new(str_(me, "character_id").unwrap_or("CHARACTER.UNKNOWN"));
    state.ascension = int(root, "ascension").unwrap_or(0);
    state.save_schema_version = Some(schema);
    state.is_standard_run = str_(root, "game_mode") == Some("standard")
        && (schema != 21 || matches!(prop(root, "is_custom_game"), Some(Value::Bool(false))))
        && array(root, "modifiers").is_empty();
    state.act = act_index + 1;
    state.act_boss = str_(prop(act, "rooms"), "boss_id").map(str::to_string);
    state.floor = floor.max(1);
    state.hp = int(me, "current_hp").unwrap_or(0);
    state.max_hp = int(me, "max_hp").unwrap_or(0);
    state.gold = int(me, "gold").unwrap_or(0);
    state.empty_potion_slots = match (potion_capacity, prop(me, "potions")) {
        (Some(cap), Some(Value::Array(_))) => Some((cap - potions.len() as i32).max(0)),
        _ => None,
    };
    state.deck = deck;
    state.relics = relics;
    state.is_multiplayer = players.len() > 1;
    state.card_removals_bought = removals;

    let player_odds = prop(me, "odds");
    let mut map_odds = BTreeMap::new();
    if let Some(Value::Object(o)) = prop(root, "odds") {
        for (k, v) in o {
            if let Some(f) = v.as_f64() {
                map_odds.insert(k.clone(), f);
            }
        }
    }

    let partners = players
        .iter()
        .enumerate()
        .filter(|(i, _)| *i != my_index)
        .map(|(i, p)| PartnerPlayer {
            net_id: net_ids[i],
            character: str_(Some(p), "character_id").unwrap_or("CHARACTER.UNKNOWN").to_string(),
            deck: array(Some(p), "deck").iter().map(read_deck_card).collect(),
            relics: array(Some(p), "relics").iter().filter_map(|r| str_(Some(r), "id").map(str::to_string)).collect(),
        })
        .collect();

    let grab_bag = obj(prop(me, "relic_grab_bag"), "relic_id_lists").and_then(Value::as_object).map(|bag| {
        // C# ToDictionary on duplicate keys throws; serde_json keeps one value per key, so no duplicates here.
        bag.iter().map(|(k, v)| (k.clone(), v.as_array().map(|a| a.iter().filter_map(Value::as_str).map(str::to_string).collect()).unwrap_or_default())).collect()
    });

    Ok(ParsedSave {
        state,
        map,
        seed: str_(prop(root, "rng"), "seed").map(str::to_string),
        schema_version: schema,
        my_net_id,
        my_player_matched: matched,
        player_net_ids: net_ids.clone(),
        partners,
        potions,
        unlocked_epochs: strings(prop(me, "unlock_state"), "unlocked_epochs"),
        relic_grab_bag: grab_bag,
        relic_details,
        max_potion_slots: potion_capacity.unwrap_or(3),
        card_rarity_offset: double(player_odds, "card_rarity_odds_value"),
        potion_reward_odds: double(player_odds, "potion_reward_odds_value"),
        unknown_map_point_odds: map_odds,
        save_time: long(root, "save_time").filter(|t| *t > 0),
        act_id,
        game_mode: str_(root, "game_mode").map(str::to_string),
        modifier_count: prop(root, "modifiers").and_then(Value::as_array).map_or(-1, |a| a.len() as i32),
        events: parse_events(root, act, act_index, history, visited.len() as i32, me),
    })
}

/// Hallway-fight progress of the current act (`acts[cur].rooms`): easy (`_WEAK`) encounters count, and fights done,
/// plus one when the save was written on entering a monster room whose fight is not pulled yet.
pub fn read_hallway_fights(rooms: Option<&Value>, map: &MapGraph, visited: &[MapCoord], history_in_act: i32) -> (Option<i32>, Option<i32>) {
    let Some(rooms @ Value::Object(_)) = rooms else { return (None, None) };
    let ids = array(Some(rooms), "normal_encounter_ids");
    let easy = (!ids.is_empty()).then(|| ids.iter().filter(|e| e.as_str().is_some_and(|s| s.ends_with("_WEAK"))).count() as i32);
    let Some(done) = int(Some(rooms), "normal_encounters_visited") else { return (None, easy) };
    let pending = visited.len() as i32 == history_in_act + 1
        && visited.last().and_then(|c| map.nodes.get(c)).is_some_and(|n| n.kind == MapNodeType::Monster);
    (Some(done + i32::from(pending)), easy)
}

fn read_deck_card(c: &Value) -> DeckCard {
    let ench = prop(Some(c), "enchantment");
    let mut card = parse_card(c);
    // The save parser reads ids as "?" when missing and does not clamp levels or accept string enchantments.
    card.card.id = str_(Some(c), "id").unwrap_or("?").to_string();
    card.card.upgrade = int(Some(c), "current_upgrade_level").unwrap_or(0);
    card.floor_added = int(Some(c), "floor_added_to_deck").unwrap_or(0);
    card.enchantment_id = str_(ench, "id").map(str::to_string);
    card.enchantment_amount = int(ench, "amount").unwrap_or(0);
    card
}

fn parse_events(root: Option<&Value>, act: Option<&Value>, act_index: i32, history: Option<&Value>, visited_count: i32, me: Option<&Value>) -> Option<EventRoomState> {
    let rooms = obj(act, "rooms")?;
    let rooms = Some(rooms);
    let act_history = history.and_then(Value::as_array).and_then(|a| usize::try_from(act_index).ok().and_then(|i| a.get(i)));
    let last = act_history.and_then(Value::as_array).and_then(|a| a.last());
    let prev_shop = array(last, "rooms").iter().any(|r| str_(Some(r), "room_type") == Some("shop"));
    let rng = prop(root, "rng");
    Some(EventRoomState {
        act_index,
        event_ids: strings(rooms, "event_ids"),
        events_visited: int(rooms, "events_visited").unwrap_or(0),
        ancient_id: str_(rooms, "ancient_id").map(str::to_string),
        events_seen: strings(root, "events_seen"),
        visited_in_act: visited_count,
        history_in_act: mph::floors_in_act(history, act_index),
        total_history: mph::total_floors(history),
        pre_finished_room: !matches!(prop(root, "pre_finished_room"), None | Some(Value::Null)),
        // Schema <= 18: rng.counters.<name>; 19+: rng.rngs.<name>.counter.
        unknown_map_point_counter: int(prop(rng, "counters"), "unknown_map_point")
            .or_else(|| int(prop(prop(rng, "rngs"), "unknown_map_point"), "counter"))
            .unwrap_or(0),
        previous_was_shop: prev_shop,
        number_of_runs: int(prop(me, "unlock_state"), "number_of_runs").unwrap_or(-1),
    })
}

/// `saved_map` -> [`MapGraph`]: nodes from `start`, `points[]`, `boss`, `second_boss`; edges are `children`.
pub fn parse_map(saved: &Value, act_id: &str, act_number: i32, current: Option<MapCoord>) -> MapGraph {
    let saved = Some(saved);
    let mut nodes = HashMap::new();
    let mut add = |point: Option<&Value>| {
        let Some(p @ Value::Object(_)) = point else { return };
        let Some(coord) = read_coord(prop(Some(p), "coord")) else { return };
        nodes.insert(coord, MapNode { coord, kind: MapNodeType::parse(str_(Some(p), "type")), children: read_coords(prop(Some(p), "children")) });
    };
    add(prop(saved, "start"));
    for p in array(saved, "points") {
        add(Some(p));
    }
    add(prop(saved, "boss"));
    add(prop(saved, "second_boss"));

    let start = read_coord(prop(prop(saved, "start"), "coord")).unwrap_or(MapCoord::new(0, 0));
    let boss = read_coord(prop(prop(saved, "boss"), "coord")).unwrap_or(start);
    MapGraph {
        act_id: act_id.to_string(),
        act: act_number,
        width: int(saved, "width").unwrap_or(0),
        height: int(saved, "height").unwrap_or(0),
        is_blitz: false,
        centered_rows: false,
        nodes,
        start,
        boss,
        second_boss: read_coord(prop(prop(saved, "second_boss"), "coord")),
        current,
        visited: Vec::new(),
        normal_fights_done: None,
        easy_fights: None,
    }
}

fn read_coord(e: Option<&Value>) -> Option<MapCoord> {
    Some(MapCoord::new(int(e, "col")?, int(e, "row")?))
}

fn read_coords(arr: Option<&Value>) -> Vec<MapCoord> {
    arr.and_then(Value::as_array).map(|a| a.iter().filter_map(|c| read_coord(Some(c))).collect()).unwrap_or_default()
}
