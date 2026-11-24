//! Port of PckReader.cs: the directory of a Godot 4 `.pck` (pack format v3; layout from Godot 4.5
//! core/io/file_access_pack.cpp). File data is stored raw, so reading a file is a seek and a copy.

use std::collections::HashMap;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::Path;
use std::sync::Mutex;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PckEntry {
    /// Resource path without "res://", e.g. "localization/kor/cards.json".
    pub path: String,
    pub offset: u64,
    pub size: u64,
    pub encrypted: bool,
}

pub struct PckReader {
    file: Mutex<File>,
    /// Ordinal order by path.
    pub entries: Vec<PckEntry>,
    pub godot_version: String,
    by_path: HashMap<String, usize>,
}

const MAGIC: u32 = 0x4350_4447; // "GDPC"
const PACK_DIR_ENCRYPTED: u32 = 1;
const FILE_ENCRYPTED: u32 = 1;
const FILE_REMOVAL: u32 = 2;

fn err(msg: impl Into<String>) -> std::io::Error {
    std::io::Error::new(std::io::ErrorKind::InvalidData, msg.into())
}

impl PckReader {
    pub fn open(path: &Path) -> std::io::Result<PckReader> {
        let mut f = File::open(path)?;
        let mut head = [0u8; 40];
        f.read_exact(&mut head)?;
        let u32_at = |o: usize| u32::from_le_bytes(head[o..o + 4].try_into().unwrap());
        let u64_at = |o: usize| u64::from_le_bytes(head[o..o + 8].try_into().unwrap());
        if u32_at(0) != MAGIC {
            return Err(err("Not a Godot pack: missing 'GDPC' magic at offset 0."));
        }
        let format = u32_at(4);
        let godot_version = format!("{}.{}.{} (pack format v{format})", u32_at(8), u32_at(12), u32_at(16));
        if format != 3 {
            return Err(err(format!("Only pack format v3 (Godot 4.4+) is supported, got v{format}.")));
        }
        if u32_at(20) & PACK_DIR_ENCRYPTED != 0 {
            return Err(err("The pack directory is encrypted; cannot read it without the key."));
        }
        let file_base = u64_at(24);
        let dir_offset = u64_at(32);
        f.seek(SeekFrom::Start(dir_offset))?;
        let mut r = std::io::BufReader::new(&mut f);
        let mut b4 = [0u8; 4];
        let mut b8 = [0u8; 8];
        r.read_exact(&mut b4)?;
        let count = u32::from_le_bytes(b4);
        let mut by_path: HashMap<String, PckEntry> = HashMap::new();
        for _ in 0..count {
            r.read_exact(&mut b4)?;
            let len = u32::from_le_bytes(b4) as usize;
            let mut name = vec![0u8; len];
            r.read_exact(&mut name)?;
            let mut path = String::from_utf8_lossy(&name).trim_end_matches('\0').to_string();
            if let Some(p) = path.strip_prefix("res://") {
                path = p.to_string();
            }
            r.read_exact(&mut b8)?;
            let offset = u64::from_le_bytes(b8);
            r.read_exact(&mut b8)?;
            let size = u64::from_le_bytes(b8);
            let mut md5 = [0u8; 16];
            r.read_exact(&mut md5)?;
            r.read_exact(&mut b4)?;
            let flags = u32::from_le_bytes(b4);
            if flags & FILE_REMOVAL != 0 {
                by_path.remove(&path);
                continue;
            }
            by_path.insert(path.clone(), PckEntry { path, offset: file_base + offset, size, encrypted: flags & FILE_ENCRYPTED != 0 });
        }
        let mut entries: Vec<PckEntry> = by_path.into_values().collect();
        entries.sort_by(|a, b| a.path.cmp(&b.path));
        let index = entries.iter().enumerate().map(|(i, e)| (e.path.clone(), i)).collect();
        Ok(PckReader { file: Mutex::new(f), entries, godot_version, by_path: index })
    }

    pub fn find(&self, path: &str) -> Option<&PckEntry> {
        self.by_path.get(path).map(|&i| &self.entries[i])
    }

    pub fn read_all_bytes(&self, entry: &PckEntry) -> std::io::Result<Vec<u8>> {
        if entry.encrypted {
            return Err(err(format!("'{}' is encrypted.", entry.path)));
        }
        let mut f = self.file.lock().unwrap_or_else(|e| e.into_inner());
        f.seek(SeekFrom::Start(entry.offset))?;
        let mut buf = vec![0u8; entry.size as usize];
        f.read_exact(&mut buf)?;
        Ok(buf)
    }

    pub fn extract_to(&self, entry: &PckEntry, output: &Path) -> std::io::Result<()> {
        if entry.encrypted {
            return Err(err(format!("'{}' is encrypted.", entry.path)));
        }
        if let Some(dir) = output.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let mut f = self.file.lock().unwrap_or_else(|e| e.into_inner());
        f.seek(SeekFrom::Start(entry.offset))?;
        let mut out = File::create(output)?;
        let mut buf = vec![0u8; 81_920];
        let mut remaining = entry.size;
        while remaining > 0 {
            let n = remaining.min(buf.len() as u64) as usize;
            f.read_exact(&mut buf[..n])?;
            out.write_all(&buf[..n])?;
            remaining -= n as u64;
        }
        Ok(())
    }
}
