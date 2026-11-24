//! The app's brain without UI (the platform-independent part of the C# AppController): it follows the run
//! (tracker snapshots), what is on screen (recognitions), and the helper's lines, and keeps what the panel and the
//! layer should show. Single-threaded: the UI thread owns it and calls [`Brain::poll`] every tick.

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Receiver};
use std::time::Instant;

use ov_core::model::OfferContext;
use ov_gamestate::tracking::deck_effects;
use ov_gamestate::tracking::tracked::{RunOutcome, ScreenHint, TrackedRun};

use crate::advisor::decision::parse_line;
use crate::advisor::host::Log;
use crate::advisor::{AdvisorCommand, AdvisorHost, AdvisorLine, CombatAdviceMessage, CombatAdviceState, DecisionAnswer, DecisionStatus, HostEvent};
use crate::broker::DecisionBroker;
use crate::offer::{OfferLifecycle, PartialVerdict};
use crate::recognition::{Recognition, ScreenKind};
use crate::request::{self, DecideRequest, Target};

pub const SAVE_SOLO: &str = "current_run.save";
pub const SAVE_COOP: &str = "current_run_mp.save";

/// What a decision section shows.
#[derive(Clone, Debug, PartialEq)]
pub enum AdviceStatus {
    /// Asked, no answer yet.
    Waiting,
    Ok,
    /// No policy for this run or screen ("AI 추천 없음"), with the helper's reason.
    Unsupported(String),
    Error(String),
}

#[derive(Clone, Debug, PartialEq)]
pub struct Choice {
    pub target: Target,
    pub key: String,
    pub picked: bool,
    /// 0 = best score.
    pub rank: usize,
    pub score: Option<f64>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct DecisionView {
    pub kind: &'static str,
    pub status: AdviceStatus,
    pub choices: Vec<Choice>,
    /// The top two are within the policy's tie margin.
    pub close: bool,
    /// A co-op run: the answer is the solo policy ("1인 기준").
    pub coop: bool,
    pub source: String,
    pub request: DecideRequest,
}

impl DecisionView {
    pub fn of(req: &DecideRequest, answer: Option<&DecisionAnswer>, coop: bool) -> DecisionView {
        let mut v = DecisionView { kind: req.kind, status: AdviceStatus::Waiting, choices: Vec::new(), close: false, coop, source: String::new(), request: req.clone() };
        let Some(a) = answer else { return v };
        v.source = a.source.clone();
        v.close = a.close;
        v.status = match a.status {
            DecisionStatus::Ok => AdviceStatus::Ok,
            DecisionStatus::Unsupported => AdviceStatus::Unsupported(a.reason.clone()),
            DecisionStatus::Error => AdviceStatus::Error(a.reason.clone()),
        };
        let ranks = a.ranks();
        v.choices = a
            .candidates
            .iter()
            .enumerate()
            .map(|(i, c)| Choice { target: req.target(&c.key), key: c.key.clone(), picked: a.pick.contains(&i), rank: ranks[i], score: c.score })
            .collect();
        v
    }

    pub fn picked(&self) -> impl Iterator<Item = &Choice> {
        self.choices.iter().filter(|c| c.picked)
    }
}

pub struct BrainOptions {
    pub advisor: Option<AdvisorCommand>,
    /// Advice on (the helper runs). The setting `combatAdvisorEnabled`.
    pub enabled: bool,
    pub log_file: Option<PathBuf>,
    /// The helper's data folder (`--data`), normally the helper exe's folder.
    pub advisor_data: Option<PathBuf>,
}

pub struct Brain {
    options: BrainOptions,
    log: Log,
    host: Option<AdvisorHost>,
    host_rx: Option<Receiver<HostEvent>>,
    run: Option<TrackedRun>,
    saves_dir: Option<PathBuf>,
    combat: Option<CombatAdviceMessage>,
    broker: DecisionBroker,
    lifecycle: OfferLifecycle,
    /// The card / shop screen shown now (accepted by the lifecycle).
    shown: Option<Recognition>,
    verdict: PartialVerdict,
    rest_seen: bool,
    tab_request: bool,
    rest_site: Option<ov_vision::rest::RestSiteResult>,
    /// Event buttons on screen (bounds, locked) when their count equals the logged options.
    event_buttons: Vec<(crate::recognition::Rect, bool)>,
    epoch: i32,
    problems: HashSet<String>,
    /// Lines written to the helper (tests read them; capped).
    pub sent: Vec<String>,
}

impl Brain {
    pub fn new(options: BrainOptions, log: Log) -> Brain {
        Brain {
            options,
            log,
            host: None,
            host_rx: None,
            run: None,
            saves_dir: None,
            combat: None,
            broker: DecisionBroker::default(),
            lifecycle: OfferLifecycle::default(),
            shown: None,
            verdict: PartialVerdict::Complete,
            rest_seen: false,
            tab_request: false,
            rest_site: None,
            event_buttons: Vec::new(),
            epoch: 0,
            problems: HashSet::new(),
            sent: Vec::new(),
        }
    }

    pub fn advisor_available(&self) -> bool {
        self.options.advisor.is_some()
    }

    pub fn enabled(&self) -> bool {
        self.options.enabled
    }

    pub fn set_enabled(&mut self, on: bool) {
        self.options.enabled = on;
        self.update_helper();
    }

    /// Recognitions captured from now on carry this epoch (results from before a reset are dropped).
    pub fn epoch(&self) -> i32 {
        self.epoch
    }

    pub fn run(&self) -> Option<&TrackedRun> {
        self.run.as_ref()
    }

    pub fn is_coop(&self) -> bool {
        self.run.as_ref().is_some_and(|r| r.player_count > 1 || r.state.is_multiplayer)
    }

    /// The combat section: solo runs only (the helper's combat search is solo-only).
    pub fn combat(&self) -> Option<&CombatAdviceMessage> {
        if self.is_coop() {
            return None;
        }
        self.combat.as_ref().filter(|m| m.state != CombatAdviceState::Idle)
    }

    pub fn shown(&self) -> Option<&Recognition> {
        self.shown.as_ref()
    }

    /// A card / shop screen newly opened since the last call: the panel shows the 추천 tab.
    pub fn take_tab_request(&mut self) -> bool {
        std::mem::take(&mut self.tab_request)
    }

    /// Some(ancient?) while an event room with logged options is open: the capture reads its buttons.
    pub fn event_room(&self) -> Option<bool> {
        let e = self.run.as_ref()?.current_event.as_ref()?;
        e.option_keys.as_ref().filter(|k| !k.is_empty())?;
        Some(deck_effects::is_ancient(&e.event_id))
    }

    /// The event buttons on screen (screen pixels, locked), top to bottom; empty when not matched to the options.
    pub fn event_buttons(&self) -> &[(crate::recognition::Rect, bool)] {
        &self.event_buttons
    }

    /// The last rest-site buttons seen (for the layer marks).
    pub fn rest_site(&self) -> Option<&ov_vision::rest::RestSiteResult> {
        self.rest_site.as_ref()
    }

    pub fn verdict(&self) -> PartialVerdict {
        self.verdict
    }

    /// The decision of `kind` (`card`, `shop`, `rest`, `path`, `event`, `ancient`) when its screen is current.
    pub fn decision(&self, kind: &str) -> Option<DecisionView> {
        let (req, answer) = self.broker.get(kind)?;
        Some(DecisionView::of(req, answer, self.is_coop()))
    }

    /// The decision the 추천 tab shows now: the open card / shop / rest / event screen.
    pub fn current_offer(&self) -> Option<DecisionView> {
        let kind = match self.shown.as_ref().map(|s| s.kind) {
            Some(ScreenKind::Reward) => "card",
            Some(ScreenKind::Shop) => "shop",
            _ if self.rest_seen => "rest",
            _ => match self.run.as_ref().and_then(|r| r.current_event.as_ref()) {
                Some(e) if deck_effects::is_ancient(&e.event_id) => "ancient",
                Some(_) => "event",
                None => return None,
            },
        };
        self.decision(kind)
    }

    /// A new tracker snapshot.
    pub fn on_run(&mut self, run: Option<TrackedRun>, saves_dir: Option<PathBuf>) {
        let before = self.run.as_ref().map(|r| r.screen);
        self.run = run;
        self.saves_dir = saves_dir;
        let now_hint = self.run.as_ref().map(|r| r.screen);
        // The log says the card screen closed (card taken, room left): forget it for good.
        if matches!(before, Some(ScreenHint::RewardScreenLikely | ScreenHint::ShopLikely)) && before != now_hint {
            self.epoch += 1;
            self.lifecycle.forget(self.shown.as_ref(), self.epoch, Instant::now());
            self.shown = None;
            self.broker.clear("card");
            self.broker.clear("shop");
        }
        if now_hint != Some(ScreenHint::RestSite) && before == Some(ScreenHint::RestSite) {
            self.rest_seen = false;
            self.rest_site = None;
            self.broker.clear("rest");
        }
        self.update_helper();
        self.ask_from_run();
    }

    fn run_overrides(&self) -> Option<serde_json::Value> {
        let r = self.run.as_ref()?;
        (r.state.vitals_known && r.hp_gold_reliable).then(|| request::run_overrides(Some(r.state.hp), Some(r.state.gold))).flatten()
    }

    fn ask_from_run(&mut self) {
        let Some(run) = self.run.as_ref() else {
            self.broker.forget_all();
            return;
        };
        let screen = run.screen;
        let path_req = run.map.as_ref().and_then(request::path);
        let ev = run.current_event.as_ref().and_then(|e| e.option_keys.as_ref().map(|k| (e.event_id.clone(), k.clone())));
        // Path: on the map screen, from the saved map.
        match (screen, path_req) {
            (ScreenHint::Map, Some(req)) => self.ask(req),
            (ScreenHint::Map, None) => {}
            _ => self.broker.clear("path"),
        }
        // Event / ancient buttons from the log.
        match ev {
            Some((id, keys)) if !keys.is_empty() => {
                let ancient = deck_effects::is_ancient(&id);
                let buttons = if self.event_buttons.len() == keys.len() { self.event_buttons.clone() } else { Vec::new() };
                let req = request::event(&id, ancient, &keys, &|i| buttons.get(i).is_some_and(|b| b.1));
                self.broker.clear(if ancient { "event" } else { "ancient" });
                self.ask(req);
            }
            _ => {
                self.event_buttons.clear();
                self.broker.clear("event");
                self.broker.clear("ancient");
            }
        }
    }

    /// One recognition result (UI thread, after the capture worker).
    pub fn on_recognition(&mut self, r: Recognition, now: Instant) {
        if !self.lifecycle.accept(&r, now) {
            return;
        }
        if r.kind == ScreenKind::Other && self.event_room().is_some() && r.event_buttons != self.event_buttons {
            let count = self.run.as_ref().and_then(|x| x.current_event.as_ref()).and_then(|e| e.option_keys.as_ref()).map_or(0, Vec::len);
            if r.event_buttons.len() == count || r.event_buttons.is_empty() {
                self.event_buttons = r.event_buttons.clone();
                self.ask_from_run();
            }
        }
        if let Some(site) = r.rest_site.as_ref().filter(|s| s.choice_screen) {
            self.rest_seen = true;
            self.rest_site = Some(site.clone());
            if let Some(req) = request::rest(site) {
                self.ask(req);
            }
        }
        match r.kind {
            ScreenKind::Reward | ScreenKind::Shop => {
                if self.lifecycle.select_tab(&r) {
                    self.tab_request = true;
                }
                self.verdict = self.lifecycle.verdict(&r, now);
                let req = if r.kind == ScreenKind::Shop {
                    Some(request::shop(&r, self.run_overrides()))
                } else if self.verdict == PartialVerdict::Hold || r.cards.is_empty() || self.covered_by_asked_row(&r) {
                    None
                } else {
                    let context = match self.run.as_ref() {
                        Some(run) if run.screen == ScreenHint::Event => OfferContext::Event,
                        Some(run) => run.offer_context().unwrap_or(OfferContext::CardReward),
                        None => OfferContext::CardReward,
                    };
                    Some(request::card_reward(&r, context, self.run_overrides()))
                };
                if let Some(req) = req {
                    self.ask(req);
                }
                self.shown = Some(r);
            }
            ScreenKind::Choice | ScreenKind::Grid => {
                // Card selections (remove / transform / pick) are not asked yet: no advice is drawn on them.
                self.shown = Some(r);
            }
            ScreenKind::Other => {
                if self.shown.take().is_some() {
                    self.lifecycle.screen_closed();
                    self.broker.clear("card");
                    self.broker.clear("shop");
                }
            }
        }
    }

    /// A reward row that shows only cards of the row already asked (one is covered or enlarged for a moment): the
    /// asked row stays the question.
    fn covered_by_asked_row(&self, r: &Recognition) -> bool {
        let Some((req, _)) = self.broker.get("card") else { return false };
        let crate::request::Shown::Cards(asked) = &req.shown else { return false };
        let mut left: Vec<(&str, i32)> = asked.iter().map(|c| (c.id.as_str(), c.upgrade)).collect();
        r.cards.len() < asked.len()
            && r.cards.iter().all(|c| match left.iter().position(|x| *x == (c.id.as_str(), c.upgrade)) {
                Some(i) => {
                    left.swap_remove(i);
                    true
                }
                None => false,
            })
    }

    fn ask(&mut self, req: DecideRequest) {
        if self.host.is_none() {
            // No helper: nothing would answer. The section shows that advice is off.
            return;
        }
        if let Some(line) = self.broker.ask(req) {
            self.send(&line);
        }
    }

    fn send(&mut self, line: &str) {
        if let Some(h) = &self.host {
            h.send(line);
        }
        if self.sent.len() >= 64 {
            self.sent.remove(0);
        }
        self.sent.push(line.to_string());
    }

    /// The helper's arguments for the current run, or None when it should not run.
    fn desired_args(&self) -> Option<Vec<String>> {
        let _ = self.options.advisor.as_ref()?;
        if !self.options.enabled {
            return None;
        }
        let run = self.run.as_ref().filter(|r| r.outcome == RunOutcome::InProgress)?;
        let save = self.saves_dir.as_ref()?.join(if run.player_count > 1 || run.state.is_multiplayer { SAVE_COOP } else { SAVE_SOLO });
        let log = self.options.log_file.as_ref()?;
        let data = self.options.advisor_data.as_deref().map(path_text);
        Some(AdvisorCommand::live_args(&path_text(&save), &path_text(log), data.as_deref()))
    }

    fn update_helper(&mut self) {
        match self.desired_args() {
            Some(args) => {
                if self.host.is_none() {
                    let (tx, rx) = mpsc::channel();
                    let cmd = self.options.advisor.clone().expect("checked by desired_args");
                    self.host = Some(AdvisorHost::new(cmd, self.log.clone(), tx, None, true));
                    self.host_rx = Some(rx);
                }
                let host = self.host.as_ref().expect("set above");
                let restart = host.arguments().is_some_and(|a| a != args);
                host.run(&args);
                if restart {
                    self.broker.forget_all();
                    self.combat = None;
                }
            }
            None => {
                if let Some(h) = &self.host
                    && h.arguments().is_some()
                {
                    h.stop();
                    self.combat = None;
                    self.broker.forget_all();
                }
            }
        }
    }

    /// Drains the helper's events. True when something shown changed.
    pub fn poll(&mut self) -> bool {
        let Some(rx) = &self.host_rx else { return false };
        let events: Vec<HostEvent> = rx.try_iter().collect();
        let mut changed = false;
        for e in events {
            changed |= match e {
                HostEvent::Line(l) => self.on_line(&l),
                HostEvent::Exited(_) => {
                    // What it said last is about a fight it no longer follows; pending questions are lost.
                    self.combat = None;
                    let again = self.broker.reask_all();
                    for l in again {
                        self.send(&l);
                    }
                    true
                }
            };
        }
        changed
    }

    /// One stdout line of the helper. True when something shown changed.
    pub fn on_line(&mut self, line: &str) -> bool {
        match parse_line(line) {
            Ok(Some(AdvisorLine::Combat(m))) => {
                let changed = self.combat.as_ref() != Some(&m);
                self.combat = Some(m);
                changed
            }
            Ok(Some(AdvisorLine::Decision(a))) => self.broker.on_answer(a),
            Ok(Some(AdvisorLine::Run(n))) => {
                (self.log)(&format!("advisor run: {:?} A{:?} act {:?} floor {:?} supported {}", n.character, n.ascension, n.act, n.floor, n.supported));
                false
            }
            Ok(None) => false,
            Err(problem) => {
                if self.problems.len() < 32 && self.problems.insert(problem.clone()) {
                    (self.log)(&format!("advisor: ignored a line ({problem}); further lines like it are not logged"));
                }
                false
            }
        }
    }
}

fn path_text(p: &Path) -> String {
    p.to_string_lossy().into_owned()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(unix)]
    use crate::recognition::{Rect, SeenCard};
    use ov_core::model::RunState;
    use std::sync::{Arc, Mutex};

    fn brain(script: &str) -> Brain {
        let logs = Arc::new(Mutex::new(Vec::<String>::new()));
        let cmd = AdvisorCommand { file_name: "/bin/sh".into(), leading_args: vec!["-c".into(), script.into()], source: "setting" };
        Brain::new(
            BrainOptions { advisor: Some(cmd), enabled: true, log_file: Some("/g/godot.log".into()), advisor_data: Some("/a".into()) },
            Arc::new(move |m: &str| logs.lock().unwrap().push(m.to_string())),
        )
    }

    #[cfg(unix)]
    fn card(slot: i32, id: &str) -> SeenCard {
        SeenCard { slot, id: id.into(), upgrade: 0, bounds: Rect::default(), confidence: 1.0, price: None, unaffordable: None, on_sale: false, price_bounds: None, missing_portrait: false }
    }

    #[cfg(unix)]
    #[test]
    fn a_reward_screen_is_asked_answered_and_forgotten() {
        // The fake helper answers every request with "card:1" and also prints one combat line.
        let script = r#"echo '{"v":1,"state":"advice","steps":[],"confidence":"exact"}'; while read l; do id=$(echo "$l" | sed 's/^{"v":2,"type":"decide","id":\([0-9]*\).*/\1/'); echo "{\"v\":2,\"type\":\"decision\",\"id\":$id,\"kind\":\"card\",\"status\":\"ok\",\"pick\":[1],\"candidates\":[{\"key\":\"card:0\",\"score\":1},{\"key\":\"card:1\",\"score\":2},{\"key\":\"skip\",\"score\":0}],\"close\":false,\"source\":\"t\"}"; done"#;
        let mut b = brain(script);
        let mut run = TrackedRun::new(RunState::new("CHARACTER.DEFECT"));
        run.screen = ScreenHint::RewardScreenLikely;
        b.on_run(Some(run.clone()), Some("/s".into()));
        assert_eq!(b.host.as_ref().unwrap().arguments().unwrap(), vec!["live", "--save", "/s/current_run.save", "--log", "/g/godot.log", "--exit-on-stdin-eof", "--data", "/a"]);
        let r = Recognition { kind: ScreenKind::Reward, cards: vec![card(0, "CARD.ZAP"), card(1, "CARD.DUALCAST")], slots: 2, screen_id: 1, ..Recognition::other() };
        b.on_recognition(r.clone(), Instant::now());
        assert_eq!(b.sent.len(), 1);
        assert!(b.sent[0].contains(r#""kind":"card","screen":{"context":"reward","cards":[{"id":"ZAP","upgrade":0},{"id":"DUALCAST","upgrade":0}]"#), "{}", b.sent[0]);
        let deadline = Instant::now() + std::time::Duration::from_secs(5);
        while b.current_offer().is_none_or(|v| v.status == AdviceStatus::Waiting) && Instant::now() < deadline {
            b.poll();
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
        let v = b.current_offer().expect("card decision");
        assert_eq!(v.status, AdviceStatus::Ok);
        assert_eq!(v.picked().map(|c| c.target).collect::<Vec<_>>(), vec![Target::Card(1)]);
        assert_eq!(v.choices.iter().map(|c| c.rank).collect::<Vec<_>>(), vec![1, 0, 2]);
        assert!(b.combat().is_some());

        b.on_recognition(r.clone(), Instant::now());
        assert_eq!(b.sent.len(), 1, "same screen: not asked again");
        b.on_recognition(Recognition { cards: vec![card(1, "CARD.DUALCAST")], ..r.clone() }, Instant::now());
        assert_eq!(b.sent.len(), 1, "one card covered: the asked row stays");
        run.screen = ScreenHint::Map;
        b.on_run(Some(run), Some("/s".into()));
        assert!(b.current_offer().is_none(), "the log closed the screen");
        assert!(b.decision("card").is_none());
        b.on_recognition(Recognition { epoch: 0, ..r }, Instant::now());
        assert!(b.shown().is_none(), "a result from before the reset is dropped");
    }

    #[test]
    fn no_helper_without_a_run_or_when_off() {
        let mut b = brain("cat");
        b.on_run(None, Some("/s".into()));
        assert!(b.host.is_none());
        let mut run = TrackedRun::new(RunState::new("CHARACTER.DEFECT"));
        run.player_count = 2;
        b.set_enabled(false);
        b.on_run(Some(run.clone()), Some("/s".into()));
        assert!(b.host.is_none());
        b.set_enabled(true);
        let want = std::path::Path::new("/s").join(SAVE_COOP).to_string_lossy().into_owned();
        assert_eq!(b.host.as_ref().unwrap().arguments().unwrap()[2], want, "co-op reads the multiplayer save");
        assert!(b.is_coop());
        b.on_line(r#"{"v":1,"state":"advice","steps":[]}"#);
        assert!(b.combat().is_none(), "no combat advice in co-op");
    }
}
