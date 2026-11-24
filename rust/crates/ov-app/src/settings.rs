//! `settings.json`, shared with the C# app (`AppSettings` + `SettingsStore`): camelCase keys, `//` and `/* */`
//! comments and trailing commas allowed. Keys this crate does not know are kept as they are and written back, so the
//! other parts of the app (and the C# app during the switch) lose nothing.
//!
//! A broken file: the app runs on defaults, keeps the file as `settings.json.bad`, and does not overwrite the original
//! unless the user changes a setting (same rule as C#).

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::path::Path;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    pub panel_x: Option<i32>,
    pub panel_y: Option<i32>,
    pub panel_collapsed: bool,
    pub selected_tab: Option<String>,
    /// "UI 배율" override (1.5 = 150 %). None = automatic from the game's height.
    pub ui_scale: Option<f64>,
    pub show_overlay_in_capture: bool,
    pub always_on_top: bool,
    pub map_overlay: bool,
    pub combat_advisor_enabled: bool,
    /// "auto", "kor" or "eng".
    pub language: String,
    pub debug_captures: bool,
    pub panel_opacity: f64,
    pub auto_update: bool,
    pub update_url: Option<String>,
    /// Every other key, kept for the parts of the app that own them.
    #[serde(flatten)]
    pub other: Map<String, Value>,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            panel_x: None,
            panel_y: None,
            panel_collapsed: false,
            selected_tab: None,
            ui_scale: None,
            show_overlay_in_capture: true,
            always_on_top: true,
            map_overlay: true,
            combat_advisor_enabled: true,
            language: "auto".into(),
            debug_captures: false,
            panel_opacity: 0.86,
            auto_update: true,
            update_url: None,
            other: Map::new(),
        }
    }
}

/// Saved on every exit whether or not the user touched them: not a user change (C# `SessionKeys`).
const SESSION_KEYS: [&str; 4] = ["panelX", "panelY", "selectedTab", "tierCharacter"];

#[derive(Debug, Default)]
pub struct Store {
    pub settings: Settings,
    /// Set when the file could not be read: the user's choices at start, to tell whether they changed anything.
    broken_choices: Option<String>,
}

impl Store {
    pub fn load(path: &Path) -> Store {
        let text = match std::fs::read_to_string(path) {
            Ok(t) => t,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Store::default(),
            Err(e) => return Store::broken(path, &e.to_string()),
        };
        match serde_json::from_str::<Settings>(&strip_jsonc(&text)) {
            Ok(settings) => Store { settings, broken_choices: None },
            Err(e) => Store::broken(path, &e.to_string()),
        }
    }

    fn broken(path: &Path, why: &str) -> Store {
        crate::applog::warn(format!("settings: could not read {}, using defaults ({why})", path.display()));
        let mut bad = path.as_os_str().to_owned();
        bad.push(".bad");
        match std::fs::copy(path, &bad) {
            Ok(_) => crate::applog::warn("settings: the unreadable file was copied to settings.json.bad"),
            Err(e) => crate::applog::warn(format!("settings: could not copy it to settings.json.bad ({e})")),
        }
        let settings = Settings::default();
        let broken_choices = Some(user_choices(&settings));
        Store { settings, broken_choices }
    }

    pub fn loaded_from_broken_file(&self) -> bool {
        self.broken_choices.is_some()
    }

    /// Writes through a temp file. Skipped while running on defaults after a broken file and nothing was changed.
    pub fn save(&mut self, path: &Path) -> std::io::Result<()> {
        if self.broken_choices.as_deref() == Some(user_choices(&self.settings).as_str()) {
            crate::applog::info("settings: not saved (settings.json could not be read at start and no setting was changed)");
            return Ok(());
        }
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let mut tmp = path.as_os_str().to_owned();
        tmp.push(".tmp");
        let json = serde_json::to_string_pretty(&self.settings).map_err(std::io::Error::other)?;
        std::fs::write(&tmp, json)?;
        std::fs::rename(&tmp, path)?;
        self.broken_choices = None;
        Ok(())
    }
}

fn user_choices(s: &Settings) -> String {
    let mut v = serde_json::to_value(s).unwrap_or(Value::Null);
    if let Value::Object(m) = &mut v {
        for k in SESSION_KEYS {
            m.remove(k);
        }
    }
    v.to_string()
}

/// Removes `//` and `/* */` comments and trailing commas outside strings, so serde_json reads what System.Text.Json
/// read with `ReadCommentHandling.Skip` and `AllowTrailingCommas`.
pub fn strip_jsonc(src: &str) -> String {
    let b = src.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        match b[i] {
            b'"' => {
                out.push(b'"');
                i += 1;
                while i < b.len() {
                    out.push(b[i]);
                    if b[i] == b'\\' && i + 1 < b.len() {
                        out.push(b[i + 1]);
                        i += 2;
                        continue;
                    }
                    i += 1;
                    if b[i - 1] == b'"' {
                        break;
                    }
                }
            }
            b'/' if b.get(i + 1) == Some(&b'/') => {
                while i < b.len() && b[i] != b'\n' {
                    i += 1;
                }
            }
            b'/' if b.get(i + 1) == Some(&b'*') => {
                i += 2;
                while i < b.len() && !(b[i] == b'*' && b.get(i + 1) == Some(&b'/')) {
                    i += 1;
                }
                i = (i + 2).min(b.len());
            }
            b',' => {
                let mut j = i + 1;
                // A comment may sit between the comma and the closing bracket.
                let rest = strip_ws_and_comments(&b[j..]);
                j += rest;
                if !matches!(b.get(j), Some(b'}') | Some(b']')) {
                    out.push(b',');
                }
                i += 1;
            }
            c => {
                out.push(c);
                i += 1;
            }
        }
    }
    String::from_utf8(out).unwrap_or_default()
}

/// Length of the leading whitespace and comments.
fn strip_ws_and_comments(b: &[u8]) -> usize {
    let mut i = 0;
    loop {
        while i < b.len() && b[i].is_ascii_whitespace() {
            i += 1;
        }
        if b.get(i) == Some(&b'/') && b.get(i + 1) == Some(&b'/') {
            while i < b.len() && b[i] != b'\n' {
                i += 1;
            }
        } else if b.get(i) == Some(&b'/') && b.get(i + 1) == Some(&b'*') {
            i += 2;
            while i < b.len() && !(b[i] == b'*' && b.get(i + 1) == Some(&b'/')) {
                i += 1;
            }
            i = (i + 2).min(b.len());
        } else {
            return i;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_c_sharp_file_and_keeps_unknown_keys() {
        let text = r#"{
  // comment
  "panelX": 10, "panelY": -5, /* block */
  "alwaysOnTop": false,
  "updateUrl": "C:\\vpk\\out // not a comment",
  "recognizeHz": 3,
  "packDir": null,
}"#;
        let s: Settings = serde_json::from_str(&strip_jsonc(text)).expect("parse");
        assert_eq!((s.panel_x, s.panel_y, s.always_on_top), (Some(10), Some(-5), false));
        assert_eq!(s.update_url.as_deref(), Some("C:\\vpk\\out // not a comment"));
        assert_eq!(s.panel_opacity, 0.86, "missing key = C# default");
        assert_eq!(s.other.get("recognizeHz"), Some(&Value::from(3)));
        let back = serde_json::to_value(&s).unwrap();
        assert!(back.get("packDir").is_some(), "unknown keys are written back: {back}");
    }

    #[test]
    fn broken_file_is_kept_and_not_overwritten_until_a_change() {
        let dir = std::env::temp_dir().join(format!("ov-app-settings-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("settings.json");
        std::fs::write(&path, "{ broken").unwrap();
        let mut store = Store::load(&path);
        assert!(store.loaded_from_broken_file());
        assert!(dir.join("settings.json.bad").exists());
        store.settings.panel_x = Some(3); // session key only
        store.save(&path).unwrap();
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "{ broken");
        store.settings.always_on_top = false;
        store.save(&path).unwrap();
        assert!(!Store::load(&path).settings.always_on_top);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
