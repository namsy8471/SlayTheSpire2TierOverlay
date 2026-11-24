//! Port of OfferLifecycle.cs: which recognition results may (re)draw badges, when the panel jumps to the 추천 tab,
//! and when a half-read reward row may show its verdict. Pure (time is passed in).

use std::time::{Duration, Instant};

use crate::recognition::{Recognition, ScreenKind};

/// What a partly read reward row may show.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PartialVerdict {
    /// Every slot read (or not a row screen): show everything.
    Complete,
    /// Some slots unread, still waiting: no best-pick ribbon, no skip verdict.
    Hold,
    /// Waited long enough: show the verdict with a note that a card was not read.
    ShowIncomplete,
}

pub struct OfferLifecycle {
    pub suppress_for: Duration,
    pub partial_wait: Duration,
    drop_before: i32,
    forgotten: Option<(ScreenKind, Vec<String>)>,
    forgotten_until: Option<Instant>,
    tab_screen: Option<(ScreenKind, Vec<String>)>,
    tab_screen_open: bool,
    partial_screen: Option<i32>,
    partial_since: Option<Instant>,
}

impl Default for OfferLifecycle {
    fn default() -> Self {
        OfferLifecycle {
            suppress_for: Duration::from_millis(1500),
            partial_wait: Duration::from_secs(2),
            drop_before: 0,
            forgotten: None,
            forgotten_until: None,
            tab_screen: None,
            tab_screen_open: false,
            partial_screen: None,
            partial_since: None,
        }
    }
}

impl OfferLifecycle {
    /// The log says the card screen closed: drop results from before `epoch`, and suppress fresh results that show
    /// only the forgotten cards until the screen is seen closed or `suppress_for` passed.
    pub fn forget(&mut self, shown: Option<&Recognition>, epoch: i32, now: Instant) {
        self.drop_before = self.drop_before.max(epoch);
        if let Some(s) = shown.filter(|s| s.kind != ScreenKind::Other && !s.cards.is_empty()) {
            self.forgotten = Some((s.kind, s.sorted_ids()));
            self.forgotten_until = Some(now + self.suppress_for);
        }
    }

    /// Whether a result may be applied at all.
    pub fn accept(&mut self, r: &Recognition, now: Instant) -> bool {
        if r.epoch < self.drop_before {
            return false;
        }
        let Some((kind, ids)) = &self.forgotten else { return true };
        if r.kind == ScreenKind::Other || self.forgotten_until.is_none_or(|t| now >= t) || r.kind != *kind || !is_subset(&r.sorted_ids(), ids) {
            self.forgotten = None;
            return true;
        }
        false
    }

    /// True when the panel should switch to the 추천 tab: the screen newly opened.
    pub fn select_tab(&mut self, r: &Recognition) -> bool {
        let ids = r.sorted_ids();
        let same = self.tab_screen_open
            && self.tab_screen.as_ref().is_some_and(|(k, s)| *k == r.kind && (r.kind == ScreenKind::Shop || is_subset(&ids, s) || is_subset(s, &ids)));
        self.tab_screen = Some((r.kind, ids));
        self.tab_screen_open = true;
        !same
    }

    /// Vision says no card screen is open.
    pub fn screen_closed(&mut self) {
        self.tab_screen_open = false;
        self.partial_screen = None;
    }

    pub fn verdict(&mut self, r: &Recognition, now: Instant) -> PartialVerdict {
        if !matches!(r.kind, ScreenKind::Reward | ScreenKind::Choice) || r.slots <= r.cards.len() {
            return PartialVerdict::Complete;
        }
        if self.partial_screen != Some(r.screen_id) {
            self.partial_screen = Some(r.screen_id);
            self.partial_since = Some(now);
        }
        let since = self.partial_since.unwrap_or(now);
        if now.saturating_duration_since(since) < self.partial_wait { PartialVerdict::Hold } else { PartialVerdict::ShowIncomplete }
    }

    /// When a held verdict is due (re-render then even without a new frame).
    pub fn verdict_due(&self, r: &Recognition) -> Option<Instant> {
        (self.partial_screen == Some(r.screen_id) && r.slots > r.cards.len()).then(|| self.partial_since.map(|t| t + self.partial_wait)).flatten()
    }
}

/// Multiset inclusion of two sorted id lists.
fn is_subset(small: &[String], big: &[String]) -> bool {
    let mut j = 0;
    for id in small {
        while j < big.len() && big[j] < *id {
            j += 1;
        }
        if j == big.len() || big[j] != *id {
            return false;
        }
        j += 1;
    }
    true
}

/// Capture pause: after `after` of the overlay hidden (or the game not in front), stop capturing.
pub struct CapturePauseClock {
    pub after: Duration,
    idle_since: Option<Instant>,
}

impl Default for CapturePauseClock {
    fn default() -> Self {
        CapturePauseClock { after: Duration::from_secs(3), idle_since: None }
    }
}

impl CapturePauseClock {
    /// Whether capture should pause now, and when to ask again (grace period running).
    pub fn should_pause(&mut self, overlay_shown: bool, now: Instant) -> (bool, Option<Duration>) {
        if overlay_shown {
            self.idle_since = None;
            return (false, None);
        }
        let since = *self.idle_since.get_or_insert(now);
        let idle = now.saturating_duration_since(since);
        if idle >= self.after { (true, None) } else { (false, Some(self.after - idle)) }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::recognition::{Rect, SeenCard};

    fn screen(kind: ScreenKind, ids: &[&str], slots: usize, screen_id: i32, epoch: i32) -> Recognition {
        let cards = ids
            .iter()
            .map(|id| SeenCard {
                slot: 0,
                id: id.to_string(),
                upgrade: 0,
                bounds: Rect::default(),
                confidence: 1.0,
                price: None,
                unaffordable: None,
                on_sale: false,
                price_bounds: None,
                missing_portrait: false,
            })
            .collect();
        Recognition { kind, cards, slots, screen_id, epoch, ..Recognition::other() }
    }

    #[test]
    fn forget_drops_old_epochs_and_suppresses_the_closing_screen() {
        let t = Instant::now();
        let mut l = OfferLifecycle::default();
        let shown = screen(ScreenKind::Reward, &["A", "B", "C"], 3, 1, 0);
        l.forget(Some(&shown), 2, t);
        assert!(!l.accept(&screen(ScreenKind::Reward, &["X"], 1, 2, 1), t), "older epoch");
        assert!(!l.accept(&screen(ScreenKind::Reward, &["A", "C"], 3, 2, 2), t + Duration::from_millis(100)), "closing animation");
        assert!(l.accept(&screen(ScreenKind::Reward, &["A", "D"], 3, 3, 2), t), "other cards = a new screen");
        l.forget(Some(&shown), 2, t);
        assert!(l.accept(&screen(ScreenKind::Reward, &["A"], 3, 2, 2), t + Duration::from_millis(1500)), "suppression ran out");
        l.forget(Some(&shown), 2, t);
        assert!(l.accept(&Recognition { epoch: 2, ..Recognition::other() }, t), "seen closed");
        assert!(l.accept(&screen(ScreenKind::Reward, &["A"], 3, 2, 2), t), "after the close it shows at once");
    }

    #[test]
    fn select_tab_only_for_newly_opened_screens_and_partial_rows_wait() {
        let t = Instant::now();
        let mut l = OfferLifecycle::default();
        assert!(l.select_tab(&screen(ScreenKind::Reward, &["A", "B"], 3, 1, 0)));
        assert!(!l.select_tab(&screen(ScreenKind::Reward, &["A", "B", "C"], 3, 1, 0)), "a card read late");
        assert!(l.select_tab(&screen(ScreenKind::Shop, &["A"], 0, 2, 0)));
        assert!(!l.select_tab(&screen(ScreenKind::Shop, &["Z"], 0, 2, 0)), "a shop stays the same screen");
        l.screen_closed();
        assert!(l.select_tab(&screen(ScreenKind::Shop, &["Z"], 0, 3, 0)));

        let half = screen(ScreenKind::Reward, &["A", "B"], 3, 7, 0);
        assert_eq!(l.verdict(&half, t), PartialVerdict::Hold);
        assert_eq!(l.verdict_due(&half), Some(t + Duration::from_secs(2)));
        assert_eq!(l.verdict(&half, t + Duration::from_secs(2)), PartialVerdict::ShowIncomplete);
        assert_eq!(l.verdict(&screen(ScreenKind::Reward, &["A", "B", "C"], 3, 7, 0), t), PartialVerdict::Complete);
        assert_eq!(l.verdict(&screen(ScreenKind::Shop, &["A"], 5, 7, 0), t), PartialVerdict::Complete);
    }

    #[test]
    fn capture_pauses_after_three_seconds_hidden() {
        let t = Instant::now();
        let mut c = CapturePauseClock::default();
        assert_eq!(c.should_pause(true, t), (false, None));
        assert_eq!(c.should_pause(false, t), (false, Some(Duration::from_secs(3))));
        assert_eq!(c.should_pause(false, t + Duration::from_secs(1)), (false, Some(Duration::from_secs(2))));
        assert_eq!(c.should_pause(false, t + Duration::from_secs(3)), (true, None));
        assert_eq!(c.should_pause(true, t + Duration::from_secs(4)), (false, None));
    }
}
