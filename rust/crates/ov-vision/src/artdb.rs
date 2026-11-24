//! Port of CardArtDatabase.cs: NCC thumbnails of every card portrait, built once and cached.

use std::collections::{HashMap, HashSet};
use std::io::{Read, Write};
use std::path::Path;

use rayon::prelude::*;

use crate::catalog::{self, ArtFile, VisionCatalog, MISSING_PORTRAIT};
use crate::geometry::{PixelRect, RectF, ScreenFrame};
use crate::image_io;
use crate::integral::IntegralImage;
use crate::ncc::{self, COARSE_H, COARSE_LENGTH, COARSE_W, FULL_H, FULL_LENGTH, FULL_W};
use crate::results::ArtMatch;

#[derive(Clone, Debug, PartialEq)]
pub struct ArtEntry {
    pub index: usize,
    pub card_id: String,
    pub pool: String,
    pub character: Option<String>,
    pub art_path: String,
    pub is_beta: bool,
}

impl ArtEntry {
    pub fn is_missing_portrait(&self) -> bool {
        self.art_path == MISSING_PORTRAIT
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct ArtTwin {
    pub card_id_a: String,
    pub card_id_b: String,
    pub score: f32,
}

const MAGIC: &[u8; 10] = b"STS2ART-RS";
/// Bump when the vector definition changes: old caches get rebuilt.
pub const FORMAT_VERSION: u32 = 1;

pub struct CardArtDatabase {
    full: Vec<f32>,
    coarse: Vec<f32>,
    pub entries: Vec<ArtEntry>,
    pub twins: Vec<ArtTwin>,
    pub source_fingerprint: String,
    by_card: HashMap<String, Vec<usize>>,
    twins_of: HashMap<String, HashSet<String>>,
}

static NO_TWINS: std::sync::OnceLock<HashSet<String>> = std::sync::OnceLock::new();

impl CardArtDatabase {
    pub fn new(entries: Vec<ArtEntry>, full: Vec<f32>, coarse: Vec<f32>, twins: Vec<ArtTwin>, fingerprint: String) -> Self {
        let mut by_card: HashMap<String, Vec<usize>> = HashMap::new();
        for e in &entries {
            by_card.entry(e.card_id.clone()).or_default().push(e.index);
        }
        let mut twins_of: HashMap<String, HashSet<String>> = HashMap::new();
        for t in &twins {
            twins_of.entry(t.card_id_a.clone()).or_default().insert(t.card_id_b.clone());
            twins_of.entry(t.card_id_b.clone()).or_default().insert(t.card_id_a.clone());
        }
        Self { full, coarse, entries, twins, source_fingerprint: fingerprint, by_card, twins_of }
    }

    pub fn count(&self) -> usize {
        self.entries.len()
    }

    pub fn full_vector(&self, entry: usize) -> &[f32] {
        &self.full[entry * FULL_LENGTH..(entry + 1) * FULL_LENGTH]
    }

    pub fn coarse_vector(&self, entry: usize) -> &[f32] {
        &self.coarse[entry * COARSE_LENGTH..(entry + 1) * COARSE_LENGTH]
    }

    pub fn twins_of(&self, card_id: &str) -> &HashSet<String> {
        self.twins_of.get(card_id).unwrap_or_else(|| NO_TWINS.get_or_init(HashSet::new))
    }

    pub fn entries_of(&self, card_id: &str) -> &[usize] {
        self.by_card.get(card_id).map(Vec::as_slice).unwrap_or(&[])
    }

    /// Best `k` entries by NCC (one per card id); the query length picks the full or coarse vectors.
    pub fn top_k(&self, query: &[f32], k: usize, allowed: Option<&dyn Fn(usize) -> bool>) -> Vec<ArtMatch> {
        let full = query.len() == FULL_LENGTH;
        assert!(full || query.len() == COARSE_LENGTH, "Query length must be full or coarse.");
        let data = if full { &self.full } else { &self.coarse };
        let len = query.len();
        let mut best: Vec<ArtMatch> = Vec::with_capacity(k + 1);
        for (i, e) in self.entries.iter().enumerate() {
            if let Some(a) = allowed {
                if !a(i) {
                    continue;
                }
            }
            let s = ncc::dot(query, &data[i * len..(i + 1) * len]);
            insert(&mut best, i, &e.card_id, s, k);
        }
        best
    }

    pub fn best_of(&self, query: &[f32], entries: &[usize]) -> (f32, Option<usize>) {
        let full = query.len() == FULL_LENGTH;
        let data = if full { &self.full } else { &self.coarse };
        let len = query.len();
        let mut best = f32::NEG_INFINITY;
        let mut best_entry = None;
        for &i in entries {
            let s = ncc::dot(query, &data[i * len..(i + 1) * len]);
            if s > best {
                best = s;
                best_entry = Some(i);
            }
        }
        (best, best_entry)
    }

    pub fn build(art_root: &Path, catalog: Option<&VisionCatalog>, twin_threshold: f32, log: &mut dyn FnMut(String)) -> Result<Self, String> {
        let started = std::time::Instant::now();
        let files = catalog::enumerate_art(art_root, catalog)?;
        let entries: Vec<ArtEntry> = files
            .iter()
            .enumerate()
            .map(|(i, f)| ArtEntry {
                index: i,
                card_id: f.card_id.clone(),
                pool: f.pool.clone(),
                character: f.character.clone(),
                art_path: f.relative_path.clone(),
                is_beta: f.is_beta,
            })
            .collect();
        let vectors: Vec<Result<(Vec<f32>, Vec<f32>), String>> = entries
            .par_iter()
            .map(|e| {
                let art = image_io::load(&art_root.join(&e.art_path))?;
                let mut full = vec![0f32; FULL_LENGTH];
                let mut coarse = vec![0f32; COARSE_LENGTH];
                compute_art_vectors(&art.view(), &mut full, &mut coarse);
                Ok((full, coarse))
            })
            .collect();
        let mut full = Vec::with_capacity(entries.len() * FULL_LENGTH);
        let mut coarse = Vec::with_capacity(entries.len() * COARSE_LENGTH);
        for v in vectors {
            let (f, c) = v?;
            full.extend_from_slice(&f);
            coarse.extend_from_slice(&c);
        }
        log(format!("art vectors: {} entries in {} ms", entries.len(), started.elapsed().as_millis()));
        let mut twins = Vec::new();
        for a in 0..entries.len() {
            for b in a + 1..entries.len() {
                if entries[a].card_id == entries[b].card_id {
                    continue;
                }
                let s = ncc::dot(&full[a * FULL_LENGTH..(a + 1) * FULL_LENGTH], &full[b * FULL_LENGTH..(b + 1) * FULL_LENGTH]);
                if s >= twin_threshold {
                    twins.push(ArtTwin { card_id_a: entries[a].card_id.clone(), card_id_b: entries[b].card_id.clone(), score: s });
                }
            }
        }
        log(format!("identical-art pairs (NCC >= {twin_threshold}): {}", twins.len()));
        let fp = fingerprint(art_root, &files, catalog);
        Ok(Self::new(entries, full, coarse, twins, fp))
    }

    pub fn save(&self, path: &Path) -> Result<(), String> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
        }
        let tmp = path.with_extension("tmp");
        let mut w = Vec::new();
        w.extend_from_slice(MAGIC);
        w.extend_from_slice(&FORMAT_VERSION.to_le_bytes());
        put_str(&mut w, &self.source_fingerprint);
        w.extend_from_slice(&(self.entries.len() as u32).to_le_bytes());
        for e in &self.entries {
            put_str(&mut w, &e.card_id);
            put_str(&mut w, &e.pool);
            put_str(&mut w, e.character.as_deref().unwrap_or(""));
            put_str(&mut w, &e.art_path);
            w.push(e.is_beta as u8);
        }
        w.extend_from_slice(&(self.twins.len() as u32).to_le_bytes());
        for t in &self.twins {
            put_str(&mut w, &t.card_id_a);
            put_str(&mut w, &t.card_id_b);
            w.extend_from_slice(&t.score.to_le_bytes());
        }
        for v in self.full.iter().chain(self.coarse.iter()) {
            w.extend_from_slice(&v.to_le_bytes());
        }
        let mut f = std::fs::File::create(&tmp).map_err(|e| e.to_string())?;
        f.write_all(&w).map_err(|e| e.to_string())?;
        drop(f);
        std::fs::rename(&tmp, path).map_err(|e| e.to_string())
    }

    /// Loads a cache file; None if it is missing, corrupt or of another format version.
    pub fn try_load(path: &Path) -> Option<Self> {
        let mut bytes = Vec::new();
        std::fs::File::open(path).ok()?.read_to_end(&mut bytes).ok()?;
        let mut r = Reader { b: &bytes, p: 0 };
        if r.take(MAGIC.len())? != MAGIC || r.u32()? != FORMAT_VERSION {
            return None;
        }
        let fingerprint = r.str()?;
        let n = r.u32()? as usize;
        let mut entries = Vec::with_capacity(n);
        for i in 0..n {
            let card_id = r.str()?;
            let pool = r.str()?;
            let ch = r.str()?;
            let art_path = r.str()?;
            let is_beta = r.take(1)?[0] != 0;
            entries.push(ArtEntry { index: i, card_id, pool, character: if ch.is_empty() { None } else { Some(ch) }, art_path, is_beta });
        }
        let tn = r.u32()? as usize;
        let mut twins = Vec::with_capacity(tn);
        for _ in 0..tn {
            let a = r.str()?;
            let b = r.str()?;
            let s = f32::from_le_bytes(r.take(4)?.try_into().ok()?);
            twins.push(ArtTwin { card_id_a: a, card_id_b: b, score: s });
        }
        let full = r.floats(n * FULL_LENGTH)?;
        let coarse = r.floats(n * COARSE_LENGTH)?;
        Some(Self::new(entries, full, coarse, twins, fingerprint))
    }

    /// App start-up: load the cache if it matches the current art files, else build and save it.
    pub fn load_or_build(art_root: &Path, catalog_path: Option<&Path>, cache_path: &Path, log: &mut dyn FnMut(String)) -> Result<Self, String> {
        let catalog = match catalog_path {
            Some(p) if p.is_file() => Some(VisionCatalog::load(p)?),
            _ => None,
        };
        if let Some(cached) = Self::try_load(cache_path) {
            let current = fingerprint(art_root, &catalog::enumerate_art(art_root, catalog.as_ref())?, catalog.as_ref());
            if current == cached.source_fingerprint {
                return Ok(cached);
            }
            log("art cache is stale, rebuilding".into());
        }
        let db = Self::build(art_root, catalog.as_ref(), 0.97, log)?;
        db.save(cache_path)?;
        Ok(db)
    }
}

fn insert(best: &mut Vec<ArtMatch>, i: usize, card_id: &str, score: f32, k: usize) {
    if let Some(same) = best.iter().position(|b| b.card_id == card_id) {
        if best[same].score >= score {
            return;
        }
        best.remove(same);
    }
    let mut pos = best.len();
    while pos > 0 && best[pos - 1].score < score {
        pos -= 1;
    }
    if pos >= k {
        return;
    }
    best.insert(pos, ArtMatch { entry_index: i, card_id: card_id.to_string(), score });
    if best.len() > k {
        best.pop();
    }
}

/// Vectors of a full portrait image (the reference side of the comparison).
pub fn compute_art_vectors(art: &ScreenFrame<'_>, full: &mut [f32], coarse: &mut [f32]) {
    let img = IntegralImage::build(art, PixelRect::new(0, 0, art.width, art.height), 2);
    let b = RectF::new(0.0, 0.0, (img.width * img.step) as f32, (img.height * img.step) as f32);
    ncc::extract(&img, b, FULL_W, FULL_H, full);
    ncc::extract(&img, b, COARSE_W, COARSE_H, coarse);
}

pub fn fingerprint(art_root: &Path, files: &[ArtFile], catalog: Option<&VisionCatalog>) -> String {
    let (mut bytes, mut newest) = (0u64, 0u128);
    for f in files {
        if let Ok(m) = std::fs::metadata(art_root.join(&f.relative_path)) {
            bytes += m.len();
            let t = m.modified().ok().and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok()).map_or(0, |d| d.as_nanos());
            newest = newest.max(t);
        }
    }
    format!(
        "v{FORMAT_VERSION};{FULL_W}x{FULL_H};{COARSE_W}x{COARSE_H};files={};bytes={bytes};newest={newest};catalog={}",
        files.len(),
        catalog.map_or(0, |c| c.cards.len())
    )
}

fn put_str(w: &mut Vec<u8>, s: &str) {
    w.extend_from_slice(&(s.len() as u32).to_le_bytes());
    w.extend_from_slice(s.as_bytes());
}

struct Reader<'a> {
    b: &'a [u8],
    p: usize,
}

impl Reader<'_> {
    fn take(&mut self, n: usize) -> Option<&[u8]> {
        let s = self.b.get(self.p..self.p.checked_add(n)?)?;
        self.p += n;
        Some(s)
    }
    fn u32(&mut self) -> Option<u32> {
        Some(u32::from_le_bytes(self.take(4)?.try_into().ok()?))
    }
    fn str(&mut self) -> Option<String> {
        let n = self.u32()? as usize;
        String::from_utf8(self.take(n)?.to_vec()).ok()
    }
    fn floats(&mut self, n: usize) -> Option<Vec<f32>> {
        let raw = self.take(n.checked_mul(4)?)?;
        Some(raw.chunks_exact(4).map(|c| f32::from_le_bytes([c[0], c[1], c[2], c[3]])).collect())
    }
}
