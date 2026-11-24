//! Port of VersionGuard.cs: the installed game's version and cards against the data pack.

use std::collections::HashSet;
use std::path::Path;

use ov_core::loc;
use serde_json::Value;

use crate::version::GameVersion;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct GuardCard {
    pub id: String,
    pub pool: Option<String>,
    pub rarity: Option<String>,
}

#[derive(Clone, Debug)]
pub struct VersionGuardResult {
    pub game: Option<GameVersion>,
    pub pack: Option<GameVersion>,
    /// <0 game older than the pack, 0 same, >0 newer; None when either is unknown.
    pub comparison: Option<i32>,
    pub unrated_cards: Vec<String>,
    pub removed_cards: Vec<String>,
}

impl VersionGuardResult {
    pub fn version_mismatch(&self) -> bool {
        self.comparison.is_some_and(|c| c != 0)
    }

    /// Panel lines in the current overlay language; empty when everything matches.
    pub fn panel_lines(&self, default_tier: &str, max_names: usize, card_name: Option<&dyn Fn(&str) -> Option<String>>) -> Vec<String> {
        let mut lines = Vec::new();
        if self.version_mismatch()
            && let (Some(g), Some(p)) = (&self.game, &self.pack)
        {
            let newer = self.comparison.unwrap_or(0) > 0;
            lines.push(loc::tr(
                format!("게임 버전 {} 데이터 팩 {}보다 {}: 티어가 맞지 않을 수 있음", subject(&g.text), p.text, if newer { "새롭습니다" } else { "오래되었습니다" }),
                format!("Game version {} is {} than data pack {}: tiers may be off", g.text, if newer { "newer" } else { "older" }, p.text),
            ));
        }
        if !self.unrated_cards.is_empty() {
            let names: Vec<String> = self
                .unrated_cards
                .iter()
                .take(max_names)
                .map(|id| card_name.and_then(|f| f(id)).filter(|n| !n.is_empty()).unwrap_or_else(|| short(id).to_string()))
                .collect();
            let names = names.join(", ");
            let n = self.unrated_cards.len();
            let rest = n.saturating_sub(max_names);
            let more = if rest > 0 { loc::tr(format!(" 외 {rest}장"), format!(" and {rest} more")) } else { String::new() };
            lines.push(loc::tr(
                format!("티어 없는 카드 {n}장 (기본 티어 {default_tier}로 계산): {names}{more}"),
                format!("{} without a tier (scored as {default_tier}): {names}{more}", cards(n)),
            ));
        }
        if self.version_mismatch() && !self.removed_cards.is_empty() {
            let n = self.removed_cards.len();
            lines.push(loc::tr(format!("게임에서 빠진 카드 {n}장의 티어는 무시합니다"), format!("Ignoring tiers of {} no longer in the game", cards(n))));
        }
        lines
    }
}

fn cards(n: usize) -> String {
    if n == 1 { "1 card".into() } else { format!("{n} cards") }
}

fn short(id: &str) -> &str {
    id.strip_prefix("CARD.").unwrap_or(id)
}

/// Korean subject particle 이/가 by how the last digit is read.
pub fn subject(version: &str) -> String {
    let last = version.chars().last().unwrap_or(' ');
    format!("{version}{}", if matches!(last, '2' | '4' | '5' | '9') { "가" } else { "이" })
}

const UNOFFERED: [&str; 8] = ["basic", "curse", "status", "token", "quest", "event", "ancient", "none"];

/// A rarity that can be offered (reward / shop); unknown counts as offered.
pub fn is_offered_rarity(rarity: Option<&str>) -> bool {
    rarity.is_none_or(|r| !UNOFFERED.contains(&r.to_lowercase().as_str()))
}

pub fn evaluate<'a>(
    game_version: Option<&str>,
    pack_version: Option<&str>,
    catalog: &[GuardCard],
    offerable_pools: &HashSet<String>,
    is_rated: &dyn Fn(&str) -> bool,
    tiered_ids: impl IntoIterator<Item = &'a str>,
) -> VersionGuardResult {
    let game = GameVersion::parse(game_version);
    let pack = GameVersion::parse(pack_version);
    let comparison = match (&game, &pack) {
        (Some(g), Some(p)) => Some(g.cmp(p) as i32),
        _ => None,
    };
    let mut unrated: Vec<String> = catalog
        .iter()
        .filter(|c| c.pool.as_ref().is_some_and(|p| offerable_pools.contains(p)))
        .filter(|c| is_offered_rarity(c.rarity.as_deref()))
        .filter(|c| !is_rated(&c.id))
        .map(|c| c.id.clone())
        .collect();
    unrated.sort();
    unrated.dedup();
    let known: HashSet<&str> = catalog.iter().map(|c| c.id.as_str()).collect();
    let mut removed: Vec<String> =
        if known.is_empty() { Vec::new() } else { tiered_ids.into_iter().filter(|id| !known.contains(id)).map(str::to_string).collect() };
    removed.sort();
    removed.dedup();
    VersionGuardResult { game, pack, comparison, unrated_cards: unrated, removed_cards: removed }
}

/// id / pool / rarity from a catalog JSON; a missing or broken file gives an empty list.
pub fn load_catalog(path: Option<&Path>) -> Vec<GuardCard> {
    let Some(text) = path.and_then(|p| std::fs::read_to_string(p).ok()) else { return Vec::new() };
    let Ok(doc) = serde_json::from_str::<Value>(&text) else { return Vec::new() };
    let Some(cards) = doc.get("cards").and_then(Value::as_array) else { return Vec::new() };
    let s = |c: &Value, k: &str| c.get(k).and_then(Value::as_str).map(str::to_string);
    cards.iter().filter_map(|c| s(c, "id").map(|id| GuardCard { id, pool: s(c, "pool"), rarity: s(c, "rarity") })).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guard_lists_unrated_and_removed_cards() {
        let card = |id: &str, pool: &str, rarity: &str| GuardCard { id: id.into(), pool: Some(pool.into()), rarity: Some(rarity.into()) };
        let catalog = [card("CARD.B", "P", "Rare"), card("CARD.A", "P", "Common"), card("CARD.S", "P", "Basic"), card("CARD.X", "Q", "Rare")];
        let pools: HashSet<String> = ["P".to_string()].into();
        let r = evaluate(Some("v0.114"), Some("v0.113.0"), &catalog, &pools, &|id| id == "CARD.A", ["CARD.A", "CARD.GONE"]);
        assert_eq!((r.comparison, r.unrated_cards.clone(), r.removed_cards.clone()), (Some(1), vec!["CARD.B".to_string()], vec!["CARD.GONE".to_string()]));
        let _g = loc::use_lang(loc::Lang::Kor);
        let lines = r.panel_lines("C", 6, None);
        assert_eq!(lines[0], "게임 버전 v0.114가 데이터 팩 v0.113.0보다 새롭습니다: 티어가 맞지 않을 수 있음");
        assert_eq!(subject("v0.108"), "v0.108이");
    }
}
