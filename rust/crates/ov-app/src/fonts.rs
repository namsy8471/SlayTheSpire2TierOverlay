//! Korean glyphs: egui's built-in fonts have no Hangul, so the system's Malgun Gothic is added as a fallback.
//! Headings use the "title" family: the game's own Korean heading font when it could be read from the user's game
//! files (cut out of the .pck once and cached under %LOCALAPPDATA%), otherwise the same fonts as the body. The font
//! is never shipped with the app.

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};

/// The game's Korean heading font inside its pack.
const GAME_TITLE_FONT: &str = "fonts/kor/GyeonggiCheonnyeonBatangBold.ttf";

static HAS_TITLE: AtomicBool = AtomicBool::new(false);

/// The font for headings (panel title, section heads).
pub fn title_font(size: f32) -> egui::FontId {
    if HAS_TITLE.load(Ordering::Relaxed) {
        egui::FontId::new(size, egui::FontFamily::Name("title".into()))
    } else {
        egui::FontId::proportional(size)
    }
}

pub fn install(ctx: &egui::Context, title: Option<(String, Vec<u8>)>) {
    let windir = std::env::var_os("WINDIR").map(std::path::PathBuf::from).unwrap_or_else(|| "C:\\Windows".into());
    let candidates = [windir.join("Fonts").join("malgun.ttf"), windir.join("Fonts").join("gulim.ttc")];
    let mut fonts = egui::FontDefinitions::default();
    match candidates.iter().find_map(|p| std::fs::read(p).ok().map(|b| (p.clone(), b))) {
        Some((path, bytes)) => {
            fonts.font_data.insert("korean".into(), egui::FontData::from_owned(bytes).into());
            for family in [egui::FontFamily::Proportional, egui::FontFamily::Monospace] {
                fonts.families.entry(family).or_default().push("korean".into());
            }
            crate::applog::info(format!("fonts: Korean fallback {}", path.display()));
        }
        None => crate::applog::warn("fonts: no Korean system font found (malgun.ttf, gulim.ttc); Hangul will not render"),
    }
    if let Some((name, bytes)) = title {
        // The heading font first, then the body fonts for any glyph it lacks.
        fonts.font_data.insert("title".into(), egui::FontData::from_owned(bytes).into());
        let mut chain = vec!["title".to_string()];
        chain.extend(fonts.families.get(&egui::FontFamily::Proportional).cloned().unwrap_or_default());
        fonts.families.insert(egui::FontFamily::Name("title".into()), chain);
        HAS_TITLE.store(true, Ordering::Relaxed);
        crate::applog::info(format!("fonts: heading font {name}"));
    }
    ctx.set_fonts(fonts);
}

fn cache_dir() -> PathBuf {
    crate::paths::data_root().join("fonts")
}

fn cached_files() -> Vec<PathBuf> {
    let Ok(rd) = std::fs::read_dir(cache_dir()) else { return Vec::new() };
    rd.filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.file_name().and_then(|n| n.to_str()).is_some_and(|n| n.starts_with("title-") && n.ends_with(".ttf")))
        .collect()
}

/// The heading font cut out on an earlier run, if any (read at start so the first frame already uses it).
pub fn cached_title() -> Option<(String, Vec<u8>)> {
    let p = cached_files().into_iter().max_by_key(|p| std::fs::metadata(p).and_then(|m| m.modified()).ok())?;
    let bytes = std::fs::read(&p).ok()?;
    Some((p.display().to_string(), bytes))
}

/// Make sure the cached heading font matches this game's pack; cut it out again when the pack changed (a game update)
/// and switch the fonts. Runs on a background thread; failures only log, the body font stays.
pub fn ensure_title_from_game(ctx: &egui::Context, pck: &Path) {
    let Ok(meta) = std::fs::metadata(pck) else { return };
    let mtime = meta.modified().ok().and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok()).map_or(0, |d| d.as_secs());
    let file = cache_dir().join(format!("title-{}-{mtime}.ttf", meta.len()));
    if file.exists() {
        if !HAS_TITLE.load(Ordering::Relaxed)
            && let Ok(bytes) = std::fs::read(&file)
        {
            install(ctx, Some((file.display().to_string(), bytes)));
            ctx.request_repaint();
        }
        return;
    }
    let t = std::time::Instant::now();
    let bytes = match ov_resources::pck::PckReader::open(pck).map_err(|e| e.to_string()).and_then(|r| ov_resources::pck::font::extract_font(&r, GAME_TITLE_FONT)) {
        Ok(b) => b,
        Err(e) => {
            crate::applog::warn(format!("fonts: heading font not read from the game ({e}); using the body font"));
            return;
        }
    };
    let _ = std::fs::create_dir_all(cache_dir());
    let tmp = file.with_extension("tmp");
    if std::fs::write(&tmp, &bytes).and_then(|()| std::fs::rename(&tmp, &file)).is_err() {
        crate::applog::warn(format!("fonts: could not cache the heading font at {}", file.display()));
    }
    for old in cached_files().into_iter().filter(|p| *p != file) {
        let _ = std::fs::remove_file(old);
    }
    crate::applog::info(format!("fonts: heading font read from the game ({} bytes, {} ms)", bytes.len(), t.elapsed().as_millis()));
    install(ctx, Some((file.display().to_string(), bytes)));
    ctx.request_repaint();
}
