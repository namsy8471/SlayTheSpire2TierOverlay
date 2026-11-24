//! Where the app keeps its files. Same folders as the C# app (`AppPaths`), so settings and logs carry over.

use std::path::PathBuf;

/// Folder name under `%LOCALAPPDATA%` (C# `AppPaths.DataFolderName`).
pub const DATA_FOLDER_NAME: &str = "Sts2Overlay";

/// `%LOCALAPPDATA%\Sts2Overlay`. Off Windows (tests, cloud): `$XDG_DATA_HOME` or `~/.local/share`, then the temp dir.
pub fn data_root() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA")
        .or_else(|| std::env::var_os("XDG_DATA_HOME"))
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".local").join("share")))
        .unwrap_or_else(std::env::temp_dir);
    base.join(DATA_FOLDER_NAME)
}

pub fn settings_file() -> PathBuf {
    data_root().join("settings.json")
}

pub fn log_file() -> PathBuf {
    data_root().join("app.log")
}

pub fn captures_dir() -> PathBuf {
    data_root().join("captures")
}
