//! Slay the Spire 2 overlay (Rust). The window side of the app: panel, full-game layer, capture, hotkeys, tray,
//! settings and Velopack updates. Game state, recognition and advice come from the other `ov-*` crates and the
//! private advisor process.
#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]
// Off Windows only the settings / update logic is built (for tests); the window code that uses the rest is not.
#![cfg_attr(not(windows), allow(dead_code))]

mod applog;
mod paths;
mod settings;
mod spike;
mod update;

#[cfg(windows)]
mod fonts;
#[cfg(windows)]
mod tray;
#[cfg(windows)]
mod ui;
#[cfg(windows)]
mod win;

const USAGE: &str = "Sts2Overlay [--capture-once [out.png]] [--update-check [--update-url URL|DIR] [--apply]] [--version]";

fn main() {
    // Velopack's install / update / uninstall hooks must run before anything else (they may exit the process).
    velopack::VelopackApp::build().run();
    let args: Vec<String> = std::env::args().skip(1).collect();
    #[cfg(windows)]
    if !args.is_empty() {
        win::attach_parent_console();
    }
    std::process::exit(run(&args));
}

fn run(args: &[String]) -> i32 {
    let mut it = args.iter().map(String::as_str).peekable();
    match it.next() {
        None => run_overlay(),
        Some("--version") => {
            println!("{}", env!("CARGO_PKG_VERSION"));
            0
        }
        #[cfg(windows)]
        Some("--capture-once") => {
            win::set_dpi_awareness();
            spike::capture_cli(it.next().map(Into::into))
        }
        Some("--update-check") => {
            let (mut url, mut apply) = (None, false);
            while let Some(a) = it.next() {
                match a {
                    "--update-url" => url = it.next().map(str::to_string),
                    "--apply" => apply = true,
                    other => {
                        eprintln!("unknown option {other}\n{USAGE}");
                        return 64;
                    }
                }
            }
            spike::update_cli(url, apply)
        }
        Some(other) => {
            eprintln!("unknown option {other}\n{USAGE}");
            64
        }
    }
}

#[cfg(windows)]
fn run_overlay() -> i32 {
    win::set_dpi_awareness();
    let store = settings::Store::load(&paths::settings_file());
    let result = eframe::run_native(ui::PANEL_TITLE, ui::native_options(), Box::new(move |cc| Ok(Box::new(ui::OverlayApp::new(cc, store)))));
    match result {
        Ok(()) => 0,
        Err(e) => {
            applog::warn(format!("ui: could not start ({e})"));
            1
        }
    }
}

#[cfg(not(windows))]
fn run_overlay() -> i32 {
    eprintln!("The overlay window runs on Windows only.");
    1
}
