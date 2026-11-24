//! `progress.save`: which characters are unlocked (`ProgressSave`).

use std::path::Path;

use serde_json::Value;

pub const FILE_NAME: &str = "progress.save";
const CHARACTER_EPOCHS: [&str; 4] = ["SILENT1_EPOCH", "REGENT1_EPOCH", "NECROBINDER1_EPOCH", "DEFECT1_EPOCH"];

pub fn all_characters_unlocked(revealed: &[String], include_fool: bool) -> bool {
    let has = |e: &str| revealed.iter().any(|x| x == e);
    CHARACTER_EPOCHS.iter().all(|e| has(e)) && (!include_fool || has("FOOL1_EPOCH"))
}

pub fn all_characters_unlocked_json(json: &str, include_fool: bool) -> Option<bool> {
    let root: Value = serde_json::from_str(json).ok()?;
    let epochs = root.get("epochs")?.as_array()?;
    let revealed: Vec<String> = epochs
        .iter()
        .filter(|e| e.get("state").and_then(Value::as_str) == Some("revealed"))
        .filter_map(|e| e.get("id").and_then(Value::as_str).map(str::to_string))
        .collect();
    Some(all_characters_unlocked(&revealed, include_fool))
}

pub fn all_characters_unlocked_in(saves_dir: &Path, include_fool: bool) -> Option<bool> {
    crate::io::read_all_text(&saves_dir.join(FILE_NAME)).and_then(|j| all_characters_unlocked_json(&j, include_fool))
}
