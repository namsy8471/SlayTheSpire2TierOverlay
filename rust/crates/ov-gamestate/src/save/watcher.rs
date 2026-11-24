//! Watches `current_run.save` / `current_run_mp.save` by polling (`SaveWatcher`).
//!
//! Deviation: the C# watcher used `FileSystemWatcher` events plus a debounce timer. Here the owner calls
//! [`SaveWatcher::poll`] on its own timer; a file is read once its size and write time stayed the same for the
//! debounce time. The result is the same: one read per finished write, and the same text is never reported twice.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime};

use super::ParsedSave;
use super::parser::{RUN_SAVE_FILE_NAMES, parse};
use crate::io;

/// One read of a save file.
#[derive(Clone, Debug)]
pub struct SaveSnapshot {
    pub path: String,
    pub save: Arc<ParsedSave>,
    /// Last write time, unix milliseconds.
    pub file_write_ms: i64,
    pub file_bytes: i64,
}

#[derive(Clone, Debug)]
pub enum SaveChange {
    Changed(SaveSnapshot),
    Removed(String),
    /// Could not be read or parsed (usually a half-written file; the next write fixes it).
    Error { path: String, message: String },
}

#[derive(Default)]
struct FileState {
    stat: Option<(u64, SystemTime)>,
    since: Option<Instant>,
    last_text: Option<String>,
    read_once: bool,
}

pub struct SaveWatcher {
    dir: PathBuf,
    debounce: Duration,
    files: HashMap<String, FileState>,
}

fn stat(path: &Path) -> Option<(u64, SystemTime)> {
    let m = std::fs::metadata(path).ok()?;
    Some((m.len(), m.modified().unwrap_or(SystemTime::UNIX_EPOCH)))
}

fn unix_ms(t: SystemTime) -> i64 {
    t.duration_since(SystemTime::UNIX_EPOCH).map_or(0, |d| d.as_millis() as i64)
}

impl SaveWatcher {
    pub fn new(dir: impl Into<PathBuf>, debounce: Duration) -> Self {
        Self { dir: dir.into(), debounce, files: HashMap::new() }
    }

    pub fn directory(&self) -> &Path {
        &self.dir
    }

    /// Checks both run saves. The first call reads them at once (like `Start`).
    pub fn poll(&mut self, local_net_id: Option<u64>) -> Vec<SaveChange> {
        let mut out = Vec::new();
        for name in RUN_SAVE_FILE_NAMES {
            let path = self.dir.join(name);
            let st = self.files.entry(name.to_string()).or_default();
            let now = stat(&path);
            if st.read_once && now == st.stat {
                // Unchanged since the last look: read when it has been quiet long enough.
                match st.since {
                    Some(t) if t.elapsed() >= self.debounce => st.since = None,
                    _ => continue,
                }
            } else if st.read_once {
                st.stat = now;
                st.since = Some(Instant::now());
                if !self.debounce.is_zero() {
                    continue;
                }
                st.since = None;
            } else {
                st.stat = now;
                st.read_once = true;
            }
            out.extend(Self::read_now(&path, st, local_net_id));
        }
        out
    }

    fn read_now(path: &Path, st: &mut FileState, local_net_id: Option<u64>) -> Option<SaveChange> {
        let p = path.to_string_lossy().into_owned();
        let mut last_err = String::new();
        for attempt in 1..=3u64 {
            let Some(bytes) = io::read_all_bytes(path, 6, 50) else {
                return st.last_text.take().map(|_| SaveChange::Removed(p));
            };
            let text = ov_core::json::decode(&bytes);
            if st.last_text.as_deref() == Some(text.as_str()) {
                return None;
            }
            match parse(&text, local_net_id) {
                Ok(save) => {
                    st.last_text = Some(text);
                    let written = std::fs::metadata(path).and_then(|m| m.modified()).map_or(0, unix_ms);
                    return Some(SaveChange::Changed(SaveSnapshot {
                        path: p,
                        save: Arc::new(save),
                        file_write_ms: written,
                        file_bytes: bytes.len() as i64,
                    }));
                }
                Err(e) => {
                    last_err = e.0;
                    if attempt < 3 {
                        std::thread::sleep(Duration::from_millis(150 * attempt));
                    }
                }
            }
        }
        Some(SaveChange::Error { path: p, message: last_err })
    }
}
