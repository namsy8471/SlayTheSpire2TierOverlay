//! The overlay's brain without UI (port of the C# App's Integration and Controllers logic).
//!
//! Structure A (`ADVISOR_PROTOCOL.md`): this app only shows things. The private helper `sts2-advisor.exe` computes all
//! advice. This crate starts and watches the helper ([`advisor`]), turns captured frames into what is on screen
//! ([`recognition`]), builds the `decide` requests from it ([`request`]), reads the helper's lines (combat advice v1,
//! decision answers v2) and keeps the rules of a card screen's life ([`offer`]). It is platform independent except
//! the job object in the advisor host.

pub mod advisor;
pub mod brain;
pub mod broker;
pub mod offer;
pub mod paths;
pub mod present;
pub mod recognition;
pub mod request;
pub mod text;
