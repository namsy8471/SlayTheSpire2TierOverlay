//! `godot.log`: typed events (`LogEvents`), the line parser and the tailer.

pub mod events;
pub mod parser;
pub mod tailer;

pub use events::*;
pub use parser::{kind_of, parse};
pub use tailer::LogTailer;
