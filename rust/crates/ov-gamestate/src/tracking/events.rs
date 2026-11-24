//! Which event a "?" room pulls, predicted from the room-entry save (`EventPrediction`).

use std::collections::HashSet;

use ov_core::map::MapNodeType;
use ov_core::model::{DeckCard, RunState};

use super::data::{GameDataVersion, cards};
use super::relics;
use super::rng::RngScheme;
use crate::save::{ParsedSave, SavedRelic};

#[derive(Clone, Debug, PartialEq)]
pub struct EventGuess {
    pub event_id: String,
    pub certain: bool,
    pub alternatives: Vec<String>,
}

const WAR_HISTORIAN: &str = "EVENT.WAR_HISTORIAN_REPY";

pub fn predict(save: &ParsedSave, fallback: Option<GameDataVersion>) -> Option<EventGuess> {
    let version = if save.schema_version > 0 { GameDataVersion::for_run(save.schema_version, None) } else { fallback.unwrap_or_default() };
    let ev = save.events.as_ref()?;
    if !ev.is_room_entry_save() || save.player_net_ids.len() > 1 {
        return None;
    }
    let state = &save.state;
    if let Some(map) = &save.map
        && let Some(cur) = map.current
        && map.nodes.get(&cur).is_some_and(|n| n.kind == MapNodeType::Ancient)
    {
        return ev.ancient_id.as_ref().map(|a| EventGuess { event_id: a.clone(), certain: true, alternatives: vec![] });
    }
    if ev.event_ids.is_empty() {
        return None;
    }
    let facts = Facts {
        act: ev.act_index,
        gold: state.gold,
        hp: state.hp,
        max_hp: state.max_hp,
        total_floor: ev.total_history,
        state,
        potions: Some(save.potions.iter().map(|p| p.id.clone()).collect()),
        relics: Some(&save.relic_details),
        unlocked_epochs: Some(&save.unlocked_epochs),
        relic_grab_bag: save.relic_grab_bag.as_deref(),
        version,
        ancient_id: ev.ancient_id.as_deref(),
    };
    let seen: HashSet<&str> = ev.events_seen.iter().map(String::as_str).collect();
    let (guess, _) = pull(&ev.event_ids, ev.events_visited, &seen, &facts);
    if ev.act_index == 2 && state.deck.iter().any(|c| c.id() == "CARD.LANTERN_KEY") {
        return Some(EventGuess { event_id: WAR_HISTORIAN.into(), certain: true, alternatives: vec![] });
    }
    Some(guess)
}

/// The next allowed, unseen event from `start_index`, and the index after it.
pub fn pull(ids: &[String], start_index: i32, seen: &HashSet<&str>, facts: &Facts) -> (EventGuess, i32) {
    let mut alternatives = Vec::new();
    let mut certain = true;
    let mut pick: Option<&String> = None;
    let mut next_index = start_index + 1;
    let n = ids.len() as i32;
    let mut i = start_index;
    for _ in 0..n {
        let e = &ids[i.rem_euclid(n) as usize];
        let cur = i;
        i += 1;
        if seen.contains(e.as_str()) {
            continue;
        }
        let allowed = is_allowed(e, facts);
        match allowed {
            Allowed::No => continue,
            Allowed::ProbablyNot => {
                certain = false;
                if pick.is_none() {
                    alternatives.push(e.clone());
                }
                continue;
            }
            _ => {}
        }
        if pick.is_none() {
            pick = Some(e);
            next_index = cur + 1;
            if allowed == Allowed::Yes {
                break;
            }
            certain = false;
            continue;
        }
        alternatives.push(e.clone());
        break;
    }
    let event_id = pick.cloned().unwrap_or_else(|| ids[start_index.rem_euclid(n) as usize].clone());
    (EventGuess { event_id, certain, alternatives }, next_index)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Allowed {
    Yes,
    No,
    Probably,
    ProbablyNot,
}

/// What an event's `IsAllowed` reads.
pub struct Facts<'a> {
    pub act: i32,
    pub gold: i32,
    pub hp: i32,
    pub max_hp: i32,
    pub total_floor: i32,
    pub state: &'a RunState,
    /// None = unknown (replay): potion checks say Probably.
    pub potions: Option<Vec<String>>,
    pub relics: Option<&'a [SavedRelic]>,
    pub unlocked_epochs: Option<&'a [String]>,
    pub relic_grab_bag: Option<&'a [(String, Vec<String>)]>,
    pub version: GameDataVersion,
    pub ancient_id: Option<&'a str>,
}

const DROPS_EXHAUST_V0107: [&str; 19] = [
    "CARD.CHILL", "CARD.DEMONIC_SHIELD", "CARD.DISCOVERY", "CARD.FUSION", "CARD.GRAVEBLAST", "CARD.HOLOGRAM", "CARD.HOTFIX",
    "CARD.IGNITION", "CARD.KNOW_THY_PLACE", "CARD.MIMIC", "CARD.PROLONG", "CARD.RAINBOW", "CARD.SCARE", "CARD.SECRET_TECHNIQUE",
    "CARD.SECRET_WEAPON", "CARD.SYNCHRONIZE", "CARD.THINKING_AHEAD", "CARD.TRANSFIGURE", "CARD.VOLTAIC",
];
const DROPS_EXHAUST_V0111: [&str; 19] = [
    "CARD.CHILL", "CARD.DEMONIC_SHIELD", "CARD.DISCOVERY", "CARD.FUSION", "CARD.GRAVEBLAST", "CARD.HOLOGRAM", "CARD.HOTFIX",
    "CARD.IGNITION", "CARD.KNOW_THY_PLACE", "CARD.MIMIC", "CARD.MIRAGE", "CARD.PROLONG", "CARD.RAINBOW", "CARD.SECRET_TECHNIQUE",
    "CARD.SECRET_WEAPON", "CARD.THINKING_AHEAD", "CARD.TRANSFIGURE", "CARD.UNDERWORLD", "CARD.VOLTAIC",
];

/// The upgraded card loses Exhaust.
pub fn upgrade_drops_exhaust(card_id: &str, scheme: RngScheme) -> bool {
    if scheme == RngScheme::Legacy32 { DROPS_EXHAUST_V0107.contains(&card_id) } else { DROPS_EXHAUST_V0111.contains(&card_id) }
}

const ETERNAL_ENCHANTMENT: &str = "ENCHANTMENT.TEZCATARAS_EMBER";
const CHARACTER_EPOCHS: [&str; 4] = ["SILENT1_EPOCH", "REGENT1_EPOCH", "NECROBINDER1_EPOCH", "DEFECT1_EPOCH"];

fn b(x: bool) -> Allowed {
    if x { Allowed::Yes } else { Allowed::No }
}

fn both(a: Allowed, c: Allowed) -> Allowed {
    use Allowed::*;
    if a == No || c == No {
        No
    } else if a == ProbablyNot || c == ProbablyNot {
        ProbablyNot
    } else if a == Probably || c == Probably {
        Probably
    } else {
        Yes
    }
}

pub fn is_allowed(event_id: &str, f: &Facts) -> Allowed {
    let deck = &f.state.deck;
    let v = f.version;
    let potions = |test: &dyn Fn(&[String]) -> bool| f.potions.as_deref().map_or(Allowed::Probably, |p| b(test(p)));
    let unknown_card = deck.iter().any(|c| !cards::knows(c.id(), v));
    let cards_ = |result: bool| {
        if unknown_card {
            if result { Allowed::Probably } else { Allowed::ProbablyNot }
        } else {
            b(result)
        }
    };
    let removable = |c: &DeckCard| !cards::has_keyword(c.id(), "Eternal", v) && c.enchantment_id.as_deref() != Some(ETERNAL_ENCHANTMENT);
    let base_enchantable = |c: &DeckCard| {
        c.enchantment_id.is_none() && matches!(cards::type_of(c.id(), v), Some("Attack" | "Skill" | "Power")) && !cards::has_keyword(c.id(), "Unplayable", v)
    };
    let strike_or_defend = |c: &DeckCard| c.id().starts_with("CARD.STRIKE_") || c.id().starts_with("CARD.DEFEND_");
    let has_event_pet = || deck.iter().any(|c| c.id() == "CARD.BYRDONIS_EGG") || f.state.relics.iter().any(|r| relics::adds_pet(r, v));
    let has_exhaust = |c: &DeckCard| cards::has_keyword(c.id(), "Exhaust", v) && !(c.upgrade() > 0 && upgrade_drops_exhaust(c.id(), v.scheme()));
    let tradable = |at_least: i32| {
        let owned: Vec<SavedRelic>;
        let rs: &[SavedRelic] = match f.relics {
            Some(r) => r,
            None => {
                owned = f.state.relics.iter().map(|id| SavedRelic { id: id.clone(), true_bools: Default::default() }).collect();
                &owned
            }
        };
        let (n, exact) = relics::count_tradable(rs, v);
        match (n >= at_least, exact) {
            (true, true) => Allowed::Yes,
            (true, false) => Allowed::Probably,
            (false, true) => Allowed::No,
            (false, false) => Allowed::ProbablyNot,
        }
    };
    let bag_has_relic = || {
        let Some(bag) = f.relic_grab_bag else { return Allowed::Probably };
        let ids: Vec<&String> = ["common", "uncommon", "rare", "shop"]
            .iter()
            .flat_map(|k| bag.iter().find(|(name, _)| name == k).map(|(_, v)| v.iter()).into_iter().flatten())
            .collect();
        if ids.is_empty() {
            Allowed::No
        } else if ids.iter().any(|id| relics::always_pullable(id, v)) {
            Allowed::Yes
        } else {
            Allowed::Probably
        }
    };
    match event_id {
        "EVENT.BRAIN_LEECH" | "EVENT.ROOM_FULL_OF_CHEESE" => b(f.act < 2),
        "EVENT.CRYSTAL_SPHERE" => b(f.gold >= 100 && f.act > 0),
        "EVENT.DOLL_ROOM" => b(f.act == 1),
        "EVENT.WELCOME_TO_WONGOS" => b(f.act == 1 && f.gold >= 100),
        "EVENT.STONE_OF_ALL_TIME" => {
            if f.act == 1 {
                potions(&|p| !p.is_empty())
            } else {
                Allowed::No
            }
        }
        "EVENT.TEA_MASTER" => b(f.act < 2 && f.gold >= 150),
        "EVENT.POTION_COURIER" | "EVENT.SYMBIOTE" => b(f.act > 0),
        "EVENT.THE_LEGENDS_WERE_TRUE" => b(f.act == 0 && f.hp >= 10 && !deck.is_empty()),
        "EVENT.ENDLESS_CONVEYOR" => b(f.gold >= 120),
        "EVENT.ZEN_WEAVER" => b(f.gold >= 125),
        "EVENT.WATERLOGGED_SCRIPTORIUM" => b(f.gold >= 55),
        "EVENT.WHISPERING_HOLLOW" => b(f.gold >= 44),
        "EVENT.COLOSSAL_FLOWER" => b(f.hp >= 19),
        "EVENT.ROUND_TEA_PARTY" => b(f.hp >= 12),
        "EVENT.TRASH_HEAP" => b(f.hp > 5),
        // hp <= maxHp * 0.7m, exact in decimal.
        "EVENT.UNREST_SITE" => b(10 * f.hp as i64 <= 7 * f.max_hp as i64),
        "EVENT.PUNCH_OFF" => b(f.total_floor >= 6),
        "EVENT.THE_FUTURE_OF_POTIONS" => potions(&|p| p.len() >= 2),
        "EVENT.WAR_HISTORIAN_REPY" => Allowed::No,
        "EVENT.FAKE_MERCHANT" => {
            if f.act < 1 {
                Allowed::No
            } else if f.gold >= 100 {
                Allowed::Yes
            } else {
                potions(&|p| p.iter().any(|x| x == "POTION.FOUL_POTION"))
            }
        }
        "EVENT.AMALGAMATOR" => cards_(
            deck.iter().filter(|c| c.id().starts_with("CARD.STRIKE_") && removable(c)).count() >= 2
                && deck.iter().filter(|c| c.id().starts_with("CARD.DEFEND_") && removable(c)).count() >= 2,
        ),
        "EVENT.BYRDONIS_NEST" => b(!has_event_pet()),
        "EVENT.WUPPY_IN_TROUBLE" => b(f.hp > 0 && !has_event_pet()),
        "EVENT.WOOD_CARVINGS" => cards_(deck.iter().any(|c| cards::rarity(c.id(), v) == Some("Basic") && removable(c))),
        "EVENT.FIELD_OF_MAN_SIZED_HOLES" => cards_(deck.iter().any(base_enchantable)),
        "EVENT.SPIRALING_WHIRLPOOL" => cards_(deck.iter().any(|c| base_enchantable(c) && strike_or_defend(c))),
        "EVENT.GRAVE_OF_THE_FORGOTTEN" => cards_(deck.iter().any(|c| base_enchantable(c) && has_exhaust(c))),
        "EVENT.LUMINOUS_CHOIR" => {
            if f.gold >= 149 {
                bag_has_relic()
            } else {
                Allowed::No
            }
        }
        "EVENT.MORPHIC_GROVE" => {
            if f.gold >= 100 {
                cards_(deck.iter().filter(|c| removable(c)).count() >= 2)
            } else {
                Allowed::No
            }
        }
        "EVENT.RANWID_THE_ELDER" => {
            if f.act != 0 && f.gold >= 100 {
                both(potions(&|p| !p.is_empty()), tradable(1))
            } else {
                Allowed::No
            }
        }
        "EVENT.RELIC_TRADER" => {
            if f.act != 0 {
                tradable(5)
            } else {
                Allowed::No
            }
        }
        "EVENT.SLIPPERY_BRIDGE" => {
            if f.total_floor > 6 {
                cards_(deck.iter().any(removable))
            } else {
                Allowed::No
            }
        }
        "EVENT.COLORFUL_PHILOSOPHERS" => match f.unlocked_epochs {
            Some(ep) => b(CHARACTER_EPOCHS.iter().any(|e| ep.iter().any(|x| x == e))),
            None => Allowed::Probably,
        },
        "EVENT.MUSEUM_OF_TORTURE" => b(f.act == 1),
        "EVENT.SHRINE_OF_THE_WARLORD" => b(f.ancient_id != Some("EVENT.TANX")),
        "EVENT.THERMAE_NONUPEIPE" => b(f.gold > 175 && f.hp * 4 <= f.max_hp * 3 && f.ancient_id != Some("EVENT.NONUPEIPE")),
        _ => Allowed::Yes,
    }
}
