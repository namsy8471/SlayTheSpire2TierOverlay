//! Korean glyphs: egui's built-in fonts have no Hangul, so the system's Malgun Gothic is added as a fallback.

pub fn install(ctx: &egui::Context) {
    let windir = std::env::var_os("WINDIR").map(std::path::PathBuf::from).unwrap_or_else(|| "C:\\Windows".into());
    let candidates = [windir.join("Fonts").join("malgun.ttf"), windir.join("Fonts").join("gulim.ttc")];
    let Some((path, bytes)) = candidates.iter().find_map(|p| std::fs::read(p).ok().map(|b| (p.clone(), b))) else {
        crate::applog::warn("fonts: no Korean system font found (malgun.ttf, gulim.ttc); Hangul will not render");
        return;
    };
    let mut fonts = egui::FontDefinitions::default();
    fonts.font_data.insert("korean".into(), egui::FontData::from_owned(bytes).into());
    for family in [egui::FontFamily::Proportional, egui::FontFamily::Monospace] {
        fonts.families.entry(family).or_default().push("korean".into());
    }
    ctx.set_fonts(fonts);
    crate::applog::info(format!("fonts: Korean fallback {}", path.display()));
}
