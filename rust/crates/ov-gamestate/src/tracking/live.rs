//! Runs a [`Tracker`] against the real files: save watcher, log tailer, saves-folder following
//! (the I/O half of `GameStateTracker`: `Start`, `WatchSaves`, `CheckSavesFolder`).
//!
//! Call [`LiveTracker::tick`] on a timer (or use [`LiveTracker::spawn`]): it polls the log every tick, the saves
//! every tick (debounced inside the watcher), and looks for a missing or switched saves folder every
//! `saves_retry` (at once when the log names another profile).

use std::path::PathBuf;
use std::sync::mpsc;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use super::tracked::TrackedRun;
use super::tracker::{Tracker, TrackerOptions};
use crate::log::LogTailer;
use crate::paths::{self, find_saves_directory};
use crate::save::watcher::{SaveChange, SaveWatcher};

pub struct LiveOptions {
    pub tracker: TrackerOptions,
    pub poll_interval: Duration,
    pub save_debounce: Duration,
    pub saves_retry: Duration,
    /// Replay the existing godot.log on start (rebuilds a run already in progress).
    pub replay_existing_log: bool,
}

impl Default for LiveOptions {
    fn default() -> Self {
        Self {
            tracker: TrackerOptions::default(),
            poll_interval: Duration::from_millis(300),
            save_debounce: Duration::from_millis(250),
            saves_retry: Duration::from_secs(10),
            replay_existing_log: true,
        }
    }
}

pub struct LiveTracker {
    pub tracker: Tracker,
    watcher: Option<SaveWatcher>,
    tailer: Option<LogTailer>,
    save_debounce: Duration,
    saves_retry: Duration,
    next_folder_check: Instant,
    last_wanted: Option<PathBuf>,
}

impl LiveTracker {
    pub fn new(o: LiveOptions) -> Self {
        let tailer = o.tracker.paths.as_ref().and_then(|p| p.log_file.clone()).map(|f| LogTailer::new(f, !o.replay_existing_log));
        let mut t = Self {
            tracker: Tracker::new(o.tracker),
            watcher: None,
            tailer,
            save_debounce: o.save_debounce,
            saves_retry: o.saves_retry,
            next_folder_check: Instant::now() + o.saves_retry,
            last_wanted: None,
        };
        let start = t.tracker.paths().and_then(|p| p.saves_directory.clone()).filter(|d| d.is_dir());
        match start {
            Some(d) => t.watch(d),
            None => {
                let root = t.tracker.paths().and_then(|p| p.root.clone());
                t.tracker.report_diag(if root.is_none() {
                    "saves folder not found; save watching disabled"
                } else {
                    "saves folder not found yet; looking for it again periodically"
                });
            }
        }
        t
    }

    /// The saves folder watched now.
    pub fn saves_directory(&self) -> Option<&std::path::Path> {
        self.watcher.as_ref().map(|w| w.directory())
    }

    fn watch(&mut self, dir: PathBuf) {
        self.tracker.set_saves_directory(dir.clone());
        self.watcher = Some(SaveWatcher::new(dir, self.save_debounce));
    }

    /// One poll of everything. True when the snapshot may have changed.
    pub fn tick(&mut self) -> bool {
        let mut changed = false;
        if let Some(t) = &mut self.tailer {
            match t.poll() {
                Ok(p) => {
                    if p.rotated {
                        self.tracker.on_log_rotated();
                        changed = true;
                    }
                    if !p.lines.is_empty() {
                        changed |= self.tracker.process_lines(&p.lines);
                    }
                }
                Err(e) => self.tracker.report_problem("godot.log handling failed", &e.to_string()),
            }
        }
        let wanted = self.tracker.wanted_saves().cloned();
        if wanted != self.last_wanted {
            self.last_wanted = wanted;
            self.next_folder_check = Instant::now();
        }
        if Instant::now() >= self.next_folder_check {
            self.next_folder_check = Instant::now() + self.saves_retry;
            changed |= self.check_saves_folder();
        }
        if let Some(w) = &mut self.watcher {
            let steam = paths::steam_id_from_path(w.directory());
            let id = self.tracker.save_local_id(steam);
            for c in w.poll(id) {
                match c {
                    SaveChange::Changed(s) => {
                        self.tracker.on_save(s);
                        changed = true;
                    }
                    SaveChange::Removed(p) => changed |= self.tracker.on_save_removed(&p),
                    SaveChange::Error { path, message } => self.tracker.on_save_error(&path, &message),
                }
            }
        }
        changed
    }

    /// Starts watching once the saves folder exists; moves to the profile the log names (only to a folder that exists).
    fn check_saves_folder(&mut self) -> bool {
        let Some(root) = self.tracker.paths().and_then(|p| p.root.clone()) else { return false };
        let want = self.tracker.wanted_saves().cloned();
        let current = self.watcher.as_ref().map(|w| w.directory().to_path_buf());
        if let Some(c) = &current
            && want.as_ref().is_none_or(|w| paths::same_path(w, c))
        {
            return false;
        }
        let dir = match &want {
            Some(w) => w.is_dir().then(|| w.clone()),
            None => find_saves_directory(&root),
        };
        let Some(dir) = dir else { return false };
        let mut changed = false;
        if let Some(c) = current {
            self.tracker.report_diag(&format!("saves folder switched to the profile the game uses: {} -> {}", c.display(), dir.display()));
            changed = self.tracker.forget_saves_in(&c);
        } else {
            self.tracker.report_diag(&format!("saves folder found: {}", dir.display()));
        }
        self.watch(dir);
        changed
    }

    /// Runs on a background thread; `on_change` gets each new snapshot (None = no run), `on_diag` each note.
    /// Dropping the returned handle's sender (or `stop`) ends the thread.
    pub fn spawn(
        o: LiveOptions,
        mut on_change: impl FnMut(Option<TrackedRun>) + Send + 'static,
        mut on_diag: impl FnMut(String) + Send + 'static,
    ) -> LiveHandle {
        let (tx, rx) = mpsc::channel::<LiveCommand>();
        let interval = o.poll_interval;
        let join = std::thread::Builder::new()
            .name("sts2-tracker".into())
            .spawn(move || {
                let mut live = LiveTracker::new(o);
                loop {
                    let changed = live.tick();
                    for d in live.tracker.take_diagnostics() {
                        on_diag(d);
                    }
                    if changed {
                        on_change(live.tracker.current().cloned());
                    }
                    match rx.recv_timeout(interval) {
                        Ok(LiveCommand::Stop) | Err(mpsc::RecvTimeoutError::Disconnected) => break,
                        Ok(LiveCommand::Run(f)) => {
                            f(&mut live.tracker);
                            on_change(live.tracker.current().cloned());
                        }
                        Err(mpsc::RecvTimeoutError::Timeout) => {}
                    }
                }
            })
            .expect("tracker thread");
        LiveHandle { tx, join: Some(join) }
    }
}

type TrackerFn = Box<dyn FnOnce(&mut Tracker) + Send>;

enum LiveCommand {
    Stop,
    Run(TrackerFn),
}

/// Handle of a spawned tracker thread.
pub struct LiveHandle {
    tx: mpsc::Sender<LiveCommand>,
    join: Option<JoinHandle<()>>,
}

impl LiveHandle {
    /// Runs `f` on the tracker thread (e.g. `on_card_offer_seen`, `replayed_reward_cards` with a reply channel).
    pub fn with_tracker(&self, f: impl FnOnce(&mut Tracker) + Send + 'static) {
        let _ = self.tx.send(LiveCommand::Run(Box::new(f)));
    }

    pub fn stop(mut self) {
        let _ = self.tx.send(LiveCommand::Stop);
        if let Some(j) = self.join.take() {
            let _ = j.join();
        }
    }
}

impl Drop for LiveHandle {
    fn drop(&mut self) {
        let _ = self.tx.send(LiveCommand::Stop);
    }
}
