//! Run state as the overlay reads it (`Sts2Overlay.Core.Model`).

use std::collections::BTreeMap;

/// A card id with its upgrade level (`CardRef`).
#[derive(Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct CardRef {
    pub id: String,
    pub upgrade: i32,
}

impl CardRef {
    pub fn new(id: impl Into<String>, upgrade: i32) -> Self {
        Self { id: id.into(), upgrade }
    }

    pub fn is_upgraded(&self) -> bool {
        self.upgrade > 0
    }
}

impl std::fmt::Display for CardRef {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        if self.upgrade > 0 { write!(f, "{}+{}", self.id, self.upgrade) } else { f.write_str(&self.id) }
    }
}

/// Numbers saved on one copy of a card (`SavedCardProperties`), sorted by name (ordinal), compared by value.
#[derive(Clone, Debug, Default, PartialEq, Eq, Hash)]
pub struct SavedCardProperties(pub BTreeMap<String, i64>);

impl SavedCardProperties {
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }

    /// Reads `props.ints[]` (`{name, value}`) of a SerializableCard. Only int32 values count.
    pub fn read(card: &serde_json::Value) -> Self {
        let mut values = BTreeMap::new();
        if let Some(ints) = card.get("props").and_then(|p| p.as_object()).and_then(|p| p.get("ints")).and_then(|i| i.as_array()) {
            for entry in ints {
                let (Some(name), Some(value)) = (entry.get("name").and_then(|n| n.as_str()), entry.get("value")) else { continue };
                if name.is_empty() {
                    continue;
                }
                if let Some(v) = value.as_i64().filter(|v| i32::try_from(*v).is_ok()) {
                    values.insert(name.to_string(), v);
                }
            }
        }
        Self(values)
    }
}

/// One card of a deck (`DeckCard`).
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct DeckCard {
    pub card: CardRef,
    pub floor_added: i32,
    pub enchantment_id: Option<String>,
    pub enchantment_amount: i32,
    pub saved: SavedCardProperties,
}

impl DeckCard {
    pub fn new(card: CardRef) -> Self {
        Self { card, floor_added: 0, enchantment_id: None, enchantment_amount: 0, saved: SavedCardProperties::default() }
    }

    pub fn id(&self) -> &str {
        &self.card.id
    }

    pub fn upgrade(&self) -> i32 {
        self.card.upgrade
    }
}

/// The run as the overlay sees it (`RunState`).
#[derive(Clone, Debug, PartialEq)]
pub struct RunState {
    pub character: String,
    pub ascension: i32,
    pub save_schema_version: Option<i32>,
    pub is_standard_run: bool,
    /// 1-based.
    pub act: i32,
    pub act_boss: Option<String>,
    pub floor: i32,
    pub hp: i32,
    pub max_hp: i32,
    pub gold: i32,
    pub empty_potion_slots: Option<i32>,
    /// False for a co-op guest whose HP and gold are not known.
    pub vitals_known: bool,
    pub deck: Vec<DeckCard>,
    pub relics: Vec<String>,
    pub is_multiplayer: bool,
    pub card_removals_bought: i32,
}

impl RunState {
    pub fn new(character: impl Into<String>) -> Self {
        Self {
            character: character.into(),
            ascension: 0,
            save_schema_version: None,
            is_standard_run: true,
            act: 1,
            act_boss: None,
            floor: 1,
            hp: 0,
            max_hp: 0,
            gold: 0,
            empty_potion_slots: None,
            vitals_known: true,
            deck: Vec::new(),
            relics: Vec::new(),
            is_multiplayer: false,
            card_removals_bought: 0,
        }
    }

    pub fn hp_ratio(&self) -> f64 {
        if self.max_hp <= 0 { 1.0 } else { (self.hp as f64 / self.max_hp as f64).clamp(0.0, 1.0) }
    }
}

/// Where a card offer was shown (`OfferContext`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum OfferContext {
    CardReward,
    EliteReward,
    BossReward,
    Event,
    Shop,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ShopCardOffer {
    pub card: CardRef,
    pub price: i32,
    pub price_known: bool,
    pub affordable_on_screen: Option<bool>,
    pub on_sale: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ShopRelicOffer {
    pub relic_id: String,
    pub rarity: Option<String>,
    pub price: i32,
    pub price_known: bool,
    pub affordable_on_screen: Option<bool>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ShopPotionOffer {
    pub potion_id: String,
    pub price: Option<i32>,
    pub affordable_on_screen: Option<bool>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ShopOffer {
    pub cards: Vec<ShopCardOffer>,
    pub removal_price: Option<i32>,
    pub relics: Vec<ShopRelicOffer>,
    pub potions: Vec<ShopPotionOffer>,
    pub removal_available: bool,
}
