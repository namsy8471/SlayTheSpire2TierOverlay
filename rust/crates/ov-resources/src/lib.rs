//! Port of Sts2Overlay.Resources: local resources from the installed game.
//!
//! - `pck`: the Godot pack reader, texture decoding (BC3, BC7), PNG writing and relic icon cutting.
//! - `catalog`: the card catalog from sts2.dll metadata and IL (no game code runs).
//! - `install`, `version`, `manifest`, `preparer`, `guard`: finding the game, matching data versions, and the
//!   first-run preparation of `data/local`.

// Ports keep the C# index loops and signatures so they can be compared line by line.
#![allow(clippy::needless_range_loop, clippy::type_complexity, clippy::too_many_arguments)]

pub mod catalog;
pub mod guard;
pub mod install;
pub mod io;
pub mod manifest;
pub mod pck;
pub mod preparer;
pub mod version;
