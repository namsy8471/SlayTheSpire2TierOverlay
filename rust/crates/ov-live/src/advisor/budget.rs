//! Port of LineBudget (AdvisorProcessHost.cs): at most `max` lines per window; the dropped ones are counted and
//! reported in one line when the next window opens.

use std::sync::Mutex;
use std::time::{Duration, Instant};

pub struct LineBudget {
    max: usize,
    window: Duration,
    state: Mutex<(Option<Instant>, usize, usize)>,
}

impl LineBudget {
    pub fn new(max: usize, window: Duration) -> Self {
        LineBudget { max, window, state: Mutex::new((None, 0, 0)) }
    }

    pub fn next(&self, line: &str, now: Instant) -> Vec<String> {
        let mut s = self.state.lock().unwrap_or_else(|e| e.into_inner());
        let (start, used, dropped) = &mut *s;
        let mut lines = Vec::with_capacity(2);
        if start.is_none_or(|t| now.saturating_duration_since(t) >= self.window) {
            if *dropped > 0 {
                lines.push(format!("({dropped} more lines dropped)"));
            }
            *start = Some(now);
            *used = 0;
            *dropped = 0;
        }
        if *used < self.max {
            *used += 1;
            lines.push(cut(line, 400));
        } else {
            *dropped += 1;
        }
        lines
    }
}

/// The first `n` UTF-16 units like C# `line[..n] + "…"` (kept on a char boundary).
fn cut(line: &str, n: usize) -> String {
    if line.encode_utf16().count() <= n {
        return line.to_string();
    }
    let mut units = 0;
    let mut out = String::new();
    for ch in line.chars() {
        units += ch.len_utf16();
        if units > n {
            break;
        }
        out.push(ch);
    }
    out.push('…');
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn drops_over_budget_and_reports_next_window() {
        let b = LineBudget::new(2, Duration::from_secs(60));
        let t = Instant::now();
        assert_eq!(b.next("a", t), vec!["a"]);
        assert_eq!(b.next("b", t), vec!["b"]);
        assert!(b.next("c", t).is_empty());
        assert!(b.next("d", t + Duration::from_secs(59)).is_empty());
        assert_eq!(b.next("e", t + Duration::from_secs(60)), vec!["(2 more lines dropped)", "e"]);
        let long = "x".repeat(401);
        assert_eq!(b.next(&long, t + Duration::from_secs(60))[0].chars().count(), 401);
    }
}
