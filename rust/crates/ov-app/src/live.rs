//! The app's live side on Windows: the save/log tracker, a capture worker that recognises the screen, the local
//! resources, and the [`Brain`] that asks the advisor helper. The UI thread owns [`Live`] and calls
//! [`Live::tick`] every frame.

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use ov_core::loc;
use ov_gamestate::paths::GamePaths;
use ov_gamestate::tracking::live::{LiveHandle, LiveOptions, LiveTracker};
use ov_gamestate::tracking::tracked::{ScreenHint, TrackedRun};
use ov_gamestate::tracking::tracker::TrackerOptions;
use ov_live::advisor::AdvisorCommand;
use ov_live::brain::{Brain, BrainOptions};
use ov_live::paths::AppLayout;
use ov_live::recognition::{Frame, Recognition, VisionRecognizer};
use ov_live::text::GameText;
use ov_resources::install::{GameInstall, InstalledGame};
use ov_resources::manifest::{LocalLayout, ResourceManifest};
use ov_resources::preparer::{PrepareError, PrepareNeed, ResourcePreparer};

use crate::win::PixelRect;
use crate::win::capture::CaptureSession;
use crate::win::game::GameWindow;

/// What the capture worker should look at.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Target {
    hwnd: isize,
    client: PixelRect,
}

enum Cmd {
    Target(Option<Target>),
    /// None = paused.
    Interval(Option<Duration>),
    Hint(Option<String>),
    /// Some(ancient?) in an event room: read its buttons.
    EventRoom(Option<bool>),
    Now,
}

pub struct Live {
    pub brain: Brain,
    pub text: GameText,
    pub layout: AppLayout,
    run_rx: Receiver<(Option<TrackedRun>, Option<PathBuf>)>,
    rec_rx: Receiver<Recognition>,
    cmd_tx: Sender<Cmd>,
    epoch: Arc<AtomicI32>,
    recognizer: Arc<VisionRecognizer>,
    /// The resource preparation line (progress or a problem); None when nothing to say.
    pub prepare_line: Arc<Mutex<Option<String>>>,
    prepared: Arc<AtomicBool>,
    target: Option<Target>,
    interval: Option<Duration>,
    hint: Option<String>,
    event_room: Option<bool>,
    _tracker: LiveHandle,
}

fn log() -> ov_live::advisor::host::Log {
    Arc::new(|m: &str| crate::applog::info(m))
}

impl Live {
    pub fn new(ctx: &egui::Context, settings: &crate::settings::Settings) -> Live {
        let exe_dir = std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.to_path_buf())).unwrap_or_default();
        let layout = AppLayout::resolve(&exe_dir, &crate::paths::data_root(), &|p| p.exists());
        crate::applog::info(format!("layout: {:?} content {} local {}", layout.kind, layout.content_root.display(), layout.local_data_dir.display()));
        let local = LocalLayout::new(&layout.local_data_dir);
        let game_version = ResourceManifest::try_load(&local.manifest_path()).and_then(|m| m.game_version);
        let version = layout.card_data_version(game_version.as_deref()).unwrap_or_else(|| "v0.113.0".into());
        let recognizer = Arc::new(VisionRecognizer::new(
            &layout.local_data_dir,
            &layout.committed_catalog(&version),
            &layout.committed_relics(&version),
            Box::new(|m: &str| crate::applog::info(m)),
        ));

        // Save + log tracker on its own thread.
        let paths = GamePaths::discover(None, None, None);
        let log_file = paths.log_file.clone();
        let (run_tx, run_rx) = mpsc::channel();
        let tracker = {
            let ctx = ctx.clone();
            let options = LiveOptions { tracker: TrackerOptions { paths: Some(paths), ..Default::default() }, ..Default::default() };
            LiveTracker::spawn_with_folder(
                options,
                move |r, dir| {
                    let _ = run_tx.send((r, dir));
                    ctx.request_repaint();
                },
                |d| crate::applog::info(format!("tracker: {d}")),
            )
        };

        // The advisor helper.
        let setting = settings.other.get("combatAdvisorCommand").and_then(|v| v.as_str());
        let (advisor, warning) = AdvisorCommand::locate(setting, &exe_dir, &|p| p.is_file());
        if let Some(w) = warning {
            crate::applog::warn(format!("advisor: {w}"));
        }
        crate::applog::info(match &advisor {
            Some(c) => format!("advisor: {} ({})", c.file_name.display(), c.source),
            None => "advisor: no helper found (advice off)".into(),
        });
        let advisor_data = advisor.as_ref().and_then(|c| c.file_name.parent().map(|d| d.to_path_buf()));
        let brain = Brain::new(BrainOptions { advisor, enabled: settings.combat_advisor_enabled, log_file, advisor_data }, log());

        let epoch = Arc::new(AtomicI32::new(0));
        let (cmd_tx, cmd_rx) = mpsc::channel();
        let (rec_tx, rec_rx) = mpsc::channel();
        spawn_capture(cmd_rx, rec_tx, recognizer.clone(), epoch.clone(), ctx.clone());

        let prepare_line = Arc::new(Mutex::new(None));
        let prepared = Arc::new(AtomicBool::new(false));
        let game_dir = settings.other.get("gameDir").and_then(|v| v.as_str()).map(str::to_string);
        spawn_prepare(local.clone(), game_dir, prepare_line.clone(), prepared.clone(), ctx.clone());

        Live {
            brain,
            text: GameText::load(&local.localization_dir()),
            layout,
            run_rx,
            rec_rx,
            cmd_tx,
            epoch,
            recognizer,
            prepare_line,
            prepared,
            target: None,
            interval: None,
            hint: None,
            event_room: None,
            _tracker: tracker,
        }
    }

    /// One UI tick. `game`: the game window now; `shown`: the overlay draws (not hidden, game in front).
    pub fn tick(&mut self, game: Option<GameWindow>, shown: bool) {
        if self.prepared.swap(false, Ordering::Relaxed) {
            self.text = GameText::load(&LocalLayout::new(&self.layout.local_data_dir).localization_dir());
            self.recognizer.reload();
        }
        let mut last = None;
        while let Ok(r) = self.run_rx.try_recv() {
            last = Some(r);
        }
        if let Some((run, dir)) = last {
            self.brain.on_run(run, dir);
            self.epoch.store(self.brain.epoch(), Ordering::Relaxed);
            let hint = self.brain.run().map(|r| r.state.character.clone());
            if hint != self.hint {
                self.hint = hint.clone();
                let _ = self.cmd_tx.send(Cmd::Hint(hint));
            }
        }
        let er = self.brain.event_room();
        if er != self.event_room {
            self.event_room = er;
            let _ = self.cmd_tx.send(Cmd::EventRoom(er));
        }
        while let Ok(r) = self.rec_rx.try_recv() {
            self.brain.on_recognition(r, Instant::now());
        }
        self.brain.poll();

        let target = game.filter(|g| !g.minimized && !g.client.is_empty()).map(|g| Target { hwnd: g.hwnd, client: g.client });
        if target != self.target {
            self.target = target;
            let _ = self.cmd_tx.send(Cmd::Target(target));
        }
        let interval = if !shown || target.is_none() {
            None
        } else {
            Some(match self.brain.run().map(|r| r.screen) {
                Some(ScreenHint::RewardScreenLikely | ScreenHint::ShopLikely | ScreenHint::RestSite | ScreenHint::Event | ScreenHint::Treasure) => Duration::from_millis(333),
                Some(ScreenHint::Combat | ScreenHint::MainMenu) => Duration::from_millis(1500),
                _ => Duration::from_millis(700),
            })
        };
        if interval != self.interval {
            self.interval = interval;
            let _ = self.cmd_tx.send(Cmd::Interval(interval));
        }
    }

    /// Hotkey "recognise now".
    pub fn recognize_now(&self) {
        let _ = self.cmd_tx.send(Cmd::Now);
    }
}

fn spawn_capture(rx: Receiver<Cmd>, tx: Sender<Recognition>, recognizer: Arc<VisionRecognizer>, epoch: Arc<AtomicI32>, ctx: egui::Context) {
    let _ = std::thread::Builder::new().name("capture".into()).spawn(move || {
        unsafe {
            let _ = windows::Win32::System::WinRT::RoInitialize(windows::Win32::System::WinRT::RO_INIT_MULTITHREADED);
        }
        // The recogniser builds its art database on first use (can take seconds the very first time).
        recognizer.preload();
        let mut target: Option<Target> = None;
        let mut interval: Option<Duration> = None;
        let mut hint: Option<String> = None;
        let mut event_room: Option<bool> = None;
        let mut session: Option<CaptureSession> = None;
        let mut next = Instant::now();
        let mut failures = 0u32;
        loop {
            let wait = match interval {
                Some(_) => next.saturating_duration_since(Instant::now()),
                None => Duration::from_secs(3600),
            };
            let mut now_asked = false;
            match rx.recv_timeout(wait) {
                Ok(Cmd::Target(t)) => {
                    if t.map(|t| t.hwnd) != target.map(|t| t.hwnd) {
                        session = None;
                    }
                    target = t;
                    continue;
                }
                Ok(Cmd::Interval(i)) => {
                    interval = i;
                    if i.is_none() {
                        session = None; // paused: stop the capture session (a yellow border must not stay)
                    }
                    next = Instant::now();
                    continue;
                }
                Ok(Cmd::Hint(h)) => {
                    hint = h;
                    continue;
                }
                Ok(Cmd::EventRoom(e)) => {
                    event_room = e;
                    continue;
                }
                Ok(Cmd::Now) => now_asked = true,
                Err(RecvTimeoutError::Disconnected) => break,
                Err(RecvTimeoutError::Timeout) => {}
            }
            let Some(t) = target else { continue };
            if interval.is_none() && !now_asked {
                continue;
            }
            next = Instant::now() + interval.unwrap_or(Duration::from_millis(700));
            if session.is_none() {
                match CaptureSession::start(t.hwnd) {
                    Ok(s) => session = Some(s),
                    Err(e) => {
                        failures += 1;
                        if failures <= 5 {
                            crate::applog::warn(format!("capture: could not start ({e})"));
                        }
                        next = Instant::now() + Duration::from_secs(5);
                        continue;
                    }
                }
            }
            let Some(s) = session.as_mut() else { continue };
            let frame = match s.grab(t.client) {
                Ok(Some(f)) => f,
                Ok(None) => continue,
                Err(e) => {
                    crate::applog::warn(format!("capture: frame failed ({e}); restarting the session"));
                    session = None;
                    continue;
                }
            };
            let e = epoch.load(Ordering::Relaxed);
            let f = Frame { bgra: &frame.bgra, width: frame.width, height: frame.height, stride: frame.width * 4, origin: (frame.rect.x, frame.rect.y) };
            if let Some(mut r) = recognizer.recognize_with(&f, hint.as_deref(), event_room) {
                r.epoch = e;
                if tx.send(r).is_err() {
                    break;
                }
                ctx.request_repaint();
            }
        }
    });
}

fn spawn_prepare(
    local: LocalLayout,
    game_dir: Option<String>,
    line: Arc<Mutex<Option<String>>>,
    done: Arc<AtomicBool>,
    ctx: egui::Context,
) {
    let _ = std::thread::Builder::new().name("resources".into()).spawn(move || {
        let set = |s: Option<String>| {
            if let Ok(mut l) = line.lock() {
                *l = s;
            }
            ctx.request_repaint();
        };
        let Some(install) = GameInstall::locate(game_dir.as_deref(), None) else {
            crate::applog::warn(format!("resources: Slay the Spire 2 install not found (gameDir = {})", game_dir.as_deref().unwrap_or("(auto)")));
            if local.manifest_catalog().is_none() {
                set(Some(loc::t("게임 설치 폴더를 찾지 못해 카드 그림·이름을 준비하지 못했습니다 (settings.json의 gameDir 지정)", "Game folder not found, so card art and names were not prepared (set gameDir in settings.json)").into()));
            }
            return;
        };
        let game = match InstalledGame::detect(install) {
            Ok(g) => g,
            Err(e) => {
                crate::applog::warn(format!("resources: could not read the game files ({e})"));
                return;
            }
        };
        crate::applog::info(format!("resources: game {} at {}", game.version_label(), game.install.directory.display()));
        let preparer = ResourcePreparer::new(local.clone());
        let check = preparer.check(&game);
        crate::applog::info(format!("resources: {:?} ({})", check.need, check.reason));
        if check.need == PrepareNeed::UpToDate {
            return;
        }
        let mut progress = |p: ov_resources::preparer::PrepareProgress| set(Some(format!("{} · {:.0}%", p.text, p.fraction * 100.0)));
        match preparer.prepare(&game, &mut progress, &|| false) {
            Ok(m) => {
                crate::applog::info(format!("resources: prepared {} cards, {} portraits", m.catalog_cards, m.card_art_files));
                set(None);
                done.store(true, Ordering::Relaxed);
            }
            Err(PrepareError::Busy(_)) => set(Some(loc::t("다른 프로세스가 리소스를 준비 중입니다. 끝나면 오버레이를 다시 시작해 주세요", "Another process is preparing resources. Restart the overlay when it is done").into())),
            Err(e) => {
                crate::applog::warn(format!("resources: preparation failed ({e:?})"));
                set(Some(loc::t("게임 리소스 준비에 실패했습니다. 게임이 업데이트됐다면 새 오버레이 버전이 필요할 수 있습니다", "Preparing game resources failed. A game update may need a newer overlay").into()));
            }
        }
    });
}
