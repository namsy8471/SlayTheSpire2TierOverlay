//! Saves parse exactly as the C# `RunSaveParser` did (golden from the C# parity dumper, local id = fixture id).

use std::io::Read;

use ov_core::golden;
use serde_json::{Value, json};

fn gunzip(path: &std::path::Path) -> String {
    let mut s = String::new();
    flate2::read::GzDecoder::new(std::fs::File::open(path).unwrap()).read_to_string(&mut s).unwrap();
    s
}

fn dump(p: &ov_gamestate::save::ParsedSave) -> Value {
    let e = p.events.as_ref().map(|e| json!({"act_index": e.act_index, "event_ids": e.event_ids, "events_visited": e.events_visited,
        "ancient_id": e.ancient_id, "events_seen": e.events_seen, "visited_in_act": e.visited_in_act,
        "history_in_act": e.history_in_act, "total_history": e.total_history, "pre_finished_room": e.pre_finished_room,
        "unknown_map_point_counter": e.unknown_map_point_counter, "previous_was_shop": e.previous_was_shop,
        "number_of_runs": e.number_of_runs}));
    json!({"state": golden::state(&p.state), "map": golden::map(p.map.as_ref()), "seed": p.seed, "schema_version": p.schema_version,
        "my_net_id": p.my_net_id, "my_player_matched": p.my_player_matched, "player_net_ids": p.player_net_ids,
        "partners": p.partners.iter().map(|x| json!({"net_id": x.net_id, "character": x.character, "deck": golden::cards(&x.deck), "relics": x.relics})).collect::<Vec<_>>(),
        "potions": p.potions.iter().map(|x| json!({"id": x.id, "slot": x.slot})).collect::<Vec<_>>(),
        "unlocked_epochs": p.unlocked_epochs,
        "relic_grab_bag": p.relic_grab_bag.as_ref().map(|b| b.iter().map(|(k, v)| (k.clone(), json!(v))).collect::<serde_json::Map<_, _>>()),
        "relic_details": p.relic_details.iter().map(|r| json!({"id": r.id, "true_bools": r.true_bools})).collect::<Vec<_>>(),
        "max_potion_slots": p.max_potion_slots, "card_rarity_offset": p.card_rarity_offset, "potion_reward_odds": p.potion_reward_odds,
        "unknown_map_point_odds": p.unknown_map_point_odds, "save_time": p.save_time, "act_id": p.act_id, "game_mode": p.game_mode,
        "modifier_count": p.modifier_count, "events": e})
}

#[test]
fn saves_match_csharp() {
    let golden_text = gunzip(std::path::Path::new("tests/golden/saves.jsonl.gz"));
    let root = std::path::Path::new("../../../tests/Sts2Overlay.GameState.Tests/Fixtures");
    let mut n = 0;
    for (file, want) in golden::read_jsonl(&golden_text) {
        let path = ["event_saves/runs", "event_saves/room_entry", "."].iter().map(|d| root.join(d).join(&file)).find(|p| p.exists()).expect(&file);
        let text = if file.ends_with(".gz") { gunzip(&path) } else { std::fs::read_to_string(&path).unwrap() };
        let got = match ov_gamestate::save::parse(&text, Some(76561190000000001)) {
            Ok(p) => dump(&p),
            Err(_) => json!({"error": "JsonException"}),
        };
        assert!(golden::first_diff(&got, &want, "").is_none(), "{file}: {:?}", golden::first_diff(&got, &want, ""));
        n += 1;
    }
    assert_eq!(n, 34);
}
