//! Win32 / WinRT plumbing. Only built on Windows.

pub mod game;
pub mod hotkey;
pub mod style;
pub mod wgc;

/// A rectangle in physical screen pixels.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PixelRect {
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
}

impl PixelRect {
    pub fn is_empty(&self) -> bool {
        self.w <= 0 || self.h <= 0
    }

    pub fn intersect(&self, o: &PixelRect) -> PixelRect {
        let (l, t) = (self.x.max(o.x), self.y.max(o.y));
        let (r, b) = ((self.x + self.w).min(o.x + o.w), (self.y + self.h).min(o.y + o.h));
        if r <= l || b <= t { PixelRect::default() } else { PixelRect { x: l, y: t, w: r - l, h: b - t } }
    }
}

impl std::fmt::Display for PixelRect {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}x{} at ({},{})", self.w, self.h, self.x, self.y)
    }
}

/// Lets a `windows_subsystem = "windows"` exe print to the console it was started from (spike commands).
pub fn attach_parent_console() {
    use windows::Win32::System::Console::{ATTACH_PARENT_PROCESS, AttachConsole};
    unsafe {
        let _ = AttachConsole(ATTACH_PARENT_PROCESS);
    }
}

/// Per-monitor DPI v2 before any window exists, so every rect we read and set is in physical pixels.
pub fn set_dpi_awareness() {
    use windows::Win32::UI::HiDpi::{DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetProcessDpiAwarenessContext};
    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
}
