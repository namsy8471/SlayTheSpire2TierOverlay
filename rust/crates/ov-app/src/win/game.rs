//! Finds the game window (C# `GameWindowTracker`): the visible top-level window of the `SlayTheSpire2` process.

use super::PixelRect;
use windows::Win32::Foundation::{CloseHandle, HWND, LPARAM, POINT, RECT};
use windows::Win32::Graphics::Gdi::{ClientToScreen, GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromWindow};
use windows::Win32::System::Threading::{OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GW_OWNER, GetClientRect, GetForegroundWindow, GetWindow, GetWindowRect, GetWindowThreadProcessId, IsIconic, IsWindow,
    IsWindowVisible,
};
use windows::core::{BOOL, PWSTR};

pub const PROCESS_NAME: &str = "SlayTheSpire2";

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct GameWindow {
    pub hwnd: isize,
    pub pid: u32,
    /// The client area clipped to its monitor (Godot's fullscreen window sticks out 2 px).
    pub client: PixelRect,
    pub minimized: bool,
    pub foreground: bool,
}

/// Remembers the last game window, so a lookup is one `IsWindow` call while the game runs.
#[derive(Default)]
pub struct GameTracker {
    hwnd: isize,
    pid: u32,
}

impl GameTracker {
    pub fn poll(&mut self) -> Option<GameWindow> {
        if !self.still_valid() {
            let (h, p) = find_game_window(PROCESS_NAME)?;
            self.hwnd = h;
            self.pid = p;
        }
        Some(describe(self.hwnd, self.pid))
    }

    fn still_valid(&self) -> bool {
        if self.hwnd == 0 {
            return false;
        }
        let h = HWND(self.hwnd as _);
        let mut pid = 0u32;
        unsafe { IsWindow(Some(h)).as_bool() && GetWindowThreadProcessId(h, Some(&mut pid)) != 0 && pid == self.pid }
    }
}

pub fn describe(hwnd: isize, pid: u32) -> GameWindow {
    let h = HWND(hwnd as _);
    unsafe {
        let mut fg_pid = 0u32;
        GetWindowThreadProcessId(GetForegroundWindow(), Some(&mut fg_pid));
        let minimized = IsIconic(h).as_bool() || !IsWindowVisible(h).as_bool();
        let mut cr = RECT::default();
        let _ = GetClientRect(h, &mut cr);
        let mut origin = POINT::default();
        let _ = ClientToScreen(h, &mut origin);
        let client = PixelRect { x: origin.x, y: origin.y, w: cr.right - cr.left, h: cr.bottom - cr.top };
        GameWindow { hwnd, pid, client: client.intersect(&monitor_rect(hwnd)), minimized, foreground: fg_pid == pid }
    }
}

pub fn monitor_rect(hwnd: isize) -> PixelRect {
    unsafe {
        let mon = MonitorFromWindow(HWND(hwnd as _), MONITOR_DEFAULTTONEAREST);
        let mut mi = MONITORINFO { cbSize: size_of::<MONITORINFO>() as u32, ..Default::default() };
        if GetMonitorInfoW(mon, &mut mi).as_bool() {
            let r = mi.rcMonitor;
            PixelRect { x: r.left, y: r.top, w: r.right - r.left, h: r.bottom - r.top }
        } else {
            PixelRect::default()
        }
    }
}

pub fn window_rect(hwnd: isize) -> PixelRect {
    let mut r = RECT::default();
    unsafe {
        let _ = GetWindowRect(HWND(hwnd as _), &mut r);
    }
    PixelRect { x: r.left, y: r.top, w: r.right - r.left, h: r.bottom - r.top }
}

struct Search {
    exe: String,
    found: Option<(isize, u32)>,
}

/// The first visible, unowned top-level window whose process exe is `<name>.exe` (like .NET `MainWindowHandle`).
pub fn find_game_window(name: &str) -> Option<(isize, u32)> {
    let mut s = Search { exe: format!("{name}.exe").to_ascii_lowercase(), found: None };
    unsafe {
        let _ = EnumWindows(Some(enum_proc), LPARAM(&mut s as *mut Search as isize));
    }
    s.found
}

unsafe extern "system" fn enum_proc(h: HWND, lp: LPARAM) -> BOOL {
    let s = unsafe { &mut *(lp.0 as *mut Search) };
    unsafe {
        if !IsWindowVisible(h).as_bool() || GetWindow(h, GW_OWNER).is_ok_and(|o| !o.is_invalid()) {
            return true.into();
        }
        let mut pid = 0u32;
        GetWindowThreadProcessId(h, Some(&mut pid));
        if pid != 0 && process_exe(pid).is_some_and(|e| e.to_ascii_lowercase().ends_with(&s.exe)) {
            s.found = Some((h.0 as isize, pid));
            return false.into();
        }
    }
    true.into()
}

fn process_exe(pid: u32) -> Option<String> {
    unsafe {
        let p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buf = [0u16; 520];
        let mut len = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(p, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &mut len).is_ok();
        let _ = CloseHandle(p);
        ok.then(|| String::from_utf16_lossy(&buf[..len as usize]))
    }
}
