//! Per-game-version tables embedded from `data/catalog` (`GameDataVersion`, `CardRarities`, `StartingLoadouts`).

use std::collections::{HashMap, HashSet};
use std::sync::OnceLock;

use serde_json::Value;

use super::rng::{self, RngScheme};

pub const V0107: &str = "v0.107.1";
pub const V0111: &str = "v0.111.0";
pub const V0113: &str = "v0.113.0";
pub const ALL: [&str; 3] = [V0107, V0111, V0113];

/// Which game's tables to read. `Latest` (the default) is v0.113.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum GameDataVersion {
    V0107,
    V0111,
    #[default]
    Latest,
}

impl From<RngScheme> for GameDataVersion {
    /// Callers that only know the seed arithmetic use that generation's original tables.
    fn from(s: RngScheme) -> Self {
        if s == RngScheme::Legacy32 { Self::V0107 } else { Self::V0111 }
    }
}

impl GameDataVersion {
    pub fn version(self) -> &'static str {
        match self {
            Self::V0107 => V0107,
            Self::V0111 => V0111,
            Self::Latest => V0113,
        }
    }

    pub fn scheme(self) -> RngScheme {
        if self == Self::V0107 { RngScheme::Legacy32 } else { RngScheme::Xx64 }
    }

    pub fn for_scheme(scheme: RngScheme) -> &'static str {
        GameDataVersion::from(scheme).version()
    }

    pub fn for_run(schema_version: i32, game_version: Option<&str>) -> Self {
        Self::detect(schema_version, game_version).unwrap_or_default()
    }

    pub fn detect(schema_version: i32, game_version: Option<&str>) -> Option<Self> {
        if schema_version >= 21 {
            return Some(Self::Latest);
        }
        if let Some(s) = rng::for_save_schema(schema_version) {
            return Some(s.into());
        }
        let v = rng::parse_version(game_version)?;
        Some(if v >= (0, 113, 0) { Self::Latest } else { rng::for_game_version(game_version).into() })
    }

    fn index(self) -> usize {
        match self {
            Self::V0107 => 0,
            Self::V0111 => 1,
            Self::Latest => 2,
        }
    }
}

/// Renamed ids by seed scheme: an old id reads as the card the game migrated it to.
pub fn renames(scheme: RngScheme) -> &'static [(&'static str, &'static str)] {
    if scheme == RngScheme::Legacy32 { &[] } else { &[("CARD.SCARE", "CARD.SIDESTEP")] }
}

const SHARED_EVENTS_V111: [&str; 8] =
    ["BATTLEWORN_DUMMY", "DENSE_VEGETATION", "FAKE_MERCHANT", "JUNGLE_MAZE_ADVENTURE", "MORPHIC_GROVE", "PUNCH_OFF", "THE_LANTERN_KEY", "WAR_HISTORIAN_REPY"];

/// Events whose RNG stream is shared by all players (`IsShared`), from v0.111.
pub fn is_shared_event(scheme: RngScheme, event_id: &str) -> bool {
    if scheme == RngScheme::Legacy32 {
        return false;
    }
    SHARED_EVENTS_V111.contains(&event_id.strip_prefix("EVENT.").unwrap_or(event_id))
}

macro_rules! catalog {
    ($table:literal) => {
        [
            include_str!(concat!("../../../../../data/catalog/", $table, ".v0.107.1.json")),
            include_str!(concat!("../../../../../data/catalog/", $table, ".v0.111.0.json")),
            include_str!(concat!("../../../../../data/catalog/", $table, ".v0.113.0.json")),
        ]
    };
}

static CARDS_JSON: [&str; 3] = catalog!("cards");
static DECKS_JSON: [&str; 3] = catalog!("starting-decks");
pub static CARD_POOLS_JSON: [&str; 3] = catalog!("card-pools");
pub static RELICS_JSON: [&str; 3] = catalog!("relics");

pub fn embedded(table: &str, version: GameDataVersion) -> Option<&'static str> {
    let set = match table {
        "cards" => &CARDS_JSON,
        "starting-decks" => &DECKS_JSON,
        "card-pools" => &CARD_POOLS_JSON,
        "relics" => &RELICS_JSON,
        _ => return None,
    };
    Some(set[version.index()])
}

/// Lazily built table per version.
pub struct PerVersion<T>([OnceLock<T>; 3]);

impl<T> PerVersion<T> {
    pub const fn new() -> Self {
        Self([OnceLock::new(), OnceLock::new(), OnceLock::new()])
    }

    pub fn get(&self, v: GameDataVersion, load: impl FnOnce(GameDataVersion) -> T) -> &T {
        self.0[v.index()].get_or_init(|| load(v))
    }
}

impl<T> Default for PerVersion<T> {
    fn default() -> Self {
        Self::new()
    }
}

// ---------------------------------------------------------------------------------------------------- cards

#[derive(Debug)]
struct CardInfo {
    rarity: String,
    kind: String,
    exhaust: bool,
    max_upgrade: i32,
    multiplayer: String,
    keywords: HashSet<String>,
    tags: HashSet<String>,
}

static CARD_TABLES: PerVersion<HashMap<String, std::sync::Arc<CardInfo>>> = PerVersion::new();

fn str_set(c: &Value, name: &str) -> HashSet<String> {
    c.get(name).and_then(Value::as_array).map(|a| a.iter().filter_map(Value::as_str).map(str::to_string).collect()).unwrap_or_default()
}

fn card_table(v: GameDataVersion) -> &'static HashMap<String, std::sync::Arc<CardInfo>> {
    CARD_TABLES.get(v, |v| {
        let mut map = HashMap::new();
        let doc: Value = serde_json::from_str(embedded("cards", v).unwrap()).expect("embedded cards");
        for c in doc.get("cards").and_then(Value::as_array).into_iter().flatten() {
            let Some(id) = c.get("id").and_then(Value::as_str) else { continue };
            let keywords = str_set(c, "keywords");
            let info = CardInfo {
                rarity: c.get("rarity").and_then(Value::as_str).unwrap_or("").to_string(),
                kind: c.get("type").and_then(Value::as_str).unwrap_or("").to_string(),
                exhaust: keywords.contains("Exhaust"),
                max_upgrade: c.get("maxUpgrade").and_then(Value::as_i64).and_then(|m| i32::try_from(m).ok()).unwrap_or(1),
                multiplayer: c.get("multiplayer").and_then(Value::as_str).unwrap_or("None").to_string(),
                tags: str_set(c, "tags"),
                keywords,
            };
            map.insert(id.to_string(), std::sync::Arc::new(info));
        }
        for (old, now) in renames(v.scheme()) {
            if !map.contains_key(*old)
                && let Some(info) = map.get(*now).cloned()
            {
                map.insert(old.to_string(), info);
            }
        }
        map
    })
}

/// Card facts from the embedded catalog (`CardRarities`).
pub mod cards {
    use super::*;

    fn find(id: &str, v: GameDataVersion) -> Option<&'static CardInfo> {
        card_table(v).get(id).map(|a| a.as_ref())
    }

    pub fn rarity(id: &str, v: GameDataVersion) -> Option<&'static str> {
        find(id, v).map(|c| c.rarity.as_str())
    }

    pub fn knows(id: &str, v: GameDataVersion) -> bool {
        card_table(v).contains_key(id)
    }

    pub fn has_keyword(id: &str, keyword: &str, v: GameDataVersion) -> bool {
        find(id, v).is_some_and(|c| c.keywords.contains(keyword))
    }

    pub fn has_tag(id: &str, tag: &str, v: GameDataVersion) -> bool {
        find(id, v).is_some_and(|c| c.tags.contains(tag))
    }

    pub fn type_of(id: &str, v: GameDataVersion) -> Option<&'static str> {
        find(id, v).map(|c| c.kind.as_str())
    }

    pub fn max_upgrade(id: &str, v: GameDataVersion) -> i32 {
        find(id, v).map_or(1, |c| c.max_upgrade)
    }

    pub fn allowed_with_players(id: &str, multiplayer: bool, v: GameDataVersion) -> bool {
        match find(id, v).map(|c| c.multiplayer.as_str()) {
            Some("SingleplayerOnly") => !multiplayer,
            Some("MultiplayerOnly") => multiplayer,
            _ => true,
        }
    }

    pub fn is_combat_only(id: &str, v: GameDataVersion) -> bool {
        matches!(rarity(id, v), Some("Token" | "Status"))
    }

    pub fn leaves_combat_when_played(id: &str, v: GameDataVersion) -> bool {
        find(id, v).is_some_and(|c| c.kind == "Power" || c.exhaust)
    }

    /// Thieving Hopper's steal order (lower first); None = never stolen.
    pub fn steal_priority(id: &str, enchantment_id: Option<&str>, v: GameDataVersion) -> Option<i32> {
        let r = rarity(id, v);
        if r == Some("Ancient") || enchantment_id == Some("ENCHANTMENT.IMBUED") {
            return Some(3);
        }
        match r {
            Some("Uncommon") => Some(0),
            Some("Common" | "Rare" | "Event") => Some(1),
            Some("Basic" | "Quest") => Some(2),
            _ => None,
        }
    }
}

// ---------------------------------------------------------------------------------------------------- loadouts

pub const ASCENDERS_BANE: &str = "CARD.ASCENDERS_BANE";
pub const ASCENDERS_BANE_LEVEL: i32 = 5;

#[derive(Clone, Debug, PartialEq)]
pub struct StartingLoadout {
    pub character: String,
    pub starting_hp: Option<i32>,
    pub starting_gold: Option<i32>,
    pub max_energy: Option<i32>,
    pub deck: Vec<String>,
    pub relics: Vec<String>,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct StartingLoadouts {
    by_character: Vec<StartingLoadout>,
}

static LOADOUTS: PerVersion<StartingLoadouts> = PerVersion::new();

/// Property by name, case-insensitive (`PropertyNameCaseInsensitive`).
fn ci<'a>(o: &'a Value, name: &str) -> Option<&'a Value> {
    o.as_object()?.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v)
}

impl StartingLoadouts {
    pub fn embedded_for(v: GameDataVersion) -> &'static StartingLoadouts {
        LOADOUTS.get(v, |v| Self::load(embedded("starting-decks", v).unwrap()).expect("embedded starting decks"))
    }

    pub fn embedded() -> &'static StartingLoadouts {
        Self::embedded_for(GameDataVersion::Latest)
    }

    pub fn load(json: &str) -> Result<Self, String> {
        let doc: Value = serde_json::from_str(json).map_err(|e| e.to_string())?;
        let chars = ci(&doc, "characters").and_then(Value::as_array).ok_or("no characters")?;
        let int = |o: &Value, n: &str| ci(o, n).and_then(Value::as_i64).map(|v| v as i32);
        let strs = |o: &Value, n: &str| ci(o, n).and_then(Value::as_array).map(|a| a.iter().filter_map(Value::as_str).map(str::to_string).collect()).unwrap_or_default();
        let mut by_character: Vec<StartingLoadout> = Vec::new();
        for c in chars {
            let l = StartingLoadout {
                character: ci(c, "character").and_then(Value::as_str).unwrap_or("").to_string(),
                starting_hp: int(c, "startingHp"),
                starting_gold: int(c, "startingGold"),
                max_energy: int(c, "maxEnergy"),
                deck: strs(c, "deck"),
                relics: strs(c, "relics"),
            };
            if by_character.iter().any(|x| x.character == l.character) {
                return Err(format!("duplicate character {}", l.character));
            }
            by_character.push(l);
        }
        Ok(Self { by_character })
    }

    pub fn all(&self) -> &[StartingLoadout] {
        &self.by_character
    }

    pub fn for_character(&self, character: &str) -> Option<&StartingLoadout> {
        let key = if character.starts_with("CHARACTER.") { character.to_string() } else { format!("CHARACTER.{character}") };
        self.by_character.iter().find(|l| l.character == key)
    }
}
