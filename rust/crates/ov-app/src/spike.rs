//! M0 spike commands, also reachable from the panel:
//! `Sts2Overlay.exe --capture-once [out.png]` and `Sts2Overlay.exe --update-check [--apply]`.

#[cfg(windows)]
use std::path::PathBuf;
#[cfg(windows)]
use std::time::{Duration, Instant};

/// Captures one WGC frame of `hwnd` and writes it as PNG. Returns the line to show.
#[cfg(windows)]
pub fn capture_to_file(hwnd: isize, out: Option<PathBuf>) -> String {
    let t0 = Instant::now();
    let frame = match crate::win::wgc::capture_once(hwnd, Duration::from_secs(5)) {
        Ok(f) => f,
        Err(e) => {
            crate::applog::warn(format!("spike capture: failed ({e})"));
            return format!("캡처 실패: {e}");
        }
    };
    let ms = t0.elapsed().as_millis();
    let path = out.unwrap_or_else(|| crate::paths::captures_dir().join("rust-wgc-spike.png"));
    if let Some(d) = path.parent() {
        let _ = std::fs::create_dir_all(d);
    }
    let blank = frame.rgba.chunks_exact(4).all(|p| p[0] == 0 && p[1] == 0 && p[2] == 0);
    let saved = image::save_buffer(&path, &frame.rgba, frame.width, frame.height, image::ColorType::Rgba8);
    let line = match saved {
        Ok(()) => format!(
            "캡처 성공: {}x{}, {ms} ms{} → {}",
            frame.width,
            frame.height,
            if blank { " (전부 검은색: 확인 필요)" } else { "" },
            path.display()
        ),
        Err(e) => format!("캡처는 됐지만 저장 실패: {e}"),
    };
    crate::applog::info(format!("spike capture: {line}"));
    line
}

/// `--capture-once [out.png]`: finds the game and saves one frame. Exit code 0 on success.
#[cfg(windows)]
pub fn capture_cli(out: Option<PathBuf>) -> i32 {
    let Some((hwnd, _)) = crate::win::game::find_game_window(crate::win::game::PROCESS_NAME) else {
        println!("게임 창을 찾지 못했습니다 (SlayTheSpire2.exe)");
        return 2;
    };
    let line = capture_to_file(hwnd, out);
    println!("{line}");
    if line.starts_with("캡처 성공") { 0 } else { 1 }
}

/// `--update-check [--apply]`: check + download from the `updateUrl` setting (or `--update-url`), print each step.
pub fn update_cli(url: Option<String>, apply: bool) -> i32 {
    let store = crate::settings::Store::load(&crate::paths::settings_file());
    let url = url.or(store.settings.update_url.clone());
    let mut up = crate::update::Updater::new(url.as_deref());
    println!("v{} ({}), source {}", up.current_version(), if up.is_installed() { "installed" } else { "not installed" }, up.source);
    let (tx, rx) = std::sync::mpsc::channel();
    up.check_and_download(&tx);
    drop(tx);
    let mut last_pct = -1;
    for e in rx {
        if let crate::update::UpdateEvent::Progress(p) = e {
            if p / 10 == last_pct / 10 {
                continue;
            }
            last_pct = p;
        }
        println!("{}", e.message_ko());
    }
    if apply && up.ready_version().is_some() {
        println!("적용하고 다시 시작합니다");
        if let Err(e) = up.apply_and_restart() {
            println!("적용 실패: {e}");
            return 1;
        }
    }
    0
}
