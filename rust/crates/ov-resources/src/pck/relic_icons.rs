//! Port of RelicIconExtractor.cs: cuts the small relic and potion icons out of the game's atlases.
//!
//! `images/atlases/relic_atlas.sprites/<id>.tres` is an AtlasTexture: a `region` of the atlas (a BC7 VRAM texture)
//! plus a `margin` that restores the trimmed transparent border. Icons are written at region + margin size.

use std::collections::HashSet;
use std::path::Path;

use super::ctex::{CtexImage, CtexPayload};
use super::extract::find_ctex_path;
use super::glob::Glob;
use super::png;
use super::reader::PckReader;

pub const ATLAS_IMPORT: &str = "images/atlases/relic_atlas.png.import";
pub const POTION_ATLAS_IMPORT: &str = "images/atlases/potion_atlas.png.import";
pub const SPRITES_PREFIX: &str = "images/atlases/relic_atlas.sprites/";
pub const OUTLINE_ATLAS_IMPORT: &str = "images/atlases/relic_outline_atlas.png.import";
pub const OUTLINE_SPRITES_PREFIX: &str = "images/atlases/relic_outline_atlas.sprites/";
pub const OUTLINE_DIR: &str = "outline";

fn io(e: std::io::Error) -> String {
    e.to_string()
}

/// Writes `<out>/<name>.png` for every relic atlas sprite (and `<out>/outline/` for the outline atlas), then the
/// standalone and beta images for names the atlas lacks. Returns how many icons.
pub fn extract(pck: &PckReader, out_dir: &Path, cancel: &dyn Fn() -> bool) -> Result<usize, String> {
    let mut names: HashSet<String> = HashSet::new();
    let mut written = extract_atlas(pck, ATLAS_IMPORT, SPRITES_PREFIX, "relic_atlas.png", out_dir, cancel, Some(&mut names))?;
    if pck.find(OUTLINE_ATLAS_IMPORT).is_some() {
        extract_atlas(pck, OUTLINE_ATLAS_IMPORT, OUTLINE_SPRITES_PREFIX, "relic_outline_atlas.png", &out_dir.join(OUTLINE_DIR), cancel, None)?;
    }
    for pattern in ["images/relics/*.png.import", "images/relics/beta/*.png.import"] {
        let glob = Glob::new(pattern);
        for import in pck.entries.iter().filter(|e| glob.is_match(&e.path)) {
            if cancel() {
                return Err("cancelled".into());
            }
            // "<name>.png.import" -> "<name>.png"
            let file = import.path.rsplit('/').next().unwrap_or(&import.path);
            let name = file.strip_suffix(".import").unwrap_or(file).to_string();
            if names.contains(&name.to_lowercase()) {
                continue;
            }
            let text = pck.read_all_bytes(import).map_err(io)?;
            let Some(tex) = find_ctex_path(&String::from_utf8_lossy(&text)).and_then(|p| pck.find(&p)) else { continue };
            let img = CtexImage::parse(&pck.read_all_bytes(tex).map_err(io)?)?;
            if img.embedded_file.is_none() && !img.can_decode_rgba() {
                continue;
            }
            let bytes = if img.payload == CtexPayload::Png {
                img.embedded_file.clone().unwrap_or_default()
            } else {
                png::encode(&read_rgba(&img, &name)?, img.width, img.height)?
            };
            std::fs::create_dir_all(out_dir).map_err(io)?;
            std::fs::write(out_dir.join(&name), bytes).map_err(io)?;
            names.insert(name.to_lowercase());
            written += 1;
        }
    }
    Ok(written)
}

pub fn extract_potions(pck: &PckReader, out_dir: &Path, cancel: &dyn Fn() -> bool) -> Result<usize, String> {
    let n = extract_atlas(pck, POTION_ATLAS_IMPORT, "images/atlases/potion_atlas.sprites/", "potion_atlas.png", out_dir, cancel, None)?;
    const OUTLINE: &str = "images/atlases/potion_outline_atlas.png.import";
    if pck.find(OUTLINE).is_some() {
        extract_atlas(pck, OUTLINE, "images/atlases/potion_outline_atlas.sprites/", "potion_outline_atlas.png", &out_dir.join(OUTLINE_DIR), cancel, None)?;
    }
    Ok(n)
}

/// The merchant shelf position ("Relics" / "Potions") from merchant_inventory.tscn: (118 + offset_left, 80 + offset_top).
pub fn merchant_shelf(pck: &PckReader, shelf: &str) -> (Option<f32>, Option<f32>) {
    let Some(scene) = pck.find("scenes/merchant/merchant_inventory.tscn") else { return (None, None) };
    let Ok(bytes) = pck.read_all_bytes(scene) else { return (None, None) };
    let text = String::from_utf8_lossy(&bytes);
    match shelf_offsets(&text).into_iter().find(|(name, _, _)| name == shelf) {
        Some((_, x, y)) => match (x.parse::<f32>(), y.parse::<f32>()) {
            (Ok(x), Ok(y)) => (Some(118.0 + x), Some(80.0 + y)),
            _ => (None, None),
        },
        None => (None, None),
    }
}

/// Matches of `\[node name="(Potions|Relics)"[^\]]*parent="SlotsContainer"[^\]]*\][^\[]*?offset_left = ([-\d.]+)[^\[]*?offset_top = ([-\d.]+)`.
fn shelf_offsets(text: &str) -> Vec<(String, String, String)> {
    let mut out = Vec::new();
    let mut from = 0;
    while let Some(i) = text[from..].find("[node name=\"") {
        let start = from + i;
        from = start + 1;
        let rest = &text[start + "[node name=\"".len()..];
        let name = if rest.starts_with("Potions\"") {
            "Potions"
        } else if rest.starts_with("Relics\"") {
            "Relics"
        } else {
            continue;
        };
        let after_name = &rest[name.len() + 1..];
        let Some(close) = after_name.find(']') else { continue };
        if !after_name[..close].contains("parent=\"SlotsContainer\"") {
            continue;
        }
        let body = &after_name[close + 1..];
        let body = &body[..body.find('[').unwrap_or(body.len())];
        let num = |key: &str| -> Option<String> {
            let k = body.find(key)?;
            let v: String = body[k + key.len()..].chars().take_while(|c| c.is_ascii_digit() || *c == '-' || *c == '.').collect();
            (!v.is_empty()).then_some(v)
        };
        let Some(left_at) = body.find("offset_left = ") else { continue };
        let (Some(x), Some(y)) = (num("offset_left = "), body[left_at..].find("offset_top = ").and_then(|_| {
            let tail = &body[left_at..];
            let k = tail.find("offset_top = ")?;
            let v: String = tail[k + "offset_top = ".len()..].chars().take_while(|c| c.is_ascii_digit() || *c == '-' || *c == '.').collect();
            (!v.is_empty()).then_some(v)
        })) else {
            continue;
        };
        out.push((name.to_string(), x, y));
        from = start + 1;
    }
    out
}

#[allow(clippy::too_many_arguments)]
fn extract_atlas(
    pck: &PckReader,
    import_path: &str,
    sprites_prefix: &str,
    atlas_file: &str,
    out_dir: &Path,
    cancel: &dyn Fn() -> bool,
    mut names: Option<&mut HashSet<String>>,
) -> Result<usize, String> {
    let import = pck.find(import_path).ok_or_else(|| format!("{import_path} not found in the pack"))?;
    let text = pck.read_all_bytes(import).map_err(io)?;
    let ctex_path = find_ctex_path(&String::from_utf8_lossy(&text)).ok_or_else(|| format!("no texture path in {import_path}"))?;
    let entry = pck.find(&ctex_path).ok_or_else(|| format!("{ctex_path} not found in the pack"))?;
    let ctex = CtexImage::parse(&pck.read_all_bytes(entry).map_err(io)?)?;
    let atlas = read_rgba(&ctex, atlas_file)?;
    std::fs::create_dir_all(out_dir).map_err(io)?;
    let mut written = 0;
    for e in pck.entries.iter().filter(|e| e.path.starts_with(sprites_prefix) && e.path.ends_with(".tres")) {
        if cancel() {
            return Err("cancelled".into());
        }
        let tres = String::from_utf8_lossy(&pck.read_all_bytes(e).map_err(io)?).to_string();
        if !tres.contains(atlas_file) {
            continue;
        }
        let Some(region) = parse_rect(&tres, "region") else { continue };
        let margin = parse_rect(&tres, "margin").unwrap_or((0, 0, 0, 0));
        let Some((icon, w, h)) = cut(&atlas, ctex.width as i32, ctex.height as i32, region, margin) else { continue };
        let file = e.path.rsplit('/').next().unwrap_or(&e.path);
        let name = format!("{}.png", file.strip_suffix(".tres").unwrap_or(file));
        std::fs::write(out_dir.join(&name), png::encode(&icon, w as u32, h as u32)?).map_err(io)?;
        if let Some(n) = names.as_deref_mut() {
            n.insert(name.to_lowercase());
        }
        written += 1;
    }
    Ok(written)
}

fn read_rgba(ctex: &CtexImage, name: &str) -> Result<Vec<u8>, String> {
    if ctex.can_decode_rgba() {
        return ctex.decode_rgba();
    }
    let Some(encoded) = &ctex.embedded_file else {
        return Err(format!("{name} has an unsupported format {}", ctex.pixel_format));
    };
    let img = image::load_from_memory(encoded).map_err(|e| format!("Cannot decode {name} ({:?}): {e}", ctex.payload))?;
    if img.width() != ctex.width || img.height() != ctex.height {
        return Err(format!("Cannot decode {name} ({:?})", ctex.payload));
    }
    Ok(img.to_rgba8().into_raw())
}

pub type Rect = (i32, i32, i32, i32);

/// The sprite at its original size: the region placed at the margin offset in a transparent canvas.
pub fn cut(atlas: &[u8], aw: i32, ah: i32, region: Rect, margin: Rect) -> Option<(Vec<u8>, i32, i32)> {
    let w = region.2 + margin.2;
    let h = region.3 + margin.3;
    if region.2 <= 0 || region.3 <= 0 || region.0 < 0 || region.1 < 0 || region.0 + region.2 > aw || region.1 + region.3 > ah {
        return None;
    }
    if w <= 0 || h <= 0 || margin.0 < 0 || margin.1 < 0 || margin.0 + region.2 > w || margin.1 + region.3 > h {
        return None;
    }
    let mut icon = vec![0u8; (w * h * 4) as usize];
    for y in 0..region.3 {
        let src = (((region.1 + y) * aw + region.0) * 4) as usize;
        let dst = (((margin.1 + y) * w + margin.0) * 4) as usize;
        let n = (region.2 * 4) as usize;
        icon[dst..dst + n].copy_from_slice(&atlas[src..src + n]);
    }
    Some((icon, w, h))
}

/// `key = Rect2(x, y, w, h)` with each number rounded (banker's rounding, like Math.Round).
pub fn parse_rect(tres: &str, key: &str) -> Option<Rect> {
    let mut from = 0;
    while let Some(i) = tres[from..].find(key) {
        let at = from + i;
        from = at + 1;
        let rest = tres[at + key.len()..].trim_start();
        let Some(rest) = rest.strip_prefix('=') else { continue };
        let Some(rest) = rest.trim_start().strip_prefix("Rect2(") else { continue };
        let Some(end) = rest.find(')') else { continue };
        let parts: Vec<&str> = rest[..end].split(',').map(str::trim).collect();
        if parts.len() != 4 || parts.iter().any(|p| p.is_empty() || !p.chars().all(|c| c.is_ascii_digit() || c == '-' || c == '.')) {
            continue;
        }
        let n: Vec<f64> = parts.iter().filter_map(|p| p.parse::<f64>().ok()).collect();
        if n.len() != 4 {
            continue;
        }
        let r = |v: f64| v.round_ties_even() as i32;
        return Some((r(n[0]), r(n[1]), r(n[2]), r(n[3])));
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rects_shelves_and_cutting() {
        let tres = "[resource]\natlas = ExtResource(\"1\")\nregion = Rect2(10, 20.5, 3, 2)\nmargin = Rect2(1, 1, 2, 2)\n";
        assert_eq!(parse_rect(tres, "region"), Some((10, 20, 3, 2)));
        assert_eq!(parse_rect(tres, "margin"), Some((1, 1, 2, 2)));
        let scene = "[node name=\"Relics\" type=\"Control\" parent=\"SlotsContainer\"]\nlayout_mode = 0\noffset_left = 812.0\noffset_top = -4.5\n\n[node name=\"Potions\" parent=\"SlotsContainer\"]\noffset_left = 10\noffset_top = 20\n";
        let found = shelf_offsets(scene);
        assert_eq!(found[0], ("Relics".into(), "812.0".into(), "-4.5".into()));
        assert_eq!(found[1].0, "Potions");
        let atlas: Vec<u8> = (0..4 * 4 * 4).map(|i| i as u8).collect();
        let (icon, w, h) = cut(&atlas, 4, 4, (1, 1, 2, 2), (1, 0, 1, 1)).unwrap();
        assert_eq!((w, h), (3, 3));
        assert_eq!(&icon[4..8], &atlas[20..24]);
    }
}
