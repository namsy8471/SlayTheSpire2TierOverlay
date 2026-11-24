//! What an event option or a relic does to the deck when the log does not say (`DeckEffects`).

use super::data::GameDataVersion;
use super::rng::RngScheme;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum SelectMeaning {
    #[default]
    None,
    Remove,
    Transform,
    Upgrade,
    Enchant,
    AddChosen,
    Duplicate,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum UpgradePick {
    #[default]
    Unknown,
    EventShuffle,
    EventNextItem,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct DeckEffect {
    pub select: SelectMeaning,
    pub transform_to: Option<String>,
    pub enchantment: Option<String>,
    pub adds: Vec<String>,
    pub removes: Vec<String>,
    pub random_upgrades: i32,
    pub random_upgrade_type: Option<String>,
    pub pick: UpgradePick,
    pub event_rng_call: i32,
    pub transform_rng_call: Option<i32>,
    pub unknown_adds: i32,
    pub random_removes: i32,
    pub upgrade_basic_strike_and_defend: bool,
    pub transform_all_basics: bool,
    pub transform_one_strike_and_defend: bool,
    pub transcend: bool,
    pub upgrade_all: bool,
    pub add_basic_strike_and_defend: bool,
    pub enchant_all_defends: Option<String>,
}

fn cards(xs: &[&str]) -> Vec<String> {
    xs.iter().map(|a| format!("CARD.{a}")).collect()
}

fn sel(m: SelectMeaning, adds: &[&str]) -> DeckEffect {
    DeckEffect { select: m, adds: cards(adds), ..Default::default() }
}

fn add(xs: &[&str]) -> DeckEffect {
    DeckEffect { adds: cards(xs), ..Default::default() }
}

fn ench(e: Option<&str>) -> DeckEffect {
    DeckEffect { select: SelectMeaning::Enchant, enchantment: e.map(|e| format!("ENCHANTMENT.{e}")), ..Default::default() }
}

fn upgrades(n: i32, kind: Option<&str>) -> DeckEffect {
    DeckEffect { random_upgrades: n, random_upgrade_type: kind.map(str::to_string), ..Default::default() }
}

fn event_transform(call: i32) -> DeckEffect {
    DeckEffect { select: SelectMeaning::Transform, transform_rng_call: Some(call), ..Default::default() }
}

fn decipher(k: i32) -> DeckEffect {
    DeckEffect { random_upgrades: 1, pick: UpgradePick::EventNextItem, event_rng_call: k - 1, ..Default::default() }
}

fn unknown(n: i32) -> DeckEffect {
    DeckEffect { unknown_adds: n, ..Default::default() }
}

use SelectMeaning as S;

fn event(key: &str) -> Option<DeckEffect> {
    Some(match key {
        "AMALGAMATOR.pages.INITIAL.options.COMBINE_STRIKES" => sel(S::Remove, &["ULTIMATE_STRIKE"]),
        "AMALGAMATOR.pages.INITIAL.options.COMBINE_DEFENDS" => sel(S::Remove, &["ULTIMATE_DEFEND"]),
        "AROMA_OF_CHAOS.pages.INITIAL.options.LET_GO" => event_transform(0),
        "AROMA_OF_CHAOS.pages.INITIAL.options.MAINTAIN_CONTROL" => sel(S::Upgrade, &[]),
        "BATTLEWORN_DUMMY.pages.INITIAL.options.SETTING_2" => upgrades(2, None),
        "BRAIN_LEECH.pages.INITIAL.options.SHARE_KNOWLEDGE" => sel(S::AddChosen, &[]),
        "BYRDONIS_NEST.pages.INITIAL.options.TAKE" => add(&["BYRDONIS_EGG"]),
        "CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN" => add(&["DEBT"]),
        "DOORS_OF_LIGHT_AND_DARK.pages.INITIAL.options.LIGHT" => DeckEffect { random_upgrades: 2, pick: UpgradePick::EventShuffle, ..Default::default() },
        "DOORS_OF_LIGHT_AND_DARK.pages.INITIAL.options.DARK" => sel(S::Remove, &[]),
        "ENDLESS_CONVEYOR.pages.INITIAL.options.OBSERVE_CHEF" => upgrades(1, None),
        "ENDLESS_CONVEYOR.pages.ALL.options.JELLY_LIVER" => sel(S::Transform, &[]),
        "ENDLESS_CONVEYOR.pages.ALL.options.SPICY_SNAPPY" => upgrades(1, None),
        "ENDLESS_CONVEYOR.pages.ALL.options.SEAPUNK_SALAD" => add(&["FEEDING_FRENZY"]),
        "ENDLESS_CONVEYOR.pages.ALL.options.FRIED_EEL" => unknown(1),
        "FIELD_OF_MAN_SIZED_HOLES.pages.INITIAL.options.RESIST" => sel(S::Remove, &["NORMALITY"]),
        "FIELD_OF_MAN_SIZED_HOLES.pages.INITIAL.options.ENTER_YOUR_HOLE" => ench(Some("PERFECT_FIT")),
        "GRAVE_OF_THE_FORGOTTEN.pages.INITIAL.options.CONFRONT" => DeckEffect { adds: cards(&["DECAY"]), ..ench(Some("SOULS_POWER")) },
        "INFESTED_AUTOMATON.pages.INITIAL.options.STUDY" => unknown(1),
        "INFESTED_AUTOMATON.pages.INITIAL.options.TOUCH_CORE" => unknown(1),
        "LOST_WISP.pages.INITIAL.options.CLAIM" => add(&["DECAY"]),
        "LUMINOUS_CHOIR.pages.INITIAL.options.REACH_INTO_THE_FLESH" => sel(S::Remove, &["SPORE_MIND"]),
        "MORPHIC_GROVE.pages.INITIAL.options.GROUP" => event_transform(0),
        "PUNCH_OFF.pages.INITIAL.options.NAB" => add(&["INJURY"]),
        "REFLECTIONS.pages.INITIAL.options.SHATTER" => add(&["BAD_LUCK"]),
        "ROOM_FULL_OF_CHEESE.pages.INITIAL.options.GORGE" => sel(S::AddChosen, &[]),
        "SAPPHIRE_SEED.pages.INITIAL.options.EAT" => sel(S::Upgrade, &[]),
        "SAPPHIRE_SEED.pages.INITIAL.options.PLANT" => ench(Some("SOWN")),
        "SELF_HELP_BOOK.pages.INITIAL.options.READ_THE_BACK" | "SELF_HELP_BOOK.pages.INITIAL.options.READ_PASSAGE" | "SELF_HELP_BOOK.pages.INITIAL.options.READ_ENTIRE_BOOK" => ench(None),
        "SLIPPERY_BRIDGE.pages.INITIAL.options.OVERCOME" => DeckEffect { random_removes: 1, ..Default::default() },
        "SPIRALING_WHIRLPOOL.pages.INITIAL.options.OBSERVE" => ench(Some("SPIRAL")),
        "SPIRIT_GRAFTER.pages.INITIAL.options.LET_IT_IN" => add(&["METAMORPHOSIS"]),
        "SPIRIT_GRAFTER.pages.INITIAL.options.REJECTION" => sel(S::Upgrade, &[]),
        "STONE_OF_ALL_TIME.pages.INITIAL.options.PUSH" => ench(None),
        "SUNKEN_TREASURY.pages.INITIAL.options.SECOND_CHEST" => add(&["GREED"]),
        "SYMBIOTE.pages.INITIAL.options.APPROACH" => ench(Some("CORRUPTED")),
        "SYMBIOTE.pages.INITIAL.options.KILL_WITH_FIRE" => event_transform(0),
        "TABLET_OF_TRUTH.pages.INITIAL.options.DECIPHER_1" => decipher(1),
        "TABLET_OF_TRUTH.pages.DECIPHER_1.options.DECIPHER" => decipher(2),
        "TABLET_OF_TRUTH.pages.DECIPHER_2.options.DECIPHER" => decipher(3),
        "TABLET_OF_TRUTH.pages.DECIPHER_3.options.DECIPHER" => decipher(4),
        "TABLET_OF_TRUTH.pages.DECIPHER_4.options.DECIPHER" => DeckEffect { upgrade_all: true, ..Default::default() },
        "THE_LEGENDS_WERE_TRUE.pages.INITIAL.options.NAB_THE_MAP" => add(&["SPOILS_MAP"]),
        "THIS_OR_THAT.pages.INITIAL.options.ORNATE" => add(&["CLUMSY"]),
        "TRASH_HEAP.pages.INITIAL.options.GRAB" => unknown(1),
        "TRIAL.pages.MERCHANT.options.GUILTY" => add(&["REGRET"]),
        "TRIAL.pages.MERCHANT.options.INNOCENT" => sel(S::Upgrade, &["SHAME"]),
        "TRIAL.pages.NOBLE.options.INNOCENT" => add(&["REGRET"]),
        "TRIAL.pages.NONDESCRIPT.options.GUILTY" => add(&["DOUBT"]),
        "TRIAL.pages.NONDESCRIPT.options.INNOCENT" => sel(S::Transform, &["DOUBT"]),
        "UNREST_SITE.pages.INITIAL.options.REST" => add(&["POOR_SLEEP"]),
        "WAR_HISTORIAN_REPY.pages.INITIAL.options.UNLOCK_CAGE" | "WAR_HISTORIAN_REPY.pages.INITIAL.options.UNLOCK_CHEST" => {
            DeckEffect { removes: cards(&["LANTERN_KEY"]), ..Default::default() }
        }
        "WATERLOGGED_SCRIPTORIUM.pages.INITIAL.options.TENTACLE_QUILL" | "WATERLOGGED_SCRIPTORIUM.pages.INITIAL.options.PRICKLY_SPONGE" => ench(Some("STEADY")),
        "WELLSPRING.pages.INITIAL.options.BATHE" => sel(S::Remove, &["GUILTY"]),
        "WELCOME_TO_WONGOS.pages.INITIAL.options.BARGAIN_BIN" => upgrades(2, None),
        "WHISPERING_HOLLOW.pages.INITIAL.options.HUG" => event_transform(1),
        "WOOD_CARVINGS.pages.INITIAL.options.BIRD" => DeckEffect { select: S::Transform, transform_to: Some("CARD.PECK".into()), ..Default::default() },
        "WOOD_CARVINGS.pages.INITIAL.options.SNAKE" => ench(Some("SLITHER")),
        "WOOD_CARVINGS.pages.INITIAL.options.TORUS" => DeckEffect { select: S::Transform, transform_to: Some("CARD.TORIC_TOUGHNESS".into()), ..Default::default() },
        "ZEN_WEAVER.pages.INITIAL.options.BREATHING_TECHNIQUES" => add(&["ENLIGHTENMENT", "ENLIGHTENMENT"]),
        "ZEN_WEAVER.pages.INITIAL.options.EMOTIONAL_AWARENESS" | "ZEN_WEAVER.pages.INITIAL.options.ARACHNID_ACUPUNCTURE" => sel(S::Remove, &[]),
        _ => return None,
    })
}

fn event_v0113(key: &str) -> Option<DeckEffect> {
    match key {
        "DOORS_OF_LIGHT_AND_DARK.pages.INITIAL.options.LIGHT" => Some(sel(S::Upgrade, &[])),
        "WUPPY_IN_TROUBLE.pages.INITIAL.options.HELP_IMMEDIATELY" => Some(add(&["INJURY"])),
        _ => None,
    }
}

fn relic_v0107(id: &str) -> Option<DeckEffect> {
    match id {
        "RELIC.SERE_TALON" => Some(DeckEffect { unknown_adds: 2, ..add(&["WISH", "WISH", "WISH"]) }),
        _ => None,
    }
}

fn relic_v0111(id: &str) -> Option<DeckEffect> {
    Some(match id {
        "RELIC.DISTINGUISHED_CAPE" => DeckEffect { unknown_adds: 2, ..add(&["APPARITION", "APPARITION", "APPARITION"]) },
        "RELIC.SERE_TALON" => add(&["WISH", "WISH", "WISH"]),
        "RELIC.DOWSING_ROD" => add(&["DOWSING"]),
        "RELIC.NEOWS_SACRIFICE" => add(&["GUILTY"]),
        _ => return None,
    })
}

fn relic_any(id: &str) -> Option<DeckEffect> {
    Some(match id {
        "RELIC.ASTROLABE" | "RELIC.NEW_LEAF" => sel(S::Transform, &[]),
        "RELIC.ARCANE_SCROLL" | "RELIC.NEOWS_BONES" => unknown(1),
        "RELIC.ARCHAIC_TOOTH" => DeckEffect { transcend: true, ..Default::default() },
        "RELIC.BEAUTIFUL_BRACELET" => ench(Some("SWIFT")),
        "RELIC.BIIIG_HUG" | "RELIC.EMPTY_CAGE" | "RELIC.PAELS_TOOTH" | "RELIC.PRECARIOUS_SHEARS" | "RELIC.PRECISE_SCISSORS" => sel(S::Remove, &[]),
        "RELIC.BLOOD_SOAKED_ROSE" => add(&["ENTHRALLED"]),
        "RELIC.CALLING_BELL" => add(&["CURSE_OF_THE_BELL"]),
        "RELIC.CURSED_PEARL" => add(&["GREED"]),
        "RELIC.DISTINGUISHED_CAPE" => add(&["APPARITION", "APPARITION", "APPARITION"]),
        "RELIC.DOLLYS_MIRROR" => sel(S::Duplicate, &[]),
        "RELIC.ELECTRIC_SHRYMP" | "RELIC.GNARLED_HAMMER" | "RELIC.KIFUDA" | "RELIC.PUNCH_DAGGER" => ench(None),
        "RELIC.FRAGRANT_MUSHROOM" => upgrades(2, None),
        "RELIC.HEFTY_TABLET" => sel(S::AddChosen, &["INJURY"]),
        "RELIC.JEWELRY_BOX" => add(&["APOTHEOSIS"]),
        "RELIC.LARGE_CAPSULE" => DeckEffect { add_basic_strike_and_defend: true, ..Default::default() },
        "RELIC.LEAD_PAPERWEIGHT" | "RELIC.MASSIVE_SCROLL" | "RELIC.SCROLL_BOXES" | "RELIC.SEA_GLASS" => sel(S::AddChosen, &[]),
        "RELIC.LEAFY_POULTICE" => DeckEffect { transform_one_strike_and_defend: true, ..Default::default() },
        "RELIC.NEOWS_TALISMAN" => DeckEffect { upgrade_basic_strike_and_defend: true, ..Default::default() },
        "RELIC.NEOWS_TORMENT" => add(&["NEOWS_FURY"]),
        "RELIC.PAELS_CLAW" => DeckEffect { enchant_all_defends: Some("ENCHANTMENT.GOOPY".into()), ..Default::default() },
        "RELIC.PAELS_GROWTH" => ench(Some("CLONE")),
        "RELIC.PAELS_HORN" => add(&["RELAX", "RELAX"]),
        "RELIC.PANDORAS_BOX" => DeckEffect { transform_all_basics: true, ..Default::default() },
        "RELIC.POMANDER" | "RELIC.YUMMY_COOKIE" => sel(S::Upgrade, &[]),
        "RELIC.PRESERVED_FOG" => sel(S::Remove, &["FOLLY"]),
        "RELIC.ROYAL_STAMP" => ench(Some("ROYALLY_APPROVED")),
        "RELIC.SAND_CASTLE" => upgrades(6, None),
        "RELIC.STORYBOOK" => add(&["BRIGHTEST_FLAME"]),
        "RELIC.TANXS_WHISTLE" => add(&["WHISTLE"]),
        "RELIC.TRI_BOOMERANG" => ench(Some("INSTINCT")),
        "RELIC.WAR_PAINT" => upgrades(2, Some("Skill")),
        "RELIC.WHETSTONE" => upgrades(2, Some("Attack")),
        _ => return None,
    })
}

/// Archaic Tooth: starter card -> its transcended card.
pub fn transcendence(id: &str) -> Option<&'static str> {
    Some(match id {
        "CARD.BASH" => "CARD.BREAK",
        "CARD.NEUTRALIZE" => "CARD.SUPPRESS",
        "CARD.UNLEASH" => "CARD.PROTECTOR",
        "CARD.FALLING_STAR" => "CARD.METEOR_SHOWER",
        "CARD.DUALCAST" => "CARD.QUADCAST",
        "CARD.FOOLISH_SHOT" => "CARD.DIVINE_SHOT",
        _ => return None,
    })
}

pub fn for_choice(_event_id: &str, choice_key: &str, v: GameDataVersion) -> Option<DeckEffect> {
    if v == GameDataVersion::Latest
        && let Some(e) = event_v0113(choice_key)
    {
        return Some(e);
    }
    if let Some(e) = event(choice_key) {
        return Some(e);
    }
    // Ancients: "NEOW.pages.INITIAL.options.PRECARIOUS_SHEARS" -> RELIC.PRECARIOUS_SHEARS.
    let dot = choice_key.rfind('.')?;
    for_relic(&format!("RELIC.{}", &choice_key[dot + 1..]), v.scheme())
}

pub fn for_relic(relic_id: &str, scheme: RngScheme) -> Option<DeckEffect> {
    let first = if scheme == RngScheme::Legacy32 { relic_v0107(relic_id) } else { relic_v0111(relic_id) };
    first.or_else(|| relic_any(relic_id))
}

const ANCIENTS: [&str; 8] = ["EVENT.NEOW", "EVENT.DARV", "EVENT.NONUPEIPE", "EVENT.OROBAS", "EVENT.PAEL", "EVENT.TANX", "EVENT.TEZCATARA", "EVENT.VAKUU"];

pub fn is_ancient(event_id: &str) -> bool {
    ANCIENTS.contains(&event_id)
}

pub fn is_basic_strike(id: &str) -> bool {
    id.starts_with("CARD.STRIKE_")
}

pub fn is_basic_defend(id: &str) -> bool {
    id.starts_with("CARD.DEFEND_")
}

pub fn has_defend_tag(id: &str) -> bool {
    is_basic_defend(id) || id == "CARD.ULTIMATE_DEFEND"
}
