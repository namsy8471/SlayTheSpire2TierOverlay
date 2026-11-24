//! Port of ResourcePreparer.cs: generates the local resources in-process.
//!
//! 1. localization (kor, eng) from the pck -> `pck/localization/<lang>/*.json`
//! 2. card portraits -> `pck/card_art/<pool>/[beta/]<id>.webp`, relic and potion icons
//! 3. the card catalog from sts2.dll (+ names from step 1) -> `catalog.<version>.json`
//! 4. `manifest.json` with the install fingerprint (last)
//!
//! Everything goes to a staging folder first and is swapped in at the end, so a failure or cancel keeps the old
//! resources. A lock file stops two processes from preparing at once.

use std::path::{Path, PathBuf};
use std::time::Instant;

use ov_core::loc;

use crate::catalog::{self, CatalogFile, CatalogOptions};
use crate::install::{InstalledGame, ticks_of};
use crate::manifest::{LocalLayout, ResourceManifest};
use crate::pck::{PckReader, extract, relic_icons};

/// Bump when the generated layout or content changes (same value as the C# overlay, so its output is reused).
pub const FORMAT_VERSION: i32 = 4;
pub const CARD_ART_GLOB: &str = "images/packed/card_portraits/**/*.png";
pub const LOCALIZATION_LANGUAGES: [&str; 2] = ["kor", "eng"];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PrepareNeed {
    UpToDate,
    Missing,
    Stale,
}

#[derive(Clone, Debug)]
pub struct PrepareCheck {
    pub need: PrepareNeed,
    pub reason: String,
    pub manifest: Option<ResourceManifest>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PrepareProgress {
    pub fraction: f64,
    pub text: String,
}

#[derive(Debug)]
pub enum PrepareError {
    /// Another process is preparing right now.
    Busy(String),
    Cancelled,
    Failed(String),
}

impl std::fmt::Display for PrepareError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            PrepareError::Busy(s) | PrepareError::Failed(s) => f.write_str(s),
            PrepareError::Cancelled => f.write_str("cancelled"),
        }
    }
}

impl std::error::Error for PrepareError {}

fn fail(e: impl std::fmt::Display) -> PrepareError {
    let s = e.to_string();
    if s == "cancelled" { PrepareError::Cancelled } else { PrepareError::Failed(s) }
}

pub type CatalogBuilder = Box<dyn Fn(&CatalogOptions) -> Result<CatalogFile, String> + Send + Sync>;

pub struct ResourcePreparer {
    pub layout: LocalLayout,
    build_catalog: CatalogBuilder,
}

fn non_empty_dir(p: &Path) -> bool {
    std::fs::read_dir(p).is_ok_and(|mut d| d.next().is_some())
}

impl ResourcePreparer {
    pub fn new(layout: LocalLayout) -> Self {
        ResourcePreparer { layout, build_catalog: Box::new(|o| catalog::build(o).map(|r| r.catalog)) }
    }

    pub fn with_catalog_builder(layout: LocalLayout, build: CatalogBuilder) -> Self {
        ResourcePreparer { layout, build_catalog: build }
    }

    /// Cheap check: the manifest against the game's fingerprint, and the outputs on disk.
    pub fn check(&self, game: &InstalledGame) -> PrepareCheck {
        let l = &self.layout;
        let Some(m) = ResourceManifest::try_load(&l.manifest_path()) else {
            return PrepareCheck { need: PrepareNeed::Missing, reason: "no manifest.json".into(), manifest: None };
        };
        let r = |need, reason: String, m: ResourceManifest| PrepareCheck { need, reason, manifest: Some(m) };
        if m.prepare_format != FORMAT_VERSION {
            let why = format!("prepare format {} -> {FORMAT_VERSION}", m.prepare_format);
            return r(PrepareNeed::Stale, why, m);
        }
        if m.fingerprint.as_ref() != Some(&game.fingerprint) {
            let old = m.fingerprint.as_ref().map(|f| f.to_string()).unwrap_or_default();
            let why = format!("game files changed: {old} -> {}", game.fingerprint);
            return r(PrepareNeed::Stale, why, m);
        }
        if m.catalog_file.as_ref().is_none_or(|c| !l.local_dir.join(c).is_file()) {
            return r(PrepareNeed::Missing, "catalog file missing".into(), m);
        }
        if !non_empty_dir(&l.card_art_dir()) {
            return r(PrepareNeed::Missing, "card art missing".into(), m);
        }
        if !l.localization_dir().is_dir() {
            return r(PrepareNeed::Missing, "localization missing".into(), m);
        }
        if m.relic_icon_files > 0 && !l.relic_icon_dir().is_dir() {
            return r(PrepareNeed::Missing, "relic icons missing".into(), m);
        }
        if m.potion_icon_files > 0 && !l.potion_icon_dir().is_dir() {
            return r(PrepareNeed::Missing, "potion icons missing".into(), m);
        }
        r(PrepareNeed::UpToDate, "up to date".into(), m)
    }

    /// Runs all steps and returns the new manifest. On failure the old resources stay in place.
    pub fn prepare(
        &self,
        game: &InstalledGame,
        progress: &mut dyn FnMut(PrepareProgress),
        cancel: &dyn Fn() -> bool,
    ) -> Result<ResourceManifest, PrepareError> {
        let started = Instant::now();
        let l = &self.layout;
        std::fs::create_dir_all(&l.local_dir).map_err(fail)?;
        let _lock = acquire_lock(&l.lock_path())?;
        let staging = l.staging_dir();
        delete_directory(&staging);
        std::fs::create_dir_all(&staging).map_err(fail)?;
        let result = self.prepare_in(game, &staging, started, progress, cancel);
        delete_directory(&staging);
        result
    }

    fn prepare_in(
        &self,
        game: &InstalledGame,
        staging: &Path,
        started: Instant,
        progress: &mut dyn FnMut(PrepareProgress),
        cancel: &dyn Fn() -> bool,
    ) -> Result<ResourceManifest, PrepareError> {
        let l = &self.layout;
        let st_loc = staging.join("localization");
        let st_art = staging.join("card_art");
        let version_label = game.version_label();
        let catalog_name = l.catalog_path(&version_label).file_name().unwrap().to_string_lossy().to_string();
        let st_catalog = staging.join(&catalog_name);
        const LOC_END: f64 = 0.05;
        const ART_END: f64 = 0.75;
        const CATALOG_END: f64 = 0.97;
        let st_relics = staging.join("relic_icons");
        let st_potions = staging.join("potion_icons");
        let (mut loc_files, mut relic_files, mut potion_files) = (0, 0, 0);
        let (mut potion_shelf, mut relic_shelf) = ((None, None), (None, None));
        progress(PrepareProgress { fraction: 0.0, text: loc::t("리소스 준비: 게임 파일 여는 중", "Preparing resources: opening game files").into() });
        let art_files;
        {
            let pck = PckReader::open(&game.install.pck_path()).map_err(fail)?;
            for lang in LOCALIZATION_LANGUAGES {
                loc_files += extract::extract_files(&pck, &format!("localization/{lang}/*.json"), &st_loc.join(lang), cancel).map_err(fail)?;
            }
            progress(PrepareProgress {
                fraction: LOC_END,
                text: loc::tr(format!("리소스 준비: 현지화 {loc_files}개"), format!("Preparing resources: localization files {loc_files}")),
            });
            let mut last_percent = -1i64;
            let mut art_progress = |done: usize, total: usize| {
                let percent = (done * 100).checked_div(total).map_or(100, |p| p as i64);
                if percent == last_percent && done != total {
                    return;
                }
                last_percent = percent;
                progress(PrepareProgress {
                    fraction: LOC_END + (ART_END - LOC_END) * done as f64 / total.max(1) as f64,
                    text: loc::tr(format!("리소스 준비: 카드 그림 {done}/{total}"), format!("Preparing resources: card art {done}/{total}")),
                });
            };
            let art = extract::extract_images(&pck, CARD_ART_GLOB, &st_art, &mut art_progress, cancel).map_err(fail)?;
            art_files = art.written;
            if art_files == 0 {
                return Err(PrepareError::Failed(format!("no card portraits found in {} ({CARD_ART_GLOB})", game.install.pck_path().display())));
            }
            if pck.find(relic_icons::ATLAS_IMPORT).is_some() {
                relic_files = relic_icons::extract(&pck, &st_relics, cancel).map_err(fail)?;
                relic_shelf = relic_icons::merchant_shelf(&pck, "Relics");
            }
            if pck.find(relic_icons::POTION_ATLAS_IMPORT).is_some() {
                potion_files = relic_icons::extract_potions(&pck, &st_potions, cancel).map_err(fail)?;
                potion_shelf = relic_icons::merchant_shelf(&pck, "Potions");
            }
        }
        if cancel() {
            return Err(PrepareError::Cancelled);
        }
        progress(PrepareProgress {
            fraction: ART_END,
            text: loc::t("리소스 준비: 카드 목록 분석 중 (sts2.dll)", "Preparing resources: reading the card list (sts2.dll)").into(),
        });
        let options = CatalogOptions {
            dll_path: game.install.dll_path(),
            localization_dir: Some(st_loc.clone()),
            game_version: version_label.clone(),
            ..Default::default()
        };
        let catalog = (self.build_catalog)(&options).map_err(PrepareError::Failed)?;
        catalog::write(&catalog, &st_catalog).map_err(fail)?;
        let n_cards = catalog.cards.len();
        progress(PrepareProgress { fraction: CATALOG_END, text: loc::tr(format!("리소스 준비: 카드 {n_cards}장"), format!("Preparing resources: cards {n_cards}")) });

        if cancel() {
            return Err(PrepareError::Cancelled);
        }
        std::fs::create_dir_all(l.pck_dir()).map_err(fail)?;
        replace_directory(&st_loc, &l.localization_dir()).map_err(fail)?;
        replace_directory(&st_art, &l.card_art_dir()).map_err(fail)?;
        if relic_files > 0 {
            replace_directory(&st_relics, &l.relic_icon_dir()).map_err(fail)?;
        }
        if potion_files > 0 {
            replace_directory(&st_potions, &l.potion_icon_dir()).map_err(fail)?;
        }
        let target = l.local_dir.join(&catalog_name);
        let _ = std::fs::remove_file(&target);
        std::fs::rename(&st_catalog, &target).map_err(fail)?;

        let manifest = ResourceManifest {
            prepare_format: FORMAT_VERSION,
            game_version: game.version.as_ref().map(|v| v.text.clone()),
            game_commit: game.release.as_ref().and_then(|r| r.commit.clone()),
            game_dir: Some(game.install.directory.to_string_lossy().to_string()),
            fingerprint: Some(game.fingerprint.clone()),
            prepared_at_utc: crate::manifest::now_iso(),
            duration_seconds: (started.elapsed().as_secs_f64() * 100.0).round_ties_even() / 100.0,
            catalog_file: Some(catalog_name),
            catalog_cards: n_cards as i32,
            card_art_files: art_files as i32,
            localization_files: loc_files as i32,
            relic_icon_files: relic_files as i32,
            potion_icon_files: potion_files as i32,
            potion_shelf_x: potion_shelf.0,
            potion_shelf_y: potion_shelf.1,
            relic_shelf_x: relic_shelf.0,
            relic_shelf_y: relic_shelf.1,
            ..Default::default()
        };
        manifest.save(&l.manifest_path()).map_err(fail)?;
        let secs = manifest.duration_seconds;
        progress(PrepareProgress { fraction: 1.0, text: loc::tr(format!("리소스 준비 완료 ({secs:.1}초)"), format!("Resources ready ({secs:.1}s)")) });
        Ok(manifest)
    }
}

/// An exclusive lock file that is deleted when dropped.
pub struct PrepareLock {
    _file: std::fs::File,
    #[cfg(not(windows))]
    path: PathBuf,
}

#[cfg(not(windows))]
impl Drop for PrepareLock {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.path);
    }
}

fn busy(e: std::io::Error) -> PrepareError {
    PrepareError::Busy(format!("{} ({e})", loc::t("다른 프로세스가 리소스를 준비하는 중입니다", "Another process is preparing the resources")))
}

#[cfg(windows)]
fn acquire_lock(path: &Path) -> Result<PrepareLock, PrepareError> {
    use std::os::windows::fs::OpenOptionsExt;
    const FILE_FLAG_DELETE_ON_CLOSE: u32 = 0x0400_0000;
    let f = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .share_mode(0)
        .custom_flags(FILE_FLAG_DELETE_ON_CLOSE)
        .open(path)
        .map_err(busy)?;
    Ok(PrepareLock { _file: f })
}

#[cfg(not(windows))]
fn acquire_lock(path: &Path) -> Result<PrepareLock, PrepareError> {
    let f = std::fs::OpenOptions::new().read(true).write(true).create_new(true).open(path).map_err(busy)?;
    Ok(PrepareLock { _file: f, path: path.to_path_buf() })
}

/// Moves `source` to `target`, replacing it (the old copy is renamed away first, then deleted).
fn replace_directory(source: &Path, target: &Path) -> std::io::Result<()> {
    let mut old = None;
    if target.is_dir() {
        let o = PathBuf::from(format!("{}.old-{}", target.display(), ticks_of(std::time::SystemTime::now())));
        std::fs::rename(target, &o)?;
        old = Some(o);
    }
    std::fs::rename(source, target)?;
    if let Some(o) = old {
        delete_directory(&o);
    }
    Ok(())
}

/// Best-effort recursive delete of a folder we generated (a reader may hold a file for a moment).
fn delete_directory(dir: &Path) {
    for attempt in 0..3u64 {
        if !dir.exists() {
            return;
        }
        if std::fs::remove_dir_all(dir).is_err() {
            std::thread::sleep(std::time::Duration::from_millis(100 * (attempt + 1)));
        }
    }
}
