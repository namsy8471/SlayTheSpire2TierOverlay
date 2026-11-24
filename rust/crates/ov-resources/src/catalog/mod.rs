//! Port of Sts2Overlay.Resources.Catalog: the card catalog built by statically reading sts2.dll (no game code runs).

pub mod game;
pub mod il;
pub mod meta;
pub mod slug;

use std::collections::{HashMap, HashSet};
use std::path::Path;

use serde::Serialize;
use serde_json::Value;

use game::GameReader;

pub const LANGUAGES: [&str; 2] = ["kor", "eng"];

/// Input of [`build`].
#[derive(Clone, Debug, Default)]
pub struct CatalogOptions {
    pub dll_path: std::path::PathBuf,
    pub localization_dir: Option<std::path::PathBuf>,
    pub game_version: String,
    pub include_all: bool,
    pub with_starting_loadouts: bool,
    pub with_relics: bool,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CardRow {
    pub id: String,
    pub class: String,
    pub pool: Option<String>,
    pub character: Option<String>,
    #[serde(rename = "type")]
    pub card_type: Option<String>,
    pub rarity: Option<String>,
    pub cost: Option<i32>,
    pub cost_x: Option<bool>,
    pub star_cost: Option<i32>,
    pub star_cost_x: Option<bool>,
    pub target: Option<String>,
    pub gains_block: Option<bool>,
    pub max_upgrade: Option<i32>,
    pub multiplayer: Option<String>,
    pub show_in_library: Option<bool>,
    pub keywords: Option<Vec<String>>,
    pub tags: Option<Vec<String>>,
    pub names: serde_json::Map<String, Value>,
    pub description_template: serde_json::Map<String, Value>,
    pub unresolved: Vec<String>,
    pub dynamic: Vec<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogFile {
    pub game_version: String,
    pub source: String,
    pub cards: Vec<CardRow>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StartingLoadout {
    pub character: String,
    pub starting_hp: Option<i32>,
    pub starting_gold: Option<i32>,
    pub max_energy: Option<i32>,
    pub deck: Vec<String>,
    pub relics: Vec<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RelicRow {
    pub id: String,
    pub rarity: Option<String>,
    pub has_upon_pickup_effect: Option<bool>,
    pub spawns_pets: Option<bool>,
    pub used_up_override: bool,
    pub adds_pet: Option<bool>,
    pub allowed_override: bool,
}

/// Everything [`build`] found.
#[derive(Clone, Debug)]
pub struct CatalogBuildResult {
    pub catalog: CatalogFile,
    pub character_classes: Vec<String>,
    pub loaded_languages: Vec<String>,
    pub multi_pool_cards: usize,
    pub starting_loadouts: Option<Vec<StartingLoadout>>,
    pub relics: Option<Vec<RelicRow>>,
    pub warnings: Vec<String>,
}

/// Card text from the extracted localization: `<dir>/<lang>/cards.json`, a flat object of strings.
#[derive(Clone, Debug, Default)]
pub struct Localization {
    by_lang: Vec<(String, HashMap<String, String>)>,
}

impl Localization {
    pub fn load(dir: Option<&Path>, languages: &[&str]) -> Localization {
        let mut loc = Localization::default();
        let Some(dir) = dir else { return loc };
        for &lang in languages {
            let path = dir.join(lang).join("cards.json");
            let Ok(text) = std::fs::read_to_string(&path) else { continue };
            let Ok(Value::Object(obj)) = serde_json::from_str::<Value>(&text) else { continue };
            let table = obj.into_iter().filter_map(|(k, v)| v.as_str().map(|s| (k, s.to_string()))).collect();
            loc.by_lang.push((lang.to_string(), table));
        }
        loc
    }

    pub fn loaded_languages(&self) -> Vec<String> {
        self.by_lang.iter().map(|(l, _)| l.clone()).collect()
    }

    /// `entry` is the ModelId entry ("STRIKE_DEFECT"); `suffix` is "title" or "description".
    pub fn get(&self, lang: &str, entry: &str, suffix: &str) -> Option<String> {
        let key = if suffix == "title" { title_key(entry, 0) } else { format!("{entry}.{suffix}") };
        self.by_lang.iter().find(|(l, _)| l == lang).and_then(|(_, t)| t.get(&key).cloned())
    }
}

/// CardTextBook.TitleKey: Dingy Dagger has one title per upgrade level.
pub fn title_key(entry: &str, level: i32) -> String {
    if entry == "DINGY_DAGGER" {
        format!("{entry}{}", if level < 3 { ".title.dingy" } else if level < 6 { ".title.fine" } else { ".title.pristine" })
    } else {
        format!("{entry}.title")
    }
}

pub fn build(options: &CatalogOptions) -> Result<CatalogBuildResult, String> {
    let game = GameReader::open(&options.dll_path)?;
    build_with(&game, options)
}

pub fn build_with(game: &GameReader, options: &CatalogOptions) -> Result<CatalogBuildResult, String> {
    let model_db = game.model_db;
    // 1. characters, their pools and starting decks
    let characters = game.model_db_generic_calls(game.find_method(model_db, "get_AllCharacters"), "Character");
    let mut character_by_pool: HashMap<u32, String> = HashMap::new();
    let mut starting_decks: Vec<(String, Vec<u32>)> = Vec::new();
    for &ch in &characters {
        let ch_id = game.model_id_of(ch);
        for pool in game.model_db_generic_calls(game.find_method(ch, "get_CardPool"), "CardPool") {
            character_by_pool.insert(pool, ch_id.clone());
        }
        let deck = game.model_db_generic_calls(game.find_method(ch, "get_StartingDeck"), "Card");
        match starting_decks.iter_mut().find(|(k, _)| *k == ch_id) {
            Some(e) => e.1 = deck,
            None => starting_decks.push((ch_id, deck)),
        }
    }

    let starting_loadouts = options.with_starting_loadouts.then(|| {
        characters
            .iter()
            .map(|&ch| {
                let id = game.model_id_of(ch);
                StartingLoadout {
                    character: id.clone(),
                    starting_hp: game.evaluate_int_getter(ch, "get_StartingHp"),
                    starting_gold: game.evaluate_int_getter(ch, "get_StartingGold"),
                    max_energy: game.evaluate_int_getter(ch, "get_MaxEnergy"),
                    deck: starting_decks.iter().find(|(k, _)| *k == id).map(|(_, d)| d.iter().map(|&t| game.model_id_of(t)).collect()).unwrap_or_default(),
                    relics: game.model_db_generic_calls(game.find_method(ch, "get_StartingRelics"), "Relic").into_iter().map(|t| game.model_id_of(t)).collect(),
                }
            })
            .collect()
    });

    let relics = options.with_relics.then(|| {
        let mut rows: Vec<RelicRow> = game
            .concrete_subclasses_of(game.relic_model)
            .into_iter()
            .map(|r| {
                let d = game.decode_relic(r);
                RelicRow {
                    id: d.id,
                    rarity: d.rarity,
                    has_upon_pickup_effect: d.has_upon_pickup_effect,
                    spawns_pets: d.spawns_pets,
                    used_up_override: d.used_up_override,
                    adds_pet: d.adds_pet,
                    allowed_override: d.allowed_override,
                }
            })
            .collect();
        rows.sort_by(|a, b| a.id.cmp(&b.id));
        rows
    });

    // 2. card pools: character pools first, then ModelDb.AllSharedCardPools
    let shared = game.model_db_generic_calls(game.find_method(model_db, "get_AllSharedCardPools"), "CardPool");
    let mut in_game_pools: Vec<u32> = Vec::new();
    for p in characters.iter().flat_map(|&ch| game.model_db_generic_calls(game.find_method(ch, "get_CardPool"), "CardPool")).chain(shared) {
        if !in_game_pools.contains(&p) {
            in_game_pools.push(p);
        }
    }
    let other_pools: Vec<u32> =
        game.concrete_subclasses_of(game.card_pool_model).into_iter().filter(|p| !in_game_pools.contains(p)).collect();

    let mut pool_of_card: HashMap<u32, u32> = HashMap::new();
    let mut pool_order: Vec<String> = Vec::new();
    let mut multi_pool_cards = 0;
    let mut warnings = Vec::new();
    let pools: Vec<u32> = in_game_pools.iter().copied().chain(if options.include_all { other_pools } else { Vec::new() }).collect();
    for &pool in &pools {
        pool_order.push(game.model_id_of(pool));
        let cards = game.model_db_generic_calls(game.find_method(pool, "GenerateAllCards"), "Card");
        if cards.is_empty() {
            warnings.push(format!("no cards decoded for pool {}", game.type_name(pool)));
        }
        for c in cards {
            if let std::collections::hash_map::Entry::Vacant(e) = pool_of_card.entry(c) {
                e.insert(pool);
            } else {
                multi_pool_cards += 1;
            }
        }
    }

    let in_game: HashSet<u32> = pool_of_card.keys().copied().chain(starting_decks.iter().flat_map(|(_, d)| d.iter().copied())).collect();

    // 3. decode every card class
    let loc = Localization::load(options.localization_dir.as_deref(), &LANGUAGES);
    let mut rows = Vec::new();
    for card in game.concrete_subclasses_of(game.card_model) {
        if !options.include_all && !in_game.contains(&card) {
            continue;
        }
        let d = game.decode_card(card);
        let pool = pool_of_card.get(&card).copied();
        let character = pool.and_then(|p| character_by_pool.get(&p).cloned());
        let entry = d.id.split_once('.').map_or(d.id.as_str(), |(_, e)| e).to_string();
        let lang_map = |suffix: &str| -> serde_json::Map<String, Value> {
            LANGUAGES.iter().map(|&l| (l.to_string(), loc.get(l, &entry, suffix).map_or(Value::Null, Value::String))).collect()
        };
        rows.push(CardRow {
            id: d.id.clone(),
            class: game.type_name(card).to_string(),
            pool: pool.map(|p| game.model_id_of(p)),
            character,
            card_type: d.card_type,
            rarity: d.rarity,
            cost: d.cost,
            cost_x: d.cost_x,
            star_cost: d.star_cost,
            star_cost_x: d.star_cost_x,
            target: d.target,
            gains_block: d.gains_block,
            max_upgrade: d.max_upgrade,
            multiplayer: d.multiplayer,
            show_in_library: d.show_in_library,
            keywords: d.keywords,
            tags: d.tags,
            names: lang_map("title"),
            description_template: lang_map("description"),
            unresolved: d.unresolved,
            dynamic: d.dynamic,
        });
    }
    // Stable order: by pool (character pools first), then by id.
    let pool_index = |p: &Option<String>| p.as_ref().map_or(usize::MAX, |p| pool_order.iter().position(|x| x == p).unwrap_or(usize::MAX));
    rows.sort_by(|a, b| pool_index(&a.pool).cmp(&pool_index(&b.pool)).then_with(|| a.id.cmp(&b.id)));

    Ok(CatalogBuildResult {
        catalog: CatalogFile { game_version: options.game_version.clone(), source: "static-il".into(), cards: rows },
        character_classes: characters.iter().map(|&c| game.type_name(c).to_string()).collect(),
        loaded_languages: loc.loaded_languages(),
        multi_pool_cards,
        starting_loadouts,
        relics,
        warnings,
    })
}

/// Writes the catalog JSON (creates the folder).
pub fn write(catalog: &CatalogFile, path: &Path) -> std::io::Result<()> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    std::fs::write(path, serde_json::to_string_pretty(catalog).map_err(std::io::Error::other)?)
}
