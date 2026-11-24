//! The `decide` requests (`ADVISOR_PROTOCOL.md` §2): what is on screen, as the helper reads it. Each request keeps
//! what its candidate keys point at ([`Shown`]), so an answer can be drawn on the right card, button or map point.

use ov_core::map::{MapCoord, MapGraph};
use ov_core::model::OfferContext;
use ov_vision::rest::{RestOption, RestSiteResult};
use serde_json::{Value, json};

use crate::recognition::{Recognition, SeenCard, SeenShopItem};

/// What a request showed, in the order of its arrays, so `card:i`, `shop_relic:i`, `opt:i` ... can be resolved.
#[derive(Clone, Debug, PartialEq)]
pub enum Shown {
    Cards(Vec<SeenCard>),
    Shop { cards: Vec<SeenCard>, relics: Vec<SeenShopItem>, potions: Vec<SeenShopItem>, removal: bool },
    Rest(Vec<RestOption>),
    Path(Vec<MapCoord>),
    /// Event / ancient option keys.
    Options(Vec<String>),
}

/// One request before it gets an id.
#[derive(Clone, Debug, PartialEq)]
pub struct DecideRequest {
    pub kind: &'static str,
    pub screen: Value,
    pub run: Option<Value>,
    pub shown: Shown,
}

impl DecideRequest {
    /// The stdin line (compact JSON: the helper looks for `"type":"decide"` without spaces).
    pub fn line(&self, id: i64) -> String {
        let mut o = serde_json::Map::new();
        o.insert("v".into(), json!(2));
        o.insert("type".into(), json!("decide"));
        o.insert("id".into(), json!(id));
        o.insert("kind".into(), json!(self.kind));
        o.insert("screen".into(), self.screen.clone());
        if let Some(r) = &self.run {
            o.insert("run".into(), r.clone());
        }
        Value::Object(o).to_string()
    }

    /// A key of the answer (`card:1`, `shop_potion:0`, `opt:2/deck:5`, `remove:3`, ...) as the thing it points at.
    pub fn target(&self, key: &str) -> Target {
        let (head, rest) = key.split_once('/').map_or((key, None), |(h, r)| (h, Some(r)));
        let deck = rest.and_then(|r| r.strip_prefix("deck:")).and_then(|d| d.parse().ok());
        let (name, index) = match head.split_once(':') {
            Some((n, i)) => (n, i.parse::<usize>().ok()),
            None => (head, None),
        };
        match (&self.shown, name, index) {
            (_, "skip", _) => Target::Skip,
            (_, "leave", _) => Target::Leave,
            (Shown::Cards(c), "card", Some(i)) if i < c.len() => Target::Card(i),
            (Shown::Shop { cards, .. }, "shop_card", Some(i)) if i < cards.len() => Target::ShopCard(i),
            (Shown::Shop { relics, .. }, "shop_relic", Some(i)) if i < relics.len() => Target::ShopRelic(i),
            (Shown::Shop { potions, .. }, "shop_potion", Some(i)) if i < potions.len() => Target::ShopPotion(i),
            (Shown::Shop { .. }, "remove", i) => Target::Removal(i),
            (Shown::Rest(o), "opt", Some(i)) if i < o.len() => Target::Rest(o[i], deck),
            (Shown::Path(p), "opt", Some(i)) if i < p.len() => Target::Path(p[i]),
            (Shown::Options(o), "opt", Some(i)) if i < o.len() => Target::Option(i),
            (_, "alt", Some(i)) => Target::Alt(i),
            (_, "deck", Some(i)) => Target::Deck(i),
            _ => Target::Unknown,
        }
    }
}

/// What a candidate key points at.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Target {
    Card(usize),
    Skip,
    ShopCard(usize),
    ShopRelic(usize),
    ShopPotion(usize),
    /// Card removal (with the deck index of the card to remove when given).
    Removal(Option<usize>),
    Leave,
    /// A rest-site button (smith: with the deck index of the card).
    Rest(RestOption, Option<usize>),
    Path(MapCoord),
    Option(usize),
    Alt(usize),
    Deck(usize),
    Unknown,
}

/// `CARD.X` / `X` -> `X`.
pub fn entry(id: &str) -> &str {
    id.split_once('.').map_or(id, |(_, e)| e)
}

fn card_json(c: &SeenCard) -> Value {
    json!({"id": entry(&c.id), "upgrade": c.upgrade})
}

/// The `run` overrides: HP and gold the log knows better than the save (the save is written on room entry, before
/// a fight's rewards and before shop purchases). `None` when the tracker's values are not reliable.
pub fn run_overrides(hp: Option<i32>, gold: Option<i32>) -> Option<Value> {
    (hp.is_some() || gold.is_some()).then(|| json!({"hp": hp, "gold": gold}))
}

pub fn context_name(c: OfferContext) -> &'static str {
    match c {
        OfferContext::EliteReward => "elite",
        OfferContext::BossReward => "boss",
        OfferContext::Event => "event",
        OfferContext::CardReward | OfferContext::Shop => "reward",
    }
}

/// A card reward row (`kind: card`).
pub fn card_reward(r: &Recognition, context: OfferContext, run: Option<Value>) -> DecideRequest {
    let mut cards = r.cards.clone();
    cards.sort_by_key(|c| c.slot);
    DecideRequest {
        kind: "card",
        screen: json!({"context": context_name(context), "cards": cards.iter().map(card_json).collect::<Vec<_>>(), "skip": true}),
        run,
        shown: Shown::Cards(cards),
    }
}

/// The shop shelf (`kind: shop`). Only read items are sent (in shelf order); keys index these lists. The removal
/// price is left to the helper (the game's formula from the run).
pub fn shop(r: &Recognition, run: Option<Value>) -> DecideRequest {
    let mut cards = r.cards.clone();
    cards.sort_by_key(|c| c.slot);
    let mut relics = r.relics.clone();
    relics.sort_by_key(|x| x.slot);
    let mut potions = r.potions.clone();
    potions.sort_by_key(|x| x.slot);
    let removal = r.removal_service.is_some();
    let item = |x: &SeenShopItem| json!({"id": entry(&x.id), "price": x.price});
    DecideRequest {
        kind: "shop",
        screen: json!({
            "cards": cards.iter().map(|c| json!({"id": entry(&c.id), "upgrade": c.upgrade, "price": c.price, "sale": c.on_sale})).collect::<Vec<_>>(),
            "relics": relics.iter().map(item).collect::<Vec<_>>(),
            "potions": potions.iter().map(item).collect::<Vec<_>>(),
            "removal": {"available": removal},
        }),
        run,
        shown: Shop { cards, relics, potions, removal },
    }
}
use Shown::Shop;

/// The simulator's option id of a rest-site button.
pub fn rest_option_id(o: RestOption) -> Option<&'static str> {
    Some(match o {
        RestOption::Heal => "heal",
        RestOption::Smith => "smith",
        RestOption::Mend => "mend",
        RestOption::Lift => "lift",
        RestOption::Dig => "dig",
        RestOption::Cook => "cook",
        RestOption::Clone => "clone",
        RestOption::Hatch => "hatch",
        RestOption::Kindle => "kindle",
        RestOption::Toke => "toke",
        RestOption::Unknown => return None,
    })
}

/// The rest-site buttons (`kind: rest`), in button order. None when a button was not identified.
pub fn rest(site: &RestSiteResult) -> Option<DecideRequest> {
    if !site.choice_screen || site.buttons.is_empty() {
        return None;
    }
    let mut buttons = site.buttons.clone();
    buttons.sort_by_key(|b| b.index);
    let ids: Option<Vec<&str>> = buttons.iter().map(|b| rest_option_id(b.option)).collect();
    let ids = ids?;
    Some(DecideRequest { kind: "rest", screen: json!({"options": ids}), run: None, shown: Shown::Rest(buttons.iter().map(|b| b.option).collect()) })
}

/// The next map points (`kind: path`): the children of the current point, or the first row before the first step.
pub fn path(map: &MapGraph) -> Option<DecideRequest> {
    let mut next: Vec<MapCoord> = match map.current {
        Some(c) => map.nodes.get(&c)?.children.clone(),
        None => map.nodes.get(&map.start).map(|n| n.children.clone()).unwrap_or_default(),
    };
    if next.is_empty() {
        return None;
    }
    next.sort_by_key(|c| (c.row, c.col));
    let from = map.current.unwrap_or(map.start);
    Some(DecideRequest {
        kind: "path",
        screen: json!({"from": {"col": from.col, "row": from.row}, "next": next.iter().map(|c| json!({"col": c.col, "row": c.row})).collect::<Vec<_>>(), "vote": null}),
        run: None,
        shown: Shown::Path(next),
    })
}

/// Event or ancient buttons (`kind: event` / `ancient`): the option keys the tracker knows, `locked` per key when read.
pub fn event(event_id: &str, ancient: bool, keys: &[String], locked: &dyn Fn(usize) -> bool) -> DecideRequest {
    let options: Vec<Value> = keys.iter().enumerate().map(|(i, k)| json!({"id": k, "locked": locked(i)})).collect();
    let (kind, field) = if ancient { ("ancient", "ancient") } else { ("event", "event") };
    DecideRequest { kind, screen: json!({field: entry(event_id), "page": null, "options": options}), run: None, shown: Shown::Options(keys.to_vec()) }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::recognition::{Rect, ScreenKind};

    fn card(slot: i32, id: &str, up: i32, price: Option<i32>) -> SeenCard {
        SeenCard { slot, id: id.into(), upgrade: up, bounds: Rect::default(), confidence: 1.0, price, unaffordable: None, on_sale: slot == 2, price_bounds: None, missing_portrait: false }
    }

    fn relic(slot: i32, id: &str, price: Option<i32>) -> SeenShopItem {
        SeenShopItem { id: id.into(), rarity: None, slot, bounds: Rect::default(), confidence: 1.0, price, unaffordable: None }
    }

    #[test]
    fn card_and_shop_requests_and_their_keys() {
        let r = Recognition { kind: ScreenKind::Reward, cards: vec![card(1, "CARD.ZAP", 1, None), card(0, "CARD.BALL_LIGHTNING", 0, None)], ..Recognition::other() };
        let q = card_reward(&r, OfferContext::EliteReward, run_overrides(Some(40), None));
        assert_eq!(
            q.line(17),
            r#"{"v":2,"type":"decide","id":17,"kind":"card","screen":{"context":"elite","cards":[{"id":"BALL_LIGHTNING","upgrade":0},{"id":"ZAP","upgrade":1}],"skip":true},"run":{"hp":40,"gold":null}}"#
        );
        assert_eq!(q.target("card:1"), Target::Card(1));
        assert_eq!(q.target("card:2"), Target::Unknown);
        assert_eq!(q.target("skip"), Target::Skip);

        let s = Recognition {
            kind: ScreenKind::Shop,
            cards: vec![card(2, "CARD.ZAP", 0, Some(37)), card(5, "CARD.FINESSE", 0, None)],
            relics: vec![relic(1, "RELIC.ANCHOR", Some(150))],
            potions: vec![relic(0, "POTION.FIRE_POTION", Some(50))],
            removal_service: Some(Rect::default()),
            ..Recognition::other()
        };
        let q = shop(&s, None);
        assert_eq!(
            q.line(3),
            r#"{"v":2,"type":"decide","id":3,"kind":"shop","screen":{"cards":[{"id":"ZAP","upgrade":0,"price":37,"sale":true},{"id":"FINESSE","upgrade":0,"price":null,"sale":false}],"relics":[{"id":"ANCHOR","price":150}],"potions":[{"id":"FIRE_POTION","price":50}],"removal":{"available":true}}}"#
        );
        assert_eq!(q.target("shop_card:1"), Target::ShopCard(1));
        assert_eq!(q.target("shop_relic:0"), Target::ShopRelic(0));
        assert_eq!(q.target("shop_potion:0"), Target::ShopPotion(0));
        assert_eq!(q.target("remove:4"), Target::Removal(Some(4)));
        assert_eq!(q.target("leave"), Target::Leave);
    }

    #[test]
    fn path_and_event_requests() {
        use ov_core::map::{MapNode, MapNodeType};
        let mut map = MapGraph {
            act_id: "ACT".into(),
            act: 1,
            width: 7,
            height: 15,
            is_blitz: false,
            centered_rows: true,
            nodes: Default::default(),
            start: MapCoord::new(3, 0),
            boss: MapCoord::new(3, 16),
            second_boss: None,
            current: None,
            visited: Vec::new(),
            normal_fights_done: None,
            easy_fights: None,
        };
        let a = MapCoord::new(3, 5);
        map.nodes.insert(a, MapNode { coord: a, kind: MapNodeType::Monster, children: vec![MapCoord::new(4, 6), MapCoord::new(2, 6)] });
        map.current = Some(a);
        let q = path(&map).unwrap();
        assert_eq!(q.screen.to_string(), r#"{"from":{"col":3,"row":5},"next":[{"col":2,"row":6},{"col":4,"row":6}],"vote":null}"#);
        assert_eq!(q.target("opt:1"), Target::Path(MapCoord::new(4, 6)));

        let keys = vec!["A".to_string(), "B".to_string()];
        let q = event("EVENT.NEOW", true, &keys, &|i| i == 1);
        assert_eq!(q.line(1), r#"{"v":2,"type":"decide","id":1,"kind":"ancient","screen":{"ancient":"NEOW","page":null,"options":[{"id":"A","locked":false},{"id":"B","locked":true}]}}"#);
        assert_eq!(q.target("opt:1"), Target::Option(1));
    }
}
