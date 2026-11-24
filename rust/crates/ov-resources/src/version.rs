//! Ports of GameVersion.cs and DataVersionSelector.cs.

use std::cmp::Ordering;
use std::path::Path;

/// A game version like `v0.107.1`. Parts compare as numbers; a pre-release sorts before its release.
#[derive(Clone, Debug)]
pub struct GameVersion {
    parts: Vec<i32>,
    pub pre_release: Option<String>,
    /// Display form, always with a leading "v".
    pub text: String,
}

impl GameVersion {
    pub fn parse(text: Option<&str>) -> Option<GameVersion> {
        let s = text?.trim();
        if s.is_empty() {
            return None;
        }
        let mut s = s.strip_prefix('v').or_else(|| s.strip_prefix('V')).unwrap_or(s);
        if let Some(i) = s.find('+') {
            s = &s[..i];
        }
        let mut pre = None;
        if let Some(i) = s.find('-') {
            let p = &s[i + 1..];
            if p.is_empty() {
                return None;
            }
            pre = Some(p.to_string());
            s = &s[..i];
        }
        let pieces: Vec<&str> = s.split('.').collect();
        if pieces.is_empty() || pieces.len() > 4 {
            return None;
        }
        let mut parts = Vec::with_capacity(pieces.len());
        for p in &pieces {
            // NumberStyles.None: digits only.
            if p.is_empty() || !p.bytes().all(|b| b.is_ascii_digit()) {
                return None;
            }
            parts.push(p.parse::<i32>().ok()?);
        }
        let mut len = parts.len();
        while len > 1 && parts[len - 1] == 0 {
            len -= 1;
        }
        parts.truncate(len);
        let text = format!("v{s}{}", pre.as_ref().map(|p| format!("-{p}")).unwrap_or_default());
        Some(GameVersion { parts, pre_release: pre, text })
    }

    pub fn parts(&self) -> &[i32] {
        &self.parts
    }
}

impl Ord for GameVersion {
    fn cmp(&self, other: &Self) -> Ordering {
        let n = self.parts.len().max(other.parts.len());
        for i in 0..n {
            let a = self.parts.get(i).copied().unwrap_or(0);
            let b = other.parts.get(i).copied().unwrap_or(0);
            if a != b {
                return a.cmp(&b);
            }
        }
        match (&self.pre_release, &other.pre_release) {
            (None, None) => Ordering::Equal,
            (None, Some(_)) => Ordering::Greater,
            (Some(_), None) => Ordering::Less,
            (Some(a), Some(b)) => a.cmp(b),
        }
    }
}

impl PartialOrd for GameVersion {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl PartialEq for GameVersion {
    fn eq(&self, other: &Self) -> bool {
        self.cmp(other) == Ordering::Equal
    }
}

impl Eq for GameVersion {}

impl std::fmt::Display for GameVersion {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.text)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DataVersionReason {
    NewestNotNewerThanGame,
    GameOlderThanAll,
    GameUnknown,
}

#[derive(Clone, Debug)]
pub struct DataVersionChoice {
    pub version: Option<String>,
    pub game: Option<GameVersion>,
    pub reason: DataVersionReason,
}

/// The newest data version that is not newer than the game; the oldest when the game is older than all; the newest
/// when the game version is unknown. Names that are not versions (`sample`) are ignored.
pub fn select<'a>(available: impl IntoIterator<Item = &'a str>, game_version: Option<&str>) -> DataVersionChoice {
    let mut versions: Vec<(String, GameVersion)> =
        available.into_iter().filter_map(|n| GameVersion::parse(Some(n)).map(|v| (n.to_string(), v))).collect();
    versions.sort_by(|a, b| a.1.cmp(&b.1)); // stable, like OrderBy
    let game = GameVersion::parse(game_version);
    if versions.is_empty() {
        let reason = if game.is_none() { DataVersionReason::GameUnknown } else { DataVersionReason::GameOlderThanAll };
        return DataVersionChoice { version: None, game, reason };
    }
    let Some(g) = game else {
        return DataVersionChoice { version: Some(versions.last().unwrap().0.clone()), game: None, reason: DataVersionReason::GameUnknown };
    };
    match versions.iter().rfind(|x| x.1 <= g) {
        Some(x) => DataVersionChoice { version: Some(x.0.clone()), game: Some(g), reason: DataVersionReason::NewestNotNewerThanGame },
        None => DataVersionChoice { version: Some(versions[0].0.clone()), game: Some(g), reason: DataVersionReason::GameOlderThanAll },
    }
}

/// The same rule for a tool given no data version: pack folders under `<content>/data/packs` against the game version
/// the local resources were prepared from.
pub fn for_local_resources(content_root: &Path, local_dir: &Path) -> DataVersionChoice {
    let packs = content_root.join("data").join("packs");
    let names: Vec<String> = std::fs::read_dir(&packs)
        .map(|rd| rd.flatten().filter(|e| e.path().is_dir()).map(|e| e.file_name().to_string_lossy().to_string()).collect())
        .unwrap_or_default();
    let m = crate::manifest::ResourceManifest::try_load(&crate::manifest::LocalLayout::new(local_dir).manifest_path());
    select(names.iter().map(String::as_str), m.and_then(|m| m.game_version).as_deref())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn versions_compare_as_numbers_and_selection_follows_the_rule() {
        let v = |s| GameVersion::parse(Some(s)).unwrap();
        assert!(v("v0.99") < v("v0.107"));
        assert!(v("v0.108-beta") < v("v0.108"));
        assert_eq!(v("v0.108"), v("0.108.0"));
        assert_eq!(v("0.108.0").text, "v0.108.0");
        assert!(GameVersion::parse(Some("v1.x")).is_none());
        let avail = ["sample", "v0.107.1", "v0.111.0", "v0.113.0"];
        for (game, want, reason) in [
            (Some("v0.112.5"), "v0.111.0", DataVersionReason::NewestNotNewerThanGame),
            (Some("v0.113"), "v0.113.0", DataVersionReason::NewestNotNewerThanGame),
            (Some("v0.100"), "v0.107.1", DataVersionReason::GameOlderThanAll),
            (None, "v0.113.0", DataVersionReason::GameUnknown),
        ] {
            let c = select(avail, game);
            assert_eq!((c.version.as_deref(), c.reason), (Some(want), reason), "{game:?}");
        }
    }
}
