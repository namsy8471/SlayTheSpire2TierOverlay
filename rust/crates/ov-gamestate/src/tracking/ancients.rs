//! The options an act-start ancient offers, replayed exactly (`AncientOptions`).

use std::collections::HashSet;

use super::data::{GameDataVersion, cards};
use super::deck_effects;
use super::relics;
use super::rng::{self, DEFAULT_SCHEME, GameRng, RngScheme};

/// A deck card as the ancients' rules see it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AncientDeckCard {
    pub id: String,
    pub enchantment_id: Option<String>,
}

#[derive(Clone, Debug)]
pub struct AncientInputs {
    pub run_seed: String,
    pub slot: i32,
    pub act_index: i32,
    pub character: String,
    pub deck: Vec<AncientDeckCard>,
    pub relics: Vec<String>,
    /// None = all unlocked.
    pub unlocked_epochs: Option<Vec<String>>,
    pub deck_clearing_modifier: bool,
    pub uses_v113_rules: bool,
    pub scheme: RngScheme,
}

impl AncientInputs {
    pub fn new(run_seed: impl Into<String>) -> Self {
        Self {
            run_seed: run_seed.into(),
            slot: 0,
            act_index: 0,
            character: String::new(),
            deck: vec![],
            relics: vec![],
            unlocked_epochs: None,
            deck_clearing_modifier: false,
            uses_v113_rules: false,
            scheme: DEFAULT_SCHEME,
        }
    }

    fn card_version(&self) -> GameDataVersion {
        if self.uses_v113_rules { GameDataVersion::Latest } else { self.scheme.into() }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AncientDraw {
    pub options: Vec<String>,
    pub sea_glass_character: Option<String>,
}

pub const SEA_GLASS: &str = "SEA_GLASS";
pub const OROBAS_LOCKED: &str = "OPTION_POOL_3_LOCKED";

const ANCIENTS: [&str; 7] = ["PAEL", "TEZCATARA", "OROBAS", "NONUPEIPE", "TANX", "VAKUU", "DARV"];

const CHARACTERS: [(&str, Option<&str>); 6] = [
    ("CHARACTER.IRONCLAD", None),
    ("CHARACTER.SILENT", Some("SILENT1_EPOCH")),
    ("CHARACTER.REGENT", Some("REGENT1_EPOCH")),
    ("CHARACTER.NECROBINDER", Some("NECROBINDER1_EPOCH")),
    ("CHARACTER.DEFECT", Some("DEFECT1_EPOCH")),
    ("CHARACTER.FOOL", Some("FOOL1_EPOCH")),
];

fn entry(event_id: &str) -> &str {
    event_id.strip_prefix("EVENT.").unwrap_or(event_id)
}

pub fn handles(event_id: &str) -> bool {
    event_id.strip_prefix("EVENT.").is_some_and(|e| ANCIENTS.contains(&e))
}

pub fn stream_seed(run_seed: &str, slot: i32, event_id: &str, scheme: RngScheme) -> u64 {
    rng::event_stream(scheme, run_seed, slot, entry(event_id), false)
}

pub fn key_prefix(event_id: &str) -> String {
    format!("{}.pages.INITIAL.options.", entry(event_id))
}

pub fn options(event_id: &str, p: &AncientInputs) -> Option<Vec<String>> {
    draw(event_id, p).map(|d| d.options)
}

pub fn keys(event_id: &str, p: &AncientInputs) -> Option<Vec<String>> {
    let prefix = key_prefix(event_id);
    options(event_id, p).map(|o| o.into_iter().map(|x| format!("{prefix}{x}")).collect())
}

pub fn draw(event_id: &str, p: &AncientInputs) -> Option<AncientDraw> {
    if !handles(event_id) {
        return None;
    }
    let mut rng = GameRng::new(stream_seed(&p.run_seed, p.slot, event_id, p.scheme));
    let mut sea_glass = None;
    let options = match entry(event_id) {
        "PAEL" => pael(&mut rng, p),
        "TEZCATARA" => tezcatara(&mut rng, p),
        "OROBAS" => orobas(&mut rng, p, &mut sea_glass),
        "NONUPEIPE" => take_shuffled(&mut rng, p, &NONUPEIPE_POOL, bracelet_allowed(p).then_some("BEAUTIFUL_BRACELET")),
        "TANX" => take_shuffled(&mut rng, p, &TANX_POOL, boomerang_allowed(p).then_some("TRI_BOOMERANG")),
        "VAKUU" => vakuu(&mut rng, p),
        "DARV" => darv(&mut rng, p),
        _ => unreachable!(),
    };
    Some(AncientDraw { options, sea_glass_character: sea_glass })
}

fn s(v: &[&str]) -> Vec<String> {
    v.iter().map(|x| x.to_string()).collect()
}

pub fn all_options(event_id: &str, scheme: RngScheme) -> Vec<String> {
    match entry(event_id) {
        "PAEL" => s(&["PAELS_FLESH", "PAELS_HORN", "PAELS_TEARS", "PAELS_WING", "PAELS_EYE", "PAELS_BLOOD", "PAELS_CLAW", "PAELS_TOOTH", "PAELS_LEGION", "PAELS_GROWTH"]),
        "TEZCATARA" => s(&[
            "VERY_HOT_COCOA", "YUMMY_COOKIE", "BIIIG_HUG", "STORYBOOK", "TOASTY_MITTENS", "GOLDEN_COMPASS", "PUMPKIN_CANDLE", "TOY_BOX", "SEAL_OF_GOLD",
            "NUTRITIOUS_SOUP",
        ]),
        "OROBAS" => {
            let mut v = s(orobas_pool1(scheme));
            v.extend(s(orobas_pool2(scheme)));
            v.extend(s(&["TOUCH_OF_OROBAS", "ARCHAIC_TOOTH", SEA_GLASS, "PRISMATIC_GEM"]));
            v
        }
        "NONUPEIPE" => {
            let mut v = s(&NONUPEIPE_POOL);
            v.push("BEAUTIFUL_BRACELET".into());
            v
        }
        "TANX" => {
            let mut v = s(&TANX_POOL);
            v.push("TRI_BOOMERANG".into());
            v
        }
        "VAKUU" => VAKUU_POOLS.iter().flat_map(|p| s(p)).collect(),
        "DARV" => {
            let mut v: Vec<String> = darv_sets(scheme).iter().flat_map(|(_, r)| s(r)).collect();
            v.push("DUSTY_TOME".into());
            v
        }
        _ => vec![],
    }
}

pub fn possible_keys(event_id: &str, p: &AncientInputs) -> Option<Vec<String>> {
    if !handles(event_id) {
        return None;
    }
    let (touch, tooth) = (touch_allowed(p), archaic_tooth_allowed(p));
    let mut excluded: HashSet<&str> = HashSet::new();
    let mut unless = |allowed: bool, option: &'static str| {
        if !allowed {
            excluded.insert(option);
        }
    };
    match entry(event_id) {
        "PAEL" => {
            unless(claw_allowed(p), "PAELS_CLAW");
            unless(paels_tooth_allowed(p), "PAELS_TOOTH");
            unless(!has_event_pet(p), "PAELS_LEGION");
        }
        "TEZCATARA" => unless(soup_allowed(p), "NUTRITIOUS_SOUP"),
        "OROBAS" => {
            unless(touch, "TOUCH_OF_OROBAS");
            unless(tooth, "ARCHAIC_TOOTH");
        }
        "NONUPEIPE" => unless(bracelet_allowed(p), "BEAUTIFUL_BRACELET"),
        "TANX" => unless(boomerang_allowed(p), "TRI_BOOMERANG"),
        "DARV" => {
            for (gate, relics) in darv_sets(p.scheme) {
                for r in *relics {
                    unless(darv_gate_open(*gate, p), r);
                }
            }
        }
        _ => {}
    }
    let mut options: Vec<String> = all_options(event_id, p.scheme).into_iter().filter(|o| !excluded.contains(o.as_str())).collect();
    if entry(event_id) == "OROBAS" && !touch && !tooth {
        options.push(OROBAS_LOCKED.into());
    }
    let prefix = key_prefix(event_id);
    Some(options.into_iter().map(|o| format!("{prefix}{o}")).collect())
}

/// `Rng.NextItem`: nothing drawn for an empty list.
fn next_item<T: Clone>(rng: &mut GameRng, items: &[T]) -> Option<T> {
    if items.is_empty() { None } else { Some(items[rng.next_int(items.len() as i32) as usize].clone()) }
}

fn next_bool(rng: &mut GameRng) -> bool {
    rng.next_int(2) == 0
}

fn pael(rng: &mut GameRng, p: &AncientInputs) -> Vec<String> {
    let first = next_item(rng, &["PAELS_FLESH", "PAELS_HORN", "PAELS_TEARS"]).unwrap();
    let mut second = vec!["PAELS_WING"];
    if claw_allowed(p) {
        second.push("PAELS_CLAW");
    }
    if paels_tooth_allowed(p) {
        second.push("PAELS_TOOTH");
    }
    second.extend(second.clone());
    second.push("PAELS_GROWTH");
    let middle = next_item(rng, &second).unwrap();
    let mut third = vec!["PAELS_EYE", "PAELS_BLOOD"];
    if !has_event_pet(p) {
        third.push("PAELS_LEGION");
    }
    s(&[first, middle, next_item(rng, &third).unwrap()])
}

fn tezcatara(rng: &mut GameRng, p: &AncientInputs) -> Vec<String> {
    let mut first = vec!["VERY_HOT_COCOA", "YUMMY_COOKIE"];
    if soup_allowed(p) {
        first.push("NUTRITIOUS_SOUP");
    }
    let a = next_item(rng, &first).unwrap();
    let b = next_item(rng, &["BIIIG_HUG", "STORYBOOK", "TOASTY_MITTENS"]).unwrap();
    let c = next_item(rng, &["GOLDEN_COMPASS", "PUMPKIN_CANDLE", "TOY_BOX", "SEAL_OF_GOLD"]).unwrap();
    s(&[a, b, c])
}

fn orobas(rng: &mut GameRng, p: &AncientInputs, sea_glass: &mut Option<String>) -> Vec<String> {
    let others: Vec<&str> = CHARACTERS
        .iter()
        .filter(|(c, _)| p.uses_v113_rules || *c != "CHARACTER.FOOL")
        .filter(|(_, e)| match (e, &p.unlocked_epochs) {
            (None, _) | (_, None) => true,
            (Some(e), Some(ep)) => ep.iter().any(|x| x == e),
        })
        .map(|(c, _)| *c)
        .filter(|c| *c != p.character)
        .collect();
    let other = next_item(rng, &others).map(str::to_string).unwrap_or_else(|| p.character.clone());
    let first = if rng.next_float() < 0.3333333f32 {
        *sea_glass = None;
        "PRISMATIC_GEM"
    } else {
        *sea_glass = Some(other);
        SEA_GLASS
    };
    let mut pool1 = orobas_pool1(p.scheme).to_vec();
    pool1.push(first);
    let a = next_item(rng, &pool1).unwrap();
    if a != SEA_GLASS {
        *sea_glass = None;
    }
    let b = next_item(rng, orobas_pool2(p.scheme)).unwrap();
    let mut third = vec![];
    if touch_allowed(p) {
        third.push("TOUCH_OF_OROBAS");
    }
    if archaic_tooth_allowed(p) {
        third.push("ARCHAIC_TOOTH");
    }
    if third.is_empty() {
        third.push(OROBAS_LOCKED);
    }
    s(&[a, b, next_item(rng, &third).unwrap()])
}

fn orobas_pool1(scheme: RngScheme) -> &'static [&'static str] {
    if scheme == RngScheme::Legacy32 { &["ELECTRIC_SHRYMP", "GLASS_EYE", "SAND_CASTLE"] } else { &["ELECTRIC_SHRYMP", "GLASS_EYE"] }
}

fn orobas_pool2(scheme: RngScheme) -> &'static [&'static str] {
    if scheme == RngScheme::Legacy32 {
        &["ALCHEMICAL_COFFER", "DRIFTWOOD", "RADIANT_PEARL"]
    } else {
        &["ALCHEMICAL_COFFER", "DRIFTWOOD", "RADIANT_PEARL", "SAND_CASTLE"]
    }
}

const NONUPEIPE_POOL: [&str; 9] =
    ["BLESSED_ANTLER", "BRILLIANT_SCARF", "DELICATE_FROND", "DIAMOND_DIADEM", "FUR_COAT", "GLITTER", "JEWELRY_BOX", "LOOMING_FRUIT", "SIGNET_RING"];
const TANX_POOL: [&str; 9] = ["CLAWS", "CROSSBOW", "IRON_CLUB", "MEAT_CLEAVER", "SAI", "SPIKED_GAUNTLETS", "TANXS_WHISTLE", "THROWING_AXE", "WAR_HAMMER"];

fn take_shuffled(rng: &mut GameRng, p: &AncientInputs, pool: &[&str], extra: Option<&str>) -> Vec<String> {
    let mut list = pool.to_vec();
    list.extend(extra);
    rng.shuffle(&mut list, p.uses_v113_rules);
    s(&list[..3])
}

const VAKUU_POOLS: [&[&str]; 3] = [
    &["BLOOD_SOAKED_ROSE", "WHISPERING_EARRING", "FIDDLE"],
    &["PRESERVED_FOG", "SERE_TALON", "DISTINGUISHED_CAPE"],
    &["CHOICES_PARADOX", "MUSIC_BOX", "LORDS_PARASOL", "JEWELED_MASK"],
];

fn vakuu(rng: &mut GameRng, p: &AncientInputs) -> Vec<String> {
    let mut pools: Vec<Vec<&str>> = VAKUU_POOLS.iter().map(|x| x.to_vec()).collect();
    for pool in &mut pools {
        rng.shuffle(pool, p.uses_v113_rules);
    }
    s(&[pools[0][0], pools[1][0], pools[2][0]])
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum DarvGate {
    None,
    NoDeckClearingModifier,
    Act2,
    Act3,
    Act2OrLater,
}

type DarvSet = (DarvGate, &'static [&'static str]);

const DARV_SETS_V107: [DarvSet; 9] = [
    (DarvGate::None, &["ASTROLABE"]),
    (DarvGate::None, &["BLACK_STAR"]),
    (DarvGate::None, &["CALLING_BELL"]),
    (DarvGate::None, &["EMPTY_CAGE"]),
    (DarvGate::NoDeckClearingModifier, &["PANDORAS_BOX"]),
    (DarvGate::None, &["RUNIC_PYRAMID"]),
    (DarvGate::None, &["SNECKO_EYE"]),
    (DarvGate::Act2, &["ECTOPLASM", "SOZU"]),
    (DarvGate::Act3, &["PHILOSOPHERS_STONE", "VELVET_CHOKER"]),
];

const DARV_SETS_V109: [DarvSet; 11] = [
    (DarvGate::None, &["ASTROLABE"]),
    (DarvGate::None, &["BLACK_STAR"]),
    (DarvGate::None, &["CALLING_BELL"]),
    (DarvGate::None, &["EMPTY_CAGE"]),
    (DarvGate::NoDeckClearingModifier, &["PANDORAS_BOX"]),
    (DarvGate::None, &["RUNIC_PYRAMID"]),
    (DarvGate::None, &["SNECKO_EYE"]),
    (DarvGate::Act2, &["ECTOPLASM"]),
    (DarvGate::Act2, &["SOZU"]),
    (DarvGate::Act2OrLater, &["PHILOSOPHERS_STONE"]),
    (DarvGate::Act2OrLater, &["VELVET_CHOKER"]),
];

fn darv_sets(scheme: RngScheme) -> &'static [DarvSet] {
    if scheme == RngScheme::Legacy32 { &DARV_SETS_V107 } else { &DARV_SETS_V109 }
}

fn darv_gate_open(gate: DarvGate, p: &AncientInputs) -> bool {
    match gate {
        DarvGate::NoDeckClearingModifier => !p.deck_clearing_modifier,
        DarvGate::Act2 => p.act_index == 1,
        DarvGate::Act3 => p.act_index == 2,
        DarvGate::Act2OrLater => p.act_index >= 1,
        DarvGate::None => true,
    }
}

fn darv(rng: &mut GameRng, p: &AncientInputs) -> Vec<String> {
    let mut picks: Vec<&str> =
        darv_sets(p.scheme).iter().filter(|(g, _)| darv_gate_open(*g, p)).map(|(_, r)| next_item(rng, r).unwrap()).collect();
    rng.shuffle(&mut picks, p.uses_v113_rules);
    if next_bool(rng) {
        let mut v = s(&picks[..2]);
        v.push("DUSTY_TOME".into());
        v
    } else {
        s(&picks[..3])
    }
}

fn claw_allowed(p: &AncientInputs) -> bool {
    p.deck.iter().filter(|c| can_enchant(c, "Goopy", p.card_version())).count() >= 3
}
fn paels_tooth_allowed(p: &AncientInputs) -> bool {
    p.deck.iter().filter(|c| !cards::has_keyword(&c.id, "Eternal", p.card_version())).count() >= 5
}
fn soup_allowed(p: &AncientInputs) -> bool {
    let v = p.card_version();
    p.deck.iter().any(|c| cards::has_tag(&c.id, "Strike", v) && cards::rarity(&c.id, v) == Some("Basic"))
}
fn touch_allowed(p: &AncientInputs) -> bool {
    p.relics.iter().any(|r| relics::rarity_of(r, p.card_version()) == Some("Starter"))
}
fn archaic_tooth_allowed(p: &AncientInputs) -> bool {
    p.deck.iter().any(|c| deck_effects::transcendence(&c.id).is_some())
}
fn bracelet_allowed(p: &AncientInputs) -> bool {
    p.deck.iter().filter(|c| can_enchant(c, "Swift", p.card_version())).count() >= 4
}
fn boomerang_allowed(p: &AncientInputs) -> bool {
    p.deck.iter().filter(|c| can_enchant(c, "Instinct", p.card_version())).count() >= 3
}

fn can_enchant(card: &AncientDeckCard, enchantment: &str, v: GameDataVersion) -> bool {
    let Some(t) = cards::type_of(&card.id, v) else { return false };
    if matches!(t, "Status" | "Curse" | "Quest") || cards::has_keyword(&card.id, "Unplayable", v) || card.enchantment_id.is_some() {
        return false;
    }
    match enchantment {
        "Goopy" => cards::has_tag(&card.id, "Defend", v),
        "Instinct" => t == "Attack",
        _ => true,
    }
}

fn has_event_pet(p: &AncientInputs) -> bool {
    p.relics.iter().any(|r| relics::adds_pet(r, p.card_version())) || p.deck.iter().any(|c| c.id == "CARD.BYRDONIS_EGG")
}
