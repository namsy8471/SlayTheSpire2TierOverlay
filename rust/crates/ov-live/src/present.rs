//! What the panel shows, as text rows (port of the text side of CombatAdvicePresenter and the offer rows of
//! AppController). The UI only lays these out.

use ov_core::loc::{self, t};
use ov_core::map::{MapCoord, MapNodeType};
use ov_gamestate::tracking::tracked::TrackedRun;
use ov_vision::rest::RestOption;

use crate::advisor::{CombatAdviceMessage, CombatAdviceState, CombatEnding, CombatStepKind};
use crate::brain::{AdviceStatus, DecisionView};
use crate::request::{Shown, Target};
use crate::text::{GameText, prettify};

#[derive(Clone, Debug, PartialEq)]
pub struct Row {
    pub label: String,
    /// A second, smaller text (price, target, deck card ...).
    pub sub: String,
    pub picked: bool,
    /// 0 = best.
    pub rank: usize,
}

#[derive(Clone, Debug, PartialEq)]
pub struct Section {
    pub title: String,
    /// One line under the title (status, basis, meta); empty when none.
    pub status: String,
    pub rows: Vec<Row>,
    /// A closing row (combat: end turn ...); empty when none.
    pub ending: String,
    /// Tooltip text (why uncertain, the policy source ...).
    pub note: String,
    pub warn: bool,
}

pub fn combat(m: &CombatAdviceMessage, text: &GameText) -> Section {
    let lang = loc::current();
    let mut s = Section { title: String::new(), status: String::new(), rows: Vec::new(), ending: String::new(), note: m.note.clone(), warn: false };
    match m.state {
        CombatAdviceState::Lost | CombatAdviceState::Unsupported => {
            s.title = t("전투", "Combat").into();
            s.status = if m.state == CombatAdviceState::Lost { t("이 전투는 추적이 끊겼습니다", "Lost track of this fight") } else { t("이 전투는 아직 지원하지 않습니다", "This fight is not supported yet") }.into();
            s.note = m.reason.clone();
            s.warn = true;
            return s;
        }
        CombatAdviceState::Idle => return s,
        CombatAdviceState::Advice => {}
    }
    s.title = match m.turn {
        Some(n) => loc::tr(format!("이번 턴 추천 순서 · {n}턴"), format!("This turn's plays · Turn {n}")),
        None => t("이번 턴 추천 순서", "This turn's plays").into(),
    };
    let mut meta = Vec::new();
    if let Some(e) = m.energy {
        meta.push(loc::tr(format!("에너지 {e}"), format!("Energy {e}")));
    }
    if let Some(hp) = m.hp {
        meta.push(loc::tr(format!("체력 {hp}"), format!("HP {hp}")));
    }
    if let Some(b) = m.block {
        meta.push(loc::tr(format!("방어도 {b}"), format!("Block {b}")));
    }
    if m.uncertain {
        meta.push(t("불확실", "uncertain").into());
        s.warn = true;
        if s.note.is_empty() {
            s.note = t("계산한 손패가 실제 손패와 다를 수 있습니다", "The simulated hand may differ from the real one").into();
        }
    }
    s.status = meta.join(" · ");
    for (i, st) in m.steps.iter().enumerate() {
        let label = match st.kind {
            CombatStepKind::Card => text.card(lang, &st.id, i32::from(st.upgraded)),
            CombatStepKind::Potion => format!("{} ({})", text.potion(lang, &st.id), t("포션", "Potion")),
            CombatStepKind::Unknown => prettify(&st.id),
        };
        let mut sub = Vec::new();
        if let Some(target) = &st.target {
            let name = text.monster(lang, target);
            sub.push(match (st.target_ambiguous, st.target_slot) {
                (true, Some(n)) => loc::tr(format!("→ {name} ({}번째)", n + 1), format!("→ {name} (#{})", n + 1)),
                _ => format!("→ {name}"),
            });
        }
        for picks in &st.choices {
            if picks.is_empty() {
                sub.push(t("고르지 않고 확인", "Confirm without picking").into());
            } else {
                let names: Vec<String> = picks.iter().map(|p| text.card(lang, &p.id, i32::from(p.upgraded))).collect();
                sub.push(format!("{}{}", t("고를 카드: ", "Pick: "), names.join(", ")));
            }
        }
        s.rows.push(Row { label: format!("{}. {label}", i + 1), sub: sub.join(" · "), picked: i == 0, rank: i });
    }
    if let Some(picks) = &m.choosing {
        let names: Vec<String> = picks.iter().map(|p| text.card(lang, &p.id, i32::from(p.upgraded))).collect();
        s.rows.insert(0, Row { label: format!("{}{}", t("고를 카드: ", "Pick: "), names.join(", ")), sub: String::new(), picked: true, rank: 0 });
    }
    s.ending = match m.ending {
        CombatEnding::EndTurn => t("턴 종료", "End turn").into(),
        CombatEnding::Win => t("마무리!", "Finish!").into(),
        CombatEnding::AdviseAfterPlay => t("… 이 카드를 낸 뒤 다시 추천", "… more after this play").into(),
        CombatEnding::AdviseAfterChoice => t("… 선택을 마치면 다시 추천", "… more after you choose").into(),
        CombatEnding::AwaitObservedAction => t("… 다음 행동을 확인하면 다시 추천", "… more after your next action").into(),
        CombatEnding::None => String::new(),
    };
    s
}

pub fn node_name(t_: MapNodeType) -> &'static str {
    match t_ {
        MapNodeType::Monster => t("일반 전투", "Enemy"),
        MapNodeType::Elite => t("엘리트", "Elite"),
        MapNodeType::RestSite => t("휴식처", "Rest Site"),
        MapNodeType::Shop => t("상점", "Shop"),
        MapNodeType::Treasure => t("보물", "Treasure"),
        MapNodeType::Boss => t("보스", "Boss"),
        MapNodeType::Ancient => t("고대의 존재", "Ancient"),
        MapNodeType::Unknown => t("? 이벤트", "? Event"),
    }
}

fn direction(from: MapCoord, to: MapCoord) -> &'static str {
    match (to.col - from.col).signum() {
        -1 => t("(왼쪽)", "(left)"),
        1 => t("(오른쪽)", "(right)"),
        _ => t("(위)", "(up)"),
    }
}

pub fn rest_name(o: RestOption) -> &'static str {
    match o {
        RestOption::Heal => t("휴식", "Rest"),
        RestOption::Smith => t("재련", "Smith"),
        RestOption::Mend => t("수선", "Mend"),
        RestOption::Lift => t("들어올리기", "Lift"),
        RestOption::Dig => t("파기", "Dig"),
        RestOption::Cook => t("요리", "Cook"),
        RestOption::Clone => t("복제", "Clone"),
        RestOption::Hatch => t("부화", "Hatch"),
        RestOption::Kindle => t("점화", "Kindle"),
        RestOption::Toke => t("흡입", "Toke"),
        RestOption::Unknown => "?",
    }
}

fn deck_card(run: Option<&TrackedRun>, i: usize, text: &GameText) -> String {
    match run.and_then(|r| r.state.deck.get(i)) {
        Some(c) => text.card(loc::current(), &c.card.id, c.card.upgrade),
        None => loc::tr(format!("덱 {}번 카드", i + 1), format!("deck card {}", i + 1)),
    }
}

fn price(p: Option<i32>) -> String {
    p.map_or(String::new(), |p| format!("{p}G"))
}

/// The label and sub-text of one candidate.
pub fn target_text(v: &DecisionView, target: Target, run: Option<&TrackedRun>, text: &GameText) -> (String, String) {
    let lang = loc::current();
    match (&v.request.shown, target) {
        (Shown::Cards(c), Target::Card(i)) => (text.card(lang, &c[i].id, c[i].upgrade), String::new()),
        (Shown::Shop { cards, .. }, Target::ShopCard(i)) => (text.card(lang, &cards[i].id, cards[i].upgrade), price(cards[i].price)),
        (Shown::Shop { relics, .. }, Target::ShopRelic(i)) => (text.relic(lang, &relics[i].id), price(relics[i].price)),
        (Shown::Shop { potions, .. }, Target::ShopPotion(i)) => (text.potion(lang, &potions[i].id), price(potions[i].price)),
        (_, Target::Removal(d)) => (t("카드 제거", "Card Removal").into(), d.map(|d| deck_card(run, d, text)).unwrap_or_default()),
        (_, Target::Leave) => (t("사지 않고 나가기", "Leave").into(), String::new()),
        (_, Target::Skip) => (t("건너뛰기", "Skip").into(), String::new()),
        (_, Target::Rest(o, d)) => (rest_name(o).into(), d.map(|d| deck_card(run, d, text)).unwrap_or_default()),
        (Shown::Path(_), Target::Path(c)) => {
            let map = run.and_then(|r| r.map.as_ref());
            let kind = map.and_then(|m| m.nodes.get(&c)).map_or(MapNodeType::Unknown, |n| n.kind);
            let from = map.and_then(|m| m.current).unwrap_or(c);
            (node_name(kind).into(), direction(from, c).into())
        }
        (Shown::Options(keys), Target::Option(i)) => (text.event_option(lang, &keys[i]), String::new()),
        (_, Target::Alt(k)) => (loc::tr(format!("다른 버튼 {}", k + 1), format!("other button {}", k + 1)), String::new()),
        (_, Target::Deck(i)) => (deck_card(run, i, text), String::new()),
        _ => ("?".into(), String::new()),
    }
}

pub fn title_of(v: &DecisionView, run: Option<&TrackedRun>, text: &GameText) -> String {
    let lang = loc::current();
    let event = run.and_then(|r| r.current_event.as_ref()).map(|e| text.event(lang, &e.event_id));
    match v.kind {
        "card" => t("카드 보상", "Card reward").into(),
        "shop" => t("상점", "Shop").into(),
        "rest" => t("휴식처", "Rest site").into(),
        "path" => t("다음 경로", "Next path").into(),
        "event" | "ancient" => event.unwrap_or_else(|| t("이벤트", "Event").into()),
        _ => v.kind.into(),
    }
}

/// A decision as panel rows, best first.
pub fn decision(v: &DecisionView, run: Option<&TrackedRun>, text: &GameText, incomplete: bool) -> Section {
    let mut s = Section { title: title_of(v, run, text), status: String::new(), rows: Vec::new(), ending: String::new(), note: v.source.clone(), warn: false };
    match &v.status {
        AdviceStatus::Waiting => s.status = t("AI 계산 중…", "AI thinking…").into(),
        AdviceStatus::Unsupported(why) => {
            s.status = t("AI 추천 없음", "No AI advice").into();
            s.note = why.clone();
        }
        AdviceStatus::Error(why) => {
            s.status = t("AI 추천 오류", "AI advice failed").into();
            s.note = why.clone();
            s.warn = true;
        }
        AdviceStatus::Ok => {
            let mut parts = Vec::new();
            if v.close {
                parts.push(t("박빙", "close call"));
            }
            if v.coop {
                parts.push(t("1인 기준", "solo basis"));
            }
            if incomplete {
                parts.push(t("카드 하나를 읽지 못함", "a card was not read"));
            }
            s.status = parts.join(" · ");
            let mut rows: Vec<Row> = v
                .choices
                .iter()
                .map(|c| {
                    let (label, sub) = target_text(v, c.target, run, text);
                    Row { label, sub, picked: c.picked, rank: c.rank }
                })
                .collect();
            rows.sort_by_key(|r| (!r.picked, r.rank));
            s.rows = rows;
        }
    }
    s
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::advisor::decision::{AdvisorLine, parse_line};
    use crate::recognition::{Rect, Recognition, ScreenKind, SeenCard};
    use ov_core::model::OfferContext;

    #[test]
    fn decision_rows_best_first_with_names() {
        let _g = loc::use_lang(loc::Lang::Kor);
        let card = |slot, id: &str| SeenCard { slot, id: id.into(), upgrade: 0, bounds: Rect::default(), confidence: 1.0, price: None, unaffordable: None, on_sale: false, price_bounds: None, missing_portrait: false };
        let r = Recognition { kind: ScreenKind::Reward, cards: vec![card(0, "CARD.ZAP"), card(1, "CARD.BALL_LIGHTNING")], ..Recognition::other() };
        let req = crate::request::card_reward(&r, OfferContext::CardReward, None);
        let Ok(Some(AdvisorLine::Decision(a))) = parse_line(r#"{"v":2,"type":"decision","id":1,"kind":"card","status":"ok","pick":[1],"candidates":[{"key":"card:0","score":0.5},{"key":"card:1","score":0.6},{"key":"skip","score":0.1}],"close":true}"#) else { panic!() };
        let v = DecisionView::of(&req, Some(&a), true);
        let s = decision(&v, None, &GameText::default(), false);
        assert_eq!(s.title, "카드 보상");
        assert_eq!(s.status, "박빙 · 1인 기준");
        assert_eq!(s.rows.iter().map(|r| (r.label.as_str(), r.picked, r.rank)).collect::<Vec<_>>(), vec![("Ball Lightning", true, 0), ("Zap", false, 1), ("건너뛰기", false, 2)]);
    }
}
