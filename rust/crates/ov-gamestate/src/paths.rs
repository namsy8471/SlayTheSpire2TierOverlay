//! Where the game keeps its saves and log (`GamePaths`).

use std::path::{Path, PathBuf};
use std::time::SystemTime;

pub const LOG_FILE_NAME: &str = "godot.log";

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct GamePaths {
    pub saves_directory: Option<PathBuf>,
    pub log_file: Option<PathBuf>,
    pub steam_id: Option<u64>,
    /// `%APPDATA%\SlayTheSpire2`; None when the saves folder was given directly.
    pub root: Option<PathBuf>,
}

impl GamePaths {
    pub fn default_root() -> Option<PathBuf> {
        std::env::var_os("APPDATA").map(|a| PathBuf::from(a).join("SlayTheSpire2"))
    }

    pub fn discover(root: Option<PathBuf>, saves_override: Option<PathBuf>, log_override: Option<PathBuf>) -> Self {
        let root = root.or_else(Self::default_root);
        let saves = saves_override.clone().or_else(|| root.as_deref().and_then(find_saves_directory));
        let log = log_override.or_else(|| root.as_ref().map(|r| r.join("logs").join(LOG_FILE_NAME)));
        let steam_id = saves.as_deref().and_then(steam_id_from_path);
        Self { saves_directory: saves, log_file: log, steam_id, root: if saves_override.is_none() { root } else { None } }
    }
}

pub fn saves_directory_for(root: &Path, steam_id: u64, profile: &str) -> PathBuf {
    root.join("steam").join(steam_id.to_string()).join(profile).join("saves")
}

/// The most recently written `steam/<id>/profile*/saves` folder.
pub fn find_saves_directory(root: &Path) -> Option<PathBuf> {
    let steam = root.join("steam");
    let mut best: Option<(SystemTime, PathBuf)> = None;
    for account in std::fs::read_dir(&steam).ok()?.flatten() {
        let name = account.file_name().to_string_lossy().into_owned();
        if name.is_empty() || !name.bytes().all(|b| b.is_ascii_digit()) || name.parse::<u64>().is_err() || !account.path().is_dir() {
            continue;
        }
        let Ok(profiles) = std::fs::read_dir(account.path()) else { continue };
        for profile in profiles.flatten() {
            let pname = profile.file_name().to_string_lossy().to_lowercase();
            if !pname.starts_with("profile") {
                continue;
            }
            let saves = profile.path().join("saves");
            if !saves.is_dir() {
                continue;
            }
            let w = last_write(&saves);
            if best.as_ref().is_none_or(|(t, _)| w > *t) {
                best = Some((w, saves));
            }
        }
    }
    best.map(|(_, p)| p)
}

fn last_write(dir: &Path) -> SystemTime {
    let mut newest = std::fs::metadata(dir).and_then(|m| m.modified()).unwrap_or(SystemTime::UNIX_EPOCH);
    if let Ok(rd) = std::fs::read_dir(dir) {
        for f in rd.flatten() {
            if let Ok(m) = f.metadata()
                && m.is_file()
                && let Ok(t) = m.modified()
                && t > newest
            {
                newest = t;
            }
        }
    }
    newest
}

/// The Steam ID folder in a saves path (the last all-digit part of 10+ digits).
pub fn steam_id_from_path(saves_dir: &Path) -> Option<u64> {
    let full = std::path::absolute(saves_dir).unwrap_or_else(|_| saves_dir.to_path_buf());
    let s = full.to_string_lossy().into_owned();
    s.split(['/', '\\'])
        .rev()
        .find(|p| p.len() >= 10 && p.bytes().all(|b| b.is_ascii_digit()) && p.parse::<u64>().is_ok())
        .and_then(|p| p.parse().ok())
}

pub fn same_path(a: &Path, b: &Path) -> bool {
    let norm = |p: &Path| {
        let s = std::path::absolute(p).unwrap_or_else(|_| p.to_path_buf()).to_string_lossy().into_owned();
        s.trim_end_matches(['/', '\\']).to_lowercase()
    };
    norm(a) == norm(b)
}
