//! Display language and the Korean/English string pairs (`Sts2Overlay.Core.Localization.Loc`).

use std::cell::Cell;
use std::collections::HashMap;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU8, Ordering};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Lang {
    Kor,
    Eng,
}

static GLOBAL: AtomicU8 = AtomicU8::new(0);
static LISTENERS: Mutex<Vec<Box<dyn Fn() + Send>>> = Mutex::new(Vec::new());

thread_local! {
    static SCOPE: Cell<Option<Lang>> = const { Cell::new(None) };
}

fn from_u8(v: u8) -> Lang {
    if v == 1 { Lang::Eng } else { Lang::Kor }
}

/// The language strings are produced in now (this thread's override, else the global one).
pub fn current() -> Lang {
    SCOPE.with(|s| s.get()).unwrap_or_else(|| from_u8(GLOBAL.load(Ordering::SeqCst)))
}

pub fn is_english() -> bool {
    current() == Lang::Eng
}

/// Sets the process-wide language; listeners run only when it changed.
pub fn set_language(lang: Lang) {
    let v = if lang == Lang::Eng { 1 } else { 0 };
    if GLOBAL.swap(v, Ordering::SeqCst) == v {
        return;
    }
    for f in LISTENERS.lock().unwrap().iter() {
        f();
    }
}

/// Runs `f` after every global language change (the C# `Changed` event).
pub fn on_changed(f: impl Fn() + Send + 'static) {
    LISTENERS.lock().unwrap().push(Box::new(f));
}

/// Overrides the language on this thread until the guard drops (tests, previews).
pub fn use_lang(lang: Lang) -> ScopeGuard {
    let previous = SCOPE.with(|s| s.replace(Some(lang)));
    ScopeGuard { previous }
}

pub struct ScopeGuard {
    previous: Option<Lang>,
}

impl Drop for ScopeGuard {
    fn drop(&mut self) {
        SCOPE.with(|s| s.set(self.previous));
    }
}

/// The text for the current language.
pub fn t<'a>(kor: &'a str, eng: &'a str) -> &'a str {
    if current() == Lang::Eng { eng } else { kor }
}

/// Owned variant for formatted pairs: `tr(format!(..), format!(..))`.
pub fn tr(kor: String, eng: String) -> String {
    if current() == Lang::Eng { eng } else { kor }
}

pub fn code(lang: Lang) -> &'static str {
    if lang == Lang::Eng { "eng" } else { "kor" }
}

pub fn current_code() -> &'static str {
    code(current())
}

/// `settings.save` language -> overlay language: `kor` -> Korean, any other -> English, blank -> None.
pub fn from_game_code(code: Option<&str>) -> Option<Lang> {
    let c = code?.trim();
    if c.is_empty() {
        return None;
    }
    Some(if c.eq_ignore_ascii_case("kor") { Lang::Kor } else { Lang::Eng })
}

/// Name for the current language from a `{"kor":..,"eng":..}` map: current, then English, Korean, any.
/// `order` gives the map's own key order for the "any" fallback (C# dictionary order).
pub fn pick<'a>(names: &'a HashMap<String, String>, order: &[String]) -> Option<&'a str> {
    let get = |k: &str| names.get(k).map(String::as_str).filter(|v| !v.is_empty());
    get(current_code())
        .or_else(|| get("eng"))
        .or_else(|| get("kor"))
        .or_else(|| order.iter().filter_map(|k| get(k)).next())
        .or_else(|| names.values().map(String::as_str).find(|v| !v.is_empty()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scope_overrides_and_restores() {
        let _k = use_lang(Lang::Kor);
        assert_eq!(t("가", "a"), "가");
        {
            let _e = use_lang(Lang::Eng);
            assert_eq!(t("가", "a"), "a");
        }
        assert_eq!(t("가", "a"), "가");
        assert_eq!(from_game_code(Some(" KOR ")), Some(Lang::Kor));
        assert_eq!(from_game_code(Some("deu")), Some(Lang::Eng));
        assert_eq!(from_game_code(Some("  ")), None);
    }
}
