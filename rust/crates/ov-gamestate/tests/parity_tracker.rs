//! The tracker builds the same snapshots as the C# `GameStateTracker` (golden from the C# parity dumper):
//! every fixture log fed in chunks of 25 lines (log-only rebuild, co-op guest path), and every fixture save fed as
//! a room-entry save of an event room (event prediction, ancient / Neow replay).

use std::io::Read;
use std::sync::Arc;

use ov_core::golden;
use ov_gamestate::save::watcher::SaveSnapshot;
use ov_gamestate::tracking::tracked::TrackedRun;
use ov_gamestate::tracking::tracker::{Tracker, TrackerOptions};
use serde_json::{Value, json};

fn read(path: &std::path::Path) -> String {
    if path.extension().is_some_and(|e| e == "gz") {
        let mut s = String::new();
        flate2::read::GzDecoder::new(std::fs::File::open(path).unwrap()).read_to_string(&mut s).unwrap();
        s
    } else {
        std::fs::read_to_string(path).unwrap()
    }
}

fn opt_coord(c: Option<ov_core::map::MapCoord>) -> Value {
    c.map_or(Value::Null, |c| golden::coord(&c))
}

fn dump(t: Option<&TrackedRun>, rewards: Vec<Vec<String>>) -> Value {
    let Some(t) = t else { return Value::Null };
    let e = t.current_event.as_ref().map(|e| {
        json!({"event_id": e.event_id, "option_keys": e.option_keys, "chosen_keys": e.chosen_keys, "source": format!("{:?}", e.source),
            "certain": e.certain, "alternatives": e.alternatives, "options_replayed": e.options_replayed, "possible_keys": e.possible_keys,
            "random_pages": e.random_pages, "offered_after": e.offered_after})
    });
    json!({
        "state": golden::state(&t.state),
        "map_current": opt_coord(t.map.as_ref().and_then(|m| m.current)), "map_act": t.map.as_ref().map(|m| m.act),
        "screen": format!("{:?}", t.screen), "last_combat_kind": t.last_combat_kind.map(|k| format!("{k:?}")), "last_encounter_id": t.last_encounter_id,
        "rarity_pity_offset": t.rarity_pity_offset,
        "last_reward_rolls": t.last_reward_rolls.iter().map(|r| json!({"roll": r.roll, "need": r.need, "offset_before": r.offset_before, "is_rare": r.is_rare, "source": format!("{:?}", r.source)})).collect::<Vec<_>>(),
        "potions": t.potions.iter().map(|p| json!({"id": p.id, "slot": p.slot})).collect::<Vec<_>>(),
        "current_event": e,
        "from_save": t.from_save, "deck_matches_save": t.deck_matches_save, "hp_gold_reliable": t.hp_gold_reliable, "deck_approximate": t.deck_approximate,
        "uncertain_cards": t.uncertain_cards.iter().map(|u| json!({"i": u.deck_index, "id": u.card_id, "reason": format!("{:?}", u.reason)})).collect::<Vec<_>>(),
        "unidentified": t.unidentified_cards, "unknown_upgrades": t.unknown_upgrades, "unknown_removals": t.unknown_removals,
        "deck_from_run_history": t.deck_from_run_history, "applied": t.applied_log_patches, "me": t.local_player_id, "player_count": t.player_count,
        "partners": t.partners.iter().map(|p| json!({"net_id": p.net_id, "character": p.character, "deck": p.deck.len(), "relics": p.relics})).collect::<Vec<_>>(),
        "partner_votes": t.partner_votes.iter().map(|v| json!({"p": v.player_id, "g": v.generation, "c": golden::coord(&v.coord)})).collect::<Vec<_>>(),
        "my_vote": opt_coord(t.my_vote), "seed": t.seed, "rng_scheme": format!("{:?}", t.rng_scheme), "outcome": format!("{:?}", t.outcome),
        "rewards": rewards,
    })
}

fn tracker() -> Tracker {
    Tracker::new(TrackerOptions { clock: Some(Box::new(|| 0)), ..Default::default() })
}

const FIXTURES: &str = "../../../tests/Sts2Overlay.GameState.Tests/Fixtures";

#[test]
fn log_snapshots_match_csharp() {
    let golden_text = read(std::path::Path::new("tests/golden/tracker_logs.jsonl.gz"));
    let want = golden::read_jsonl(&golden_text);
    let root = std::path::Path::new(FIXTURES);
    let mut i = 0;
    let mut files = 0;
    while i < want.len() {
        let file = want[i].0.clone();
        let path = [".", "coop_runs"].iter().map(|d| root.join(d).join(&file)).find(|p| p.exists()).expect(&file);
        let text = read(&path);
        let lines: Vec<&str> = text.split('\n').collect();
        let mut t = tracker();
        for (k, chunk) in lines.chunks(25).enumerate() {
            t.process_lines(chunk);
            let rewards = t.replayed_reward_cards(3);
            let got = dump(t.current(), rewards);
            assert_eq!(want[i].0, file, "{file}: chunk count differs at chunk {k}");
            let d = golden::first_diff(&got, &want[i].1, "");
            assert!(d.is_none(), "{file} chunk {k} (lines {}..): {d:?}", k * 25 + 1);
            i += 1;
        }
        files += 1;
    }
    assert_eq!(files, 11);
}

#[test]
fn save_snapshots_match_csharp() {
    let golden_text = read(std::path::Path::new("tests/golden/tracker_saves.jsonl.gz"));
    let root = std::path::Path::new(FIXTURES);
    let mut n = 0;
    for (file, want) in golden::read_jsonl(&golden_text) {
        let path = ["event_saves/room_entry", "event_saves/runs"].iter().map(|d| root.join(d).join(&file)).find(|p| p.exists()).expect(&file);
        let text = read(&path);
        let mut t = tracker();
        t.process_lines(&["Preloading 'Event Room' assets..."]);
        let save = ov_gamestate::save::parse(&text, None).unwrap();
        t.on_save(SaveSnapshot { path: "/saves/current_run.save".into(), save: Arc::new(save), file_write_ms: 0, file_bytes: text.len() as i64 });
        let rewards = t.replayed_reward_cards(3);
        let got = dump(t.current(), rewards);
        let d = golden::first_diff(&got, &want, "");
        assert!(d.is_none(), "{file}: {d:?}");
        n += 1;
    }
    assert_eq!(n, 33);
}
