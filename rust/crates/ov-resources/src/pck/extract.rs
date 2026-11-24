//! Port of PckExtractor.cs: copies files and converts imported textures out of a pack. Writes only below the given
//! output folder; the pack is never modified.

use std::collections::HashMap;
use std::path::{Component, Path, PathBuf};

use super::ctex::{CtexImage, CtexPayload};
use super::glob::Glob;
use super::reader::{PckEntry, PckReader};

#[derive(Clone, Debug, Default)]
pub struct ImageExtractionResult {
    pub matched: usize,
    pub written: usize,
    pub counts: HashMap<CtexPayload, usize>,
    pub skipped: Vec<String>,
}

/// Copies matching files as they are; returns how many.
pub fn extract_files(pck: &PckReader, pattern: &str, out_dir: &Path, cancel: &dyn Fn() -> bool) -> Result<usize, String> {
    let glob = Glob::new(pattern);
    let prefix = glob.literal_directory_prefix();
    let mut count = 0;
    for e in pck.entries.iter().filter(|e| glob.is_match(&e.path)) {
        if cancel() {
            return Err("cancelled".into());
        }
        let out = output_path(out_dir, &e.path, &prefix)?;
        pck.extract_to(e, &out).map_err(|err| format!("{}: {err}", e.path))?;
        count += 1;
    }
    Ok(count)
}

/// Converts imported textures to image files. The glob matches the original resource paths (the `.import` stubs
/// name the `.ctex` holding the pixels); a glob that matches `.ctex` paths directly also works.
pub fn extract_images(
    pck: &PckReader,
    pattern: &str,
    out_dir: &Path,
    progress: &mut dyn FnMut(usize, usize),
    cancel: &dyn Fn() -> bool,
) -> Result<ImageExtractionResult, String> {
    let glob = Glob::new(pattern);
    let prefix = glob.literal_directory_prefix();
    let mut res = ImageExtractionResult::default();
    let mut jobs: Vec<(String, &PckEntry)> = Vec::new();
    for e in &pck.entries {
        if let Some(logical) = e.path.strip_suffix(".import") {
            if !glob.is_match(logical) {
                continue;
            }
            let text = pck.read_all_bytes(e).map_err(|err| err.to_string())?;
            match find_ctex_path(&String::from_utf8_lossy(&text)).and_then(|p| pck.find(&p)) {
                Some(ctex) => jobs.push((logical.to_string(), ctex)),
                None => res.skipped.push(format!("{logical}: no .ctex found in its .import file")),
            }
        } else if e.path.ends_with(".ctex") && glob.is_match(&e.path) {
            jobs.push((e.path.clone(), e));
        }
    }
    let total = jobs.len();
    for (i, (logical, entry)) in jobs.iter().enumerate() {
        if cancel() {
            return Err("cancelled".into());
        }
        let img = CtexImage::parse(&pck.read_all_bytes(entry).map_err(|err| err.to_string())?)?;
        *res.counts.entry(img.payload).or_default() += 1;
        match (&img.embedded_file, img.file_extension()) {
            (Some(bytes), Some(ext)) => {
                let out = change_extension(&output_path(out_dir, logical, &prefix)?, ext);
                if let Some(dir) = out.parent() {
                    std::fs::create_dir_all(dir).map_err(|err| err.to_string())?;
                }
                std::fs::write(&out, bytes).map_err(|err| err.to_string())?;
                res.written += 1;
            }
            _ => res.skipped.push(format!("{logical}: {:?} {} {}x{}", img.payload, img.pixel_format, img.width, img.height)),
        }
        progress(i + 1, total);
    }
    res.matched = total;
    Ok(res)
}

/// The first `path="res://....ctex"` or `path.xxx="res://....ctex"` line of a `.import` file.
pub fn find_ctex_path(import_text: &str) -> Option<String> {
    for line in import_text.split('\n') {
        let Some(mut rest) = line.strip_prefix("path") else { continue };
        if let Some(r) = rest.strip_prefix('.') {
            let n = r.char_indices().find(|(_, c)| !(c.is_alphanumeric() || *c == '_')).map_or(r.len(), |(i, _)| i);
            if n == 0 {
                continue;
            }
            rest = &r[n..];
        }
        let Some(r) = rest.strip_prefix("=\"res://") else { continue };
        let Some(end) = r.find('"') else { continue };
        let p = &r[..end];
        if p.len() > ".ctex".len() && p.ends_with(".ctex") {
            return Some(p.to_string());
        }
    }
    None
}

fn change_extension(p: &Path, ext: &str) -> PathBuf {
    p.with_extension(ext.trim_start_matches('.'))
}

/// Lexical full path ("." and ".." resolved) of a possibly relative path.
fn full_path(p: &Path) -> PathBuf {
    let abs = std::path::absolute(p).unwrap_or_else(|_| p.to_path_buf());
    let mut out = PathBuf::new();
    for c in abs.components() {
        match c {
            Component::ParentDir => {
                out.pop();
            }
            Component::CurDir => {}
            other => out.push(other.as_os_str()),
        }
    }
    out
}

/// "localization/kor/cards.json" with prefix "localization/" -> `<out>/kor/cards.json`. A pack path with ".." that
/// would leave the output folder is an error.
pub fn output_path(out_dir: &Path, pack_path: &str, prefix: &str) -> Result<PathBuf, String> {
    let relative = match pack_path.get(..prefix.len()) {
        Some(head) if head.eq_ignore_ascii_case(prefix) => &pack_path[prefix.len()..],
        _ => pack_path,
    };
    let root = full_path(out_dir);
    let mut joined = root.clone();
    for part in relative.split('/') {
        joined.push(part);
    }
    let full = full_path(&joined);
    if !full.starts_with(&root) || full == root {
        return Err(format!("Pack path escapes the output folder: {pack_path}"));
    }
    Ok(full)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn import_files_and_output_paths() {
        let t = "[remap]\nimporter=\"texture\"\npath.bptc=\"res://.godot/imported/relic_atlas.png-1.bptc.ctex\"\npath=\"res://other.ctex\"\n";
        assert_eq!(find_ctex_path(t).as_deref(), Some(".godot/imported/relic_atlas.png-1.bptc.ctex"));
        assert_eq!(find_ctex_path("path=\"res://a.png\"\n"), None);
        let out = output_path(Path::new("/tmp/out"), "localization/kor/cards.json", "localization/").unwrap();
        assert!(out.ends_with("kor/cards.json"));
        assert!(output_path(Path::new("/tmp/out"), "../evil.txt", "").is_err());
    }
}
