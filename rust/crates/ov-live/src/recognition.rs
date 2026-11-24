//! Port of VisionRecognizer.cs and the App's RecognitionResult: one captured frame -> what the overlay shows on.

use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::Instant;

use ov_resources::manifest::{LocalLayout, ResourceManifest};
use ov_vision::artdb::CardArtDatabase;
use ov_vision::catalog::VisionCatalog;
use ov_vision::geometry::{PixelRect, ScreenFrame};
use ov_vision::recognizer::{CardRecognizer, RecognizerOptions};
use ov_vision::relics::{self, RelicIconDatabase, RelicRecognizer};
use ov_vision::rest::RestSiteResult;
use ov_vision::results::{RecognitionHints, ScreenKind as VisionKind};
use ov_vision::tracker::RecognitionTracker;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ScreenKind {
    Reward,
    Shop,
    Other,
    Choice,
    Grid,
}

/// A rectangle in screen pixels.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

impl Rect {
    fn of(r: &PixelRect, dx: i32, dy: i32) -> Rect {
        Rect { x: r.x + dx, y: r.y + dy, width: r.width, height: r.height }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct SeenCard {
    /// Layout slot (shop: 0-4 character cards, 5-6 colourless).
    pub slot: i32,
    pub id: String,
    pub upgrade: i32,
    pub bounds: Rect,
    pub confidence: f64,
    pub price: Option<i32>,
    pub unaffordable: Option<bool>,
    pub on_sale: bool,
    pub price_bounds: Option<Rect>,
    pub missing_portrait: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub struct SeenShopItem {
    /// `RELIC.X` / `POTION.X` as the recogniser names it.
    pub id: String,
    pub rarity: Option<String>,
    pub slot: i32,
    pub bounds: Rect,
    pub confidence: f64,
    pub price: Option<i32>,
    pub unaffordable: Option<bool>,
}

/// What one frame shows (C# RecognitionResult).
#[derive(Clone, Debug, PartialEq)]
pub struct Recognition {
    pub kind: ScreenKind,
    pub cards: Vec<SeenCard>,
    pub relics: Vec<SeenShopItem>,
    pub potions: Vec<SeenShopItem>,
    /// The shop's card-removal service, when it is still for sale.
    pub removal_service: Option<Rect>,
    pub rest_site: Option<RestSiteResult>,
    /// Event / ancient option buttons top to bottom: bounds (screen pixels) and whether the button is locked. Read
    /// only while an event room is open.
    pub event_buttons: Vec<(Rect, bool)>,
    /// Card slots of the layout (a reward row with fewer read cards is partial).
    pub slots: usize,
    /// Changes when the tracker sees a new screen.
    pub screen_id: i32,
    pub unsettled: bool,
    /// From [`VisionRecognizer::reset`] counts: results of an older epoch are dropped by the offer lifecycle.
    pub epoch: i32,
}

impl Recognition {
    pub fn other() -> Self {
        Recognition {
            kind: ScreenKind::Other,
            cards: Vec::new(),
            relics: Vec::new(),
            potions: Vec::new(),
            removal_service: None,
            rest_site: None,
            event_buttons: Vec::new(),
            slots: 0,
            screen_id: 0,
            unsettled: false,
            epoch: 0,
        }
    }

    /// Sorted card ids (multiset), as the lifecycle compares screens.
    pub fn sorted_ids(&self) -> Vec<String> {
        let mut v: Vec<String> = self.cards.iter().map(|c| c.id.clone()).collect();
        v.sort();
        v
    }
}

/// One captured frame (BGRA) and where it sits on the screen.
pub struct Frame<'a> {
    pub bgra: &'a [u8],
    pub width: i32,
    pub height: i32,
    pub stride: i32,
    /// Screen position of the frame's top-left pixel.
    pub origin: (i32, i32),
}

/// Loads the recogniser lazily from the local data and merges frames with the tracker.
pub struct VisionRecognizer {
    local_dir: PathBuf,
    committed_catalog: PathBuf,
    committed_relics: PathBuf,
    cache_path: PathBuf,
    state: Mutex<LoadState>,
    tracker: Mutex<RecognitionTracker>,
    log: Box<dyn Fn(&str) + Send + Sync>,
}

enum LoadState {
    NotYet,
    Ready(Box<CardRecognizer>),
    Unavailable,
}

impl VisionRecognizer {
    pub fn new(local_dir: &Path, committed_catalog: &Path, committed_relics: &Path, log: Box<dyn Fn(&str) + Send + Sync>) -> Self {
        VisionRecognizer {
            local_dir: local_dir.to_path_buf(),
            committed_catalog: committed_catalog.to_path_buf(),
            committed_relics: committed_relics.to_path_buf(),
            cache_path: local_dir.join("vision").join("card-art.bin"),
            state: Mutex::new(LoadState::NotYet),
            tracker: Mutex::new(RecognitionTracker::default()),
            log,
        }
    }

    /// Builds the recogniser now (background start-up). False when the local art is missing.
    pub fn preload(&self) -> bool {
        let mut s = self.state.lock().unwrap_or_else(|e| e.into_inner());
        self.ensure(&mut s);
        matches!(*s, LoadState::Ready(_))
    }

    /// After the resources were prepared again: load from scratch next time.
    pub fn reload(&self) {
        *self.state.lock().unwrap_or_else(|e| e.into_inner()) = LoadState::NotYet;
        self.reset();
    }

    pub fn reset(&self) {
        self.tracker.lock().unwrap_or_else(|e| e.into_inner()).reset();
    }

    /// None when no local art exists (behave like "can't tell").
    pub fn recognize(&self, frame: &Frame<'_>, character_hint: Option<&str>) -> Option<Recognition> {
        self.recognize_with(frame, character_hint, None)
    }

    /// [`VisionRecognizer::recognize`], plus the event buttons when `event_room` is Some (true = an ancient's room).
    pub fn recognize_with(&self, frame: &Frame<'_>, character_hint: Option<&str>, event_room: Option<bool>) -> Option<Recognition> {
        let mut s = self.state.lock().unwrap_or_else(|e| e.into_inner());
        self.ensure(&mut s);
        let LoadState::Ready(rec) = &*s else { return None };
        let vf = ScreenFrame { bgra: frame.bgra, width: frame.width, height: frame.height, stride: frame.stride };
        let raw = match rec.recognize(&vf, &RecognitionHints { character_id: character_hint.map(str::to_string) }) {
            Ok(r) => r,
            Err(e) => {
                (self.log)(&format!("[vision] frame rejected: {e}"));
                return Some(Recognition::other());
            }
        };
        let shown = self.tracker.lock().unwrap_or_else(|e| e.into_inner()).update(&raw, Instant::now());
        // Right-click inspect view: no badges, no rest marks.
        if raw.gate.inspect_likely {
            return Some(Recognition::other());
        }
        let kind = match shown.kind {
            VisionKind::RewardCardSelect => ScreenKind::Reward,
            VisionKind::Shop => ScreenKind::Shop,
            VisionKind::CardChoice => ScreenKind::Choice,
            VisionKind::CardGrid => ScreenKind::Grid,
            VisionKind::Other => ScreenKind::Other,
        };
        let rest = ov_vision::rest::detect(&vf);
        let rest_seen = (rest.choice_screen || rest.card_grid_open).then_some(rest);
        let (dx, dy) = frame.origin;
        if kind == ScreenKind::Other {
            let buttons = match event_room {
                Some(true) => ov_vision::events::ancient::detect(&vf),
                Some(false) => ov_vision::events::detect(&vf),
                None => None,
            };
            let mut event_buttons: Vec<(Rect, bool)> = buttons
                .map(|s| s.buttons.iter().map(|b| (Rect::of(&b.bounds, dx, dy), b.state == ov_vision::events::EventButtonState::Locked)).collect())
                .unwrap_or_default();
            event_buttons.sort_by_key(|(r, _)| r.y);
            return Some(Recognition { rest_site: rest_seen, event_buttons, ..Recognition::other() });
        }
        let cards = shown
            .cards
            .iter()
            .filter(|c| c.accepted && c.card_id.is_some())
            .map(|c| SeenCard {
                slot: c.slot,
                id: c.card_id.clone().unwrap_or_default(),
                upgrade: i32::from(c.upgraded),
                bounds: Rect::of(&c.card_bounds, dx, dy),
                confidence: f64::from(c.partial.as_ref().map_or(c.score, |p| p.score)),
                price: c.price,
                unaffordable: c.unaffordable(),
                on_sale: c.price_reading.as_ref().is_some_and(|p| p.on_sale()),
                price_bounds: c.price_bounds.as_ref().map(|p| Rect::of(p, dx, dy)),
                missing_portrait: c.missing_portrait,
            })
            .collect();
        let shop = kind == ScreenKind::Shop;
        let removal = shop.then(|| ov_vision::removal::read(&vf)).filter(|r| r.available).map(|r| Rect::of(&r.bounds, dx, dy));
        let items = |list: &[ov_vision::results::RecognizedRelic]| -> Vec<SeenShopItem> {
            if !shop {
                return Vec::new();
            }
            list.iter()
                .filter(|x| x.accepted() && x.relic_id.is_some())
                .map(|x| SeenShopItem {
                    id: x.relic_id.clone().unwrap_or_default(),
                    rarity: x.rarity.clone(),
                    slot: x.slot,
                    bounds: Rect::of(&x.icon_bounds, dx, dy),
                    confidence: f64::from(x.score),
                    price: x.price,
                    unaffordable: x.unaffordable(),
                })
                .collect()
        };
        Some(Recognition {
            kind,
            cards,
            relics: items(&shown.relics),
            potions: items(&shown.potions),
            removal_service: removal,
            rest_site: rest_seen,
            event_buttons: Vec::new(),
            slots: shown.slot_count,
            screen_id: shown.generation,
            unsettled: shown.unsettled,
            epoch: 0,
        })
    }

    fn ensure(&self, s: &mut LoadState) {
        if matches!(s, LoadState::NotYet) {
            *s = match self.load() {
                Some(r) => LoadState::Ready(Box::new(r)),
                None => LoadState::Unavailable,
            };
        }
    }

    fn load(&self) -> Option<CardRecognizer> {
        let layout = LocalLayout::new(&self.local_dir);
        let art_root = layout.card_art_dir();
        if !art_root.is_dir() {
            (self.log)(&format!("card art not found at {}; it is generated on start. Recognition disabled until then.", art_root.display()));
            return None;
        }
        // The catalog generated from the installed game knows cards a game update added; the committed one is the fallback.
        let catalog = layout.manifest_catalog().unwrap_or_else(|| self.committed_catalog.clone());
        let catalog = catalog.is_file().then_some(catalog);
        let mut log = |m: String| (self.log)(&format!("[vision] {m}"));
        let db = match CardArtDatabase::load_or_build(&art_root, catalog.as_deref(), &self.cache_path, &mut log) {
            Ok(db) => db,
            Err(e) => {
                (self.log)(&format!("card art database failed to load; recognition disabled: {e}"));
                return None;
            }
        };
        let mut rec = CardRecognizer::new(db, RecognizerOptions::default());
        rec.catalog = catalog.as_deref().and_then(|c| VisionCatalog::load(c).ok());
        let manifest = ResourceManifest::try_load(&layout.manifest_path());
        rec.relics = self.load_relics(&layout, manifest.as_ref());
        rec.potions = load_potions(&layout, manifest.as_ref(), &*self.log);
        (self.log)(&format!(
            "[vision] recognizer ready (catalog {})",
            catalog.as_deref().and_then(Path::file_name).map_or("none".into(), |n| n.to_string_lossy().into_owned())
        ));
        Some(rec)
    }

    fn load_relics(&self, layout: &LocalLayout, manifest: Option<&ResourceManifest>) -> Option<RelicRecognizer> {
        let icons = layout.relic_icon_dir();
        if !icons.is_dir() || !self.committed_relics.is_file() {
            (self.log)(&format!(
                "[vision] shop relics off (icons {}, catalog {})",
                if icons.is_dir() { "ok" } else { "missing" },
                if self.committed_relics.is_file() { "ok" } else { "missing" }
            ));
            return None;
        }
        let built = relics::load_relic_catalog(&self.committed_relics).and_then(|cat| RelicIconDatabase::build(&icons, &cat, &mut |m| (self.log)(&format!("[vision] {m}"))));
        match built {
            Ok(db) => {
                (self.log)(&format!("[vision] shop relic reader ready ({} relics)", db.count()));
                let x = manifest.and_then(|m| m.relic_shelf_x).unwrap_or(relics::SLOT_X0);
                let y = manifest.and_then(|m| m.relic_shelf_y).unwrap_or(relics::SLOT_Y);
                Some(RelicRecognizer::for_relics(db, x, y))
            }
            Err(e) => {
                (self.log)(&format!("relic icons failed to load; shop relics off: {e}"));
                None
            }
        }
    }
}

fn load_potions(layout: &LocalLayout, manifest: Option<&ResourceManifest>, log: &dyn Fn(&str)) -> Option<RelicRecognizer> {
    let dir = layout.potion_icon_dir();
    if !dir.is_dir() {
        return None;
    }
    match RelicIconDatabase::build_potions(&dir) {
        Ok(db) if db.count() > 0 => {
            let x = manifest.and_then(|m| m.potion_shelf_x).unwrap_or(relics::SLOT_X0);
            let y = manifest.and_then(|m| m.potion_shelf_y).unwrap_or(819.0);
            Some(RelicRecognizer::for_potions(db, x, y))
        }
        Ok(_) => None,
        Err(e) => {
            log(&format!("potion icons failed to load; shop potions off: {e}"));
            None
        }
    }
}
