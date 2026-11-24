//! Event pages and options that the event's own RNG decides (`EventRandomPages`, `EventOfferedOptions`).

use std::collections::BTreeMap;

use super::rng::GameRng;

pub const TRIAL_EVENT_ID: &str = "EVENT.TRIAL";
pub const TRIAL_PLACEHOLDER: &str = "RANDOM_SCENARIO";
const TRIAL_SCENARIOS: [&str; 3] = ["MERCHANT", "NOBLE", "NONDESCRIPT"];

pub fn random_pages_handles(event_id: &str) -> bool {
    event_id == TRIAL_EVENT_ID
}

pub fn trial_scenario(event_seed: u64) -> &'static str {
    TRIAL_SCENARIOS[GameRng::new(event_seed).next_int(3) as usize]
}

pub fn random_pages(event_id: &str, event_seed: u64) -> Option<BTreeMap<String, String>> {
    (event_id == TRIAL_EVENT_ID).then(|| BTreeMap::from([(TRIAL_PLACEHOLDER.to_string(), trial_scenario(event_seed).to_string())]))
}

pub const TINKER_TIME_EVENT_ID: &str = "EVENT.TINKER_TIME";
const TINKER: &str = "TINKER_TIME.pages.";
const TYPES: [&str; 3] = ["ATTACK", "SKILL", "POWER"];

fn riders(t: &str) -> [&'static str; 3] {
    match t {
        "ATTACK" => ["SAPPING", "VIOLENCE", "CHOKING"],
        "SKILL" => ["ENERGIZED", "WISDOM", "CHAOS"],
        _ => ["EXPERTISE", "CURIOUS", "IMPROVEMENT"],
    }
}

pub fn offered_handles(event_id: &str) -> bool {
    event_id == TINKER_TIME_EVENT_ID
}

fn take_two(rng: &mut GameRng, candidates: &[&'static str], child: bool) -> Vec<&'static str> {
    let mut list = candidates.to_vec();
    rng.shuffle(&mut list, child);
    list.truncate(2);
    list
}

/// Option key -> the option keys the next page offers.
pub fn offered_options(event_id: &str, event_seed: u64, child_shuffle: bool) -> Option<BTreeMap<String, Vec<String>>> {
    if event_id != TINKER_TIME_EVENT_ID {
        return None;
    }
    let types = take_two(&mut GameRng::new(event_seed), &TYPES, child_shuffle);
    let mut map = BTreeMap::new();
    map.insert(format!("{TINKER}INITIAL.options.CHOOSE_CARD_TYPE"), types.iter().map(|t| format!("{TINKER}CHOOSE_CARD_TYPE.options.{t}")).collect());
    for t in &types {
        let mut rng = GameRng::new(event_seed);
        take_two(&mut rng, &TYPES, child_shuffle);
        let r = take_two(&mut rng, &riders(t), child_shuffle);
        map.insert(format!("{TINKER}CHOOSE_CARD_TYPE.options.{t}"), r.iter().map(|r| format!("{TINKER}CHOOSE_RIDER.options.{r}")).collect());
    }
    Some(map)
}
