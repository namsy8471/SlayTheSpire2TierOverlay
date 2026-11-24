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
use ov_vision::events::{self, EventScreen, TextInk};
use ov_vision::geometry::RectF;
use ov_vision::map::{MapMark, MapScrollEstimator};
use ov_vision::{removal, rest};
use ov_vision::tracker::RecognitionTracker;
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

fn rf(r: RectF) -> Value {
    json!([f(r.x), f(r.y), f(r.width), f(r.height)])
}

fn ev(e: &Option<EventScreen>) -> Value {
    match e {
        None => Value::Null,
        Some(e) => Value::Array(e.buttons.iter().map(|b| json!({"i": b.index, "bounds": r(b.bounds), "state": format!("{:?}", b.state),
            "sig": b.text_signature.iter().map(|&v| f(v)).collect::<Vec<_>>(),
            "ink": b.ink.as_ref().map(|k| json!([k.width, k.height, f(k.pivot_x), f(k.pivot_y), k.count()]))})).collect()),
    }
}

fn frames() -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(fixtures().join("frames")).unwrap().flatten().map(|e| e.path()).collect();
    v.sort();
    v
}

/// The other recognisers on every frame, ink verdicts between event buttons and a tracker run (400 ms per frame).
fn rust_extras(rec: &CardRecognizer) -> Vec<Value> {
    let marks: Vec<Vec<f32>> = serde_json::from_str(&std::fs::read_to_string(fixtures().join("map_marks.json")).unwrap()).unwrap();
    let marks: Vec<MapMark> = marks.iter().map(|m| MapMark { x: m[0], y0: m[1] }).collect();
    let mut tracker = RecognitionTracker::default();
    let t0 = std::time::Instant::now();
    let mut out = Vec::new();
    for (tick, file) in frames().into_iter().enumerate() {
        let owned = image_io::load(&file).expect("frame");
        let frame = owned.view();
        let rs = rest::detect(&frame);
        let e = events::detect(&frame);
        let a = events::ancient::detect(&frame);
        let rm = removal::read(&frame);
        let mf = MapScrollEstimator::default().estimate(&frame, &marks, -800.0, 2000.0);
        let mut j = json!({
            "frame": file.file_name().unwrap().to_string_lossy(),
            "rest": {"choice": rs.choice_screen, "grid": rs.card_grid_open, "detail": rs.detail,
                "buttons": rs.buttons.iter().map(|b| json!([format!("{:?}", b.option), b.index, rf(b.rect), rf(b.slot), format!("{:?}", b.state),
                    b.enabled, f(b.residual), f(b.margin)])).collect::<Vec<_>>()},
            "event": ev(&e),
            "ancient": ev(&a),
            "removal": [rm.available, r(rm.bounds), f(rm.face_gold), f(rm.face_red), rm.price_icon],
            "map": [mf.is_map, f(mf.scroll), f(mf.score), mf.visible, mf.matched, f(mf.runner), f(mf.paper), mf.detail],
        });
        if let Some(e) = e.as_ref().filter(|e| e.buttons.len() >= 2) {
            let inks: Vec<&TextInk> = e.buttons.iter().map(|b| b.ink.as_ref().unwrap()).collect();
            let mut v = Vec::new();
            for x in &inks {
                for y in &inks {
                    let m = TextInk::match_inks(x, y);
                    v.push(json!([format!("{:?}", TextInk::judge(x, y)), f(m.distance), f(m.lost), f(m.gained)]));
                }
            }
            j["inks"] = Value::Array(v);
        }
        let res = rec.recognize(&frame, &RecognitionHints { character_id: Some("CHARACTER.IRONCLAD".into()) }).expect("recognize");
        let ts = tracker.update(&res, t0 + std::time::Duration::from_millis(400 * tick as u64));
        j["tracked"] = json!({"kind": format!("{:?}", ts.kind), "cards": ts.cards.iter().map(card).collect::<Vec<_>>(), "relics": ts.relics.len(),
            "slots": ts.slot_count, "gen": ts.generation, "held": ts.held, "unsettled": ts.unsettled});
        out.push(j);
    }
    out
}

/// Rust output lines in the golden file's shape, plus the extras lines.
fn rust_lines() -> (Vec<Value>, Vec<Value>) {
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
    for file in frames() {
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
    let extras = rust_extras(&rec);
    (lines, extras)
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
    let golden = |name: &str| -> Vec<Value> {
        let mut text = String::new();
        flate2::read::GzDecoder::new(std::fs::File::open(fixtures().join(name)).expect("golden")).read_to_string(&mut text).unwrap();
        text.lines().map(|l| serde_json::from_str(l).unwrap()).collect()
    };
    let (main, extras) = rust_lines();
    if let Ok(dir) = std::env::var("OV_VISION_DUMP") {
        for (name, v) in [("rust.jsonl", &main), ("rust_extras.jsonl", &extras)] {
            let s: String = v.iter().map(|v| v.to_string() + "\n").collect();
            std::fs::write(Path::new(&dir).join(name), s).unwrap();
        }
    }
    let mut golden_all = golden("csharp.jsonl.gz");
    golden_all.extend(golden("csharp_extras.jsonl.gz"));
    let golden = golden_all;
    let ours: Vec<Value> = main.into_iter().chain(extras).collect();
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
