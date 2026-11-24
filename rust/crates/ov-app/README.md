# ov-app

The window side of the Rust overlay (`Sts2Overlay.exe`): the panel, the full-game layer, WGC capture, hotkeys, tray,
`settings.json` and Velopack updates. Windows only; the settings and update-source logic also build and test on Linux.

```sh
cargo test -p ov-app                                                   # Linux or Windows
cargo build -p ov-app --release                                        # Windows (MSVC)
cargo build -p ov-app --release --target x86_64-pc-windows-gnu         # cross build from Linux (mingw-w64)
```

## Compatibility with the C# app

| What | Kept the same |
|---|---|
| exe name | `Sts2Overlay.exe` (Velopack `--mainExe`, shortcuts) |
| Velopack | packId `namsy8471.Sts2Overlay`, channel `win`, library 1.2.161 (same as C#) |
| data folder | `%LOCALAPPDATA%\Sts2Overlay` (`settings.json`, `app.log`, `captures\`) |
| settings | camelCase keys, comments and trailing commas read; unknown keys written back |
| `updateUrl` | empty = GitHub releases, github.com URL, other http(s) URL, absolute folder |
| hotkeys | Ctrl+F5 map, F6 on top, F7 click-through, F8 hide, F9 capture (debug), F10, F11 outlines |

## M0 spike (on the PC)

1. Window: start `Sts2Overlay.exe` with the game open. Yellow corner marks must sit on the game's corners, the
   "러스트 오버레이 M0" badge must show, clicks must reach the game, and the panel must not take focus.
2. Capture: `Sts2Overlay.exe --capture-once` (or the panel button) writes `captures\rust-wgc-spike.png`; the overlay
   itself must not be in the picture.
3. Update: pack this exe with `vpk pack --packId namsy8471.Sts2Overlay --packVersion 0.2.0-m0 --mainExe Sts2Overlay.exe
   --channel win`, point an installed C# 0.1.35 at that folder (`updateUrl`), update from the tray, and check that the
   Rust build starts as the installed version (`Sts2Overlay.exe --update-check`).
