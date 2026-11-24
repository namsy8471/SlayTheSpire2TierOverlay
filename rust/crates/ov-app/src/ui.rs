//! The overlay app on eframe: the root viewport is the panel (clickable, never takes focus), and a second viewport is
//! the full-game layer (transparent, click-through, topmost) where badges and map / rest / event marks are drawn.
//!
//! The panel shows the AI advice ([`crate::panel`]); the layer draws badges on the offered cards, shop items and the
//! advised rest-site button. Everything live (save/log tracker, capture, recognition, the advisor helper) is in
//! [`crate::live::Live`].

use crate::settings::Store;
use crate::tray::{Tray, TrayAction};
use crate::update::{UpdateEvent, Updater};
use crate::win::hotkey::Hotkey;
use crate::win::{PixelRect, game, style};
use egui::{Color32, Stroke, Vec2, ViewportBuilder, ViewportCommand, ViewportId};
use ov_core::loc::{self, t};
use crate::live::Live;
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

pub const PANEL_TITLE: &str = "Sts2Overlay";
pub const LAYER_TITLE: &str = "Sts2Overlay Layer";
const PANEL_SIZE: Vec2 = Vec2::new(380.0, 520.0);
/// The folded panel: the frame around the title row.
const COLLAPSED_HEIGHT: f32 = 62.0;
const TICK: Duration = Duration::from_millis(250);

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Tab {
    Recommend,
    Deck,
    Path,
    Review,
}

impl Tab {
    const ALL: [Tab; 4] = [Tab::Recommend, Tab::Deck, Tab::Path, Tab::Review];

    fn key(self) -> &'static str {
        match self {
            Tab::Recommend => "live",
            Tab::Deck => "deck",
            Tab::Path => "path",
            Tab::Review => "review",
        }
    }

    fn label(self) -> &'static str {
        match self {
            Tab::Recommend => t("AI 추천", "AI advice"),
            Tab::Deck => t("덱", "Deck"),
            Tab::Path => t("경로", "Path"),
            Tab::Review => t("상태", "Status"),
        }
    }

    fn from_key(k: Option<&str>) -> Tab {
        Tab::ALL.into_iter().find(|t| Some(t.key()) == k).unwrap_or(Tab::Recommend)
    }
}

pub struct OverlayApp {
    store: Store,
    tab: Tab,
    hidden: bool,
    click_through: bool,
    debug_outlines: bool,
    tracker: game::GameTracker,
    game: Option<game::GameWindow>,
    last_poll: Option<Instant>,
    panel_hwnd: isize,
    layer_hwnd: isize,
    layer_rect: PixelRect,
    tray: Option<Tray>,
    tray_rx: Receiver<TrayAction>,
    hotkey_rx: Receiver<Hotkey>,
    updater: Arc<Mutex<Updater>>,
    update_tx: Sender<UpdateEvent>,
    update_rx: Receiver<UpdateEvent>,
    update_line: String,
    capture_rx: Option<Receiver<String>>,
    capture_line: String,
    started: Instant,
    first_update_check_done: bool,
    live: Live,
}

impl OverlayApp {
    pub fn new(cc: &eframe::CreationContext<'_>, store: Store) -> Self {
        crate::fonts::install(&cc.egui_ctx, crate::fonts::cached_title());
        crate::theme::apply_visuals(&cc.egui_ctx);
        apply_language(&store.settings.language);
        if store.settings.panel_collapsed {
            // The window opens at the full size; fold it to the title row like the collapse button does.
            cc.egui_ctx.send_viewport_cmd(ViewportCommand::InnerSize(Vec2::new(PANEL_SIZE.x, COLLAPSED_HEIGHT)));
        }
        let wake = {
            let ctx = cc.egui_ctx.clone();
            move || ctx.request_repaint()
        };
        let (tray_tx, tray_rx) = mpsc::channel();
        let (hk_tx, hotkey_rx) = mpsc::channel();
        crate::win::hotkey::start(store.settings.debug_captures, hk_tx, wake.clone());
        let updater = Updater::new(store.settings.update_url.as_deref());
        let version_text = format!("v{} ({})", updater.current_version(), if updater.is_installed() { "설치판" } else { "개발판" });
        let tray = Tray::new(&version_text, tray_tx, wake);
        if let Some(tr) = &tray {
            tr.on_top.set_checked(store.settings.always_on_top);
            tr.map_overlay.set_checked(store.settings.map_overlay);
            tr.set_language(&store.settings.language);
        }
        crate::applog::info(format!("start: {version_text}, update source {}", updater.source));
        let (update_tx, update_rx) = mpsc::channel();
        let live = Live::new(&cc.egui_ctx, &store.settings);
        OverlayApp {
            live,
            tab: Tab::from_key(store.settings.selected_tab.as_deref()),
            store,
            hidden: false,
            click_through: false,
            debug_outlines: false,
            tracker: Default::default(),
            game: None,
            last_poll: None,
            panel_hwnd: 0,
            layer_hwnd: 0,
            layer_rect: PixelRect::default(),
            tray,
            tray_rx,
            hotkey_rx,
            updater: Arc::new(Mutex::new(updater)),
            update_tx,
            update_rx,
            update_line: String::new(),
            capture_rx: None,
            capture_line: String::new(),
            started: Instant::now(),
            first_update_check_done: false,
        }
    }

    fn save_settings(&mut self) {
        if let Err(e) = self.store.save(&crate::paths::settings_file()) {
            crate::applog::warn(format!("settings: could not save ({e})"));
        }
    }

    // ---- inputs -------------------------------------------------------------------------------------------------

    fn poll_inputs(&mut self, ctx: &egui::Context) {
        while let Ok(k) = self.hotkey_rx.try_recv() {
            match k {
                Hotkey::ToggleMapOverlay => self.toggle_map_overlay(),
                Hotkey::ToggleAlwaysOnTop => self.toggle_on_top(),
                Hotkey::ToggleClickThrough => self.toggle_click_through(),
                Hotkey::ToggleHidden => self.toggle_hidden(),
                Hotkey::SaveCapture => self.start_capture(),
                Hotkey::RecognizeNow => self.live.recognize_now(),
                Hotkey::ToggleDebugOutlines => self.debug_outlines = !self.debug_outlines,
            }
        }
        while let Ok(a) = self.tray_rx.try_recv() {
            match a {
                TrayAction::ToggleHidden => self.toggle_hidden(),
                TrayAction::ToggleAlwaysOnTop => self.toggle_on_top(),
                TrayAction::ToggleClickThrough => self.toggle_click_through(),
                TrayAction::ToggleMapOverlay => self.toggle_map_overlay(),
                TrayAction::Language(l) => {
                    self.store.settings.language = l.into();
                    apply_language(l);
                    if let Some(tr) = &self.tray {
                        tr.set_language(l);
                    }
                    self.save_settings();
                }
                TrayAction::CheckUpdate => self.start_update_check(ctx),
                TrayAction::ApplyUpdate => self.apply_update(ctx),
                TrayAction::Exit => ctx.send_viewport_cmd(ViewportCommand::Close),
            }
        }
        while let Ok(e) = self.update_rx.try_recv() {
            if let (UpdateEvent::Ready(_), Some(tr)) = (&e, &self.tray) {
                tr.apply_update.set_enabled(true);
            }
            self.update_line = e.message_ko();
        }
        if let Some(rx) = &self.capture_rx {
            if let Ok(line) = rx.try_recv() {
                self.capture_line = line;
                self.capture_rx = None;
            }
        }
    }

    fn toggle_on_top(&mut self) {
        let s = &mut self.store.settings;
        s.always_on_top = !s.always_on_top;
        if self.panel_hwnd != 0 {
            if s.always_on_top { style::assert_topmost(self.panel_hwnd) } else { style::clear_topmost(self.panel_hwnd) }
        }
        if let Some(tr) = &self.tray {
            tr.on_top.set_checked(s.always_on_top);
        }
        self.save_settings();
    }

    /// Ctrl+F8: nothing is drawn and the panel lets clicks through, but the window stays (so the UI loop keeps running).
    fn toggle_hidden(&mut self) {
        self.hidden = !self.hidden;
        if self.panel_hwnd != 0 {
            style::set_click_through(self.panel_hwnd, self.hidden || self.click_through);
        }
    }

    fn toggle_click_through(&mut self) {
        self.click_through = !self.click_through;
        if self.panel_hwnd != 0 {
            style::set_click_through(self.panel_hwnd, self.hidden || self.click_through);
        }
        if let Some(tr) = &self.tray {
            tr.click_through.set_checked(self.click_through);
        }
    }

    fn toggle_map_overlay(&mut self) {
        let s = &mut self.store.settings;
        s.map_overlay = !s.map_overlay;
        if let Some(tr) = &self.tray {
            tr.map_overlay.set_checked(s.map_overlay);
        }
        self.save_settings();
    }

    // ---- workers ------------------------------------------------------------------------------------------------

    fn start_update_check(&mut self, ctx: &egui::Context) {
        let (up, tx, ctx) = (self.updater.clone(), self.update_tx.clone(), ctx.clone());
        std::thread::spawn(move || {
            let (relay_tx, relay_rx) = mpsc::channel();
            let c2 = ctx.clone();
            let relay = std::thread::spawn(move || {
                for e in relay_rx {
                    let _ = tx.send(e);
                    c2.request_repaint();
                }
            });
            if let Ok(mut u) = up.try_lock() {
                u.check_and_download(&relay_tx);
            }
            drop(relay_tx);
            let _ = relay.join();
            ctx.request_repaint();
        });
    }

    fn apply_update(&mut self, ctx: &egui::Context) {
        self.save_settings();
        let result = self.updater.lock().map_err(|e| e.to_string()).and_then(|u| u.apply_and_restart());
        if let Err(e) = result {
            self.update_line = format!("업데이트 적용 실패: {e}");
            ctx.request_repaint();
        }
    }

    fn start_capture(&mut self) {
        let Some(g) = self.game else {
            self.capture_line = t("게임 창을 찾지 못해 캡처하지 않았습니다", "Game window not found; nothing captured").into();
            return;
        };
        let (tx, rx) = mpsc::channel();
        self.capture_rx = Some(rx);
        self.capture_line = t("캡처 중…", "Capturing…").into();
        std::thread::spawn(move || {
            let _ = tx.send(crate::spike::capture_to_file(g.hwnd, None));
        });
    }

    // ---- placement ----------------------------------------------------------------------------------------------

    fn find_windows(&mut self) {
        if self.panel_hwnd == 0 {
            if let Some(h) = style::find_own_window(PANEL_TITLE) {
                self.panel_hwnd = h;
                style::make_panel(h);
                style::set_exclude_from_capture(h, !self.store.settings.show_overlay_in_capture);
                if !self.store.settings.always_on_top {
                    style::clear_topmost(h);
                }
                if let (Some(x), Some(y)) = (self.store.settings.panel_x, self.store.settings.panel_y) {
                    style::set_position_in_place(h, x, y);
                }
            }
        }
        if self.layer_hwnd == 0 && self.layer_visible() {
            if let Some(h) = style::find_own_window(LAYER_TITLE) {
                self.layer_hwnd = h;
                self.layer_rect = PixelRect::default();
                style::make_layer(h);
                // Like C# OverlayCaptureVisibility: our recogniser captures the game window alone (WGC), which never
                // holds other windows, so the marks need no exclusion. Hidden from OBS / screenshots only on request.
                style::set_exclude_from_capture(h, !self.store.settings.show_overlay_in_capture);
            }
        }
    }

    fn layer_visible(&self) -> bool {
        !self.hidden && self.game.is_some_and(|g| !g.minimized && !g.client.is_empty())
    }

    fn place_layer(&mut self) {
        let (Some(g), true) = (self.game, self.layer_hwnd != 0) else { return };
        // Like C# LayerPlacement: put it back whenever the window is not exactly on the game (DPI moves can resize it).
        if g.client != self.layer_rect || game::window_rect(self.layer_hwnd) != g.client {
            style::set_bounds(self.layer_hwnd, g.client);
            if self.layer_rect != g.client {
                crate::applog::info(format!("layer: placed on the game {}", g.client));
            }
            self.layer_rect = g.client;
        } else if self.store.settings.always_on_top {
            style::assert_topmost(self.layer_hwnd);
        }
    }

    // ---- drawing ------------------------------------------------------------------------------------------------

    fn panel_ui(&mut self, ui: &mut egui::Ui) {
        let alpha = (self.store.settings.panel_opacity.clamp(0.2, 1.0) * 255.0) as u8;
        if !self.hidden {
            crate::theme::panel_frame(ui.painter(), ui.max_rect(), alpha);
        }
        let margin = crate::theme::FRAME_INSET as i8 + 12;
        let frame = egui::Frame::new().inner_margin(egui::Margin { left: margin, right: margin, top: margin - 2, bottom: margin - 2 });
        frame.show(ui, |ui| {
            ui.set_min_size(ui.available_size());
            if self.hidden {
                return;
            }
            self.title_bar(ui);
            if self.store.settings.panel_collapsed {
                return;
            }
            ui.add_space(6.0);
            let labels: Vec<&str> = Tab::ALL.iter().map(|t| t.label()).collect();
            let selected = Tab::ALL.iter().position(|t| *t == self.tab).unwrap_or(0);
            if let Some(i) = crate::theme::tabs(ui, &labels, selected) {
                let tab = Tab::ALL[i];
                if self.tab != tab {
                    self.tab = tab;
                    self.store.settings.selected_tab = Some(tab.key().into());
                }
            }
            crate::theme::divider(ui);
            egui::ScrollArea::vertical().show(ui, |ui| match self.tab {
                Tab::Recommend => crate::panel::recommend_ui(ui, &self.live),
                Tab::Deck => crate::panel::deck_ui(ui, &self.live),
                Tab::Path => crate::panel::path_ui(ui, &self.live),
                Tab::Review => self.spike_ui(ui),
            });
        });
    }

    fn title_bar(&mut self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            ui.spacing_mut().item_spacing.x = 4.0;
            let title = crate::theme::title(ui, t("StS2 오버레이", "StS2 Overlay"), 24.0);
            if title.drag_started() {
                ui.ctx().send_viewport_cmd(ViewportCommand::StartDrag);
            }
            let collapsed = self.store.settings.panel_collapsed;
            if crate::theme::small_button(ui, if collapsed { "▼" } else { "▲" }).clicked() {
                self.store.settings.panel_collapsed = !collapsed;
                let h = if collapsed { PANEL_SIZE.y } else { COLLAPSED_HEIGHT };
                ui.ctx().send_viewport_cmd(ViewportCommand::InnerSize(Vec2::new(PANEL_SIZE.x, h)));
                self.save_settings();
            }
        });
    }

    fn run_ui(&self, ui: &mut egui::Ui) {
        let Some(r) = self.live.brain.run() else {
            ui.label(t("진행 중인 런을 찾지 못했습니다", "No run in progress"));
            return;
        };
        let s = &r.state;
        egui::Grid::new("run").num_columns(2).spacing([12.0, 4.0]).show(ui, |ui| {
            ui.label(t("캐릭터", "Character"));
            ui.label(format!("{} A{}{}", s.character, s.ascension, if s.is_multiplayer { t(" · 협동", " · co-op") } else { "" }));
            ui.end_row();
            ui.label(t("위치", "Where"));
            ui.label(format!("{}막 {}층 · {:?}", s.act, s.floor, r.screen));
            ui.end_row();
            ui.label(t("체력·골드", "HP / gold"));
            ui.label(if s.vitals_known { format!("{}/{} · {}G", s.hp, s.max_hp, s.gold) } else { t("모름", "unknown").into() });
            ui.end_row();
            ui.label(t("덱·유물", "Deck / relics"));
            ui.label(format!("{} · {}", s.deck.len(), s.relics.len()));
            ui.end_row();
        });
    }

    fn spike_ui(&mut self, ui: &mut egui::Ui) {
        self.run_ui(ui);
        crate::theme::divider(ui);
        if self.store.loaded_from_broken_file() {
            ui.colored_label(crate::theme::WARN, t("settings.json을 읽지 못해 기본값으로 실행 중입니다 (원본은 settings.json.bad로 보관)", "settings.json could not be read, running with default settings (the original is kept as settings.json.bad)"));
        }
        if self.live.brain.advisor_available() {
            let mut on = self.live.brain.enabled();
            if ui.checkbox(&mut on, t("AI 추천 켜기 (조언 도우미 실행)", "AI advice on (runs the helper)")).changed() {
                self.live.brain.set_enabled(on);
                self.store.settings.combat_advisor_enabled = on;
                self.save_settings();
            }
        }
        crate::theme::section_head(ui, t("연결 상태", "Connections"));
        egui::Grid::new("m0").num_columns(2).spacing([12.0, 6.0]).show(ui, |ui| {
            ui.label(t("게임 창", "Game window"));
            match self.game {
                Some(g) => ui.label(format!("{}{}", g.client, if g.minimized { t(" (최소화)", " (minimized)") } else { "" })),
                None => ui.label(t("찾지 못함 (SlayTheSpire2.exe)", "not found (SlayTheSpire2.exe)")),
            };
            ui.end_row();
            ui.label(t("겹침 창", "Layer"));
            ui.label(if self.layer_hwnd != 0 && self.layer_visible() {
                format!("{} {}", t("게임 위", "on the game"), self.layer_rect)
            } else {
                t("없음", "none").into()
            });
            ui.end_row();
            ui.label(t("항상 맨 앞", "Always on top"));
            ui.label(if self.store.settings.always_on_top { "on" } else { "off" });
            ui.end_row();
            ui.label(t("클릭 통과", "Click-through"));
            ui.label(if self.click_through { "on" } else { "off" });
            ui.end_row();
        });
        ui.add_space(8.0);
        if ui.button(t("WGC 캡처 1장 저장", "Save one WGC frame")).clicked() {
            self.start_capture();
        }
        if !self.capture_line.is_empty() {
            ui.label(&self.capture_line);
        }
        ui.add_space(8.0);
        let (installed, version, source) = match self.updater.try_lock() {
            Ok(u) => (u.is_installed(), u.current_version(), u.source.to_string()),
            Err(_) => (true, "…".into(), String::new()),
        };
        ui.label(format!("{} v{version} · {}", if installed { t("설치판", "installed") } else { t("개발판", "dev build") }, source));
        if ui.button(t("업데이트 확인", "Check for updates")).clicked() {
            self.start_update_check(ui.ctx());
        }
        if !self.update_line.is_empty() {
            ui.label(&self.update_line);
        }
        if self.updater.try_lock().is_ok_and(|u| u.ready_version().is_some()) && ui.button(t("업데이트 적용 (재시작)", "Apply update (restart)")).clicked() {
            self.apply_update(ui.ctx());
        }
    }

    fn layer_ui(ui: &mut egui::Ui, live: &Live, layer: PixelRect, outlines: bool) {
        if outlines {
            let r = ui.ctx().content_rect();
            ui.painter().rect_stroke(r.shrink(1.0), 0.0, Stroke::new(1.0, Color32::RED), egui::StrokeKind::Inside);
        }
        crate::panel::layer_marks(ui, live, layer, 1.0, outlines);
    }
}

fn apply_language(setting: &str) {
    // "auto" follows the game (read with the game state in M4); Korean until then.
    loc::set_language(if setting == "eng" { loc::Lang::Eng } else { loc::Lang::Kor });
}

impl eframe::App for OverlayApp {
    fn logic(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.poll_inputs(ctx);
        if self.last_poll.is_none_or(|t| t.elapsed() >= TICK) {
            self.last_poll = Some(Instant::now());
            self.game = self.tracker.poll();
            self.find_windows();
            let shown = self.layer_visible() && self.game.is_some_and(|g| g.foreground);
            self.live.tick(self.game, shown, self.store.settings.map_overlay);
            if self.live.brain.take_tab_request() {
                self.tab = Tab::Recommend;
            }
            self.place_layer();
            if self.panel_hwnd != 0 && self.store.settings.always_on_top {
                style::assert_topmost(self.panel_hwnd);
            }
        }
        if !self.first_update_check_done && self.started.elapsed() >= Duration::from_secs(15) {
            self.first_update_check_done = true;
            let auto = self.store.settings.auto_update && self.updater.try_lock().is_ok_and(|u| u.is_installed());
            if auto {
                self.start_update_check(ctx);
            }
        }
        ctx.request_repaint_after(TICK);
    }

    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        self.panel_ui(ui);
        if self.layer_visible() {
            let outlines = self.debug_outlines;
            let builder = ViewportBuilder::default()
                .with_title(LAYER_TITLE)
                .with_decorations(false)
                .with_transparent(true)
                .with_mouse_passthrough(true)
                .with_window_level(egui::WindowLevel::AlwaysOnTop)
                .with_taskbar(false)
                .with_active(false)
                .with_inner_size(Vec2::new(64.0, 64.0));
            let (live, layer) = (&self.live, self.layer_rect);
            ui.ctx().show_viewport_immediate(ViewportId::from_hash_of("layer"), builder, |ui, _class| {
                OverlayApp::layer_ui(ui, live, layer, outlines);
            });
        } else if self.layer_hwnd != 0 {
            // The viewport is closed when it is not shown this pass; its HWND is gone.
            self.layer_hwnd = 0;
        }
    }

    fn clear_color(&self, _visuals: &egui::Visuals) -> [f32; 4] {
        [0.0, 0.0, 0.0, 0.0]
    }

    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        if self.panel_hwnd != 0 {
            let r = game::window_rect(self.panel_hwnd);
            self.store.settings.panel_x = Some(r.x);
            self.store.settings.panel_y = Some(r.y);
        }
        self.save_settings();
        if let Ok(u) = self.updater.lock() {
            u.apply_on_exit();
        }
        crate::applog::info("exit");
    }
}

pub fn native_options() -> eframe::NativeOptions {
    eframe::NativeOptions {
        viewport: ViewportBuilder::default()
            .with_title(PANEL_TITLE)
            .with_inner_size(PANEL_SIZE)
            .with_decorations(false)
            .with_transparent(true)
            .with_window_level(egui::WindowLevel::AlwaysOnTop)
            .with_taskbar(false)
            .with_active(false),
        ..Default::default()
    }
}
