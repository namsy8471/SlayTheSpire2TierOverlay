//! Relic facts for trading events (`RelicTrading`), from the embedded relic catalog.

use std::collections::HashMap;

use serde_json::Value;

use super::data::{GameDataVersion, PerVersion, embedded};
use crate::save::SavedRelic;

#[derive(Debug)]
struct Info {
    rarity: String,
    upon_pickup: bool,
    pets: bool,
    used_up_override: bool,
    adds_pet: bool,
    allowed_override: bool,
}

static TABLES: PerVersion<HashMap<String, Info>> = PerVersion::new();

fn table(v: GameDataVersion) -> &'static HashMap<String, Info> {
    TABLES.get(v, |v| {
        let doc: Value = serde_json::from_str(embedded("relics", v).unwrap()).expect("embedded relics");
        let b = |r: &Value, n: &str| matches!(r.get(n), Some(Value::Bool(true)));
        doc["relics"]
            .as_array()
            .expect("relics")
            .iter()
            .map(|r| {
                (r["id"].as_str().expect("relic id").to_string(), Info {
                    rarity: r.get("rarity").and_then(Value::as_str).unwrap_or("").to_string(),
                    upon_pickup: b(r, "hasUponPickupEffect"),
                    pets: b(r, "spawnsPets"),
                    used_up_override: b(r, "usedUpOverride"),
                    adds_pet: b(r, "addsPet"),
                    allowed_override: b(r, "allowedOverride"),
                })
            })
            .collect()
    })
}

/// Relics an event may trade away; `exact` is false when one could not be judged.
pub fn count_tradable(relics: &[SavedRelic], v: GameDataVersion) -> (i32, bool) {
    let t = table(v);
    let mut exact = true;
    let mut n = 0;
    for r in relics {
        let Some(info) = t.get(&r.id) else {
            exact = false;
            continue;
        };
        if matches!(info.rarity.as_str(), "Starter" | "Event" | "Ancient") || info.upon_pickup || info.pets {
            continue;
        }
        if r.true_bools.contains("IsMelted") {
            continue;
        }
        if info.used_up_override {
            // LizardTail.IsUsedUp => _wasUsed
            let Some(flag) = (r.id == "RELIC.LIZARD_TAIL").then_some("WasUsed") else {
                exact = false;
                continue;
            };
            if r.true_bools.contains(flag) {
                continue;
            }
        }
        n += 1;
    }
    (n, exact)
}

pub fn knows(id: &str, v: GameDataVersion) -> bool {
    table(v).contains_key(id)
}

pub fn rarity_of(id: &str, v: GameDataVersion) -> Option<&'static str> {
    table(v).get(id).map(|i| i.rarity.as_str())
}

pub fn adds_pet(id: &str, v: GameDataVersion) -> bool {
    table(v).get(id).is_some_and(|i| i.adds_pet)
}

pub fn always_pullable(id: &str, v: GameDataVersion) -> bool {
    table(v).get(id).is_some_and(|i| !i.allowed_override)
}
