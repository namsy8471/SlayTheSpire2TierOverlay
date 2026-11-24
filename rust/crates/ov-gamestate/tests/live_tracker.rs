//! The live driver finds a saves folder that appears later, reads a save once it is written, and follows the log.

use std::io::Read;
use std::time::Duration;

use ov_gamestate::paths::GamePaths;
use ov_gamestate::tracking::live::{LiveOptions, LiveTracker};
use ov_gamestate::tracking::tracker::TrackerOptions;

#[test]
fn finds_late_saves_folder_and_reads_save_and_log() {
    let root = std::env::temp_dir().join(format!("ov-live-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(root.join("logs")).unwrap();
    let log = root.join("logs").join("godot.log");
    std::fs::write(&log, "").unwrap();
    let paths = GamePaths { saves_directory: None, log_file: Some(log.clone()), steam_id: None, root: Some(root.clone()) };
    let mut live = LiveTracker::new(LiveOptions {
        tracker: TrackerOptions { paths: Some(paths), ..Default::default() },
        save_debounce: Duration::ZERO,
        saves_retry: Duration::ZERO,
        ..Default::default()
    });
    assert!(!live.tick());
    assert!(live.tracker.current().is_none());

    let saves = root.join("steam").join("76561190000000001").join("profile1").join("saves");
    std::fs::create_dir_all(&saves).unwrap();
    let mut text = String::new();
    let fixture = "../../../tests/Sts2Overlay.GameState.Tests/Fixtures/event_saves/room_entry/BYF4VFC70G_act0_room05.save.gz";
    flate2::read::GzDecoder::new(std::fs::File::open(fixture).unwrap()).read_to_string(&mut text).unwrap();
    std::fs::write(saves.join("current_run.save"), &text).unwrap();
    std::fs::write(&log, "Preloading 'Event Room' assets...\n").unwrap();

    assert!(live.tick(), "save and log line should change the snapshot");
    assert_eq!(live.saves_directory(), Some(saves.as_path()));
    let run = live.tracker.current().expect("run from the save");
    assert!(run.from_save);
    assert!(run.current_event.is_some(), "room-entry save predicts the event");
    std::fs::remove_file(saves.join("current_run.save")).unwrap();
    assert!(live.tick(), "removed save clears the run");
    assert!(live.tracker.current().is_none());
    let _ = std::fs::remove_dir_all(&root);
}
