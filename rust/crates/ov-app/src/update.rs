//! Velopack auto-update (C# `UpdateService`, `UpdateSourceSpec`). Same packId (`namsy8471.Sts2Overlay`) and channel
//! (`win`) as the C# releases, so an installed C# build updates itself to this one.
//!
//! The source comes from the `updateUrl` setting: empty = the official GitHub releases; a github.com URL = that repo's
//! releases; another http(s) URL = a folder on the web; an absolute path or `file:` URL = a local folder (tests).

use std::sync::mpsc::Sender;

/// Velopack package id of the releases (C# `release.ps1 --packId`). The Rust build must be packed with the same id.
#[allow(dead_code)]
pub const PACK_ID: &str = "namsy8471.Sts2Overlay";
pub const DEFAULT_REPO_URL: &str = "https://github.com/namsy8471/SlayTheSpire2TierOverlay";

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SourceSpec {
    GitHub(String),
    Web(String),
    Folder(String),
}

impl std::fmt::Display for SourceSpec {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SourceSpec::GitHub(s) => write!(f, "GitHub {s}"),
            SourceSpec::Web(s) => write!(f, "Web {s}"),
            SourceSpec::Folder(s) => write!(f, "Folder {s}"),
        }
    }
}

impl SourceSpec {
    /// C# `UpdateSourceSpec.TryParse`. None = invalid (the caller falls back to the default and logs it).
    pub fn parse(setting: Option<&str>) -> Option<SourceSpec> {
        let v = setting.map(str::trim).unwrap_or("");
        if v.is_empty() {
            return Some(SourceSpec::GitHub(DEFAULT_REPO_URL.into()));
        }
        let lower = v.to_ascii_lowercase();
        if lower.starts_with("http://") || lower.starts_with("https://") {
            let after = &v[v.find("://").unwrap() + 3..];
            let (host, path) = after.split_once('/').unwrap_or((after, ""));
            let host = host.to_ascii_lowercase();
            if host == "github.com" || host == "www.github.com" {
                let path = path.split(['?', '#']).next().unwrap_or("");
                let parts: Vec<&str> = path.split('/').filter(|p| !p.is_empty()).collect();
                if parts.len() < 2 {
                    return None;
                }
                let repo = parts[1];
                let repo = if repo.len() > 4 && repo[repo.len() - 4..].eq_ignore_ascii_case(".git") {
                    &repo[..repo.len() - 4]
                } else {
                    repo
                };
                return Some(SourceSpec::GitHub(format!("https://github.com/{}/{repo}", parts[0])));
            }
            return Some(SourceSpec::Web(v.into()));
        }
        if lower.starts_with("file:") {
            let p = v[5..].trim_start_matches('/');
            let p = if is_fully_qualified(p) { p.to_string() } else { format!("/{p}") };
            return is_fully_qualified(&p).then(|| SourceSpec::Folder(p.replace("%20", " ")));
        }
        if v.contains("://") {
            return None;
        }
        is_fully_qualified(v).then(|| SourceSpec::Folder(v.into()))
    }
}

/// Windows `Path.IsPathFullyQualified`: `C:\..`, `C:/..` or a UNC `\\server\share`. Off Windows also `/..` (tests).
fn is_fully_qualified(p: &str) -> bool {
    let b = p.as_bytes();
    let drive = b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && (b[2] == b'\\' || b[2] == b'/');
    let unc = p.starts_with("\\\\") || p.starts_with("//");
    drive || unc || (cfg!(not(windows)) && p.starts_with('/'))
}

/// What the tray and the panel show.
#[derive(Debug, Clone, PartialEq)]
pub enum UpdateEvent {
    Checking,
    UpToDate(String),
    Downloading(String),
    Progress(i16),
    Ready(String),
    NotInstalled,
    Failed(String),
}

impl UpdateEvent {
    /// The Korean line for the user (C# `UpdateService.Message`).
    pub fn message_ko(&self) -> String {
        match self {
            UpdateEvent::Checking => "업데이트 확인 중…".into(),
            UpdateEvent::UpToDate(v) => format!("최신 버전입니다 (v{v})"),
            UpdateEvent::Downloading(v) => format!("v{v} 내려받는 중…"),
            UpdateEvent::Progress(p) => format!("내려받는 중 {p}%"),
            UpdateEvent::Ready(v) => format!("v{v} 준비됨. 종료하면 적용됩니다 (트레이에서 바로 재시작 가능)"),
            UpdateEvent::NotInstalled => "설치판이 아니라서 업데이트를 확인하지 않습니다".into(),
            UpdateEvent::Failed(why) => format!("업데이트 확인 실패: {why}"),
        }
    }
}

/// Thin wrapper over `velopack::UpdateManager`. All calls block: run them on a worker thread.
pub struct Updater {
    manager: Option<velopack::UpdateManager>,
    pub source: SourceSpec,
    pending: Option<velopack::UpdateInfo>,
}

impl Updater {
    pub fn new(update_url: Option<&str>) -> Updater {
        let source = SourceSpec::parse(update_url).unwrap_or_else(|| {
            crate::applog::warn(format!("update: updateUrl '{}' is not a URL or an absolute folder; using {DEFAULT_REPO_URL}", update_url.unwrap_or("")));
            SourceSpec::GitHub(DEFAULT_REPO_URL.into())
        });
        let options = velopack::UpdateOptions { ExplicitChannel: None, ..Default::default() };
        let made = match &source {
            SourceSpec::GitHub(url) => velopack::UpdateManager::new(velopack::sources::GithubSource::new(url, None, false), Some(options), None),
            SourceSpec::Web(url) => velopack::UpdateManager::new(velopack::sources::HttpSource::new(url), Some(options), None),
            SourceSpec::Folder(dir) => velopack::UpdateManager::new(velopack::sources::FileSource::new(dir), Some(options), None),
        };
        let manager = match made {
            Ok(m) => Some(m),
            Err(e) => {
                crate::applog::info(format!("update: not an installed build ({e})"));
                None
            }
        };
        Updater { manager, source, pending: None }
    }

    pub fn is_installed(&self) -> bool {
        self.manager.is_some()
    }

    pub fn current_version(&self) -> String {
        self.manager.as_ref().map(|m| m.get_current_version_as_string()).unwrap_or_else(|| env!("CARGO_PKG_VERSION").into())
    }

    /// Checks and, when newer, downloads. Sends every step to `events`.
    pub fn check_and_download(&mut self, events: &Sender<UpdateEvent>) {
        let Some(m) = self.manager.clone() else {
            let _ = events.send(UpdateEvent::NotInstalled);
            return;
        };
        let _ = events.send(UpdateEvent::Checking);
        crate::applog::info(format!("update: v{} checking {}", m.get_current_version_as_string(), self.source));
        let info = match m.check_for_updates() {
            Ok(velopack::UpdateCheck::UpdateAvailable(info)) => *info,
            Ok(_) => {
                let _ = events.send(UpdateEvent::UpToDate(m.get_current_version_as_string()));
                return;
            }
            Err(e) => {
                crate::applog::warn(format!("update: check failed ({e})"));
                let _ = events.send(UpdateEvent::Failed(e.to_string()));
                return;
            }
        };
        let version = info.TargetFullRelease.Version.clone();
        let _ = events.send(UpdateEvent::Downloading(version.clone()));
        let (ptx, prx) = std::sync::mpsc::channel::<i16>();
        let fwd = events.clone();
        let relay = std::thread::spawn(move || {
            for p in prx {
                let _ = fwd.send(UpdateEvent::Progress(p));
            }
        });
        let result = m.download_updates(&info, Some(ptx));
        let _ = relay.join();
        match result {
            Ok(()) => {
                crate::applog::info(format!("update: v{version} downloaded"));
                self.pending = Some(info);
                let _ = events.send(UpdateEvent::Ready(version));
            }
            Err(e) => {
                crate::applog::warn(format!("update: download failed ({e})"));
                let _ = events.send(UpdateEvent::Failed(e.to_string()));
            }
        }
    }

    pub fn ready_version(&self) -> Option<String> {
        self.pending.as_ref().map(|i| i.TargetFullRelease.Version.clone())
    }

    /// Tray "업데이트 적용 (재시작)": applies now and starts the new version. The process exits.
    pub fn apply_and_restart(&self) -> Result<(), String> {
        match (&self.manager, &self.pending) {
            (Some(m), Some(info)) => m.apply_updates_and_restart(info).map_err(|e| e.to_string()),
            _ => Err("no downloaded update".into()),
        }
    }

    /// On a normal exit: apply silently after this process ends, without starting it again.
    pub fn apply_on_exit(&self) {
        if let (Some(m), Some(info)) = (&self.manager, &self.pending) {
            let no_args: Vec<String> = Vec::new();
            if let Err(e) = m.wait_exit_then_apply_updates(info, true, false, no_args) {
                crate::applog::warn(format!("update: apply on exit failed ({e})"));
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::SourceSpec::*;
    use super::*;

    #[test]
    fn parse_matches_c_sharp_rules() {
        let cases: [(Option<&str>, Option<SourceSpec>); 9] = [
            (None, Some(GitHub(DEFAULT_REPO_URL.into()))),
            (Some("  "), Some(GitHub(DEFAULT_REPO_URL.into()))),
            (Some("https://github.com/a/b.git"), Some(GitHub("https://github.com/a/b".into()))),
            (Some("https://www.github.com/a/b/releases/latest"), Some(GitHub("https://github.com/a/b".into()))),
            (Some("https://github.com/a"), None),
            (Some("https://example.com/vpk"), Some(Web("https://example.com/vpk".into()))),
            (Some("C:\\vpk\\out"), Some(Folder("C:\\vpk\\out".into()))),
            (Some("relative\\dir"), None),
            (Some("ftp://x/y"), None),
        ];
        for (input, want) in cases {
            assert_eq!(SourceSpec::parse(input), want, "input {input:?}");
        }
    }
}
