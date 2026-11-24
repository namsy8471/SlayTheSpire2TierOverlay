//! Reading files the game may be writing at the same moment (`SharedFile`).
//!
//! On Windows the file is opened with FILE_SHARE_READ | WRITE | DELETE, so the game's `.tmp -> current_run.save`
//! rename never fails because we hold the file.

use std::fs::{File, OpenOptions};
use std::io::{ErrorKind, Read};
use std::path::Path;
use std::time::Duration;

pub fn open_shared(path: &Path) -> std::io::Result<File> {
    let mut o = OpenOptions::new();
    o.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
        o.share_mode(0x1 | 0x2 | 0x4);
    }
    o.open(path)
}

/// Whole file as bytes, retrying a sharing violation with growing back-off (50 ms x attempt).
/// None when the file or its folder does not exist (or still fails after the last attempt).
pub fn read_all_bytes(path: &Path, attempts: u32, delay_ms: u64) -> Option<Vec<u8>> {
    let mut i = 1;
    loop {
        let r = open_shared(path).and_then(|mut f| {
            let mut buf = Vec::with_capacity(f.metadata().map(|m| m.len() as usize).unwrap_or(0));
            f.read_to_end(&mut buf)?;
            Ok(buf)
        });
        match r {
            Ok(b) => return Some(b),
            Err(e) if e.kind() == ErrorKind::NotFound => return None,
            Err(_) if i < attempts => {
                std::thread::sleep(Duration::from_millis(delay_ms * i as u64));
                i += 1;
            }
            Err(_) => return None,
        }
    }
}

/// UTF-8 text (BOM skipped); None when missing.
pub fn read_all_text(path: &Path) -> Option<String> {
    read_all_bytes(path, 6, 50).map(|b| ov_core::json::decode(&b))
}
