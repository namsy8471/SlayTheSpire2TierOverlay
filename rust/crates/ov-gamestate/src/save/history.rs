//! Helpers over `map_point_history` (`MapPointHistory`): an array per act, one entry per room entered.

use serde_json::Value;

/// Entries over all acts (= the game's `RunState.TotalFloor`).
pub fn total_floors(history: Option<&Value>) -> i32 {
    history.and_then(Value::as_array).map_or(0, |acts| acts.iter().filter_map(Value::as_array).map(|a| a.len() as i32).sum())
}

/// Entries in one act (0-based).
pub fn floors_in_act(history: Option<&Value>, act_index: i32) -> i32 {
    if act_index < 0 {
        return 0;
    }
    history.and_then(Value::as_array).and_then(|a| a.get(act_index as usize)).and_then(Value::as_array).map_or(0, |a| a.len() as i32)
}

/// All entries of all acts in order.
pub fn entries(history: Option<&Value>) -> impl Iterator<Item = &Value> {
    history.and_then(Value::as_array).into_iter().flatten().filter_map(Value::as_array).flatten()
}

fn is_shop(entry: &Value) -> bool {
    entry.get("rooms").and_then(Value::as_array).is_some_and(|r| r.iter().any(|r| r.get("room_type").and_then(Value::as_str) == Some("shop")))
}

/// `player_stats[]` entry of a player (single player uses 1).
pub fn player_stats(entry: &Value, player_id: u64) -> Option<&Value> {
    entry.get("player_stats")?.as_array()?.iter().find(|s| s.is_object() && s.get("player_id").and_then(Value::as_u64) == Some(player_id))
}

/// Cards a player removed in shop rooms (fallback for saves without `card_shop_removals_used`).
pub fn count_shop_removals(history: Option<&Value>, player_id: u64) -> i32 {
    entries(history)
        .filter(|e| is_shop(e))
        .filter_map(|e| player_stats(e, player_id))
        .filter_map(|s| s.get("cards_removed").and_then(Value::as_array))
        .map(|a| a.len() as i32)
        .sum()
}
