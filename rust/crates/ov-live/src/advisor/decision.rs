//! Reading every helper stdout line: v1 combat advice, v2 decision answers and run notices (`ADVISOR_PROTOCOL.md` §1, §3, §4).

use serde_json::{Map, Value};

use super::combat::{self, CombatAdviceMessage, bool_of, int_of, str_of};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DecisionStatus {
    Ok,
    /// No policy for this character / ascension / kind: the app shows "AI 추천 없음".
    Unsupported,
    Error,
}

#[derive(Clone, Debug, PartialEq)]
pub struct Candidate {
    /// `card:i`, `skip`, `shop_card:i`, `remove:<deck>`, `leave`, `opt:i`, `opt:i/deck:k`, `deck:i`, `alt:k` ...
    pub key: String,
    pub score: Option<f64>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct DecisionAnswer {
    pub id: Option<i64>,
    pub kind: String,
    pub status: DecisionStatus,
    /// Chosen candidate indices.
    pub pick: Vec<usize>,
    pub candidates: Vec<Candidate>,
    /// The top two are within the policy's tie margin: "추천! (박빙)".
    pub close: bool,
    pub source: String,
    pub basis: String,
    pub reason: String,
    pub ms: Option<f64>,
}

impl DecisionAnswer {
    /// The keys of the chosen candidates, in pick order.
    pub fn picked_keys(&self) -> Vec<&str> {
        self.pick.iter().filter_map(|&i| self.candidates.get(i)).map(|c| c.key.as_str()).collect()
    }

    /// 0-based rank of each candidate by score (best = 0; ties share the better rank; no score = last).
    pub fn ranks(&self) -> Vec<usize> {
        let s: Vec<f64> = self.candidates.iter().map(|c| c.score.unwrap_or(f64::NEG_INFINITY)).collect();
        s.iter().map(|&x| s.iter().filter(|&&y| y > x).count()).collect()
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RunNotice {
    pub character: Option<String>,
    pub ascension: Option<i32>,
    pub act: Option<i32>,
    pub floor: Option<i32>,
    pub supported: bool,
}

/// One usable helper line.
#[derive(Clone, Debug, PartialEq)]
pub enum AdvisorLine {
    Combat(CombatAdviceMessage),
    Decision(DecisionAnswer),
    Run(RunNotice),
}

/// Reads one stdout line. `Ok(None)`: blank or a line type this app ignores. `Err`: unusable, with a short reason
/// (the caller logs each kind once).
pub fn parse_line(line: &str) -> Result<Option<AdvisorLine>, String> {
    if line.trim().is_empty() {
        return Ok(None);
    }
    let root: Value = serde_json::from_str(line).map_err(|_| "not JSON".to_string())?;
    let Some(root) = root.as_object() else { return Err("not a JSON object".into()) };
    let Some(v) = int_of(root, "v") else { return Err("no protocol version (v)".into()) };
    match v {
        1 => combat::parse_object(root).map(|m| Some(AdvisorLine::Combat(m))),
        2 => match str_of(root, "type") {
            Some("decision") => Ok(Some(AdvisorLine::Decision(decision(root)))),
            Some("run") => Ok(Some(AdvisorLine::Run(RunNotice {
                character: str_of(root, "character").map(str::to_string),
                ascension: int_of(root, "ascension"),
                act: int_of(root, "act"),
                floor: int_of(root, "floor"),
                supported: bool_of(root, "supported"),
            }))),
            _ => Ok(None),
        },
        v => Err(format!("protocol version {v} (this overlay reads 1 and 2)")),
    }
}

fn decision(root: &Map<String, Value>) -> DecisionAnswer {
    let candidates: Vec<Candidate> = root
        .get("candidates")
        .and_then(Value::as_array)
        .map(|a| {
            a.iter()
                .filter_map(Value::as_object)
                .map(|c| Candidate { key: str_of(c, "key").unwrap_or("").to_string(), score: c.get("score").and_then(Value::as_f64) })
                .collect()
        })
        .unwrap_or_default();
    let pick = root
        .get("pick")
        .and_then(Value::as_array)
        .map(|a| a.iter().filter_map(Value::as_u64).map(|i| i as usize).filter(|&i| i < candidates.len()).collect())
        .unwrap_or_default();
    DecisionAnswer {
        id: root.get("id").and_then(Value::as_i64),
        kind: str_of(root, "kind").unwrap_or("").to_string(),
        status: match str_of(root, "status") {
            Some("ok") => DecisionStatus::Ok,
            Some("unsupported") => DecisionStatus::Unsupported,
            _ => DecisionStatus::Error,
        },
        pick,
        candidates,
        close: bool_of(root, "close"),
        source: str_of(root, "source").unwrap_or("").to_string(),
        basis: str_of(root, "basis").unwrap_or("solo").to_string(),
        reason: str_of(root, "reason").unwrap_or("").to_string(),
        ms: root.get("ms").and_then(Value::as_f64),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::advisor::combat::*;

    fn combat(line: &str) -> CombatAdviceMessage {
        match parse_line(line) {
            Ok(Some(AdvisorLine::Combat(m))) => m,
            other => panic!("{line}: {other:?}"),
        }
    }

    #[test]
    fn reads_v1_combat_lines_like_the_csharp_parser() {
        let m = combat(
            r#"{"v":1,"state":"advice","encounter":"JAW_WORM_WEAK","turn":2,"energy":3,"hp":70,"block":5,"played":1,"policy":"fool_rust","confidence":"exact","ending":"draw","ms":12.5,
            "steps":[{"kind":"card","id":"BASH","upgraded":true,"hand":2,"target":"JAW_WORM","targetSlot":0,"targetAmbiguous":true,"choices":[{"cards":[{"id":"STRIKE_IRONCLAD","upgraded":false},{"upgraded":true}]},7]},
                     {"kind":"orb","id":"X","target":""},{"kind":"card"}],
            "choosing":{"cards":[{"id":"DEFEND_IRONCLAD"}]}}"#,
        );
        assert_eq!(m.state, CombatAdviceState::Advice);
        assert_eq!((m.turn, m.energy, m.hp, m.block, m.played), (Some(2), Some(3), Some(70), Some(5), Some(1)));
        assert!(m.is_fool_search && !m.uncertain);
        assert_eq!(m.ending, CombatEnding::AdviseAfterPlay);
        assert_eq!(m.ms, Some(12.5));
        assert_eq!(m.steps.len(), 2, "a step without id is dropped");
        let s = &m.steps[0];
        assert_eq!((s.kind, s.id.as_str(), s.upgraded, s.hand, s.target.as_deref(), s.target_slot, s.target_ambiguous), (CombatStepKind::Card, "BASH", true, Some(2), Some("JAW_WORM"), Some(0), true));
        assert_eq!(s.choices, vec![vec![CombatPick { id: "STRIKE_IRONCLAD".into(), upgraded: false }]]);
        assert_eq!((m.steps[1].kind, m.steps[1].target.clone()), (CombatStepKind::Unknown, None));
        assert_eq!(m.choosing, Some(vec![CombatPick { id: "DEFEND_IRONCLAD".into(), upgraded: false }]));

        let u = combat(r#"{"v":1,"state":"advice","steps":[],"confidence":"likely","turn":1.5}"#);
        assert!(u.uncertain && u.turn.is_none() && u.ending == CombatEnding::None);
        let l = combat(r#"{"v":1,"state":"lost","encounter":"E","reason":"desync"}"#);
        assert_eq!((l.state, l.encounter.as_deref(), l.reason.as_str()), (CombatAdviceState::Lost, Some("E"), "desync"));
        assert_eq!(combat(r#"{"v":1,"state":"idle","encounter":"E"}"#), CombatAdviceMessage::idle());

        for (line, why) in [
            ("[1]", "not a JSON object"),
            ("{", "not JSON"),
            (r#"{"state":"idle"}"#, "no protocol version (v)"),
            (r#"{"v":3}"#, "protocol version 3 (this overlay reads 1 and 2)"),
            (r#"{"v":1}"#, "no state"),
        ] {
            assert_eq!(parse_line(line), Err(why.to_string()), "{line}");
        }
        assert_eq!(parse_line("   "), Ok(None));
    }

    #[test]
    fn reads_v2_answers_and_notices() {
        let line = r#"{"v":2,"type":"decision","id":17,"kind":"card","status":"ok","pick":[1,9],"candidates":[{"key":"card:0","score":0.41},{"key":"card:1","score":0.63},{"key":"skip","score":0.41}],"close":true,"source":"defect_a0","basis":"solo","ms":3.1}"#;
        let Ok(Some(AdvisorLine::Decision(a))) = parse_line(line) else { panic!() };
        assert_eq!((a.id, a.kind.as_str(), a.status, a.close), (Some(17), "card", DecisionStatus::Ok, true));
        assert_eq!(a.pick, vec![1], "an index past the candidates is dropped");
        assert_eq!(a.picked_keys(), vec!["card:1"]);
        assert_eq!(a.ranks(), vec![1, 0, 1]);
        let Ok(Some(AdvisorLine::Decision(u))) = parse_line(r#"{"v":2,"type":"decision","id":null,"kind":"path","status":"unsupported","reason":"no policy for X A3","basis":"solo","ms":0.1}"#) else { panic!() };
        assert_eq!((u.id, u.status, u.reason.as_str(), u.candidates.len()), (None, DecisionStatus::Unsupported, "no policy for X A3", 0));
        let Ok(Some(AdvisorLine::Run(r))) = parse_line(r#"{"v":2,"type":"run","character":"DEFECT","ascension":0,"act":1,"floor":3,"supported":true}"#) else { panic!() };
        assert_eq!(r, RunNotice { character: Some("DEFECT".into()), ascension: Some(0), act: Some(1), floor: Some(3), supported: true });
        assert_eq!(parse_line(r#"{"v":2,"type":"other"}"#), Ok(None));
    }
}
