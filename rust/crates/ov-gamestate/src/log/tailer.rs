//! "tail -f" for godot.log by polling (`LogTailer`). The file is re-opened on every poll: the game renames the old
//! log at start and creates a new one, which a kept handle would never see. A shorter file than our position means
//! a new log (rotation); creation time is not reliable on NTFS.

use std::io::{Read, Seek, SeekFrom};
use std::path::PathBuf;

const MAX_CHUNK_BYTES: u64 = 8 * 1024 * 1024;

pub struct LogTailer {
    path: PathBuf,
    start_at_end: bool,
    partial: Vec<u8>,
    position: u64,
    first_poll: bool,
}

/// What one poll found.
#[derive(Debug, Default, PartialEq)]
pub struct Poll {
    /// The file was replaced (game relaunch); comes before the new file's lines.
    pub rotated: bool,
    pub lines: Vec<String>,
}

impl LogTailer {
    /// `start_at_end`: skip what is there; false replays the whole log (rebuilds a run in progress).
    pub fn new(path: impl Into<PathBuf>, start_at_end: bool) -> Self {
        Self { path: path.into(), start_at_end, partial: Vec::new(), position: 0, first_poll: true }
    }

    /// Byte offset of the next unread byte.
    pub fn position(&self) -> u64 {
        self.position
    }

    /// Reads what was appended since the last call (at most 8 MiB per call) and returns the complete lines.
    pub fn poll(&mut self) -> std::io::Result<Poll> {
        let mut out = Poll::default();
        if !self.path.exists() {
            return Ok(out);
        }
        let mut f = crate::io::open_shared(&self.path)?;
        // The stream's length, not the directory entry (stale while the game holds the file).
        let length = f.seek(SeekFrom::End(0))?;
        if self.first_poll {
            self.first_poll = false;
            if self.start_at_end {
                self.position = length;
                return Ok(out);
            }
        }
        if length < self.position {
            self.position = 0;
            self.partial.clear();
            out.rotated = true;
        }
        if length == self.position {
            return Ok(out);
        }
        f.seek(SeekFrom::Start(self.position))?;
        let want = (length - self.position).min(MAX_CHUNK_BYTES) as usize;
        let mut buf = vec![0u8; want];
        let mut read = 0;
        while read < want {
            let n = f.read(&mut buf[read..])?;
            if n == 0 {
                break;
            }
            read += n;
        }
        self.position += read as u64;
        out.lines = self.split_lines(&buf[..read]);
        Ok(out)
    }

    /// Splits on b'\n' and decodes each complete line (a chunk can end inside a multi-byte character).
    fn split_lines(&mut self, mut chunk: &[u8]) -> Vec<String> {
        let mut lines = Vec::new();
        while let Some(nl) = chunk.iter().position(|&b| b == b'\n') {
            if self.partial.is_empty() {
                lines.push(decode(&chunk[..nl]));
            } else {
                self.partial.extend_from_slice(&chunk[..nl]);
                lines.push(decode(&self.partial));
                self.partial.clear();
            }
            chunk = &chunk[nl + 1..];
        }
        self.partial.extend_from_slice(chunk);
        lines
    }
}

fn decode(b: &[u8]) -> String {
    String::from_utf8_lossy(b).trim_end_matches('\r').to_string()
}
