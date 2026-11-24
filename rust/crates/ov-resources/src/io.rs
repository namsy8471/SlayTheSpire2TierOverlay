//! File helpers.

use std::io::Read;
use std::path::Path;

/// Reads a whole file. Rust opens files with read, write and delete sharing on Windows, so this works while the game
/// holds the file open (C#: FileShare.ReadWrite | FileShare.Delete).
pub fn read_shared(path: &Path) -> std::io::Result<Vec<u8>> {
    let mut f = std::fs::File::open(path)?;
    let mut bytes = Vec::new();
    f.read_to_end(&mut bytes)?;
    Ok(bytes)
}
