//! Korean glyphs: egui's built-in fonts have no Hangul, so the system's Malgun Gothic is added as a fallback.
//! Headings use the "title" family: a heading font when one was found, otherwise the same fonts as the body.

use std::sync::atomic::{AtomicBool, Ordering};

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
