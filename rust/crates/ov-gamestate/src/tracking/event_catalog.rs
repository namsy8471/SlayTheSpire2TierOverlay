//! First-page event buttons from the event pack (`data/packs/<v>/events.json`), for event rooms whose offered options
//! the log does not name. A solo game logs no `EventStarted` line, so a seed-predicted event has no option keys of
//! its own. Port of the C# `EventScorer` fallback: the page's buttons as `EventSlots.Displayed` lists them.

use std::collections::HashMap;
use std::sync::OnceLock;

use serde_json::Value;

/// The newest event pack in the repository (the game's v0.113 events are unchanged from it for this purpose).
static EVENTS_JSON: &str = include_str!("../../../../../data/packs/v0.111.0/events.json");

/// More buttons than any event screen shows: the page is a random subset (Colorful Philosophers, Endless Conveyor),
/// so the full list would advise options that are not on screen.
const MAX_BUTTONS: usize = 4;

fn table() -> &'static HashMap<String, Vec<String>> {
    static T: OnceLock<HashMap<String, Vec<String>>> = OnceLock::new();
    T.get_or_init(|| {
        let mut m = HashMap::new();
        let Ok(root) = serde_json::from_str::<Value>(EVENTS_JSON) else { return m };
        for e in root["events"].as_array().into_iter().flatten() {
            let (Some(id), Some(pages)) = (e["id"].as_str(), e["pages"].as_array()) else { continue };
            let Some(page) = pages.iter().find(|p| p["page"].as_str() == Some("INITIAL")) else { continue };
            let opts: Vec<(&str, &str)> = page["options"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(|o| Some((o["key"].as_str()?, o["condition"].as_str().unwrap_or(""))))
                .collect();
            m.insert(id.to_string(), displayed(&opts));
        }
        m
    })
}

/// `EventSlots.Displayed`: locked faces (`X_LOCKED`, `LOCKED`) left out, and "only option when …" fallbacks unless
/// nothing else is left.
fn displayed(options: &[(&str, &str)]) -> Vec<String> {
    let open: Vec<&(&str, &str)> = options.iter().filter(|(k, _)| !k.ends_with("_LOCKED") && k.rsplit('.').next() != Some("LOCKED")).collect();
    let lone = |c: &str| {
        let c = c.to_ascii_lowercase();
        c.starts_with("only option when") || c.starts_with("only when no")
    };
    let main: Vec<String> = open.iter().filter(|(_, c)| !lone(c)).map(|(k, _)| k.to_string()).collect();
    if main.is_empty() { open.iter().map(|(k, _)| k.to_string()).collect() } else { main }
}

/// The first page's option keys in button order (`EVENT.WOOD_CARVINGS` -> `WOOD_CARVINGS.pages.INITIAL.options.BIRD`,
/// …); None for an unknown event or a random-subset page.
pub fn initial_options(event_id: &str) -> Option<Vec<String>> {
    table().get(event_id).filter(|k| !k.is_empty() && k.len() <= MAX_BUTTONS).cloned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn first_page_buttons_from_the_pack() {
        let o = |k: &str| format!("WOOD_CARVINGS.pages.INITIAL.options.{k}");
        assert_eq!(initial_options("EVENT.WOOD_CARVINGS"), Some(vec![o("BIRD"), o("SNAKE"), o("TORUS")]));
        // Random subsets and unknown ids give nothing rather than options that may not be on screen.
        for id in ["EVENT.COLORFUL_PHILOSOPHERS", "EVENT.ENDLESS_CONVEYOR", "EVENT.NOT_AN_EVENT"] {
            assert_eq!(initial_options(id), None, "{id}");
        }
        let d = displayed(&[("E.pages.P.options.A", ""), ("E.pages.P.options.A_LOCKED", ""), ("E.pages.P.options.ALONE", "Only option when nothing else")]);
        assert_eq!(d, vec!["E.pages.P.options.A".to_string()]);
    }
}
