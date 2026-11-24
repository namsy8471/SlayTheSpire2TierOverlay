//! Port of GameInstall.cs and InstallFingerprint.cs: finding the game and fingerprinting its files (read-only).

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use sha1::Digest as _;

use crate::version::GameVersion;

/// `<install>/release_info.json`, written by the game's build pipeline.
#[derive(Clone, Debug, Default, Deserialize, Serialize, PartialEq)]
pub struct ReleaseInfo {
    pub version: Option<String>,
    pub commit: Option<String>,
    pub date: Option<String>,
    pub branch: Option<String>,
    pub main_assembly_hash: Option<i32>,
}

impl ReleaseInfo {
    pub fn try_load(path: &Path) -> Option<ReleaseInfo> {
        let text = std::fs::read_to_string(path).ok()?;
        serde_json::from_str(&text).ok()
    }

    /// The game's own main assembly hash: the first 4 bytes of SHA-1(sts2.dll) as a little-endian int.
    pub fn compute_main_assembly_hash(dll: &Path) -> std::io::Result<i32> {
        let bytes = crate::io::read_shared(dll)?;
        let h = sha1::Sha1::digest(&bytes);
        Ok(i32::from_le_bytes([h[0], h[1], h[2], h[3]]))
    }
}

/// A Slay the Spire 2 install folder.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct GameInstall {
    pub directory: PathBuf,
}

pub const STEAM_FOLDER_NAME: &str = "Slay the Spire 2";

impl GameInstall {
    pub fn new(dir: impl Into<PathBuf>) -> Self {
        GameInstall { directory: dir.into() }
    }
    pub fn pck_path(&self) -> PathBuf {
        self.directory.join("SlayTheSpire2.pck")
    }
    pub fn data_dir(&self) -> PathBuf {
        self.directory.join("data_sts2_windows_x86_64")
    }
    pub fn dll_path(&self) -> PathBuf {
        self.data_dir().join("sts2.dll")
    }
    pub fn release_info_path(&self) -> PathBuf {
        self.directory.join("release_info.json")
    }
    pub fn is_valid(&self) -> bool {
        self.pck_path().is_file() && self.dll_path().is_file()
    }

    /// The configured folder, then `STS2_GAME_DIR`, then every Steam library of the known Steam folders.
    pub fn locate(configured: Option<&str>, steam_roots: Option<Vec<PathBuf>>) -> Option<GameInstall> {
        let roots = steam_roots.unwrap_or_else(|| default_steam_roots(&read_registry_string));
        candidates(configured, &roots).into_iter().map(GameInstall::new).find(GameInstall::is_valid)
    }
}

fn candidates(configured: Option<&str>, steam_roots: &[PathBuf]) -> Vec<PathBuf> {
    let mut out = Vec::new();
    if let Some(c) = configured.filter(|c| !c.trim().is_empty()) {
        out.push(PathBuf::from(c));
    }
    if let Ok(env) = std::env::var("STS2_GAME_DIR")
        && !env.trim().is_empty()
    {
        out.push(PathBuf::from(env));
    }
    for root in steam_roots {
        out.push(root.join("steamapps").join("common").join(STEAM_FOLDER_NAME));
        let vdf = root.join("steamapps").join("libraryfolders.vdf");
        let Ok(text) = std::fs::read_to_string(&vdf) else { continue };
        for lib in parse_library_folders(&text) {
            out.push(PathBuf::from(lib).join("steamapps").join("common").join(STEAM_FOLDER_NAME));
        }
    }
    out
}

/// Where Steam records its own folder: the client's per-user key, then the installer's.
pub const STEAM_REGISTRY_VALUES: [(&str, &str); 2] =
    [(r"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"), (r"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")];

/// Steam folders without duplicates (case-insensitive): the registry ones, then Program Files (x86) and Program Files.
pub fn default_steam_roots(read_registry: &dyn Fn(&str, &str) -> Option<String>) -> Vec<PathBuf> {
    let mut roots: Vec<String> = STEAM_REGISTRY_VALUES.iter().filter_map(|(k, v)| read_registry(k, v)).collect();
    for var in ["ProgramFiles(x86)", "ProgramFiles"] {
        if let Ok(pf) = std::env::var(var)
            && !pf.is_empty()
        {
            roots.push(Path::new(&pf).join("Steam").to_string_lossy().to_string());
        }
    }
    let mut seen: Vec<String> = Vec::new();
    let mut out = Vec::new();
    for root in roots {
        if root.trim().is_empty() {
            continue;
        }
        let full = normalize_dir(root.trim());
        let key = full.to_lowercase();
        if !seen.contains(&key) {
            seen.push(key);
            out.push(PathBuf::from(full));
        }
    }
    out
}

/// Path.GetFullPath + TrimEndingDirectorySeparator for a Steam path ("c:/program files (x86)/steam").
fn normalize_dir(p: &str) -> String {
    let sep = std::path::MAIN_SEPARATOR;
    let mut s: String = if cfg!(windows) { p.replace('/', "\\") } else { p.to_string() };
    let abs = std::path::absolute(&s).map(|a| a.to_string_lossy().to_string()).unwrap_or_else(|_| s.clone());
    s = abs;
    while s.len() > 1 && s.ends_with(sep) && !(cfg!(windows) && s.len() == 3 && s.as_bytes()[1] == b':') {
        s.pop();
    }
    s
}

#[cfg(windows)]
pub fn read_registry_string(key: &str, value: &str) -> Option<String> {
    use windows::Win32::System::Registry::{HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ, RegGetValueW};
    use windows::core::HSTRING;
    let (root, sub) = key.split_once('\\')?;
    let hive = match root {
        "HKEY_CURRENT_USER" => HKEY_CURRENT_USER,
        "HKEY_LOCAL_MACHINE" => HKEY_LOCAL_MACHINE,
        _ => return None,
    };
    let (sub, name) = (HSTRING::from(sub), HSTRING::from(value));
    let mut size = 0u32;
    // SAFETY: plain registry reads into a buffer we own; sizes come from the first call.
    unsafe {
        RegGetValueW(hive, &sub, &name, RRF_RT_REG_SZ, None, None, Some(&mut size)).ok().ok()?;
        let mut buf = vec![0u16; (size as usize).div_ceil(2)];
        RegGetValueW(hive, &sub, &name, RRF_RT_REG_SZ, None, Some(buf.as_mut_ptr().cast()), Some(&mut size)).ok().ok()?;
        let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        Some(String::from_utf16_lossy(&buf[..len]))
    }
}

#[cfg(not(windows))]
pub fn read_registry_string(_key: &str, _value: &str) -> Option<String> {
    None
}

/// Library roots from `libraryfolders.vdf` (`"path"  "D:\\SteamLibrary"`, backslashes doubled).
pub fn parse_library_folders(vdf: &str) -> Vec<String> {
    let mut out = Vec::new();
    for line in vdf.lines() {
        let Some(rest) = line.trim_start().strip_prefix("\"path\"") else { continue };
        let rest = rest.trim_start();
        let Some(body) = rest.strip_prefix('"') else { continue };
        let mut value = String::new();
        let mut chars = body.chars();
        let mut closed = false;
        while let Some(c) = chars.next() {
            match c {
                '"' => {
                    closed = true;
                    break;
                }
                '\\' => match chars.next() {
                    Some(n) => {
                        value.push('\\');
                        value.push(n);
                    }
                    None => break,
                },
                c => value.push(c),
            }
        }
        if closed {
            out.push(value.replace("\\\\", "\\"));
        }
    }
    out
}

/// Identifies the exact game files the local resources came from.
#[derive(Clone, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct InstallFingerprint {
    pub dll_sha256: String,
    pub dll_size: i64,
    pub pck_size: i64,
    pub pck_write_ticks_utc: i64,
    pub version: Option<String>,
    pub commit: Option<String>,
}

/// .NET ticks (100 ns since 0001-01-01) of 1970-01-01.
const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;

pub fn ticks_of(t: std::time::SystemTime) -> i64 {
    match t.duration_since(std::time::UNIX_EPOCH) {
        Ok(d) => UNIX_EPOCH_TICKS + (d.as_nanos() / 100) as i64,
        Err(e) => UNIX_EPOCH_TICKS - (e.duration().as_nanos() / 100) as i64,
    }
}

impl InstallFingerprint {
    pub fn compute(install: &GameInstall, release: Option<&ReleaseInfo>) -> std::io::Result<InstallFingerprint> {
        let dll = crate::io::read_shared(&install.dll_path())?;
        let sha = sha2::Sha256::digest(&dll);
        let pck = std::fs::metadata(install.pck_path())?;
        Ok(InstallFingerprint {
            dll_sha256: sha.iter().map(|b| format!("{b:02x}")).collect(),
            dll_size: dll.len() as i64,
            pck_size: pck.len() as i64,
            pck_write_ticks_utc: ticks_of(pck.modified()?),
            version: release.and_then(|r| r.version.clone()),
            commit: release.and_then(|r| r.commit.clone()),
        })
    }
}

impl std::fmt::Display for InstallFingerprint {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let secs = (self.pck_write_ticks_utc - UNIX_EPOCH_TICKS).div_euclid(10_000_000);
        write!(
            f,
            "{} ({}) dll {}/{} pck {}@{}Z",
            self.version.as_deref().unwrap_or("?"),
            self.commit.as_deref().unwrap_or("?"),
            &self.dll_sha256[..self.dll_sha256.len().min(12)],
            self.dll_size,
            self.pck_size,
            crate::manifest::format_utc(secs, false)
        )
    }
}

/// What we know about the installed game.
#[derive(Clone, Debug)]
pub struct InstalledGame {
    pub install: GameInstall,
    pub release: Option<ReleaseInfo>,
    pub version: Option<GameVersion>,
    pub assembly_hash_matches: Option<bool>,
    pub fingerprint: InstallFingerprint,
}

impl InstalledGame {
    /// "v0.107.1", or "unknown-<sha>" when no version is known.
    pub fn version_label(&self) -> String {
        match &self.version {
            Some(v) => v.text.clone(),
            None => format!("unknown-{}", &self.fingerprint.dll_sha256[..8.min(self.fingerprint.dll_sha256.len())]),
        }
    }

    pub fn detect(install: GameInstall) -> std::io::Result<InstalledGame> {
        let release = ReleaseInfo::try_load(&install.release_info_path());
        let version = GameVersion::parse(release.as_ref().and_then(|r| r.version.as_deref()));
        let hash_ok = match release.as_ref().and_then(|r| r.main_assembly_hash) {
            Some(expected) => Some(ReleaseInfo::compute_main_assembly_hash(&install.dll_path())? == expected),
            None => None,
        };
        let fingerprint = InstallFingerprint::compute(&install, release.as_ref())?;
        Ok(InstalledGame { install, release, version, assembly_hash_matches: hash_ok, fingerprint })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn library_folders_and_steam_roots() {
        let vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\" { \"path\" \"D:\\\\SteamLibrary\" }\n}";
        assert_eq!(parse_library_folders(vdf), vec![r"C:\Program Files (x86)\Steam".to_string()]);
        let roots = default_steam_roots(&|k, _| k.contains("CURRENT").then(|| "/opt/steam/".to_string()));
        assert!(roots.first().is_some_and(|r| r.ends_with("steam")), "{roots:?}");
    }
}
