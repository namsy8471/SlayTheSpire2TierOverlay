//! Tray icon and its menu (C# `TrayIcon`). Menu clicks arrive as `TrayAction`s on a channel; the UI reads them.

use std::sync::mpsc::Sender;
use tray_icon::menu::{CheckMenuItem, Menu, MenuEvent, MenuId, MenuItem, PredefinedMenuItem, Submenu};
use tray_icon::{Icon, TrayIcon, TrayIconBuilder};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrayAction {
    ToggleHidden,
    ToggleAlwaysOnTop,
    ToggleClickThrough,
    ToggleMapOverlay,
    Language(&'static str),
    CheckUpdate,
    ApplyUpdate,
    Exit,
}

pub struct Tray {
    _icon: TrayIcon,
    pub on_top: CheckMenuItem,
    pub click_through: CheckMenuItem,
    pub map_overlay: CheckMenuItem,
    pub lang_auto: CheckMenuItem,
    pub lang_kor: CheckMenuItem,
    pub lang_eng: CheckMenuItem,
    pub apply_update: MenuItem,
}

const ICON: &[u8] = include_bytes!("../assets/Sts2Overlay.ico");

fn icon() -> Option<Icon> {
    let img = image::load_from_memory_with_format(ICON, image::ImageFormat::Ico).ok()?.into_rgba8();
    let (w, h) = img.dimensions();
    Icon::from_rgba(img.into_raw(), w, h).ok()
}

impl Tray {
    /// Builds the tray on the UI thread (it needs that thread's message loop). `wake` repaints the UI after a click.
    pub fn new(version_text: &str, tx: Sender<TrayAction>, wake: impl Fn() + Send + Sync + 'static) -> Option<Tray> {
        let toggle = MenuItem::new("패널 보이기/숨기기 (Ctrl+F8)", true, None);
        let on_top = CheckMenuItem::new("항상 맨 앞 (Ctrl+F6)", true, true, None);
        let click_through = CheckMenuItem::new("클릭 통과 (Ctrl+F7)", true, false, None);
        let map_overlay = CheckMenuItem::new("지도 위 표시 (Ctrl+F5)", true, true, None);
        let lang_auto = CheckMenuItem::new("게임 언어 따라가기", true, true, None);
        let lang_kor = CheckMenuItem::new("한국어", true, false, None);
        let lang_eng = CheckMenuItem::new("English", true, false, None);
        let language = Submenu::with_items("언어 / Language", true, &[&lang_auto, &lang_kor, &lang_eng]).ok()?;
        let check_update = MenuItem::new("업데이트 확인", true, None);
        let apply_update = MenuItem::new("업데이트 적용 (재시작)", false, None);
        let version = MenuItem::new(version_text, false, None);
        let exit = MenuItem::new("종료", true, None);
        let sep = PredefinedMenuItem::separator();
        let menu = Menu::with_items(&[
            &toggle,
            &on_top,
            &click_through,
            &map_overlay,
            &language,
            &sep,
            &version,
            &check_update,
            &apply_update,
            &PredefinedMenuItem::separator(),
            &exit,
        ])
        .ok()?;

        let ids: Vec<(MenuId, TrayAction)> = vec![
            (toggle.id().clone(), TrayAction::ToggleHidden),
            (on_top.id().clone(), TrayAction::ToggleAlwaysOnTop),
            (click_through.id().clone(), TrayAction::ToggleClickThrough),
            (map_overlay.id().clone(), TrayAction::ToggleMapOverlay),
            (lang_auto.id().clone(), TrayAction::Language("auto")),
            (lang_kor.id().clone(), TrayAction::Language("kor")),
            (lang_eng.id().clone(), TrayAction::Language("eng")),
            (check_update.id().clone(), TrayAction::CheckUpdate),
            (apply_update.id().clone(), TrayAction::ApplyUpdate),
            (exit.id().clone(), TrayAction::Exit),
        ];
        MenuEvent::set_event_handler(Some(move |e: MenuEvent| {
            if let Some((_, a)) = ids.iter().find(|(id, _)| *id == e.id) {
                let _ = tx.send(*a);
                wake();
            }
        }));

        let mut b = TrayIconBuilder::new().with_menu(Box::new(menu)).with_tooltip("StS2 오버레이");
        if let Some(i) = icon() {
            b = b.with_icon(i);
        }
        match b.build() {
            Ok(icon) => Some(Tray { _icon: icon, on_top, click_through, map_overlay, lang_auto, lang_kor, lang_eng, apply_update }),
            Err(e) => {
                crate::applog::warn(format!("tray: could not create the icon ({e})"));
                None
            }
        }
    }

    pub fn set_language(&self, setting: &str) {
        self.lang_auto.set_checked(setting == "auto");
        self.lang_kor.set_checked(setting == "kor");
        self.lang_eng.set_checked(setting == "eng");
    }
}
