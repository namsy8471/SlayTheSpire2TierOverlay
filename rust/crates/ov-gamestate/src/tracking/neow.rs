//! Neow's three relic options, replayed from the run seed (`NeowOptions`).

use super::rng::{self, GameRng, RngScheme};

pub const EVENT_ID: &str = "EVENT.NEOW";
const KEY_PREFIX: &str = "NEOW.pages.INITIAL.options.";

const POSITIVE_POOL: [&str; 14] = [
    "ARCANE_SCROLL", "BOOMING_CONCH", "FISHING_ROD", "GOLDEN_PEARL", "KALEIDOSCOPE", "LEAD_PAPERWEIGHT", "LOST_COFFER",
    "MASSIVE_SCROLL", "NEOWS_TORMENT", "NEW_LEAF", "PHIAL_HOLSTER", "PRECISE_SCISSORS", "SCROLL_BOXES", "WINGED_BOOTS",
];
const CURSES_V107: [&str; 8] = ["CURSED_PEARL", "HEFTY_TABLET", "LARGE_CAPSULE", "LEAFY_POULTICE", "NEOWS_BONES", "PRECARIOUS_SHEARS", "SILKEN_TRESS", "SILVER_CRUCIBLE"];
const CURSES_V109: [&str; 10] = [
    "CURSED_PEARL", "DOWSING_ROD", "HEFTY_TABLET", "LARGE_CAPSULE", "LEAFY_POULTICE", "NEOWS_BONES", "NEOWS_SACRIFICE",
    "PRECARIOUS_SHEARS", "SILKEN_TRESS", "SILVER_CRUCIBLE",
];
const PAIRS: [&str; 6] = ["LAVA_ROCK", "SMALL_CAPSULE", "NUTRITIOUS_OYSTER", "STONE_HUMIDIFIER", "NEOWS_TALISMAN", "POMANDER"];

fn curses(scheme: RngScheme) -> &'static [&'static str] {
    if scheme == RngScheme::Legacy32 { &CURSES_V107 } else { &CURSES_V109 }
}

/// A curse-slot relic removes its positive twin(s) from the pool.
fn twins_of(scheme: RngScheme, curse: &str) -> &'static [&'static str] {
    match curse {
        "CURSED_PEARL" => &["GOLDEN_PEARL"],
        "HEFTY_TABLET" => &["ARCANE_SCROLL"],
        "LEAFY_POULTICE" => &["NEW_LEAF"],
        "PRECARIOUS_SHEARS" => &["PRECISE_SCISSORS"],
        "NEOWS_SACRIFICE" if scheme != RngScheme::Legacy32 => &["PHIAL_HOLSTER", "LOST_COFFER"],
        _ => &[],
    }
}

pub fn effective_scheme(run_seed: &str, scheme: RngScheme) -> RngScheme {
    if rng::is_migrated(run_seed) { RngScheme::Legacy32 } else { scheme }
}

pub fn stream_seed(run_seed: &str, slot: i32, scheme: RngScheme) -> u64 {
    rng::event_stream(scheme, run_seed, slot, "NEOW", false)
}

fn allowed(relic: &str, player_count: i32, all_characters_unlocked: bool) -> bool {
    match relic {
        "MASSIVE_SCROLL" => player_count > 1,
        "WINGED_BOOTS" | "SILVER_CRUCIBLE" => player_count == 1,
        "KALEIDOSCOPE" => all_characters_unlocked,
        _ => true,
    }
}

fn next_bool(rng: &mut GameRng) -> bool {
    rng.next_int(2) == 0
}

/// The three relics in button order: two positives, then the curse-slot relic.
pub fn relics(run_seed: &str, slot: i32, player_count: i32, all_unlocked: bool, scheme: RngScheme, use_child_stream: bool) -> Vec<String> {
    let effective = effective_scheme(run_seed, scheme);
    let seed = run_seed.strip_prefix(rng::MIGRATED_PREFIX).unwrap_or(run_seed);
    let mut rng = GameRng::new(stream_seed(seed, slot, effective));
    let ok = |r: &str| allowed(r, player_count, all_unlocked);
    let cs: Vec<&str> = curses(effective).iter().copied().filter(|c| ok(c)).collect();
    let curse = cs[rng.next_int(cs.len() as i32) as usize];
    let twins = twins_of(effective, curse);
    let mut positives: Vec<&str> = POSITIVE_POOL.iter().copied().filter(|p| !twins.contains(p)).collect();
    if curse != "LARGE_CAPSULE" {
        positives.push(if next_bool(&mut rng) { "LAVA_ROCK" } else { "SMALL_CAPSULE" });
    }
    positives.push(if next_bool(&mut rng) { "NUTRITIOUS_OYSTER" } else { "STONE_HUMIDIFIER" });
    positives.push(if next_bool(&mut rng) { "NEOWS_TALISMAN" } else { "POMANDER" });
    positives.retain(|r| ok(r));
    rng.shuffle(&mut positives, use_child_stream && effective != RngScheme::Legacy32);
    vec![positives[0].to_string(), positives[1].to_string(), curse.to_string()]
}

pub fn keys(run_seed: &str, slot: i32, player_count: i32, all_unlocked: bool, scheme: RngScheme, use_child_stream: bool) -> Vec<String> {
    relics(run_seed, slot, player_count, all_unlocked, scheme, use_child_stream).into_iter().map(|r| format!("{KEY_PREFIX}{r}")).collect()
}

pub fn possible_relics(player_count: i32, all_unlocked: bool, scheme: RngScheme) -> Vec<String> {
    curses(scheme)
        .iter()
        .chain(POSITIVE_POOL.iter())
        .chain(PAIRS.iter())
        .filter(|r| allowed(r, player_count, all_unlocked))
        .map(|r| r.to_string())
        .collect()
}

pub fn possible_keys(player_count: i32, all_unlocked: bool, scheme: RngScheme) -> Vec<String> {
    possible_relics(player_count, all_unlocked, scheme).into_iter().map(|r| format!("{KEY_PREFIX}{r}")).collect()
}
