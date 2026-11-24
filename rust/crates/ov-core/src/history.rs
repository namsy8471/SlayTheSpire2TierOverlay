//! Finished runs: `history/<unix>.run` files (`Sts2Overlay.Core.History.RunHistory`, `RunHistoryReader`).
//!
//! Parsing is tolerant: missing fields and fields of an unexpected kind become defaults. Only malformed JSON or a
//! non-object root is an error. Deviation: the C# reader also accepted comments and trailing commas; the game never
//! writes them.

use serde_json::Value;

use crate::json;
use crate::model::{CardRef, DeckCard, SavedCardProperties};

#[derive(Clone, Debug, Default, PartialEq)]
pub struct RunHistory {
    pub schema_version: i32,
    pub build_id: String,
    pub seed: String,
    pub game_mode: String,
    pub ascension: i32,
    pub win: bool,
    pub was_abandoned: bool,
    pub start_time: i64,
    pub run_time: i32,
    pub killed_by_encounter: Option<String>,
    pub killed_by_event: Option<String>,
    pub acts: Vec<String>,
    pub players: Vec<RunPlayer>,
    /// Every visited map point in order; `nodes[i].floor == i + 1`.
    pub nodes: Vec<RunNode>,
}

impl RunHistory {
    pub fn is_multiplayer(&self) -> bool {
        self.players.len() > 1
    }

    pub fn find_player(&self, id: i64) -> Option<&RunPlayer> {
        self.players.iter().find(|p| p.id == id)
    }
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct RunPlayer {
    /// Steam id in co-op, 1 in single player.
    pub id: i64,
    pub character: String,
    pub deck: Vec<DeckCard>,
    pub relics: Vec<RelicEntry>,
    pub potions: Vec<String>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct RelicEntry {
    pub id: String,
    pub floor_added: i32,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct RunNode {
    pub act_index: i32,
    pub index_in_act: i32,
    pub floor: i32,
    pub map_point_type: String,
    pub rooms: Vec<RunRoom>,
    pub stats: Vec<PlayerNodeStats>,
}

impl RunNode {
    pub fn act(&self) -> i32 {
        self.act_index + 1
    }

    pub fn stats_for(&self, player_id: i64) -> Option<&PlayerNodeStats> {
        self.stats.iter().find(|s| s.player_id == player_id)
    }

    pub fn has_shop(&self) -> bool {
        self.rooms.iter().any(|r| r.room_type == "shop")
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct RunRoom {
    pub room_type: String,
    pub model_id: Option<String>,
    pub monster_ids: Vec<String>,
    pub turns_taken: i32,
}

#[derive(Clone, Debug, PartialEq)]
pub struct CardChoice {
    pub card: DeckCard,
    pub was_picked: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub struct CardTransform {
    pub original: DeckCard,
    pub final_card: DeckCard,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PickChoice {
    pub id: String,
    pub was_picked: bool,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct PlayerNodeStats {
    pub player_id: i64,
    pub card_choices: Vec<CardChoice>,
    pub cards_gained: Vec<DeckCard>,
    pub cards_removed: Vec<DeckCard>,
    pub cards_transformed: Vec<CardTransform>,
    pub upgraded_cards: Vec<String>,
    pub cards_enchanted: Vec<DeckCard>,
    pub rest_site_choices: Vec<String>,
    pub relic_choices: Vec<PickChoice>,
    pub potion_choices: Vec<PickChoice>,
    pub bought_relics: Vec<String>,
    pub bought_potions: Vec<String>,
    pub relics_removed: Vec<String>,
    pub current_hp: i32,
    pub max_hp: i32,
    pub current_gold: i32,
    pub damage_taken: i32,
    pub hp_healed: i32,
    pub max_hp_gained: i32,
    pub max_hp_lost: i32,
    pub gold_gained: i32,
    pub gold_spent: i32,
    pub gold_lost: i32,
    pub gold_stolen: i32,
}

#[derive(Debug)]
pub struct HistoryError(pub String);

impl std::fmt::Display for HistoryError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for HistoryError {}

// The reader's lenient numbers: an integer, or a string holding one; anything else is 0.
fn long0(e: &Value, name: &str) -> i64 {
    match e.get(name) {
        Some(Value::Number(n)) => n.as_i64().unwrap_or(0),
        Some(Value::String(s)) => s.trim().parse::<i64>().unwrap_or(0),
        _ => 0,
    }
}

fn int0(e: &Value, name: &str) -> i32 {
    long0(e, name).clamp(i32::MIN as i64, i32::MAX as i64) as i32
}

fn str_opt(e: &Value, name: &str) -> Option<String> {
    e.get(name).and_then(Value::as_str).map(str::to_string)
}

fn arr<'a>(e: &'a Value, name: &str) -> &'a [Value] {
    json::array(Some(e), name)
}

fn objs<'a>(e: &'a Value, name: &str) -> impl Iterator<Item = &'a Value> {
    arr(e, name).iter().filter(|v| v.is_object())
}

fn strs(e: &Value, name: &str) -> Vec<String> {
    arr(e, name).iter().filter_map(Value::as_str).map(str::to_string).collect()
}

/// Strings, or the `id` of objects (`upgraded_cards`, `potions`).
fn ids_or_strs(e: &Value, name: &str) -> Vec<String> {
    arr(e, name)
        .iter()
        .filter_map(|x| if x.is_object() { x.get("id").and_then(Value::as_str) } else { x.as_str() })
        .map(str::to_string)
        .collect()
}

/// Parses the text of a `.run` file.
pub fn parse(text: &str) -> Result<RunHistory, HistoryError> {
    let root: Value = serde_json::from_str(text).map_err(|e| HistoryError(e.to_string()))?;
    from_root(&root)
}

pub fn read_file(path: &std::path::Path) -> Result<RunHistory, HistoryError> {
    let bytes = std::fs::read(path).map_err(|e| HistoryError(e.to_string()))?;
    parse(&json::decode(&bytes))
}

fn from_root(root: &Value) -> Result<RunHistory, HistoryError> {
    if !root.is_object() {
        return Err(HistoryError("Run history root must be a JSON object.".into()));
    }
    Ok(RunHistory {
        schema_version: int0(root, "schema_version"),
        build_id: str_opt(root, "build_id").unwrap_or_default(),
        seed: str_opt(root, "seed").unwrap_or_default(),
        game_mode: str_opt(root, "game_mode").unwrap_or_default(),
        ascension: int0(root, "ascension"),
        win: json::is_true(Some(root), "win"),
        was_abandoned: json::is_true(Some(root), "was_abandoned"),
        start_time: long0(root, "start_time"),
        run_time: int0(root, "run_time"),
        killed_by_encounter: str_opt(root, "killed_by_encounter"),
        killed_by_event: str_opt(root, "killed_by_event"),
        acts: strs(root, "acts"),
        players: objs(root, "players").map(parse_player).collect(),
        nodes: parse_nodes(root),
    })
}

fn parse_player(p: &Value) -> RunPlayer {
    RunPlayer {
        id: long0(p, "id"),
        character: str_opt(p, "character").unwrap_or_default(),
        deck: objs(p, "deck").map(parse_card).collect(),
        relics: objs(p, "relics")
            .map(|r| RelicEntry { id: str_opt(r, "id").unwrap_or_default(), floor_added: int0(r, "floor_added_to_deck") })
            .filter(|r| !r.id.is_empty())
            .collect(),
        potions: ids_or_strs(p, "potions"),
    }
}

fn parse_nodes(root: &Value) -> Vec<RunNode> {
    let mut nodes = Vec::new();
    for (act_index, act) in arr(root, "map_point_history").iter().enumerate() {
        let mut index_in_act = 0;
        if let Some(entries) = act.as_array() {
            for node in entries.iter().filter(|n| n.is_object()) {
                let floor = nodes.len() as i32 + 1;
                nodes.push(parse_node(node, act_index as i32, index_in_act, floor));
                index_in_act += 1;
            }
        }
    }
    nodes
}

fn parse_node(n: &Value, act_index: i32, index_in_act: i32, floor: i32) -> RunNode {
    RunNode {
        act_index,
        index_in_act,
        floor,
        map_point_type: str_opt(n, "map_point_type").unwrap_or_default(),
        rooms: objs(n, "rooms")
            .map(|r| RunRoom {
                room_type: str_opt(r, "room_type").unwrap_or_default(),
                model_id: str_opt(r, "model_id"),
                monster_ids: strs(r, "monster_ids"),
                turns_taken: int0(r, "turns_taken"),
            })
            .collect(),
        stats: objs(n, "player_stats").map(parse_stats).collect(),
    }
}

fn parse_cards(s: &Value, name: &str) -> Vec<DeckCard> {
    objs(s, name).map(parse_card).filter(|c| !c.card.id.is_empty()).collect()
}

fn parse_picks(s: &Value, name: &str) -> Vec<PickChoice> {
    objs(s, name)
        .map(|c| PickChoice { id: str_opt(c, "choice").unwrap_or_default(), was_picked: json::is_true(Some(c), "was_picked") })
        .filter(|c| !c.id.is_empty())
        .collect()
}

fn parse_stats(s: &Value) -> PlayerNodeStats {
    PlayerNodeStats {
        player_id: long0(s, "player_id"),
        card_choices: objs(s, "card_choices")
            .filter_map(|c| json::obj(Some(c), "card").map(|card| (card, json::is_true(Some(c), "was_picked"))))
            .map(|(card, picked)| CardChoice { card: parse_card(card), was_picked: picked })
            .filter(|c| !c.card.card.id.is_empty())
            .collect(),
        cards_gained: parse_cards(s, "cards_gained"),
        cards_removed: parse_cards(s, "cards_removed"),
        cards_transformed: objs(s, "cards_transformed")
            .filter_map(|t| Some((json::obj(Some(t), "original_card")?, json::obj(Some(t), "final_card")?)))
            .map(|(o, f)| CardTransform { original: parse_card(o), final_card: parse_card(f) })
            .collect(),
        upgraded_cards: ids_or_strs(s, "upgraded_cards"),
        cards_enchanted: objs(s, "cards_enchanted").filter_map(parse_enchanted).collect(),
        rest_site_choices: strs(s, "rest_site_choices"),
        relic_choices: parse_picks(s, "relic_choices"),
        potion_choices: parse_picks(s, "potion_choices"),
        bought_relics: strs(s, "bought_relics"),
        bought_potions: strs(s, "bought_potions"),
        relics_removed: strs(s, "relics_removed"),
        current_hp: int0(s, "current_hp"),
        max_hp: int0(s, "max_hp"),
        current_gold: int0(s, "current_gold"),
        damage_taken: int0(s, "damage_taken"),
        hp_healed: int0(s, "hp_healed"),
        max_hp_gained: int0(s, "max_hp_gained"),
        max_hp_lost: int0(s, "max_hp_lost"),
        gold_gained: int0(s, "gold_gained"),
        gold_spent: int0(s, "gold_spent"),
        gold_lost: int0(s, "gold_lost"),
        gold_stolen: int0(s, "gold_stolen"),
    }
}

fn parse_enchanted(e: &Value) -> Option<DeckCard> {
    let mut card = parse_card(json::obj(Some(e), "card")?);
    if card.card.id.is_empty() {
        return None;
    }
    if card.enchantment_id.is_none() {
        let (id, amount) = parse_enchantment(e);
        if id.is_some() {
            card.enchantment_id = id;
            card.enchantment_amount = amount;
        }
    }
    Some(card)
}

/// A SerializableCard (`.run` and save alike).
pub fn parse_card(c: &Value) -> DeckCard {
    let (enchantment_id, enchantment_amount) = parse_enchantment(c);
    DeckCard {
        card: CardRef::new(str_opt(c, "id").unwrap_or_default(), int0(c, "current_upgrade_level").max(0)),
        floor_added: int0(c, "floor_added_to_deck"),
        enchantment_id,
        enchantment_amount,
        saved: SavedCardProperties::read(c),
    }
}

/// `enchantment` as `{id, amount}` or a bare id string.
fn parse_enchantment(owner: &Value) -> (Option<String>, i32) {
    match owner.get("enchantment") {
        Some(e @ Value::Object(_)) => (str_opt(e, "id"), int0(e, "amount")),
        Some(Value::String(s)) => (Some(s.clone()), 0),
        _ => (None, 0),
    }
}

/// The anonymized id used for the user in test fixtures.
pub const FIXTURE_SELF_ID: i64 = 76561190000000001;

/// "Me" in a run: the only player, or the player with `steam_id` (default the fixture id), else the first.
pub fn resolve_local_player(run: &RunHistory, steam_id: Option<i64>) -> Option<&RunPlayer> {
    match run.players.len() {
        0 => None,
        1 => run.players.first(),
        _ => run.find_player(steam_id.unwrap_or(FIXTURE_SELF_ID)).or(run.players.first()),
    }
}
