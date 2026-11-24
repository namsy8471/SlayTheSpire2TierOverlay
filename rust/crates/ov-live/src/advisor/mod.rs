//! The helper process and its protocol (`docs/COMBAT_ADVISOR.md`, `ADVISOR_PROTOCOL.md`).

pub mod budget;
pub mod combat;
pub mod command;
pub mod decision;
pub mod host;
#[cfg(windows)]
mod job;

pub use combat::{CombatAdviceMessage, CombatAdviceState, CombatAdviceStep, CombatEnding, CombatPick, CombatStepKind};
pub use command::AdvisorCommand;
pub use decision::{AdvisorLine, Candidate, DecisionAnswer, DecisionStatus, RunNotice};
pub use host::{AdvisorHost, HostEvent};
