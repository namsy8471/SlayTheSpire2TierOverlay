//! Port of ResourceManifest.cs: where the generated files live (`LocalLayout`) and `manifest.json`.

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use crate::install::InstallFingerprint;

/// Where the generated files live inside the local data folder.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LocalLayout {
    pub local_dir: PathBuf,
}

impl LocalLayout {
    pub fn new(local_dir: impl Into<PathBuf>) -> Self {
        LocalLayout { local_dir: local_dir.into() }
    }
    pub fn manifest_path(&self) -> PathBuf {
        self.local_dir.join("manifest.json")
    }
    pub fn pck_dir(&self) -> PathBuf {
        self.local_dir.join("pck")
    }
    pub fn card_art_dir(&self) -> PathBuf {
        self.pck_dir().join("card_art")
    }
    pub fn localization_dir(&self) -> PathBuf {
        self.pck_dir().join("localization")
    }
    pub fn relic_icon_dir(&self) -> PathBuf {
        self.pck_dir().join("relic_icons")
    }
    pub fn potion_icon_dir(&self) -> PathBuf {
        self.pck_dir().join("potion_icons")
    }
    pub fn catalog_path(&self, version_label: &str) -> PathBuf {
        self.local_dir.join(format!("catalog.{version_label}.json"))
    }
    pub(crate) fn staging_dir(&self) -> PathBuf {
        self.local_dir.join(".prepare-staging")
    }
    pub(crate) fn lock_path(&self) -> PathBuf {
        self.local_dir.join(".prepare.lock")
    }

    /// The catalog the manifest points to, if both exist.
    pub fn manifest_catalog(&self) -> Option<PathBuf> {
        let m = ResourceManifest::try_load(&self.manifest_path())?;
        let name = m.catalog_file.filter(|n| !n.is_empty())?;
        let path = self.local_dir.join(name);
        path.is_file().then_some(path)
    }
}

/// `manifest.json`: what was generated, from which game files, and when. Written last by the preparer. The field
/// names match the C# overlay, so resources it prepared are reused.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct ResourceManifest {
    pub schema: i32,
    pub prepare_format: i32,
    pub game_version: Option<String>,
    pub game_commit: Option<String>,
    pub game_dir: Option<String>,
    pub fingerprint: Option<InstallFingerprint>,
    pub prepared_at_utc: String,
    pub duration_seconds: f64,
    pub catalog_file: Option<String>,
    pub catalog_cards: i32,
    pub card_art_files: i32,
    pub localization_files: i32,
    pub relic_icon_files: i32,
    pub potion_icon_files: i32,
    pub potion_shelf_x: Option<f32>,
    pub potion_shelf_y: Option<f32>,
    pub relic_shelf_x: Option<f32>,
    pub relic_shelf_y: Option<f32>,
}

impl Default for ResourceManifest {
    fn default() -> Self {
        ResourceManifest {
            schema: 1,
            prepare_format: 0,
            game_version: None,
            game_commit: None,
            game_dir: None,
            fingerprint: None,
            prepared_at_utc: "0001-01-01T00:00:00".into(),
            duration_seconds: 0.0,
            catalog_file: None,
            catalog_cards: 0,
            card_art_files: 0,
            localization_files: 0,
            relic_icon_files: 0,
            potion_icon_files: 0,
            potion_shelf_x: None,
            potion_shelf_y: None,
            relic_shelf_x: None,
            relic_shelf_y: None,
        }
    }
}

impl ResourceManifest {
    /// A missing or broken manifest is the same as none.
    pub fn try_load(path: &Path) -> Option<ResourceManifest> {
        let text = std::fs::read_to_string(path).ok()?;
        serde_json::from_str(&text).ok()
    }

    /// Atomic save: a temp file, then a rename over the old one.
    pub fn save(&self, path: &Path) -> std::io::Result<()> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let tmp = PathBuf::from(format!("{}.tmp", path.display()));
        std::fs::write(&tmp, serde_json::to_string_pretty(self).map_err(std::io::Error::other)?)?;
        std::fs::rename(&tmp, path)
    }
}

/// UTC time from Unix seconds: "2026-11-24 06:00:00" (or ISO "2026-11-24T06:00:00Z").
pub fn format_utc(unix_secs: i64, iso: bool) -> String {
    let days = unix_secs.div_euclid(86_400);
    let rem = unix_secs.rem_euclid(86_400);
    // Civil from days (H. Hinnant).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + i64::from(m <= 2);
    let (h, mi, s) = (rem / 3600, rem % 3600 / 60, rem % 60);
    if iso { format!("{y:04}-{m:02}-{d:02}T{h:02}:{mi:02}:{s:02}Z") } else { format!("{y:04}-{m:02}-{d:02} {h:02}:{mi:02}:{s:02}") }
}

pub fn now_iso() -> String {
    let secs = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0);
    format_utc(secs, true)
}

#[cfg(test)]
mod tests {
    #[test]
    fn reads_a_csharp_manifest_and_formats_dates() {
        let json = r#"{"schema":1,"prepareFormat":4,"gameVersion":"v0.113.0","fingerprint":{"dllSha256":"ab","dllSize":1,"pckSize":2,"pckWriteTicksUtc":3,"version":"v0.113.0","commit":null},"preparedAtUtc":"2026-11-20T01:02:03.1234567Z","durationSeconds":12.5,"catalogFile":"catalog.v0.113.0.json","catalogCards":717,"relicShelfX":null}"#;
        let m: super::ResourceManifest = serde_json::from_str(json).unwrap();
        assert_eq!((m.prepare_format, m.catalog_cards, m.fingerprint.unwrap().pck_size), (4, 717, 2));
        assert_eq!(super::format_utc(1_795_478_400, true), "2026-11-24T00:00:00Z");
    }
}
