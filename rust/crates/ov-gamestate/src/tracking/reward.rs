//! Card reward replay from the run seed (`RewardRollReplay`, `RewardCardPool`): locates the logged rarity rolls on
//! the player's Rewards stream and predicts which card each reward slot shows.

use std::collections::{HashMap, HashSet};

use serde_json::Value;

use super::data::{GameDataVersion, PerVersion, cards, embedded};
use super::rng::{self, GameRng, RngScheme, RngTape};
use crate::log::{CardRarityRolled, RaritySource};

/// One located reward slot: stream counter of its rarity roll, the rarity, the card pick draw, the upgrade roll.
#[derive(Clone, Debug, PartialEq)]
pub struct OfferSlot {
    pub counter: i32,
    pub rarity: &'static str,
    pub pick: f64,
    pub upgrade_roll: f32,
}

const MAX_GAP: i32 = 400;
const WIDE_WINDOW: i32 = 20_000;
const SCARCITY_ASCENSION: i32 = 7;

pub struct RewardRollReplay {
    tape: RngTape,
    last: i32,
    pub slot: i32,
    pub scheme: RngScheme,
}

impl RewardRollReplay {
    pub fn new(run_seed: &str, slot: i32, scheme: RngScheme) -> Self {
        Self { tape: RngTape::new(GameRng::new(rng::player_stream(scheme, run_seed, slot, "rewards"))), last: -1, slot, scheme }
    }

    pub fn locate(&mut self, rolls: &[CardRarityRolled]) -> Vec<Option<OfferSlot>> {
        let mut found = Vec::with_capacity(rolls.len());
        for (i, r) in rolls.iter().enumerate() {
            let roll = r.roll_float;
            let mut c = self.tape.find_float(roll, self.last + 1, self.last + 1 + MAX_GAP);
            if c < 0 && i + 1 < rolls.len() {
                let mut from = self.last + 1;
                loop {
                    c = self.tape.find_float(roll, from, self.last + 1 + WIDE_WINDOW);
                    if c < 0 || GameRng::to_float(self.tape.at(c as usize + 3)) == rolls[i + 1].roll_float {
                        break;
                    }
                    from = c + 1;
                }
            }
            if c < 0 {
                found.push(None);
                continue;
            }
            self.last = c;
            let pick = GameRng::to_double(self.tape.at(c as usize + 1));
            let upgrade_roll = GameRng::to_float(self.tape.at(c as usize + 2));
            found.push(Some(OfferSlot { counter: c, rarity: rarity_of(r), pick, upgrade_roll }));
        }
        found
    }
}

pub fn rarity_of(r: &CardRarityRolled) -> &'static str {
    let (roll, need) = (r.roll_float, r.need as f32);
    if roll < need {
        return "Rare";
    }
    let uncommon: f32 = match r.source() {
        RaritySource::Elite => 0.4,
        RaritySource::Boss => 0.0,
        _ => 0.37,
    };
    if roll < uncommon + need { "Uncommon" } else { "Common" }
}

/// `(decimal)roll <= act * (0.25 | 0.125)`: .NET converts a float to decimal with 7 significant digits.
pub fn upgrades(roll: f32, rarity: &str, act_index: i32, ascension: i32) -> bool {
    if rarity == "Rare" {
        return false;
    }
    let odds = act_index as f64 * if ascension >= SCARCITY_ASCENSION { 0.125 } else { 0.25 };
    float_as_decimal(roll) <= odds
}

/// The value .NET's `(decimal)f` gives (7 significant digits), as f64 (exact for the comparisons made here).
pub fn float_as_decimal(f: f32) -> f64 {
    if f == 0.0 || !f.is_finite() {
        return f as f64;
    }
    format!("{f:.6e}").parse().unwrap()
}

type PoolTable = HashMap<String, (Vec<String>, Vec<Vec<String>>)>;
static POOLS: PerVersion<PoolTable> = PerVersion::new();

fn pools(v: GameDataVersion) -> &'static PoolTable {
    POOLS.get(v, |v| {
        let doc: Value = serde_json::from_str(embedded("card-pools", v).unwrap()).expect("card pools");
        let mut map = HashMap::new();
        if let Some(Value::Object(p)) = doc.get("pools") {
            for (name, pool) in p {
                let cards = pool["cards"].as_array().unwrap().iter().map(|x| x.as_str().unwrap().to_string()).collect();
                let epochs = pool["epochs"]
                    .as_object()
                    .unwrap()
                    .values()
                    .map(|e| e.as_array().unwrap().iter().map(|x| x.as_str().unwrap().to_string()).collect())
                    .collect();
                map.insert(name.clone(), (cards, epochs));
            }
        }
        map
    })
}

const ORDER: [&str; 3] = ["Common", "Uncommon", "Rare"];
const TRANSFORMABLE: [&str; 4] = ["Basic", "Common", "Uncommon", "Rare"];

/// A character's reward card pool with its epoch-locked groups (`RewardCardPool`).
pub struct RewardCardPool {
    cards: &'static [String],
    epochs: &'static [Vec<String>],
    multiplayer: bool,
    pub version: GameDataVersion,
}

impl RewardCardPool {
    pub fn for_character(character: Option<&str>, multiplayer: bool, v: GameDataVersion) -> Option<Self> {
        let (cards, epochs) = pools(v).get(character?)?;
        Some(Self { cards, epochs, multiplayer, version: v })
    }

    pub fn scheme(&self) -> RngScheme {
        self.version.scheme()
    }

    /// Number of epoch-lock hypotheses (2^epochs).
    pub fn hypotheses(&self) -> i32 {
        1 << self.epochs.len()
    }

    fn unlocked(&self, locked_mask: i32) -> Vec<&'static str> {
        let mut locked: HashSet<&str> = HashSet::new();
        for (i, e) in self.epochs.iter().enumerate() {
            if (locked_mask >> i) & 1 != 0 {
                locked.extend(e.iter().map(String::as_str));
            }
        }
        self.cards.iter().map(String::as_str).filter(|c| !locked.contains(c) && cards::allowed_with_players(c, self.multiplayer, self.version)).collect()
    }

    /// The card a random transform gives (the same under every live hypothesis), or None.
    pub fn transform_target(&self, original: &str, raw: u64, live_mask: u64) -> Option<&'static str> {
        if !self.cards.iter().any(|c| c == original) || !cards::rarity(original, self.version).is_some_and(|r| TRANSFORMABLE.contains(&r)) {
            return None;
        }
        let mut result: Option<&'static str> = None;
        let mut h = 0;
        while h < self.hypotheses() && h < 64 {
            if (live_mask >> h) & 1 != 0 {
                let options: Vec<&str> = self
                    .unlocked(h)
                    .into_iter()
                    .filter(|c| *c != original && cards::rarity(c, self.version).is_some_and(|r| ORDER.contains(&r)))
                    .collect();
                if options.is_empty() {
                    return None;
                }
                let card = options[GameRng::to_int(raw, options.len() as i32) as usize];
                if result.is_some_and(|r| r != card) {
                    return None;
                }
                result = Some(card);
            }
            h += 1;
        }
        result
    }

    /// Cards the located slots show under one lock hypothesis; None when a slot has no card.
    pub fn predict(&self, offer: &[OfferSlot], locked_mask: i32) -> Option<Vec<&'static str>> {
        let pool = self.unlocked(locked_mask);
        let mut shown: Vec<&'static str> = Vec::with_capacity(offer.len());
        for slot in offer {
            let options: Vec<&str> = pool.iter().copied().filter(|c| !shown.contains(c)).collect();
            let mut rarity = slot.rarity;
            let mut k = 0;
            while k < 3 && !options.iter().any(|c| cards::rarity(c, self.version) == Some(rarity)) {
                let i = ORDER.iter().position(|r| *r == rarity).map_or(-1, |i| i as i32);
                rarity = ORDER[((i + 1) % 3) as usize];
                k += 1;
            }
            let items: Vec<&str> = options.iter().copied().filter(|c| cards::rarity(c, self.version) == Some(rarity)).collect();
            if items.is_empty() {
                return None;
            }
            let i = ((slot.pick * items.len() as f64) as i32).min(items.len() as i32 - 1);
            shown.push(items[i as usize]);
        }
        Some(shown)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn float_to_decimal_keeps_seven_digits() {
        assert_eq!(float_as_decimal(0.25f32), 0.25);
        assert_eq!(float_as_decimal(0.1f32), 0.1);
        assert_eq!(float_as_decimal(0.2500001f32), 0.2500001);
        assert!(upgrades(0.25, "Common", 1, 0));
        assert!(!upgrades(0.2500001, "Common", 1, 0));
    }
}
