//! Port of VisionCatalog.cs and CardArtSource.cs: the card list the recogniser needs and where each portrait is.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

#[derive(Clone, Debug, PartialEq)]
pub struct CatalogCard {
    pub id: String,
    pub pool: String,
    pub character: Option<String>,
    pub rarity: Option<String>,
    pub card_type: Option<String>,
}

impl CatalogCard {
    pub fn pool_folder(&self) -> String {
        pool_folder(&self.pool)
    }
}

pub const COLORLESS_POOL: &str = "CARD_POOL.COLORLESS_CARD_POOL";
pub const OFFERABLE_POOLS: [&str; 7] = [
    "CARD_POOL.IRONCLAD_CARD_POOL",
    "CARD_POOL.SILENT_CARD_POOL",
    "CARD_POOL.DEFECT_CARD_POOL",
    "CARD_POOL.NECROBINDER_CARD_POOL",
    "CARD_POOL.REGENT_CARD_POOL",
    "CARD_POOL.FOOL_CARD_POOL",
    COLORLESS_POOL,
];

pub struct VisionCatalog {
    pub cards: Vec<CatalogCard>,
    by_id: HashMap<String, usize>,
}

impl VisionCatalog {
    pub fn new(cards: Vec<CatalogCard>) -> Self {
        let by_id = cards.iter().enumerate().map(|(i, c)| (c.id.clone(), i)).collect();
        Self { cards, by_id }
    }

    pub fn find(&self, id: &str) -> Option<&CatalogCard> {
        self.by_id.get(id).map(|&i| &self.cards[i])
    }

    pub fn load(path: &Path) -> Result<Self, String> {
        let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
        Self::parse(&text)
    }

    pub fn parse(text: &str) -> Result<Self, String> {
        let doc: serde_json::Value = serde_json::from_str(text).map_err(|e| e.to_string())?;
        let arr = doc.get("cards").and_then(|c| c.as_array()).ok_or("catalog has no cards")?;
        let s = |c: &serde_json::Value, k: &str| c.get(k).and_then(|v| v.as_str()).map(str::to_string);
        let mut cards = Vec::new();
        for c in arr {
            cards.push(CatalogCard {
                id: s(c, "id").ok_or("card without id")?,
                pool: s(c, "pool").unwrap_or_default(),
                character: s(c, "character"),
                rarity: s(c, "rarity"),
                card_type: s(c, "type"),
            });
        }
        Ok(Self::new(cards))
    }
}

pub fn pool_folder(pool: &str) -> String {
    let s = pool.strip_prefix("CARD_POOL.").unwrap_or(pool);
    let s = s.strip_suffix("_CARD_POOL").unwrap_or(s);
    s.to_lowercase()
}

pub fn pool_of_character(character_id: &str) -> String {
    let name = character_id.strip_prefix("CHARACTER.").unwrap_or(character_id);
    format!("CARD_POOL.{}_CARD_POOL", name.to_uppercase())
}

/// One portrait file that represents a card in the art database.
#[derive(Clone, Debug, PartialEq)]
pub struct ArtFile {
    pub card_id: String,
    pub pool: String,
    pub character: Option<String>,
    pub relative_path: String,
    pub is_beta: bool,
}

pub const MISSING_PORTRAIT: &str = "beta.webp";

pub fn enumerate_art(art_root: &Path, catalog: Option<&VisionCatalog>) -> Result<Vec<ArtFile>, String> {
    if !art_root.is_dir() {
        return Err(format!("Card art folder not found: {}", art_root.display()));
    }
    Ok(match catalog {
        Some(c) => enumerate_catalog(art_root, c),
        None => enumerate_all(art_root),
    })
}

fn enumerate_catalog(art_root: &Path, catalog: &VisionCatalog) -> Vec<ArtFile> {
    let mut result = Vec::new();
    for card in &catalog.cards {
        let folder = card.pool_folder();
        let name = card.id.strip_prefix("CARD.").unwrap_or(&card.id).to_lowercase();
        let main = art_root.join(&folder).join(format!("{name}.webp"));
        let beta = art_root.join(&folder).join("beta").join(format!("{name}.webp"));
        let file = |rel: String, is_beta: bool| ArtFile {
            card_id: card.id.clone(),
            pool: card.pool.clone(),
            character: card.character.clone(),
            relative_path: rel,
            is_beta,
        };
        if main.is_file() {
            result.push(file(rel(art_root, &main), false));
        } else if beta.is_file() {
            result.push(file(rel(art_root, &beta), true));
        } else {
            let before = result.len();
            let dir = art_root.join(&folder);
            if dir.is_dir() {
                let prefix = format!("{name}_");
                let mut variants: Vec<PathBuf> = std::fs::read_dir(&dir)
                    .into_iter()
                    .flatten()
                    .flatten()
                    .map(|e| e.path())
                    .filter(|p| {
                        p.is_file()
                            && p.file_name().and_then(|n| n.to_str()).is_some_and(|n| n.starts_with(&prefix) && n.ends_with(".webp"))
                    })
                    .collect();
                variants.sort_by(|a, b| a.to_string_lossy().as_bytes().cmp(b.to_string_lossy().as_bytes()));
                for v in variants {
                    result.push(file(rel(art_root, &v), false));
                }
            }
            if result.len() == before && art_root.join(MISSING_PORTRAIT).is_file() {
                result.push(file(MISSING_PORTRAIT.to_string(), true));
            }
        }
    }
    result
}

fn enumerate_all(art_root: &Path) -> Vec<ArtFile> {
    let mut files = Vec::new();
    walk(art_root, &mut files);
    files.sort_by(|a, b| a.to_string_lossy().as_bytes().cmp(b.to_string_lossy().as_bytes()));
    let mut by_id: HashMap<String, ArtFile> = HashMap::new();
    for f in files {
        let r = rel(art_root, &f);
        let parts: Vec<&str> = r.split('/').collect();
        if parts.len() < 2 {
            continue;
        }
        let is_beta = parts.contains(&"beta");
        let stem = f.file_stem().map(|s| s.to_string_lossy().to_uppercase()).unwrap_or_default();
        let id = format!("CARD.{stem}");
        let pool = format!("CARD_POOL.{}_CARD_POOL", parts[0].to_uppercase());
        let entry = ArtFile { card_id: id.clone(), pool, character: None, relative_path: r.clone(), is_beta };
        match by_id.get(&id) {
            Some(existing) if !(existing.is_beta && !is_beta) => {}
            _ => {
                by_id.insert(id, entry);
            }
        }
    }
    let mut v: Vec<ArtFile> = by_id.into_values().collect();
    v.sort_by(|a, b| a.card_id.as_bytes().cmp(b.card_id.as_bytes()));
    v
}

fn walk(dir: &Path, out: &mut Vec<PathBuf>) {
    for e in std::fs::read_dir(dir).into_iter().flatten().flatten() {
        let p = e.path();
        if p.is_dir() {
            walk(&p, out);
        } else if p.extension().is_some_and(|x| x == "webp") {
            out.push(p);
        }
    }
}

fn rel(root: &Path, file: &Path) -> String {
    file.strip_prefix(root).unwrap_or(file).to_string_lossy().replace('\\', "/")
}
