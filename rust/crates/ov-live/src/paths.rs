//! Port of AppLayout (AppPaths.cs): where the shipped data (content root) and the generated local data live.

use std::path::{Path, PathBuf};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AppLayoutKind {
    /// A source checkout: data/local inside the repo.
    Checkout,
    /// The installed app: data next to the exe, local data under %LOCALAPPDATA%.
    Installed,
    /// Nothing found: the exe folder.
    Fallback,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AppLayout {
    pub content_root: PathBuf,
    pub local_data_dir: PathBuf,
    pub kind: AppLayoutKind,
}

const CHECKOUT_MARKERS: [&str; 2] = ["SlayTheSpire2TierOverlay.sln", ".git"];

impl AppLayout {
    /// `data_root`: `%LOCALAPPDATA%\Sts2Overlay`.
    pub fn resolve(exe_dir: &Path, data_root: &Path, exists: &dyn Fn(&Path) -> bool) -> AppLayout {
        let user_local = data_root.join("local");
        // Deliberate difference from C#: the marker is data/catalog (the Rust app ships no tier packs).
        let has_packs = |d: &Path| exists(&d.join("data").join("catalog"));
        let checkout = |d: &Path| AppLayout { content_root: d.to_path_buf(), local_data_dir: d.join("data").join("local"), kind: AppLayoutKind::Checkout };
        for d in exe_dir.ancestors() {
            if has_packs(d) && CHECKOUT_MARKERS.iter().any(|m| exists(&d.join(m))) {
                return checkout(d);
            }
        }
        if has_packs(exe_dir) {
            return AppLayout { content_root: exe_dir.to_path_buf(), local_data_dir: user_local, kind: AppLayoutKind::Installed };
        }
        for d in exe_dir.ancestors().skip(1) {
            if has_packs(d) {
                return checkout(d);
            }
        }
        AppLayout { content_root: exe_dir.to_path_buf(), local_data_dir: user_local, kind: AppLayoutKind::Fallback }
    }

    /// `data/catalog/cards.<version>.json` (names-free, shipped).
    pub fn committed_catalog(&self, card_data_version: &str) -> PathBuf {
        self.content_root.join("data").join("catalog").join(format!("cards.{card_data_version}.json"))
    }

    /// The card data version: the newest `data/catalog/cards.<v>.json` not newer than the game (`game_version`).
    pub fn card_data_version(&self, game_version: Option<&str>) -> Option<String> {
        let names: Vec<String> = std::fs::read_dir(self.content_root.join("data").join("catalog"))
            .map(|rd| {
                rd.flatten()
                    .filter_map(|e| e.file_name().to_str().and_then(|n| n.strip_prefix("cards.")).and_then(|n| n.strip_suffix(".json")).map(str::to_string))
                    .collect()
            })
            .unwrap_or_default();
        ov_resources::version::select(names.iter().map(String::as_str), game_version).version
    }

    /// `data/catalog/relics.<version>.json`.
    pub fn committed_relics(&self, card_data_version: &str) -> PathBuf {
        self.content_root.join("data").join("catalog").join(format!("relics.{card_data_version}.json"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolves_checkout_installed_and_fallback() {
        let set = |paths: &'static [&'static str]| move |p: &Path| paths.iter().any(|x| Path::new(x) == p);
        let data = Path::new("/lad/Sts2Overlay");
        let co = set(&["/repo/data/catalog", "/repo/.git"]);
        let l = AppLayout::resolve(Path::new("/repo/rust/target/release"), data, &co);
        assert_eq!((l.kind, l.content_root.as_path(), l.local_data_dir.as_path()), (AppLayoutKind::Checkout, Path::new("/repo"), Path::new("/repo/data/local")));
        let inst = set(&["/app/current/data/catalog"]);
        let l = AppLayout::resolve(Path::new("/app/current"), data, &inst);
        assert_eq!((l.kind, l.local_data_dir.as_path()), (AppLayoutKind::Installed, Path::new("/lad/Sts2Overlay/local")));
        let l = AppLayout::resolve(Path::new("/x/y"), data, &|_| false);
        assert_eq!(l.kind, AppLayoutKind::Fallback);
        assert_eq!(l.committed_catalog("v0.113.0"), Path::new("/x/y/data/catalog/cards.v0.113.0.json"));
    }
}
