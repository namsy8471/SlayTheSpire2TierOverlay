//! One godot.log line -> a [`LogEvent`] (`LogLineParser`). Formats come from real logs (v0.107.1 on).
//!
//! Deviation: a number too large for its type made the C# parser throw (and the tailer skip the rest of that read);
//! here such a line is simply not an event.

use std::sync::LazyLock;

use ov_core::map::MapCoord;
use regex::{Captures, Regex};

use super::events::*;

macro_rules! rx {
    ($name:ident, $pat:expr) => {
        static $name: LazyLock<Regex> = LazyLock::new(|| Regex::new($pat).unwrap());
    };
}

rx!(LEVEL_PREFIX, r"^\[(?:INFO|DEBUG|VERYDEBUG|WARN|ERROR)\]\s?");
rx!(EMBARK_SINGLE, r"^Embarking on a (?:(?<mode>CUSTOM|DAILY) )?(?:singleplayer )?(?<ch>[A-Z0-9_]+) run(?: with (?<n>\d+) players)?\. Ascension: (?<asc>\d+) Seed: (?<seed>\S+)");
rx!(EMBARK_MULTI, r"^Embarking on a (?:(?<mode>CUSTOM|DAILY) )?multiplayer run\. Players: (?<players>.*)\. Ascension: (?<asc>\d+) Seed: (?<seed>\S+)");
rx!(LOBBY_PLAYER, r"Player (?<id>\d+), (?<ch>[A-Z0-9_]+)");
rx!(CONTINUE, r"^Continuing run with character: (?<ch>CHARACTER\.[A-Z0-9_]+)");
// The C# pattern used one group name in both branches, which Rust's regex does not allow.
rx!(LOCAL_ID, r"Our player id: (?<a>\d+)|Local player (?<b>\d+) is ready");
rx!(PROFILE_PATH, r"^Profile-scoped data path initialized: user://steam/(?<id>\d+)/(?<profile>profile\d+)?");
rx!(VOTE, r"^Player vote changed for (?<p>\d+): .*->MapVote \(gen: (?<gen>\d+) coord: \((?<col>\d+), (?<row>\d+)\)\)");
rx!(PRELOAD, r"^Preloading '(?<name>[^']+)' assets\.\.\.");
rx!(COMBAT_ROOM, r"^Creating NCombatRoom with mode=(?<mode>\w+) encounter=(?<enc>[A-Z0-9_]+)\.");
rx!(COMBAT_RESULT, r"^(?<ch>CHARACTER\.[A-Z0-9_]+) has (?<res>won against|lost to) encounter (?<enc>ENCOUNTER\.[A-Z0-9_]+)\.");
rx!(COMBAT_FIRST, r"^(?<ch>CHARACTER\.[A-Z0-9_]+) fought (?<enc>ENCOUNTER\.[A-Z0-9_]+) for the first time and (?<res>WON|LOST)");
rx!(RUN_WON, r"^(?<ch>CHARACTER\.[A-Z0-9_]+) has won a run");
rx!(RARITY, r"^Card rarity: Rolled (?<roll>\S+), need < (?<need>\S+) for rare \(offset = (?<off>[^)]+)\)");
rx!(OBTAINED, r"^(?:Player (?<p>\d+) obtained|Obtained) (?<what>.+?) from (?<src>special card reward|card reward|potion reward|relic reward|gold reward|reward)$");
rx!(CHOSE, r"^Player (?<p>\d+) chose cards \[(?<cards>[^\]]*)\]");
rx!(POTION_USE, r"^Player (?<p>\d+) (?<verb>using|discarding) potion (?<id>[A-Z0-9_]+)");
rx!(PLAYING_CARD, r"^Player (?<p>\d+) playing card (?<card>[A-Z0-9_]+)");
rx!(REST_OPTION, r"\[RestSiteSynchronizer\] Rest site option index \d+ chosen for player (?<p>\d+) with success (?<ok>True|False)\. Option: (?<opt>\w+)");
rx!(TREASURE_PICK, r"\[TreasureRoomRelicSynchronizer\] Relic index \d+ \((?<id>RELIC\.[A-Z0-9_]+) \(\d+\)\) is being picked by local player (?<p>\d+)");
rx!(EVENT_OPTION, r"^\[EventSynchronizer\] Option index \d+ chosen for player (?<p>\d+) in event (?<ev>EVENT\.[A-Z0-9_]+)\. Choice key: (?<key>\S+)");
rx!(EVENT_BEGAN, r"^\[EventSynchronizer\] Event (?<ev>EVENT\.[A-Z0-9_]+) began for player (?<p>\d+) with options: ");
rx!(TEXT_KEY, r"textKey: (?<key>[A-Za-z0-9_.]+)");
rx!(PLAY_CARD, r"^\[ActionQueueSet\] Enqueueing action PlayCardAction card: (?<card>CARD\.[A-Z0-9_]+) \(\d+\) index: (?<idx>\d+) targetid: \S* ?to player queue owned by (?<p>\d+)");
rx!(REMOTE_PURCHASE, r"^\[RunLocationTargetedMessageBuffer\] Handling map-targeted message RewardObtainedMessage type: (?:Card|Relic) .*? skipped: False (?:Card|Relic): (?<id>(?:CARD|RELIC)\.[A-Z0-9_]+) \(\d+\) from (?<p>\d+)");
rx!(SELECT_SCREEN, r"^Asset not cached: res://scenes/screens/card_selection/(?<screen>[a-z_]+)\.tscn");
rx!(WROTE, r"^Wrote (?<bytes>\d+) bytes to path=(?<path>.+?) save_dir=");
rx!(HISTORY_SAVED, r"^Saved run history: (?<file>\S+)");
rx!(RELEASE_VERSION, r"^(?:\[Sentry\.NET\] Initialized: .*\brelease=|Release Version: )(?<v>v?\d+(?:\.\d+)+\S*)");

const HOPPER_STEAL: &str = "Monster THIEVING_HOPPER performing move THIEVERY_MOVE";

fn g<'a>(c: &Captures<'a>, name: &str) -> &'a str {
    c.name(name).map_or("", |m| m.as_str())
}

fn num<T: std::str::FromStr>(c: &Captures, name: &str) -> Option<T> {
    g(c, name).parse().ok()
}

/// .NET `NumberStyles.Float` style: leading/trailing spaces, sign, decimal point, exponent ("1E-05").
fn parse_f64(s: &str) -> Option<f64> {
    let t = s.trim();
    if t.is_empty() || !t.bytes().all(|b| b.is_ascii_digit() || matches!(b, b'.' | b'-' | b'+' | b'e' | b'E')) {
        return None;
    }
    t.parse().ok()
}

/// Encounter id suffix -> combat kind; event fights and oddities are Unknown.
pub fn kind_of(encounter_id: &str) -> CombatKind {
    if encounter_id.ends_with("_BOSS") {
        CombatKind::Boss
    } else if encounter_id.ends_with("_ELITE") {
        CombatKind::Elite
    } else if encounter_id.ends_with("_WEAK") || encounter_id.ends_with("_NORMAL") {
        CombatKind::Normal
    } else {
        CombatKind::Unknown
    }
}

fn mode_of(c: &Captures) -> String {
    c.name("mode").map_or_else(|| "standard".to_string(), |m| m.as_str().to_lowercase())
}

pub fn parse(raw_line: &str) -> Option<LogEvent> {
    if raw_line.encode_utf16().count() < 10 {
        return None;
    }
    let stripped = LEVEL_PREFIX.replace(raw_line, "");
    let line = stripped.trim_end_matches('\r');
    let first = line.chars().next()?;
    match first {
        'C' => {
            if let Some(m) = RARITY.captures(line) {
                let (roll, need, off) = (parse_f64(g(&m, "roll"))?, parse_f64(g(&m, "need"))?, parse_f64(g(&m, "off"))?);
                let roll_float = parse_f64(g(&m, "roll")).map_or(f32::NAN, |v| v as f32);
                return Some(LogEvent::CardRarityRolled(CardRarityRolled { roll, need, offset: off, roll_float }));
            }
            if let Some(m) = COMBAT_ROOM.captures(line) {
                let enc = format!("ENCOUNTER.{}", g(&m, "enc"));
                let kind = kind_of(&enc);
                return Some(LogEvent::CombatStarted { encounter_id: enc, kind, visual_only: g(&m, "mode") == "VisualOnly" });
            }
            if let Some(m) = COMBAT_RESULT.captures(line) {
                return Some(result(&m, g(&m, "res") == "won against"));
            }
            if let Some(m) = COMBAT_FIRST.captures(line) {
                return Some(result(&m, g(&m, "res") == "WON"));
            }
            if let Some(m) = RUN_WON.captures(line) {
                return Some(LogEvent::RunWon { character: g(&m, "ch").into() });
            }
            CONTINUE.captures(line).map(|m| LogEvent::RunContinued { character: g(&m, "ch").into() })
        }
        'P' => {
            if let Some(m) = PLAYING_CARD.captures(line) {
                return Some(LogEvent::CardPlayLogged { player_id: num(&m, "p")?, card_id: format!("CARD.{}", g(&m, "card")) });
            }
            if let Some(m) = OBTAINED.captures(line) {
                return parse_obtained(&m);
            }
            if let Some(m) = VOTE.captures(line) {
                return Some(LogEvent::MapVote { player_id: num(&m, "p")?, generation: num(&m, "gen")?, coord: MapCoord::new(num(&m, "col")?, num(&m, "row")?) });
            }
            if let Some(m) = PRELOAD.captures(line) {
                return parse_preload(g(&m, "name"));
            }
            if let Some(m) = CHOSE.captures(line) {
                let cards = g(&m, "cards").split(',').map(str::trim).filter(|c| !c.is_empty()).map(|c| format!("CARD.{c}")).collect();
                return Some(LogEvent::CardsChosen { player_id: num(&m, "p")?, card_ids: cards });
            }
            if let Some(m) = POTION_USE.captures(line) {
                return Some(LogEvent::PotionUsed { player_id: num(&m, "p")?, potion_id: format!("POTION.{}", g(&m, "id")), discarded: g(&m, "verb") == "discarding" });
            }
            PROFILE_PATH.captures(line).and_then(|m| {
                Some(LogEvent::LocalPlayerId { net_id: num(&m, "id")?, from_profile_path: true, profile: m.name("profile").map(|p| p.as_str().to_string()) })
            })
        }
        'O' => OBTAINED.captures(line).and_then(|m| parse_obtained(&m)),
        'E' => {
            if let Some(m) = EMBARK_MULTI.captures(line) {
                let mut players = Vec::new();
                for p in LOBBY_PLAYER.captures_iter(g(&m, "players")) {
                    players.push(LobbyPlayer { net_id: num(&p, "id")?, character: format!("CHARACTER.{}", g(&p, "ch")) });
                }
                return Some(LogEvent::RunStarted { is_multiplayer: true, character: None, ascension: num(&m, "asc")?, seed: g(&m, "seed").into(), players, mode: mode_of(&m) });
            }
            let m = EMBARK_SINGLE.captures(line)?;
            let multi = match m.name("n") {
                Some(n) => n.as_str().parse::<i32>().ok()? > 1,
                None => false,
            };
            Some(LogEvent::RunStarted {
                is_multiplayer: multi,
                character: Some(format!("CHARACTER.{}", g(&m, "ch"))),
                ascension: num(&m, "asc")?,
                seed: g(&m, "seed").into(),
                players: Vec::new(),
                mode: mode_of(&m),
            })
        }
        'W' => {
            let m = WROTE.captures(line)?;
            let path = g(&m, "path").replace('\\', "/");
            let file = path.rsplit('/').next().unwrap_or("");
            if !file.starts_with("current_run") {
                return None;
            }
            Some(LogEvent::RunSaveWritten { file_name: file.to_string(), bytes: num(&m, "bytes")? })
        }
        'S' => HISTORY_SAVED.captures(line).map(|m| LogEvent::RunHistorySaved { file_name: g(&m, "file").into() }),
        'M' => line.starts_with(HOPPER_STEAL).then(|| LogEvent::MonsterMove { monster_id: "MONSTER.THIEVING_HOPPER".into(), mv: "THIEVERY_MOVE".into() }),
        'A' => {
            if let Some(m) = SELECT_SCREEN.captures(line) {
                return Some(LogEvent::CardSelectScreen { screen: g(&m, "screen").into() });
            }
            line.starts_with("Abandoning an in-progress run").then_some(LogEvent::RunAbandoned)
        }
        'R' => RELEASE_VERSION.captures(line).map(|m| LogEvent::GameVersion { version: g(&m, "v").into() }),
        '[' => {
            if line.starts_with("[Sentry.NET]") {
                return RELEASE_VERSION.captures(line).map(|m| LogEvent::GameVersion { version: g(&m, "v").into() });
            }
            if let Some(m) = PLAY_CARD.captures(line) {
                return Some(LogEvent::CardPlayed { owner_id: num(&m, "p")?, card_id: g(&m, "card").into(), combat_index: num(&m, "idx")? });
            }
            if let Some(m) = EVENT_OPTION.captures(line) {
                return Some(LogEvent::EventOptionChosen { player_id: num(&m, "p")?, event_id: g(&m, "ev").into(), choice_key: g(&m, "key").into() });
            }
            if let Some(m) = EVENT_BEGAN.captures(line) {
                let end = m.get(0).unwrap().end();
                let keys = TEXT_KEY.captures_iter(&line[end..]).map(|k| g(&k, "key").to_string()).collect();
                return Some(LogEvent::EventStarted { player_id: num(&m, "p")?, event_id: g(&m, "ev").into(), option_keys: keys });
            }
            if let Some(m) = REMOTE_PURCHASE.captures(line) {
                return Some(LogEvent::RemoteShopPurchase { player_id: num(&m, "p")?, item_id: g(&m, "id").into() });
            }
            if let Some(m) = REST_OPTION.captures(line) {
                return Some(LogEvent::RestSiteOption { player_id: num(&m, "p")?, option: g(&m, "opt").into(), success: g(&m, "ok") == "True" });
            }
            if let Some(m) = TREASURE_PICK.captures(line) {
                return Some(LogEvent::RelicObtained { player_id: Some(num(&m, "p")?), relic_id: g(&m, "id").into() });
            }
            let m = LOCAL_ID.captures(line)?;
            let id = m.name("a").or(m.name("b"))?.as_str().parse().ok()?;
            Some(LogEvent::LocalPlayerId { net_id: id, from_profile_path: false, profile: None })
        }
        _ => None,
    }
}

fn result(m: &Captures, won: bool) -> LogEvent {
    let (enc, ch) = (g(m, "enc").to_string(), g(m, "ch").to_string());
    let kind = kind_of(&enc);
    if won { LogEvent::CombatWon { character: ch, encounter_id: enc, kind } } else { LogEvent::CombatLost { character: ch, encounter_id: enc, kind } }
}

fn parse_preload(name: &str) -> Option<LogEvent> {
    if let Some(act) = name.strip_prefix("Act=") {
        return Some(LogEvent::ActEntered { act_id: format!("ACT.{act}") });
    }
    if name == "MainMenuEssentials" {
        return Some(LogEvent::MainMenu);
    }
    if !name.ends_with(" Room") {
        return None;
    }
    let room = match name {
        "Combat Room" => RoomKind::Combat,
        "Merchant Room" => RoomKind::Merchant,
        "RestSite Room" => RoomKind::RestSite,
        "Event Room" => RoomKind::Event,
        "Treasure Room" => RoomKind::Treasure,
        _ => RoomKind::Other,
    };
    Some(LogEvent::RoomEntered { room, raw_name: name.into() })
}

fn parse_obtained(m: &Captures) -> Option<LogEvent> {
    let player: Option<u64> = match m.name("p") {
        Some(p) => Some(p.as_str().parse().ok()?),
        None => None,
    };
    let (what, src) = (g(m, "what"), g(m, "src"));
    if what.starts_with("CARD.") {
        return Some(LogEvent::CardObtained { player_id: player, card_id: what.into(), special: src == "special card reward" });
    }
    if what.starts_with("POTION.") {
        return Some(LogEvent::PotionObtained { player_id: player, potion_id: what.into() });
    }
    if what.starts_with("RELIC.") {
        return Some(LogEvent::RelicObtained { player_id: player, relic_id: what.into() });
    }
    if what == "card removal" {
        return player.map(|p| LogEvent::CardRemovalReward { player_id: p });
    }
    if let Some(n) = what.strip_suffix(" gold") {
        // int.TryParse with NumberStyles.Integer: optional surrounding spaces and sign.
        if let Ok(gold) = n.trim().parse::<i32>() {
            return Some(LogEvent::GoldObtained { player_id: player, amount: gold });
        }
    }
    None
}
