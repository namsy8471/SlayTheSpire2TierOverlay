//! Canonical JSON of the model types, the same shape the C# parity dumper writes (tests compare the two).
//! Only for parity tests; not used by the app.

use serde_json::{Map, Value, json};

use crate::history::{PickChoice, RunHistory};
use crate::map::{MapCoord, MapGraph, MapNodeType};
use crate::model::{DeckCard, RunState};

pub fn card(d: &DeckCard) -> Value {
    let saved: Map<String, Value> = d.saved.0.iter().map(|(k, v)| (k.clone(), json!(v))).collect();
    json!({"id": d.card.id, "upgrade": d.card.upgrade, "floor_added": d.floor_added,
           "enchantment_id": d.enchantment_id, "enchantment_amount": d.enchantment_amount, "saved": saved})
}

pub fn cards(d: &[DeckCard]) -> Value {
    Value::Array(d.iter().map(card).collect())
}

pub fn coord(c: &MapCoord) -> Value {
    json!([c.col, c.row])
}

fn kind(k: MapNodeType) -> &'static str {
    match k {
        MapNodeType::Unknown => "Unknown",
        MapNodeType::Monster => "Monster",
        MapNodeType::Elite => "Elite",
        MapNodeType::RestSite => "RestSite",
        MapNodeType::Shop => "Shop",
        MapNodeType::Treasure => "Treasure",
        MapNodeType::Ancient => "Ancient",
        MapNodeType::Boss => "Boss",
    }
}

pub fn state(s: &RunState) -> Value {
    json!({"character": s.character, "ascension": s.ascension, "save_schema_version": s.save_schema_version,
        "is_standard_run": s.is_standard_run, "act": s.act, "act_boss": s.act_boss, "floor": s.floor,
        "hp": s.hp, "max_hp": s.max_hp, "gold": s.gold, "empty_potion_slots": s.empty_potion_slots,
        "vitals_known": s.vitals_known, "deck": cards(&s.deck), "relics": s.relics,
        "is_multiplayer": s.is_multiplayer, "card_removals_bought": s.card_removals_bought})
}

pub fn map(m: Option<&MapGraph>) -> Value {
    let Some(m) = m else { return Value::Null };
    let mut nodes: Vec<_> = m.nodes.values().collect();
    nodes.sort_by_key(|n| (n.coord.col, n.coord.row));
    json!({"act_id": m.act_id, "act": m.act, "width": m.width, "height": m.height, "is_blitz": m.is_blitz,
        "centered_rows": m.centered_rows,
        "nodes": nodes.iter().map(|n| json!({"coord": coord(&n.coord), "kind": kind(n.kind),
            "children": n.children.iter().map(coord).collect::<Vec<_>>()})).collect::<Vec<_>>(),
        "start": coord(&m.start), "boss": coord(&m.boss), "second_boss": m.second_boss.as_ref().map(coord),
        "current": m.current.as_ref().map(coord), "visited": m.visited.iter().map(coord).collect::<Vec<_>>(),
        "normal_fights_done": m.normal_fights_done, "easy_fights": m.easy_fights})
}

fn picks(xs: &[PickChoice]) -> Value {
    Value::Array(xs.iter().map(|c| json!({"id": c.id, "was_picked": c.was_picked})).collect())
}

pub fn run(r: &RunHistory) -> Value {
    json!({"schema_version": r.schema_version, "build_id": r.build_id, "seed": r.seed, "game_mode": r.game_mode,
        "ascension": r.ascension, "win": r.win, "was_abandoned": r.was_abandoned, "start_time": r.start_time,
        "run_time": r.run_time, "killed_by_encounter": r.killed_by_encounter, "killed_by_event": r.killed_by_event,
        "acts": r.acts,
        "players": r.players.iter().map(|p| json!({"id": p.id, "character": p.character, "deck": cards(&p.deck),
            "relics": p.relics.iter().map(|x| json!({"id": x.id, "floor_added": x.floor_added})).collect::<Vec<_>>(),
            "potions": p.potions})).collect::<Vec<_>>(),
        "nodes": r.nodes.iter().map(|n| json!({"act_index": n.act_index, "index_in_act": n.index_in_act, "floor": n.floor,
            "map_point_type": n.map_point_type,
            "rooms": n.rooms.iter().map(|x| json!({"room_type": x.room_type, "model_id": x.model_id,
                "monster_ids": x.monster_ids, "turns_taken": x.turns_taken})).collect::<Vec<_>>(),
            "stats": n.stats.iter().map(|s| json!({"player_id": s.player_id,
                "card_choices": s.card_choices.iter().map(|c| json!({"card": card(&c.card), "was_picked": c.was_picked})).collect::<Vec<_>>(),
                "cards_gained": cards(&s.cards_gained), "cards_removed": cards(&s.cards_removed),
                "cards_transformed": s.cards_transformed.iter().map(|t| json!({"original": card(&t.original), "final": card(&t.final_card)})).collect::<Vec<_>>(),
                "upgraded_cards": s.upgraded_cards, "cards_enchanted": cards(&s.cards_enchanted),
                "rest_site_choices": s.rest_site_choices, "relic_choices": picks(&s.relic_choices),
                "potion_choices": picks(&s.potion_choices), "bought_relics": s.bought_relics,
                "bought_potions": s.bought_potions, "relics_removed": s.relics_removed,
                "hp": [s.current_hp, s.max_hp, s.current_gold, s.damage_taken, s.hp_healed, s.max_hp_gained,
                       s.max_hp_lost, s.gold_gained, s.gold_spent, s.gold_lost, s.gold_stolen]})).collect::<Vec<_>>()
            })).collect::<Vec<_>>()})
}

/// Reads a gzipped JSON-lines golden file: `(file, value)` per line.
pub fn read_jsonl(text: &str) -> Vec<(String, Value)> {
    text.lines()
        .filter(|l| !l.trim().is_empty())
        .map(|l| {
            let v: Value = serde_json::from_str(l).expect("golden line");
            (v["file"].as_str().unwrap().to_string(), v["v"].clone())
        })
        .collect()
}

/// First differing path between two values, for readable failures.
pub fn first_diff(a: &Value, b: &Value, path: &str) -> Option<String> {
    match (a, b) {
        (Value::Object(x), Value::Object(y)) => {
            for (k, va) in x {
                let p = format!("{path}.{k}");
                match y.get(k) {
                    None => return Some(format!("{p}: missing on right")),
                    Some(vb) => {
                        if let Some(d) = first_diff(va, vb, &p) {
                            return Some(d);
                        }
                    }
                }
            }
            y.keys().find(|k| !x.contains_key(*k)).map(|k| format!("{path}.{k}: missing on left"))
        }
        (Value::Array(x), Value::Array(y)) => {
            for (i, (va, vb)) in x.iter().zip(y).enumerate() {
                if let Some(d) = first_diff(va, vb, &format!("{path}[{i}]")) {
                    return Some(d);
                }
            }
            (x.len() != y.len()).then(|| format!("{path}: len {} vs {}", x.len(), y.len()))
        }
        (Value::Number(x), Value::Number(y)) => (x.as_f64() != y.as_f64()).then(|| format!("{path}: {x} vs {y}")),
        _ => (a != b).then(|| format!("{path}: {a} vs {b}")),
    }
}
