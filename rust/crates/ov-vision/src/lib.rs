//! Screen recognition on captured frames: a port of the C# `Sts2Overlay.Vision` project.
//!
//! Frames are BGRA. Positions are given in 1920x1080 reference units and mapped onto the frame with
//! [`geometry::RefTransform`]. Float arithmetic follows the C# code step by step, so results match it.

pub mod artdb;
pub mod catalog;
pub mod classifier;
pub mod geometry;
mod glyph_data;
pub mod image_io;
pub mod integral;
pub mod layout;
pub mod ncc;
pub mod price;
pub mod recognizer;
pub mod relics;
pub mod results;
pub mod tiles;
pub mod upgrade;
