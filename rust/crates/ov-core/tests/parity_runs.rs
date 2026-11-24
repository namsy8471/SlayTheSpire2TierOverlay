//! `.run` files parse exactly as the C# reader did (golden from the C# parity dumper, fixtures in tests/fixtures).

use std::io::Read;

use ov_core::golden;

#[test]
fn run_history_matches_csharp() {
    let mut text = String::new();
    flate2::read::GzDecoder::new(std::fs::File::open("tests/golden/runs.jsonl.gz").unwrap()).read_to_string(&mut text).unwrap();
    let fixtures = std::path::Path::new("../../../tests/fixtures");
    let mut n = 0;
    for (file, want) in golden::read_jsonl(&text) {
        let path = ["runs", "regression"].iter().map(|d| fixtures.join(d).join(&file)).find(|p| p.exists()).expect(&file);
        let got = golden::run(&ov_core::history::read_file(&path).unwrap());
        assert!(golden::first_diff(&got, &want, "").is_none(), "{file}: {:?}", golden::first_diff(&got, &want, ""));
        n += 1;
    }
    assert_eq!(n, 25);
}
