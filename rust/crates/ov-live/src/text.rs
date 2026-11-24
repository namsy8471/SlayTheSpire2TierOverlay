//! Names from the game's own localization (extracted to `<local>/pck/localization/<lang>/*.json` by the resource
//! preparer; never shipped). Port of the title lookups of CardTextProvider, CombatTextProvider and EventTextProvider.

use std::collections::HashMap;
use std::path::Path;

use ov_core::loc::Lang;

const FILES: [&str; 6] = ["cards", "relics", "potions", "events", "monsters", "encounters"];

#[derive(Default)]
pub struct GameText {
    /// (lang, file) -> key -> text.
    tables: HashMap<(Lang, &'static str), HashMap<String, String>>,
}

impl GameText {
    pub fn load(localization_dir: &Path) -> GameText {
        let mut t = GameText::default();
        for (lang, code) in [(Lang::Kor, "kor"), (Lang::Eng, "eng")] {
            for f in FILES {
                let Ok(text) = std::fs::read_to_string(localization_dir.join(code).join(format!("{f}.json"))) else { continue };
                let Ok(serde_json::Value::Object(o)) = serde_json::from_str(&text) else { continue };
                t.tables.insert((lang, f), o.into_iter().filter_map(|(k, v)| v.as_str().map(|s| (k, s.to_string()))).collect());
            }
        }
        t
    }

    pub fn is_empty(&self) -> bool {
        self.tables.is_empty()
    }

    fn get(&self, lang: Lang, file: &'static str, key: &str) -> Option<String> {
        self.tables.get(&(lang, file)).and_then(|t| t.get(key)).map(|s| clean(s))
    }

    /// A card's title (`ZAP`, `CARD.ZAP`), with `+` / `+n` for upgrades; the prettified id when unknown.
    pub fn card(&self, lang: Lang, id: &str, upgrade: i32) -> String {
        let e = entry(id);
        let base = self.get(lang, "cards", &format!("{e}.title")).unwrap_or_else(|| prettify(e));
        match upgrade {
            0 => base,
            1 => format!("{base}+"),
            n => format!("{base}+{n}"),
        }
    }

    pub fn relic(&self, lang: Lang, id: &str) -> String {
        let e = entry(id);
        self.get(lang, "relics", &format!("{e}.title")).unwrap_or_else(|| prettify(e))
    }

    pub fn potion(&self, lang: Lang, id: &str) -> String {
        let e = entry(id);
        self.get(lang, "potions", &format!("{e}.title")).unwrap_or_else(|| prettify(e))
    }

    pub fn monster(&self, lang: Lang, id: &str) -> String {
        let e = entry(id);
        self.get(lang, "monsters", &format!("{e}.name")).unwrap_or_else(|| prettify(e))
    }

    pub fn event(&self, lang: Lang, id: &str) -> String {
        let e = entry(id);
        self.get(lang, "events", &format!("{e}.title")).unwrap_or_else(|| prettify(e))
    }

    /// An event button by its text key (`NEOW.pages.INITIAL.options.NEW_LEAF`). Ancient options are relics whose key
    /// ends with the relic id, so the relic title is used when the event has none.
    pub fn event_option(&self, lang: Lang, key: &str) -> String {
        if let Some(t) = self.get(lang, "events", &format!("{key}.title")) {
            return t;
        }
        let last = key.rsplit('.').next().unwrap_or(key);
        self.get(lang, "relics", &format!("{last}.title")).unwrap_or_else(|| prettify(last))
    }
}

/// `CARD.X` -> `X`.
pub fn entry(id: &str) -> &str {
    id.split_once('.').map_or(id, |(_, e)| e)
}

/// `BALL_LIGHTNING` -> `Ball Lightning`.
pub fn prettify(id: &str) -> String {
    entry(id)
        .split('_')
        .filter(|w| !w.is_empty())
        .map(|w| {
            let mut c = w.chars();
            match c.next() {
                Some(f) => f.to_uppercase().collect::<String>() + &c.as_str().to_lowercase(),
                None => String::new(),
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

/// Drops BBCode-style tags (`[gold]`, `[/gold]`) the game puts in some titles.
pub fn clean(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut depth = 0;
    for ch in s.chars() {
        match ch {
            '[' => depth += 1,
            ']' if depth > 0 => depth -= 1,
            _ if depth == 0 => out.push(ch),
            _ => {}
        }
    }
    out.trim().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn titles_with_fallbacks() {
        let dir = std::env::temp_dir().join(format!("ov-live-text-{}", std::process::id()));
        std::fs::create_dir_all(dir.join("kor")).unwrap();
        std::fs::write(dir.join("kor").join("cards.json"), r#"{"ZAP.title":"전기 충격","ZAP.description":"x"}"#).unwrap();
        std::fs::write(dir.join("kor").join("relics.json"), r#"{"SCROLL_BOXES.title":"[gold]두루마리 상자[/gold]"}"#).unwrap();
        let t = GameText::load(&dir);
        assert_eq!(t.card(Lang::Kor, "CARD.ZAP", 1), "전기 충격+");
        assert_eq!(t.card(Lang::Eng, "CARD.BALL_LIGHTNING", 0), "Ball Lightning");
        assert_eq!(t.event_option(Lang::Kor, "NEOW.pages.INITIAL.options.SCROLL_BOXES"), "두루마리 상자");
        assert_eq!(t.event_option(Lang::Kor, "NEOW.pages.INITIAL.options.NEW_LEAF"), "New Leaf");
        let _ = std::fs::remove_dir_all(&dir);
    }
}
