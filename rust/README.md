# Rust overlay (port in progress)

The overlay app is being ported from C# (`src/`) to Rust (this folder). The C# app stays the shipped app until the
Rust app replaces it in one release (v0.2.0).

| crate | port of | what |
|---|---|---|
| `ov-core` | `Sts2Overlay.Core` (model, map model, localization, run history) | data the app reads and shows |
| `ov-gamestate` | `Sts2Overlay.GameState` | `current_run.save` and `godot.log` reading, game state tracking |

Recommendations are not computed in this repository: the advisor helper (built privately, shipped as compiled files)
answers every decision.

## Parity

Each port is checked against the C# code it replaces: `tests/golden/*.jsonl.gz` hold what the C# code produced for
the repository's own fixtures, and the Rust tests must produce the same values.

```sh
cd rust
cargo test
```
