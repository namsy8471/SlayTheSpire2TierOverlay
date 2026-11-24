//! Parity with the C# resource code on synthetic fixtures: a made-up "game" assembly (fakegame.dll, built from
//! fakegame.cs.txt) and a synthetic pack (genpck.py.txt: random textures and made-up names). No game files.
//! The golden file csharp.json was written from the C# CatalogGenerator and ResourcePreparer on the same input.

use std::io::Cursor;
use std::path::{Path, PathBuf};

use ov_core::loc;
use ov_resources::catalog::{self, CatalogOptions};
use ov_resources::install::{GameInstall, InstalledGame};
use ov_resources::manifest::LocalLayout;
use ov_resources::preparer::{PrepareNeed, ResourcePreparer};
use serde_json::{Value, json};
use sha2::Digest;

fn fixtures() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/synthetic")
}

fn temp(name: &str) -> PathBuf {
    let d = std::env::temp_dir().join(format!("ov-resources-{name}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&d);
    std::fs::create_dir_all(&d).unwrap();
    d
}

/// The install folder: the fixture pack and release_info, the fake assembly as sts2.dll.
fn install() -> PathBuf {
    let dir = temp("install");
    let f = fixtures().join("install");
    std::fs::copy(f.join("SlayTheSpire2.pck"), dir.join("SlayTheSpire2.pck")).unwrap();
    std::fs::copy(f.join("release_info.json"), dir.join("release_info.json")).unwrap();
    std::fs::create_dir_all(dir.join("data_sts2_windows_x86_64")).unwrap();
    std::fs::copy(fixtures().join("fakegame.dll"), dir.join("data_sts2_windows_x86_64/sts2.dll")).unwrap();
    dir
}

/// Numbers as f64, so 92 and 92.0 compare equal (C# writes whole floats without ".0").
fn norm(v: &Value) -> Value {
    match v {
        Value::Number(n) => json!(n.as_f64().unwrap()),
        Value::Array(a) => Value::Array(a.iter().map(norm).collect()),
        Value::Object(o) => Value::Object(o.iter().map(|(k, v)| (k.clone(), norm(v))).collect()),
        other => other.clone(),
    }
}

fn digest_tree(local: &Path) -> serde_json::Map<String, Value> {
    fn walk(dir: &Path, root: &Path, out: &mut Vec<(String, String)>) {
        for e in std::fs::read_dir(dir).unwrap().flatten() {
            let p = e.path();
            if p.is_dir() {
                walk(&p, root, out);
                continue;
            }
            let rel = p.strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/");
            if rel == "manifest.json" || rel.starts_with("catalog.") {
                continue;
            }
            let bytes = std::fs::read(&p).unwrap();
            let mut h = sha2::Sha256::new();
            if rel.ends_with(".png") {
                let img = image::ImageReader::new(Cursor::new(&bytes)).with_guessed_format().unwrap().decode().unwrap().to_rgba8();
                h.update(format!("{}x{}:", img.width(), img.height()).as_bytes());
                h.update(img.as_raw());
            } else {
                h.update(&bytes);
            }
            out.push((rel, h.finalize().iter().map(|b| format!("{b:02x}")).collect()));
        }
    }
    let mut v = Vec::new();
    walk(local, local, &mut v);
    v.sort();
    v.into_iter().map(|(k, d)| (k, Value::String(d))).collect()
}

#[test]
fn resources_match_csharp() {
    let golden: Value = serde_json::from_str(&std::fs::read_to_string(fixtures().join("csharp.json")).unwrap()).unwrap();
    let install = install();
    let dll = install.join("data_sts2_windows_x86_64/sts2.dll");

    // 1. Catalog builds, with and without IncludeAll.
    for (i, include_all) in [false, true].into_iter().enumerate() {
        let r = catalog::build(&CatalogOptions {
            dll_path: dll.clone(),
            localization_dir: Some(fixtures().join("locsrc")),
            game_version: "v0.113.0".into(),
            include_all,
            with_starting_loadouts: true,
            with_relics: true,
        })
        .expect("build");
        let ours = json!({
            "includeAll": include_all,
            "catalog": serde_json::to_value(&r.catalog).unwrap(),
            "loadouts": serde_json::to_value(r.starting_loadouts.unwrap()).unwrap(),
            "relics": serde_json::to_value(r.relics.unwrap()).unwrap(),
            "characterClasses": r.character_classes,
            "languages": r.loaded_languages,
            "multiPool": r.multi_pool_cards,
            "warnings": r.warnings,
        });
        assert_eq!(golden["builds"][i], ours, "catalog build includeAll={include_all}");
    }

    // 2. The whole preparation.
    let game = InstalledGame::detect(GameInstall::new(&install)).expect("detect");
    assert_eq!((game.assembly_hash_matches, game.version_label().as_str()), (Some(true), "v0.113.0"));
    let local = temp("local");
    let prep = ResourcePreparer::new(LocalLayout::new(&local));
    assert_eq!(prep.check(&game).need, PrepareNeed::Missing);
    let _kor = loc::use_lang(loc::Lang::Kor);
    let mut progress = Vec::new();
    let m = prep.prepare(&game, &mut |p| progress.push(p), &|| false).expect("prepare");
    assert_eq!(prep.check(&game).need, PrepareNeed::UpToDate);

    let mut mv = serde_json::to_value(&m).unwrap();
    for k in ["preparedAtUtc", "durationSeconds", "gameDir"] {
        mv.as_object_mut().unwrap().remove(k);
    }
    mv["fingerprint"].as_object_mut().unwrap().remove("pckWriteTicksUtc");
    assert_eq!(norm(&golden["manifest"]), norm(&mv), "manifest");
    assert_eq!(Value::Object(digest_tree(&local)), golden["tree"], "prepared files");
    let cat: Value = serde_json::from_str(&std::fs::read_to_string(local.join(m.catalog_file.unwrap())).unwrap()).unwrap();
    assert_eq!(golden["catalog"], cat, "catalog file");
    let lines: Vec<Value> = progress[..progress.len() - 1].iter().map(|p| json!([format!("{:.3}", p.fraction), p.text])).collect();
    assert_eq!(golden["progress"], Value::Array(lines), "progress lines");

    let _ = std::fs::remove_dir_all(&local);
    let _ = std::fs::remove_dir_all(&install);
}
