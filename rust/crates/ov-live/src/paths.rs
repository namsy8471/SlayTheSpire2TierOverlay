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
        let has_packs = |d: &Path| exists(&d.join("data").join("packs"));
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
        let co = set(&["/repo/data/packs", "/repo/.git"]);
        let l = AppLayout::resolve(Path::new("/repo/rust/target/release"), data, &co);
        assert_eq!((l.kind, l.content_root.as_path(), l.local_data_dir.as_path()), (AppLayoutKind::Checkout, Path::new("/repo"), Path::new("/repo/data/local")));
        let inst = set(&["/app/current/data/packs"]);
        let l = AppLayout::resolve(Path::new("/app/current"), data, &inst);
        assert_eq!((l.kind, l.local_data_dir.as_path()), (AppLayoutKind::Installed, Path::new("/lad/Sts2Overlay/local")));
        let l = AppLayout::resolve(Path::new("/x/y"), data, &|_| false);
        assert_eq!(l.kind, AppLayoutKind::Fallback);
        assert_eq!(l.committed_catalog("v0.113.0"), Path::new("/x/y/data/catalog/cards.v0.113.0.json"));
    }
}
