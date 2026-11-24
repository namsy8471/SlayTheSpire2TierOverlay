//! Window-style plumbing for our own windows (C# `OverlayWindowHelper`): click-through, never take focus, topmost,
//! physical-pixel placement and exclusion from screen captures.

use super::PixelRect;
use windows::Win32::Foundation::{HWND, LPARAM};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GWL_EXSTYLE, GetWindowLongPtrW, GetWindowTextW, GetWindowThreadProcessId, HWND_NOTOPMOST, HWND_TOPMOST, SWP_NOACTIVATE,
    SWP_NOMOVE, SWP_NOOWNERZORDER, SWP_NOSIZE, SWP_NOZORDER, SetWindowDisplayAffinity, SetWindowLongPtrW, SetWindowPos,
    WDA_EXCLUDEFROMCAPTURE, WDA_NONE, WS_EX_LAYERED, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TRANSPARENT,
};
use windows::core::BOOL;

fn h(hwnd: isize) -> HWND {
    HWND(hwnd as _)
}

pub fn set_ex_style(hwnd: isize, bits: u32, on: bool) {
    unsafe {
        let ex = GetWindowLongPtrW(h(hwnd), GWL_EXSTYLE);
        let next = if on { ex | bits as isize } else { ex & !(bits as isize) };
        if next != ex {
            SetWindowLongPtrW(h(hwnd), GWL_EXSTYLE, next);
        }
    }
}

/// A full-game layer: clicks fall through to the game, it never takes focus and stays off the taskbar / Alt+Tab.
pub fn make_layer(hwnd: isize) {
    set_ex_style(hwnd, (WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW).0, true);
}

/// The panel: never takes focus from the game (clicks still work), off Alt+Tab.
pub fn make_panel(hwnd: isize) {
    set_ex_style(hwnd, (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW).0, true);
}

/// "클릭 통과" for the panel (Ctrl+F7).
pub fn set_click_through(hwnd: isize, on: bool) {
    set_ex_style(hwnd, (WS_EX_LAYERED | WS_EX_TRANSPARENT).0, on);
}

/// Back to the top of the TOPMOST band without moving or activating (the game or the shell can re-order above us).
pub fn assert_topmost(hwnd: isize) {
    unsafe {
        let _ = SetWindowPos(h(hwnd), Some(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }
}

pub fn clear_topmost(hwnd: isize) {
    unsafe {
        let _ = SetWindowPos(h(hwnd), Some(HWND_NOTOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }
}

/// Moves and resizes in physical pixels, topmost.
pub fn set_bounds(hwnd: isize, r: PixelRect) {
    unsafe {
        let _ = SetWindowPos(h(hwnd), Some(HWND_TOPMOST), r.x, r.y, r.w, r.h, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }
}

/// Moves in physical pixels and keeps the z-order.
pub fn set_position_in_place(hwnd: isize, x: i32, y: i32) {
    unsafe {
        let _ = SetWindowPos(h(hwnd), None, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }
}

/// Keeps the window out of screen captures (our own WGC frames, and OBS when "show in capture" is off).
pub fn set_exclude_from_capture(hwnd: isize, exclude: bool) -> bool {
    unsafe { SetWindowDisplayAffinity(h(hwnd), if exclude { WDA_EXCLUDEFROMCAPTURE } else { WDA_NONE }).is_ok() }
}

struct Find {
    pid: u32,
    title: Vec<u16>,
    found: isize,
}

/// One of this process's top-level windows by its exact title (egui viewports do not expose their HWND).
pub fn find_own_window(title: &str) -> Option<isize> {
    let mut f = Find { pid: std::process::id(), title: title.encode_utf16().collect(), found: 0 };
    unsafe {
        let _ = EnumWindows(Some(find_proc), LPARAM(&mut f as *mut Find as isize));
    }
    (f.found != 0).then_some(f.found)
}

unsafe extern "system" fn find_proc(hwnd: HWND, lp: LPARAM) -> BOOL {
    let f = unsafe { &mut *(lp.0 as *mut Find) };
    let mut pid = 0u32;
    unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
    if pid != f.pid {
        return true.into();
    }
    let mut buf = [0u16; 256];
    let n = unsafe { GetWindowTextW(hwnd, &mut buf) } as usize;
    if buf[..n] == f.title[..] {
        f.found = hwnd.0 as isize;
        return false.into();
    }
    true.into()
}
