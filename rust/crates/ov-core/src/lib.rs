//! Overlay data model shared by the app crates (port of the `Sts2Overlay.Core` parts the Rust app keeps).
//!
//! Recommendations are not computed here: under the Rust app's structure (plan A) the private advisor helper
//! answers every decision. This crate holds what the app itself reads and shows.

pub mod golden;
pub mod history;
pub mod json;
pub mod loc;
pub mod map;
pub mod model;
