//! Request ids and answers per decision kind. A newer request of a kind makes the older ones obsolete; the same
//! screen is not asked twice.

use std::collections::HashMap;

use crate::advisor::DecisionAnswer;
use crate::request::DecideRequest;

#[derive(Default)]
pub struct DecisionBroker {
    next_id: i64,
    /// Per kind: the last request sent and its id.
    asked: HashMap<&'static str, (i64, DecideRequest)>,
    /// Per kind: the answer to the last request.
    answers: HashMap<&'static str, DecisionAnswer>,
}

impl DecisionBroker {
    /// The stdin line to send for `req`, or None when the same screen (and run overrides) was already asked.
    pub fn ask(&mut self, req: DecideRequest) -> Option<String> {
        if let Some((_, last)) = self.asked.get(req.kind)
            && last.screen == req.screen
            && last.run == req.run
        {
            return None;
        }
        self.next_id += 1;
        let id = self.next_id;
        let line = req.line(id);
        self.answers.remove(req.kind);
        self.asked.insert(req.kind, (id, req));
        Some(line)
    }

    /// Stores an answer to the current request of its kind; answers to older requests are dropped. Returns whether
    /// it was taken.
    pub fn on_answer(&mut self, a: DecisionAnswer) -> bool {
        let Some((&kind, (id, _))) = self.asked.get_key_value(a.kind.as_str()) else { return false };
        if a.id != Some(*id) {
            return false;
        }
        self.answers.insert(kind, a);
        true
    }

    /// The current request of `kind` and its answer (None while waiting).
    pub fn get(&self, kind: &str) -> Option<(&DecideRequest, Option<&DecisionAnswer>)> {
        self.asked.get(kind).map(|(_, r)| (r, self.answers.get(kind)))
    }

    /// The screen of `kind` is gone.
    pub fn clear(&mut self, kind: &str) {
        self.asked.remove(kind);
        self.answers.remove(kind);
    }

    /// The helper restarted: nothing asked is pending any more, so every screen is asked again.
    pub fn forget_all(&mut self) {
        self.asked.clear();
        self.answers.clear();
    }

    /// The last request lines of every kind still on screen, with fresh ids (after a helper restart).
    pub fn reask_all(&mut self) -> Vec<String> {
        let reqs: Vec<DecideRequest> = self.asked.drain().map(|(_, (_, r))| r).collect();
        self.answers.clear();
        reqs.into_iter().filter_map(|r| self.ask(r)).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::advisor::decision::{AdvisorLine, parse_line};
    use crate::request::Shown;
    use serde_json::json;

    fn req(n: i32) -> DecideRequest {
        DecideRequest { kind: "card", screen: json!({"n": n}), run: None, shown: Shown::Options(vec![]) }
    }

    fn answer(id: i64) -> DecisionAnswer {
        let line = format!(r#"{{"v":2,"type":"decision","id":{id},"kind":"card","status":"ok","pick":[0],"candidates":[{{"key":"skip","score":1}}]}}"#);
        match parse_line(&line) {
            Ok(Some(AdvisorLine::Decision(a))) => a,
            _ => unreachable!(),
        }
    }

    #[test]
    fn asks_once_per_screen_and_keeps_only_current_answers() {
        let mut b = DecisionBroker::default();
        assert!(b.ask(req(1)).unwrap().contains(r#""id":1"#));
        assert!(b.ask(req(1)).is_none(), "same screen");
        assert!(b.ask(req(2)).unwrap().contains(r#""id":2"#));
        assert!(!b.on_answer(answer(1)), "obsolete");
        assert!(b.get("card").unwrap().1.is_none());
        assert!(b.on_answer(answer(2)));
        assert_eq!(b.get("card").unwrap().1.unwrap().id, Some(2));
        let again = b.reask_all();
        assert_eq!(again.len(), 1);
        assert!(again[0].contains(r#""id":3"#) && b.get("card").unwrap().1.is_none());
        b.clear("card");
        assert!(b.get("card").is_none());
        assert!(b.ask(req(2)).is_some(), "asked again after the screen closed");
    }
}
