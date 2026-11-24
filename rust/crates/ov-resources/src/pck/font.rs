//! A font file out of the pack: the `.import` of a font points at its imported `.fontdata` (a Godot FontFile
//! resource, usually zstd-compressed as "RSCC"); the original TrueType/OpenType bytes sit inside it as the resource's
//! `data` byte array, so after decompressing we look for the sfnt table directory and cut the font out.

use super::reader::PckReader;

const RSCC: &[u8; 4] = b"RSCC";
const RSRC: &[u8; 4] = b"RSRC";
/// Godot `Compression::Mode` values used by FileAccessCompressed.
const MODE_DEFLATE: u32 = 1;
const MODE_ZSTD: u32 = 2;
const MODE_GZIP: u32 = 3;

/// The `path="res://....fontdata"` line of a font's `.import` file.
pub fn find_fontdata_path(import_text: &str) -> Option<String> {
    import_text.lines().find_map(|line| {
        let r = line.trim().strip_prefix("path=\"res://")?;
        let p = &r[..r.find('"')?];
        p.ends_with(".fontdata").then(|| p.to_string())
    })
}

/// Undo Godot's FileAccessCompressed ("RSCC"): header (mode, block size, total size), one u32 compressed size per
/// block, then the blocks. A plain "RSRC" resource is returned as is.
pub fn decompress_resource(bytes: &[u8]) -> Result<Vec<u8>, String> {
    if bytes.starts_with(RSRC) {
        return Ok(bytes.to_vec());
    }
    if !bytes.starts_with(RSCC) || bytes.len() < 16 {
        return Err("not a Godot resource (no RSCC/RSRC magic)".into());
    }
    let u32_at = |o: usize| bytes.get(o..o + 4).map(|b| u32::from_le_bytes(b.try_into().unwrap()));
    let (mode, block, total) = (u32_at(4).unwrap(), u32_at(8).unwrap() as usize, u32_at(12).unwrap() as usize);
    if block == 0 || total > 512 << 20 {
        return Err(format!("implausible compressed header (block {block}, total {total})"));
    }
    let blocks = total.div_ceil(block);
    let mut at = 16 + 4 * blocks;
    let mut out = Vec::with_capacity(total);
    for i in 0..blocks {
        let size = u32_at(16 + 4 * i).ok_or("truncated block table")? as usize;
        let src = bytes.get(at..at + size).ok_or("truncated block")?;
        at += size;
        let want = block.min(total - i * block);
        let start = out.len();
        match mode {
            MODE_ZSTD => {
                use std::io::Read;
                let mut d = ruzstd::decoding::StreamingDecoder::new(src).map_err(|e| format!("zstd: {e}"))?;
                d.read_to_end(&mut out).map_err(|e| format!("zstd: {e}"))?;
            }
            MODE_DEFLATE | MODE_GZIP => {
                use std::io::Read;
                let r: Box<dyn Read> = if mode == MODE_GZIP { Box::new(flate2::read::GzDecoder::new(src)) } else { Box::new(flate2::read::ZlibDecoder::new(src)) };
                let mut r = r;
                r.read_to_end(&mut out).map_err(|e| format!("deflate: {e}"))?;
            }
            m => return Err(format!("unsupported compression mode {m}")),
        }
        if out.len() - start != want {
            return Err(format!("block {i}: got {} bytes, expected {want}", out.len() - start));
        }
    }
    Ok(out)
}

fn be32(b: &[u8], o: usize) -> Option<u32> {
    b.get(o..o + 4).map(|x| u32::from_be_bytes(x.try_into().unwrap()))
}

/// The length of a font whose sfnt header starts at `b[0]`, from its table directory, or None when it does not look
/// like one (bad version, odd table count, non-text tags, tables past the end, no cmap).
fn sfnt_len(b: &[u8]) -> Option<usize> {
    let version = be32(b, 0)?;
    if !(version == 0x0001_0000 || version == u32::from_be_bytes(*b"OTTO") || version == u32::from_be_bytes(*b"true")) {
        return None;
    }
    let n = u16::from_be_bytes(b.get(4..6)?.try_into().unwrap()) as usize;
    if !(4..=64).contains(&n) {
        return None;
    }
    let mut end = 12 + 16 * n;
    let mut cmap = false;
    for i in 0..n {
        let rec = 12 + 16 * i;
        let tag = b.get(rec..rec + 4)?;
        if !tag.iter().all(|c| (0x20..0x7f).contains(c)) {
            return None;
        }
        cmap |= tag == b"cmap";
        let (off, len) = (be32(b, rec + 8)? as usize, be32(b, rec + 12)? as usize);
        if off < 12 + 16 * n || off.checked_add(len)? > b.len() {
            return None;
        }
        end = end.max(off + len);
    }
    cmap.then_some(end)
}

/// The embedded font in a decompressed FontFile resource. The bytes follow their array length (a little-endian u32)
/// in the binary resource, which we check so a stray match inside other data is skipped.
pub fn find_sfnt(resource: &[u8]) -> Option<&[u8]> {
    let mut i = 4;
    while i + 12 <= resource.len() {
        if let Some(len) = sfnt_len(&resource[i..]) {
            let declared = u32::from_le_bytes(resource[i - 4..i].try_into().unwrap()) as usize;
            if declared >= len && i + declared <= resource.len() {
                return Some(&resource[i..i + declared]);
            }
        }
        i += 1;
    }
    None
}

/// The original font file for a pack font path such as "fonts/kor/GyeonggiCheonnyeonBatangBold.ttf".
pub fn extract_font(pck: &PckReader, font_path: &str) -> Result<Vec<u8>, String> {
    let import_path = format!("{font_path}.import");
    let import = pck.find(&import_path).ok_or_else(|| format!("{import_path} not in the pack"))?;
    let text = pck.read_all_bytes(import).map_err(|e| e.to_string())?;
    let target = find_fontdata_path(&String::from_utf8_lossy(&text)).ok_or("no .fontdata path in the import file")?;
    let entry = pck.find(&target).ok_or_else(|| format!("{target} not in the pack"))?;
    let raw = pck.read_all_bytes(entry).map_err(|e| e.to_string())?;
    let resource = decompress_resource(&raw)?;
    find_sfnt(&resource).map(<[u8]>::to_vec).ok_or_else(|| "no TrueType/OpenType data in the font resource".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tiny_sfnt() -> Vec<u8> {
        // Header with 4 tables, each 4 bytes long, laid out after the directory.
        let tags = [b"cmap", b"glyf", b"head", b"name"];
        let mut f = vec![0, 1, 0, 0, 0, 4, 0, 64, 0, 2, 0, 0];
        let base = 12 + 16 * tags.len();
        for (i, t) in tags.iter().enumerate() {
            f.extend_from_slice(*t);
            f.extend_from_slice(&[0; 4]);
            f.extend_from_slice(&((base + 4 * i) as u32).to_be_bytes());
            f.extend_from_slice(&4u32.to_be_bytes());
        }
        f.extend_from_slice(&[7; 16]);
        f
    }

    #[test]
    fn import_path_cut_out_of_resource_and_compressed_blocks() {
        let import = "[remap]\n\nimporter=\"font_data_dynamic\"\ntype=\"FontFile\"\npath=\"res://.godot/imported/A.ttf-0123.fontdata\"\n";
        assert_eq!(find_fontdata_path(import).as_deref(), Some(".godot/imported/A.ttf-0123.fontdata"));

        let font = tiny_sfnt();
        // A resource: noise that starts like a font but has no cmap, then the length-prefixed font.
        let mut res = b"RSRC\0\0\0\0".to_vec();
        res.extend_from_slice(&[0, 1, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0]);
        res.extend_from_slice(&(font.len() as u32).to_le_bytes());
        res.extend_from_slice(&font);
        res.extend_from_slice(b"tail");
        assert_eq!(find_sfnt(&res), Some(&font[..]));

        // RSCC with deflate (mode 1) blocks of 16 bytes.
        let block = 16usize;
        let mut rscc = b"RSCC".to_vec();
        for v in [MODE_DEFLATE, block as u32, res.len() as u32] {
            rscc.extend_from_slice(&v.to_le_bytes());
        }
        let chunks: Vec<Vec<u8>> = res
            .chunks(block)
            .map(|c| {
                use std::io::Write;
                let mut e = flate2::write::ZlibEncoder::new(Vec::new(), flate2::Compression::default());
                e.write_all(c).unwrap();
                e.finish().unwrap()
            })
            .collect();
        for c in &chunks {
            rscc.extend_from_slice(&(c.len() as u32).to_le_bytes());
        }
        for c in &chunks {
            rscc.extend_from_slice(c);
        }
        assert_eq!(decompress_resource(&rscc).unwrap(), res);
    }
}
