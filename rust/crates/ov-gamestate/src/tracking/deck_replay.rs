//! A deck rebuilt step by step from log patches (`DeckReplay`).

use std::collections::{HashMap, HashSet};

use ov_core::model::{CardRef, DeckCard};

use super::data::{GameDataVersion, cards};
use super::deck_effects;
use super::rng::GameRng;
use super::tracked::{UncertainCard, UncertainReason};

/// One play seen in a combat.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CombatPlay {
    pub owner: u64,
    pub card_id: String,
    pub index: i32,
}

/// Every card play logged during one fight.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct CombatObservation {
    pub plays: Vec<CombatPlay>,
    pub after_merchant: bool,
}

#[derive(Clone, Debug)]
struct Slot {
    uid: u32,
    card: DeckCard,
    reason: Option<UncertainReason>,
    from_observation: bool,
    combats: i32,
    peers: Option<Vec<u32>>,
}

const GUILTY: &str = "CARD.GUILTY";
const GUILTY_COMBATS: i32 = 5;
const MAX_ORDERS: usize = 4096;

type Identity = (String, i32, Option<String>);

fn identity(s: &Slot) -> Identity {
    (s.card.card.id.clone(), s.card.card.upgrade, s.card.enchantment_id.clone())
}

fn upgrade_slot(s: &mut Slot) {
    s.card.card.upgrade += 1;
}

fn doubt(s: &Slot) -> i32 {
    match s.reason {
        None => 0,
        Some(UncertainReason::UnknownCardSelect) => 2,
        Some(_) => 1,
    }
}

#[derive(Clone, Debug)]
pub struct DeckReplay {
    pub duplicate_adds: bool,
    pub upgrade_added_types: HashSet<String>,
    slots: Vec<Slot>,
    next_uid: u32,
    version: GameDataVersion,
    unidentified: i32,
    unknown_upgrades: i32,
    unknown_removals: i32,
}

impl DeckReplay {
    pub fn new(deck: impl IntoIterator<Item = DeckCard>, version: GameDataVersion) -> Self {
        let mut r = Self {
            duplicate_adds: false,
            upgrade_added_types: HashSet::new(),
            slots: vec![],
            next_uid: 0,
            version,
            unidentified: 0,
            unknown_upgrades: 0,
            unknown_removals: 0,
        };
        for c in deck {
            r.push(c, None);
        }
        r
    }

    fn push(&mut self, card: DeckCard, reason: Option<UncertainReason>) {
        self.slots.push(Slot { uid: self.next_uid, card, reason, from_observation: false, combats: 0, peers: None });
        self.next_uid += 1;
    }

    pub fn count(&self) -> usize {
        self.slots.len()
    }
    pub fn unidentified(&self) -> i32 {
        self.unidentified
    }
    pub fn unknown_upgrades(&self) -> i32 {
        self.unknown_upgrades
    }
    pub fn unknown_removals(&self) -> i32 {
        self.unknown_removals
    }

    pub fn cards(&self) -> Vec<DeckCard> {
        self.slots.iter().map(|s| s.card.clone()).collect()
    }

    pub fn uncertain(&self) -> Vec<UncertainCard> {
        self.slots
            .iter()
            .enumerate()
            .filter_map(|(i, s)| s.reason.map(|r| UncertainCard { deck_index: i as i32, card_id: s.card.card.id.clone(), reason: r }))
            .collect()
    }

    pub fn add(&mut self, id: &str, floor: i32, mut reason: Option<UncertainReason>, mut upgrade: i32) {
        let max = cards::max_upgrade(id, self.version);
        if let Some(t) = cards::type_of(id, self.version)
            && self.upgrade_added_types.contains(t)
            && upgrade < max
        {
            upgrade += 1;
        }
        let mut card = DeckCard::new(CardRef::new(id, upgrade));
        card.floor_added = floor;
        if upgrade >= max && reason == Some(UncertainReason::UpgradeRoll) {
            reason = None;
        }
        self.push(card.clone(), reason);
        if self.duplicate_adds {
            self.push(card, reason);
        }
    }

    pub fn is_exact(&self) -> bool {
        self.unidentified == 0 && self.unknown_upgrades == 0 && self.unknown_removals == 0 && self.slots.iter().all(|s| s.reason.is_none())
    }

    pub fn upgrade_all(&mut self) {
        let v = self.version;
        for s in &mut self.slots {
            if s.card.card.upgrade < cards::max_upgrade(&s.card.card.id, v) {
                upgrade_slot(s);
            }
        }
        self.unknown_upgrades = 0;
    }

    pub fn combat_ended(&mut self) {
        for s in &mut self.slots {
            s.combats += 1;
        }
        self.slots.retain(|s| !(s.card.card.id == GUILTY && s.combats >= GUILTY_COMBATS));
    }

    /// Removes the plainest copy. False if the card was not known (it still left the deck).
    pub fn remove(&mut self, id: &str) -> bool {
        match self.find(id, true, false) {
            Some(i) => {
                self.slots.remove(i);
                true
            }
            None => {
                self.identify();
                false
            }
        }
    }

    pub fn upgrade(&mut self, id: &str, floor: i32) {
        let i = self.require(id, floor, false, false);
        upgrade_slot(&mut self.slots[i]);
    }

    pub fn enchant(&mut self, id: &str, enchantment: Option<&str>, floor: i32) {
        let i = self.require(id, floor, true, true);
        let peers = self.peers_of(i);
        if let Some(e) = enchantment {
            self.slots[i].card.enchantment_id = Some(e.to_string());
            self.slots[i].card.enchantment_amount = 1;
        }
        if !peers.is_empty() {
            self.slots[i].peers.get_or_insert_with(Vec::new).extend(peers);
        }
    }

    pub fn duplicate(&mut self, id: &str, floor: i32) {
        let i = self.require(id, floor, false, false);
        let (card, reason) = (self.slots[i].card.clone(), self.slots[i].reason);
        self.push(card, reason);
    }

    /// The chosen card leaves; a fixed target joins at the end, or an unidentified card.
    pub fn transform(&mut self, id: &str, target: Option<&str>, floor: i32) {
        self.remove(id);
        match target {
            Some(t) => self.add(t, floor, None, 0),
            None => self.unidentified += 1,
        }
    }

    pub fn named(&mut self, id: &str, floor: i32) {
        self.require(id, floor, false, false);
    }

    pub fn mark_unknown(&mut self, id: &str, floor: i32) {
        let i = self.require(id, floor, false, false);
        self.slots[i].reason.get_or_insert(UncertainReason::UnknownCardSelect);
    }

    fn upgradable(&self, s: &Slot) -> bool {
        s.card.card.upgrade < cards::max_upgrade(&s.card.card.id, self.version)
    }

    fn candidates(&self, ty: Option<&str>) -> Vec<usize> {
        (0..self.slots.len())
            .filter(|&i| {
                let s = &self.slots[i];
                self.upgradable(s) && ty.is_none_or(|t| cards::type_of(&s.card.card.id, self.version) == Some(t))
            })
            .collect()
    }

    pub fn random_upgrades(&mut self, n: i32, ty: Option<&str>) {
        if self.is_exact() {
            let c = self.candidates(ty);
            if c.len() as i64 <= n as i64 {
                for i in c {
                    upgrade_slot(&mut self.slots[i]);
                }
                return;
            }
            let distinct: HashSet<Identity> = c.iter().map(|&i| identity(&self.slots[i])).collect();
            if distinct.len() == 1 {
                for &i in c.iter().take(n.max(0) as usize) {
                    upgrade_slot(&mut self.slots[i]);
                }
                return;
            }
        }
        self.unknown_upgrades += n;
    }

    /// `cards.Where(IsUpgradable).StableShuffle(rng).Take(n)`: sort by (id, upgrade), then Fisher-Yates.
    pub fn shuffle_upgrades(&mut self, rng: &mut GameRng, n: i32, ty: Option<&str>) {
        let c = self.candidates(ty);
        let mut groups: HashMap<(&str, i32), HashSet<Option<&str>>> = HashMap::new();
        for &i in &c {
            let s = &self.slots[i];
            groups.entry((s.card.card.id.as_str(), s.card.card.upgrade)).or_default().insert(s.card.enchantment_id.as_deref());
        }
        let mixed_ties = groups.values().any(|g| g.len() > 1);
        if !self.is_exact() || mixed_ties {
            self.random_upgrades(n, ty);
            return;
        }
        let mut sorted = c;
        sorted.sort_by(|&a, &b| {
            let (x, y) = (&self.slots[a].card.card, &self.slots[b].card.card);
            x.id.cmp(&y.id).then(x.upgrade.cmp(&y.upgrade))
        });
        rng.shuffle(&mut sorted, false);
        for &i in sorted.iter().take(n.max(0) as usize) {
            upgrade_slot(&mut self.slots[i]);
        }
    }

    /// `rng.NextItem(upgradable cards in deck order)` gets +1 (Tablet of Truth).
    pub fn pick_upgrade(&mut self, rng: &mut GameRng) {
        if !self.is_exact() || self.slots.iter().any(|s| s.from_observation) {
            self.random_upgrades(1, None);
            return;
        }
        let n = self.candidates(None).len();
        if n == 0 {
            return;
        }
        let k = rng.next_int(n as i32) as usize;
        let Some(orders) = self.orders() else {
            self.random_upgrades(1, None);
            return;
        };
        let by_uid: HashMap<u32, usize> = self.slots.iter().enumerate().map(|(i, s)| (s.uid, i)).collect();
        let mut pick: Option<usize> = None;
        for order in orders {
            let s = order.iter().map(|u| by_uid[u]).filter(|&i| self.upgradable(&self.slots[i])).nth(k).unwrap();
            match pick {
                None => pick = Some(s),
                Some(p) if identity(&self.slots[s]) != identity(&self.slots[p]) => {
                    self.random_upgrades(1, None);
                    return;
                }
                _ => {}
            }
        }
        upgrade_slot(&mut self.slots[pick.unwrap()]);
    }

    /// The deck orders the log allows (as slot uids); None when there are more than `MAX_ORDERS`.
    fn orders(&self) -> Option<Vec<Vec<u32>>> {
        let mut orders: Vec<Vec<u32>> = vec![self.slots.iter().map(|s| s.uid).collect()];
        for f in self.slots.iter().filter(|s| s.peers.as_ref().is_some_and(|p| !p.is_empty())) {
            let mut next = Vec::new();
            for o in &orders {
                next.push(o.clone());
                for p in f.peers.as_ref().unwrap().iter().filter(|p| o.contains(p)) {
                    let mut swapped = o.clone();
                    let a = swapped.iter().position(|x| *x == f.uid).unwrap();
                    let b = swapped.iter().position(|x| x == p).unwrap();
                    swapped.swap(a, b);
                    next.push(swapped);
                }
            }
            if next.len() > MAX_ORDERS {
                return None;
            }
            orders = next;
        }
        Some(orders)
    }

    fn peers_of(&self, i: usize) -> Vec<u32> {
        let me = identity(&self.slots[i]);
        self.slots.iter().enumerate().filter(|(j, o)| *j != i && identity(o) == me).map(|(_, o)| o.uid).collect()
    }

    pub fn unknown_adds(&mut self, n: i32) {
        self.unidentified += n;
    }
    pub fn random_removes(&mut self, n: i32) {
        self.unknown_removals += n;
    }

    /// Neow's Talisman: the last basic Strike and last basic Defend get +1.
    pub fn upgrade_basics(&mut self) {
        for is_basic in [deck_effects::is_basic_strike as fn(&str) -> bool, deck_effects::is_basic_defend] {
            if let Some(i) = self.slots.iter().rposition(|s| is_basic(&s.card.card.id)) {
                upgrade_slot(&mut self.slots[i]);
            }
        }
    }

    /// Pandora's Box: every basic Strike/Defend becomes a random card.
    pub fn transform_all_basics(&mut self) {
        let before = self.slots.len();
        self.slots.retain(|s| !(deck_effects::is_basic_strike(&s.card.card.id) || deck_effects::is_basic_defend(&s.card.card.id)));
        self.unidentified += (before - self.slots.len()) as i32;
    }

    /// Leafy Poultice: the first basic Strike and first basic Defend become random cards.
    pub fn transform_strike_and_defend(&mut self) {
        for is_basic in [deck_effects::is_basic_strike as fn(&str) -> bool, deck_effects::is_basic_defend] {
            if let Some(i) = self.slots.iter().position(|s| is_basic(&s.card.card.id)) {
                self.slots.remove(i);
                self.unidentified += 1;
            }
        }
    }

    /// Archaic Tooth: the starter card becomes its stronger form, keeping its upgrade.
    pub fn transcend(&mut self) {
        if let Some(i) = self.slots.iter().position(|s| deck_effects::transcendence(&s.card.card.id).is_some()) {
            let t = deck_effects::transcendence(&self.slots[i].card.card.id).unwrap();
            self.slots[i].card.card.id = t.to_string();
        }
    }

    /// Rest-site CLONE: every card with the Clone enchantment is copied.
    pub fn clone_enchanted(&mut self) {
        let copies: Vec<(DeckCard, Option<UncertainReason>)> = self
            .slots
            .iter()
            .filter(|s| s.card.enchantment_id.as_deref() == Some("ENCHANTMENT.CLONE"))
            .map(|s| (s.card.clone(), s.reason))
            .collect();
        for (c, r) in copies {
            self.push(c, r);
        }
    }

    pub fn enchant_all(&mut self, accepts: impl Fn(&str) -> bool, enchantment: &str) {
        for s in &mut self.slots {
            if s.card.enchantment_id.is_none() && accepts(&s.card.card.id) {
                s.card.enchantment_id = Some(enchantment.to_string());
                s.card.enchantment_amount = 1;
            }
        }
    }

    /// Uses one fight's card plays as a partial deck listing (see the C# `DeckReplay.Observe`).
    pub fn observe(&mut self, obs: &CombatObservation, order: &[u64], me: u64, floor: i32) {
        let v = self.version;
        let Some(seat) = order.iter().position(|x| *x == me) else { return };
        let mut seat_of: HashMap<u64, usize> = HashMap::new();
        for (i, id) in order.iter().enumerate() {
            seat_of.entry(*id).or_insert(i);
        }
        let mut mine: Vec<&CombatPlay> = Vec::new();
        let mut seen_idx = HashSet::new();
        for p in obs.plays.iter().filter(|p| p.owner == me) {
            if seen_idx.insert(p.index) {
                mine.push(p);
            }
        }
        let made: Vec<i32> = obs.plays.iter().filter(|p| cards::is_combat_only(&p.card_id, v)).map(|p| p.index).collect();
        mine.retain(|p| !cards::is_combat_only(&p.card_id, v));
        if mine.is_empty() {
            return;
        }
        let my_min = mine.iter().map(|p| p.index).min().unwrap();
        let size = self.slots.iter().filter(|s| !s.from_observation).count() as i32 + self.unidentified;
        let seat_is = |p: &CombatPlay, f: &dyn Fn(usize) -> bool| seat_of.get(&p.owner).is_some_and(|s| f(*s));
        let earlier: Vec<i32> = obs.plays.iter().filter(|p| seat_is(p, &|s| s < seat)).map(|p| p.index).collect();
        let later: Vec<i32> = obs.plays.iter().filter(|p| seat_is(p, &|s| s > seat)).map(|p| p.index).collect();

        let mut observed = made.clone();
        observed.extend(&later);
        observed.extend(earlier.iter().filter(|&&i| i > my_min));
        let mut bounds = observed.clone();
        let mut start_low = 0;
        if seat == 0 {
            if later.is_empty() {
                bounds.push(size);
            }
        } else {
            let below: Vec<i32> = earlier.iter().copied().filter(|&i| i < my_min).collect();
            if below.is_empty() && bounds.is_empty() {
                return;
            }
            if !below.is_empty() {
                start_low = below.iter().max().unwrap() + 1;
                bounds.push(start_low + size);
            } else {
                start_low = -1;
            }
        }
        let upper = *bounds.iter().min().unwrap();
        let deck_plays: Vec<&CombatPlay> = mine.iter().copied().filter(|p| p.index < upper).collect();
        if deck_plays.is_empty() {
            return;
        }
        let start = if seat == 0 { 0 } else { start_low };
        let end = deck_plays.iter().map(|p| p.index).max().unwrap() + 1;
        let exact = (start >= 0 && (seat == 0 || my_min == start) && !observed.is_empty() && end == *observed.iter().min().unwrap()).then(|| end - start);
        let mut missing = exact.map_or(0, |n| n - (self.slots.len() as i32 + self.unidentified));

        let mut groups: Vec<(&str, usize)> = Vec::new();
        for p in &deck_plays {
            match groups.iter_mut().find(|(id, _)| *id == p.card_id) {
                Some(g) => g.1 += 1,
                None => groups.push((&p.card_id, 1)),
            }
        }
        for (id, seen) in groups {
            let have: Vec<usize> = (0..self.slots.len()).filter(|&i| self.slots[i].card.card.id == id).collect();
            for &i in have.iter().take(seen) {
                if self.slots[i].reason == Some(UncertainReason::UnknownCardSelect) {
                    self.slots[i].reason = None;
                }
            }
            for _ in have.len()..seen {
                let counted = missing > 0;
                if counted {
                    missing -= 1;
                }
                let identifies = !counted && self.unidentified > 0;
                if !counted {
                    self.identify();
                }
                let reason = if obs.after_merchant && matches!(cards::rarity(id, v), Some("Common" | "Uncommon" | "Rare")) {
                    None
                } else {
                    Some(UncertainReason::Inferred)
                };
                self.add(id, floor, reason, 0);
                if self.duplicate_adds {
                    self.slots.pop();
                }
                self.slots.last_mut().unwrap().from_observation = !counted && !identifies;
            }
        }
        if missing > 0 {
            self.unidentified += missing;
        }
    }

    /// Thieving Hopper stole one of my cards and it never came back.
    pub fn stolen(&mut self, mine_before: &[CombatPlay], mine_after: &[CombatPlay]) {
        fn copies<'a>(plays: impl Iterator<Item = &'a CombatPlay>) -> HashMap<&'a str, usize> {
            let mut by_index: Vec<(i32, &str)> = Vec::new();
            for p in plays {
                if !by_index.iter().any(|(i, _)| *i == p.index) {
                    by_index.push((p.index, &p.card_id));
                }
            }
            let mut m = HashMap::new();
            for (_, id) in by_index {
                *m.entry(id).or_insert(0) += 1;
            }
            m
        }
        let v = self.version;
        let gone = copies(mine_before.iter().filter(|p| cards::leaves_combat_when_played(&p.card_id, v)));
        let stayed = copies(mine_after.iter());
        // Group slots by id in first-seen order; inside a group, lowest upgrade first (stable), skip the played-away copies.
        let mut order: Vec<&str> = Vec::new();
        for s in &self.slots {
            if !order.contains(&s.card.card.id.as_str()) {
                order.push(&s.card.card.id);
            }
        }
        let mut pool: Vec<&Slot> = Vec::new();
        for id in &order {
            let mut g: Vec<&Slot> = self.slots.iter().filter(|s| s.card.card.id == *id).collect();
            g.sort_by_key(|s| s.card.card.upgrade);
            pool.extend(g.into_iter().skip(gone.get(id).copied().unwrap_or(0)));
        }
        let class_of: Vec<(&Slot, i32)> =
            pool.iter().filter_map(|s| cards::steal_priority(&s.card.card.id, s.card.enchantment_id.as_deref(), v).map(|p| (*s, p))).collect();
        if class_of.is_empty() {
            self.unknown_removals += 1;
            return;
        }
        let best = class_of.iter().map(|x| x.1).min().unwrap();
        let mut counts: Vec<(&str, usize)> = Vec::new();
        for (s, p) in &class_of {
            if *p != best {
                continue;
            }
            match counts.iter_mut().find(|(id, _)| *id == s.card.card.id) {
                Some(c) => c.1 += 1,
                None => counts.push((&s.card.card.id, 1)),
            }
        }
        let candidates: Vec<String> =
            counts.into_iter().filter(|(id, n)| *n > stayed.get(id).copied().unwrap_or(0)).map(|(id, _)| id.to_string()).collect();
        if candidates.len() == 1 {
            self.remove(&candidates[0]);
        } else {
            self.unknown_removals += 1;
        }
    }

    fn identify(&mut self) {
        if self.unidentified > 0 {
            self.unidentified -= 1;
        }
    }

    fn require(&mut self, id: &str, floor: i32, plainest: bool, unenchanted: bool) -> usize {
        if let Some(i) = self.find(id, plainest, unenchanted) {
            return i;
        }
        self.identify();
        self.add(id, floor, Some(UncertainReason::Inferred), 0);
        self.slots.len() - 1
    }

    fn find(&self, id: &str, plainest: bool, unenchanted: bool) -> Option<usize> {
        let mut best: Option<usize> = None;
        for i in 0..self.slots.len() {
            let c = &self.slots[i].card;
            if c.card.id != id {
                continue;
            }
            let Some(bi) = best else {
                best = Some(i);
                continue;
            };
            let b = &self.slots[bi].card;
            if unenchanted && b.enchantment_id.is_some() != c.enchantment_id.is_some() {
                if c.enchantment_id.is_none() {
                    best = Some(i);
                }
                continue;
            }
            if c.card.upgrade < b.card.upgrade
                || (plainest && c.card.upgrade == b.card.upgrade && c.enchantment_id.is_none() && b.enchantment_id.is_some())
            {
                best = Some(i);
            } else if c.card.upgrade == b.card.upgrade
                && c.enchantment_id.is_none() == b.enchantment_id.is_none()
                && doubt(&self.slots[i]) < doubt(&self.slots[bi])
            {
                best = Some(i);
            }
        }
        best
    }
}
