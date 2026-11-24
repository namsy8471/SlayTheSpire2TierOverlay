//! godot.log lines parse into the same events as the C# `LogLineParser` (golden from the C# parity dumper over the
//! repo's log fixtures and every string literal of the C# GameState tests). Lines where C# threw (number overflow)
//! must give no event here.

use std::io::Read;

use ov_core::golden::first_diff;
use ov_gamestate::log::*;
use serde_json::{Value, json};

fn gunzip(path: &std::path::Path) -> String {
    let mut s = String::new();
    flate2::read::GzDecoder::new(std::fs::File::open(path).unwrap()).read_to_string(&mut s).unwrap();
    s
}

fn kind(k: CombatKind) -> i32 {
    k as i32
}

fn csharp(ev: &LogEvent) -> (&'static str, Value) {
    use LogEvent as E;
    match ev {
        E::RunStarted { is_multiplayer, character, ascension, seed, players, mode } => ("RunStartedEvent", json!({"IsMultiplayer": is_multiplayer, "Character": character, "Ascension": ascension, "Seed": seed,
            "Players": players.iter().map(|p| json!({"NetId": p.net_id, "Character": p.character})).collect::<Vec<_>>(), "Mode": mode})),
        E::GameVersion { version } => ("GameVersionEvent", json!({"Version": version})),
        E::RunContinued { character } => ("RunContinuedEvent", json!({"Character": character})),
        E::LocalPlayerId { net_id, from_profile_path, profile } => ("LocalPlayerIdEvent", json!({"NetId": net_id, "FromProfilePath": from_profile_path, "Profile": profile})),
        E::MapVote { player_id, generation, coord } => ("MapVoteEvent", json!({"PlayerId": player_id, "Generation": generation, "Coord": {"Col": coord.col, "Row": coord.row}})),
        E::RoomEntered { room, raw_name } => ("RoomEnteredEvent", json!({"Room": *room as i32, "RawName": raw_name})),
        E::ActEntered { act_id } => ("ActEnteredEvent", json!({"ActId": act_id})),
        E::MainMenu => ("MainMenuEvent", json!({})),
        E::CombatStarted { encounter_id, kind: k, visual_only } => ("CombatStartedEvent", json!({"EncounterId": encounter_id, "Kind": kind(*k), "VisualOnly": visual_only})),
        E::CombatWon { character, encounter_id, kind: k } => ("CombatWonEvent", json!({"Character": character, "EncounterId": encounter_id, "Kind": kind(*k)})),
        E::CombatLost { character, encounter_id, kind: k } => ("CombatLostEvent", json!({"Character": character, "EncounterId": encounter_id, "Kind": kind(*k)})),
        E::RunWon { character } => ("RunWonEvent", json!({"Character": character})),
        E::RunAbandoned => ("RunAbandonedEvent", json!({})),
        E::RunHistorySaved { file_name } => ("RunHistorySavedEvent", json!({"FileName": file_name})),
        E::RunSaveWritten { file_name, bytes } => ("RunSaveWrittenEvent", json!({"FileName": file_name, "Bytes": bytes})),
        E::CardRarityRolled(r) => ("CardRarityRolledEvent", json!({"Roll": r.roll, "Need": r.need, "Offset": r.offset, "IsRare": r.is_rare(),
            "BaseRareOdds": r.base_rare_odds(), "RollFloat": if r.roll_float.is_nan() { json!("NaN") } else { json!(r.roll_float.to_string().parse::<f64>().unwrap()) }, "Source": r.source() as i32, "IsScarcity": r.is_scarcity()})),
        E::CardObtained { player_id, card_id, special } => ("CardObtainedEvent", json!({"PlayerId": player_id, "CardId": card_id, "Special": special})),
        E::GoldObtained { player_id, amount } => ("GoldObtainedEvent", json!({"PlayerId": player_id, "Amount": amount})),
        E::PotionObtained { player_id, potion_id } => ("PotionObtainedEvent", json!({"PlayerId": player_id, "PotionId": potion_id})),
        E::RelicObtained { player_id, relic_id } => ("RelicObtainedEvent", json!({"PlayerId": player_id, "RelicId": relic_id})),
        E::CardRemovalReward { player_id } => ("CardRemovalRewardEvent", json!({"PlayerId": player_id})),
        E::CardsChosen { player_id, card_ids } => ("CardsChosenEvent", json!({"PlayerId": player_id, "CardIds": card_ids})),
        E::CardSelectScreen { screen } => ("CardSelectScreenEvent", json!({"Screen": screen})),
        E::PotionUsed { player_id, potion_id, discarded } => ("PotionUsedEvent", json!({"PlayerId": player_id, "PotionId": potion_id, "Discarded": discarded})),
        E::RestSiteOption { player_id, option, success } => ("RestSiteOptionEvent", json!({"PlayerId": player_id, "Option": option, "Success": success})),
        E::EventStarted { player_id, event_id, option_keys } => ("EventStartedEvent", json!({"PlayerId": player_id, "EventId": event_id, "OptionKeys": option_keys})),
        E::EventOptionChosen { player_id, event_id, choice_key } => ("EventOptionChosenEvent", json!({"PlayerId": player_id, "EventId": event_id, "ChoiceKey": choice_key})),
        E::CardPlayed { owner_id, card_id, combat_index } => ("CardPlayedEvent", json!({"OwnerId": owner_id, "CardId": card_id, "CombatIndex": combat_index})),
        E::CardPlayLogged { player_id, card_id } => ("CardPlayLoggedEvent", json!({"PlayerId": player_id, "CardId": card_id})),
        E::RemoteShopPurchase { player_id, item_id } => ("RemoteShopPurchaseEvent", json!({"PlayerId": player_id, "ItemId": item_id})),
        E::MonsterMove { monster_id, mv } => ("MonsterMoveEvent", json!({"MonsterId": monster_id, "Move": mv})),
    }
}

fn check(golden: &str, files: &dyn Fn(&str) -> String) -> usize {
    let mut want: std::collections::BTreeMap<(String, usize), (String, Value)> = Default::default();
    for l in golden.lines().filter(|l| !l.is_empty()) {
        let v: Value = serde_json::from_str(l).unwrap();
        want.insert((v["file"].as_str().unwrap().into(), v["line"].as_u64().unwrap() as usize),
                    (v["type"].as_str().unwrap().into(), v.get("v").cloned().unwrap_or(Value::Null)));
    }
    let names: std::collections::BTreeSet<String> = want.keys().map(|k| k.0.clone()).collect();
    let mut n = 0;
    for name in names {
        for (i, raw) in files(&name).split('\n').enumerate() {
            let key = (name.clone(), i + 1);
            let got = parse(raw);
            match want.get(&key) {
                Some((t, _)) if t.starts_with("Exception") => assert!(got.is_none(), "{name}:{}: C# threw, Rust gave {got:?}", i + 1),
                Some((t, v)) => {
                    let ev = got.unwrap_or_else(|| panic!("{name}:{}: C# {t}, Rust none: {raw}", i + 1));
                    let (gt, gv) = csharp(&ev);
                    assert_eq!(gt, t, "{name}:{}", i + 1);
                    assert!(first_diff(&gv, v, "").is_none(), "{name}:{}: {:?}", i + 1, first_diff(&gv, v, ""));
                    n += 1;
                }
                None => assert!(got.is_none(), "{name}:{}: C# none, Rust {got:?}: {raw}", i + 1),
            }
        }
    }
    n
}

#[test]
fn fixture_logs_match_csharp() {
    let root = std::path::Path::new("../../../tests/Sts2Overlay.GameState.Tests/Fixtures");
    let n = check(&gunzip(std::path::Path::new("tests/golden/logs.jsonl.gz")), &|name| {
        if name.ends_with(".gz") { gunzip(&root.join("coop_runs").join(name)) } else { std::fs::read_to_string(root.join(name)).unwrap() }
    });
    assert_eq!(n, 9473);
}

#[test]
fn test_literals_match_csharp() {
    let n = check(&gunzip(std::path::Path::new("tests/golden/synthetic.jsonl.gz")), &|_| std::fs::read_to_string("tests/golden/synthetic.log").unwrap());
    assert_eq!(n, 175);
}
