//! Global hotkeys via `RegisterHotKey` (C# `HotkeyManager`). They work while the game has focus. A dedicated thread
//! owns the registrations and its message loop, so the UI event loop never sees `WM_HOTKEY`.
//!
//! Ctrl+F5 map marks, Ctrl+F6 always on top, Ctrl+F7 click-through, Ctrl+F8 hide, Ctrl+F9 save capture (debug),
//! Ctrl+F10 recognise now, Ctrl+F11 debug outlines. A key another program holds is logged and skipped.

use std::sync::mpsc::Sender;
use windows::Win32::UI::Input::KeyboardAndMouse::{HOT_KEY_MODIFIERS, MOD_CONTROL, MOD_NOREPEAT, RegisterHotKey};
use windows::Win32::UI::WindowsAndMessaging::{GetMessageW, MSG, WM_HOTKEY};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Hotkey {
    ToggleMapOverlay,
    ToggleAlwaysOnTop,
    ToggleClickThrough,
    ToggleHidden,
    SaveCapture,
    RecognizeNow,
    ToggleDebugOutlines,
}

/// (virtual key, key name, action). VK_F5 = 0x74 .. VK_F11 = 0x7A.
pub const BINDINGS: [(u32, &str, Hotkey); 7] = [
    (0x74, "F5", Hotkey::ToggleMapOverlay),
    (0x75, "F6", Hotkey::ToggleAlwaysOnTop),
    (0x76, "F7", Hotkey::ToggleClickThrough),
    (0x77, "F8", Hotkey::ToggleHidden),
    (0x78, "F9", Hotkey::SaveCapture),
    (0x79, "F10", Hotkey::RecognizeNow),
    (0x7A, "F11", Hotkey::ToggleDebugOutlines),
];

/// Starts the hotkey thread. `wake` is called after each key (repaint the UI so it reads the channel).
pub fn start(debug_captures: bool, tx: Sender<Hotkey>, wake: impl Fn() + Send + 'static) {
    std::thread::Builder::new()
        .name("hotkeys".into())
        .spawn(move || {
            let mods = HOT_KEY_MODIFIERS(MOD_CONTROL.0 | MOD_NOREPEAT.0);
            for (i, (vk, name, key)) in BINDINGS.iter().enumerate() {
                if *key == Hotkey::SaveCapture && !debug_captures {
                    continue;
                }
                if unsafe { RegisterHotKey(None, i as i32 + 1, mods, *vk) }.is_err() {
                    crate::applog::warn(format!("hotkey Ctrl+{name} is taken by another program; skipped"));
                }
            }
            let mut msg = MSG::default();
            while unsafe { GetMessageW(&mut msg, None, 0, 0) }.as_bool() {
                if msg.message == WM_HOTKEY {
                    let idx = msg.wParam.0.wrapping_sub(1);
                    if let Some((_, _, key)) = BINDINGS.get(idx) {
                        if tx.send(*key).is_err() {
                            return;
                        }
                        wake();
                    }
                }
            }
        })
        .expect("spawn hotkey thread");
}
