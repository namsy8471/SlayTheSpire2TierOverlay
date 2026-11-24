//! Parity with the C# recogniser on synthetic fixtures (random art, icons and frames; no game images).
//! The golden file was written by the C# code (a local dumper over `Sts2Overlay.Vision`) from the same fixtures.

use std::io::Read;
use std::path::{Path, PathBuf};

use ov_vision::artdb::CardArtDatabase;
use ov_vision::geometry::PixelRect;
use ov_vision::image_io;
use ov_vision::price::PriceReading;
use ov_vision::recognizer::{CardRecognizer, RecognizerOptions};
use ov_vision::relics::{self, RelicIconDatabase, RelicRecognizer};
use ov_vision::results::*;
use serde_json::{json, Value};

fn fixtures() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/synthetic")
}

fn f(v: f32) -> Value {
    if v.is_finite() { json!(v as f64) } else { json!(format!("{v}")) }
}

fn r(p: PixelRect) -> Value {
    json!([p.x, p.y, p.width, p.height])
}

fn reading(p: &Option<PriceReading>) -> Value {
    match p {
        None => Value::Null,
        Some(p) => json!({"value": p.value, "text": p.text, "tint": format!("{:?}", p.tint), "s": f(p.min_score), "m": f(p.min_margin),
            "plausible": p.plausible, "bounds": r(p.bounds), "font": p.font, "gap": f(p.font_gap)}),
    }
}

fn kind_name(k: CardSlotKind) -> &'static str {
    match k {
        CardSlotKind::Reward => "Reward",
        CardSlotKind::ShopCharacter => "ShopCharacter",
        CardSlotKind::ShopColorless => "ShopColorless",
        CardSlotKind::Choice => "Choice",
        CardSlotKind::Grid => "Grid",
    }
}

fn card(c: &RecognizedCard) -> Value {
    json!({"slot": c.slot, "kind": kind_name(c.slot_kind), "id": c.card_id, "up": c.upgraded, "score": f(c.score), "margin": f(c.margin),
        "accepted": c.accepted, "amb": c.ambiguous_with, "upEv": f(c.upgrade_evidence), "upKnown": c.upgrade_known, "scale": f(c.node_scale),
        "art": r(c.art_bounds), "card": r(c.card_bounds), "priceb": c.price_bounds.map(r), "price": c.price,
        "cands": c.candidates.iter().map(|m| json!([m.card_id, f(m.score)])).collect::<Vec<_>>(),
        "partial": c.partial.as_ref().map(|p| json!({"id": p.card_id, "s": f(p.score), "m": f(p.margin), "vis": p.visible_tiles,
            "total": p.total_tiles, "acc": p.accepted, "stage": p.stage})),
        "missing": c.missing_portrait, "reading": reading(&c.price_reading), "rug": f(c.rug_share), "empty": c.slot_empty})
}

fn relic(x: &RecognizedRelic) -> Value {
    json!({"slot": x.slot, "state": format!("{:?}", x.state), "id": x.relic_id, "rarity": x.rarity, "score": f(x.score), "margin": f(x.margin),
        "scale": f(x.node_scale), "icon": r(x.icon_bounds), "priceb": r(x.price_bounds), "price": x.price,
        "cands": x.candidates.iter().map(|m| json!([m.0, f(m.1)])).collect::<Vec<_>>(),
        "reading": reading(&x.price_reading), "tip": f(x.tooltip_cover), "hand": f(x.hand_cover), "overlay": f(x.overlay_cover),
        "rug": f(x.rug_share), "shape": f(x.shape_score), "shapeM": f(x.shape_margin), "agree": x.shape_agrees})
}

/// Rust output lines in the golden file's shape.
fn rust_lines() -> Vec<Value> {
    let data = fixtures();
    let db = CardArtDatabase::build(&data.join("art"), None, 0.97, &mut |_| {}).expect("db");
    let mut lines = vec![json!({
        "entries": db.entries.iter().map(|e| json!([e.card_id, e.pool, e.art_path, e.is_beta])).collect::<Vec<_>>(),
        "twins": db.twins.iter().map(|t| json!([t.card_id_a, t.card_id_b, f(t.score)])).collect::<Vec<_>>(),
        "full0": db.full_vector(0)[..64].iter().map(|&v| f(v)).collect::<Vec<_>>(),
        "fullsum": (0..db.count()).map(|i| f(db.full_vector(i).iter().map(|&x| x as f64 * x as f64).sum::<f64>() as f32)).collect::<Vec<_>>(),
        "coarse3": db.coarse_vector(3)[..48].iter().map(|&v| f(v)).collect::<Vec<_>>(),
    })];
    let catalog = relics::load_relic_catalog(&data.join("relics.json")).expect("catalog");
    let relic_db = RelicIconDatabase::build(&data.join("relics"), &catalog, &mut |_| {}).expect("relics");
    let potion_db = RelicIconDatabase::build_potions(&data.join("potions")).expect("potions");
    let mut rec = CardRecognizer::new(db, RecognizerOptions::default());
    rec.relics = Some(RelicRecognizer::new(relic_db, Default::default(), Default::default()));
    rec.potions = Some(RelicRecognizer::for_potions(potion_db, relics::SLOT_X0, 819.0));
    let mut frames: Vec<PathBuf> = std::fs::read_dir(data.join("frames")).unwrap().flatten().map(|e| e.path()).collect();
    frames.sort();
    for file in frames {
        let frame = image_io::load(&file).expect("frame");
        for hint in [Some("CHARACTER.IRONCLAD"), None] {
            let res = rec.recognize(&frame.view(), &RecognitionHints { character_id: hint.map(str::to_string) }).expect("recognize");
            lines.push(json!({
                "frame": file.file_name().unwrap().to_string_lossy(),
                "hint": hint,
                "kind": format!("{:?}", res.kind),
                "detail": res.detail,
                "gate": [res.gate.reward_likely, res.gate.shop_likely, res.gate.inspect_likely, res.gate.detail],
                "cards": res.cards.iter().map(card).collect::<Vec<_>>(),
                "slots": res.slots.iter().map(card).collect::<Vec<_>>(),
                "relics": res.relics.iter().map(relic).collect::<Vec<_>>(),
                "potions": res.potions.iter().map(relic).collect::<Vec<_>>(),
            }));
        }
    }
    lines
}

/// First difference between two JSON values; numbers may differ by `tol` (summation order of SIMD dot products).
fn diff(path: &str, a: &Value, b: &Value, tol: f64) -> Option<String> {
    match (a, b) {
        (Value::Number(x), Value::Number(y)) => {
            let (x, y) = (x.as_f64().unwrap(), y.as_f64().unwrap());
            ((x - y).abs() > tol).then(|| format!("{path}: C# {x} vs Rust {y}"))
        }
        (Value::Array(x), Value::Array(y)) => {
            if x.len() != y.len() {
                return Some(format!("{path}: length C# {} vs Rust {}", x.len(), y.len()));
            }
            x.iter().zip(y).enumerate().find_map(|(i, (p, q))| diff(&format!("{path}[{i}]"), p, q, tol))
        }
        (Value::Object(x), Value::Object(y)) => {
            for (k, v) in x {
                let w = y.get(k).unwrap_or(&Value::Null);
                if let Some(d) = diff(&format!("{path}.{k}"), v, w, tol) {
                    return Some(d);
                }
            }
            None
        }
        _ => (a != b).then(|| format!("{path}: C# {a} vs Rust {b}")),
    }
}

#[test]
fn recognition_matches_csharp() {
    let mut text = String::new();
    flate2::read::GzDecoder::new(std::fs::File::open(fixtures().join("csharp.jsonl.gz")).expect("golden"))
        .read_to_string(&mut text)
        .unwrap();
    let golden: Vec<Value> = text.lines().map(|l| serde_json::from_str(l).unwrap()).collect();
    let ours = rust_lines();
    if let Ok(dir) = std::env::var("OV_VISION_DUMP") {
        let s: String = ours.iter().map(|v| v.to_string() + "\n").collect();
        std::fs::write(Path::new(&dir).join("rust.jsonl"), s).unwrap();
    }
    assert_eq!(golden.len(), ours.len(), "line count");
    let mut failures = Vec::new();
    for (i, (g, o)) in golden.iter().zip(&ours).enumerate() {
        let name = g.get("frame").and_then(|v| v.as_str()).unwrap_or("database");
        let hint = g.get("hint").map(|v| v.to_string()).unwrap_or_default();
        if let Some(d) = diff("", g, o, 1e-4) {
            failures.push(format!("line {i} ({name} {hint}): {d}"));
        }
    }
    assert!(failures.is_empty(), "{} of {} lines differ:\n{}", failures.len(), golden.len(), failures.join("\n"));
}
