//! Port of CombatAdviceMessage.cs: the helper's v1 combat lines (`docs/COMBAT_ADVISOR.md`). Each line replaces the
//! previous one. Only reading: no game logic.

use serde_json::{Map, Value};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CombatAdviceState {
    /// No fight is followed: the section is hidden.
    Idle,
    /// A play order for the rest of this turn.
    Advice,
    /// The helper lost track of this fight.
    Lost,
    /// The helper cannot follow this fight.
    Unsupported,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CombatStepKind {
    Card,
    Potion,
    Unknown,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum CombatEnding {
    #[default]
    None,
    EndTurn,
    Win,
    /// `draw`: the last step draws or rolls something; advice again after it resolves.
    AdviseAfterPlay,
    /// `choice`: wait for the player's selection.
    AdviseAfterChoice,
    /// `observe`: wait for another observed action.
    AwaitObservedAction,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CombatPick {
    pub id: String,
    pub upgraded: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CombatAdviceStep {
    pub kind: CombatStepKind,
    /// Model id as in godot.log (`BASH`, `FIRE_POTION`).
    pub id: String,
    pub upgraded: bool,
    pub hand: Option<i32>,
    pub target: Option<String>,
    pub target_slot: Option<i32>,
    pub target_ambiguous: bool,
    /// Per selection the step opens: the cards the helper would pick there.
    pub choices: Vec<Vec<CombatPick>>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct CombatAdviceMessage {
    pub state: CombatAdviceState,
    pub encounter: Option<String>,
    pub turn: Option<i32>,
    pub energy: Option<i32>,
    pub hp: Option<i32>,
    pub block: Option<i32>,
    pub played: Option<i32>,
    pub steps: Vec<CombatAdviceStep>,
    pub choosing: Option<Vec<CombatPick>>,
    pub ending: CombatEnding,
    pub is_fool_search: bool,
    pub uncertain: bool,
    pub note: String,
    pub reason: String,
    pub ms: Option<f64>,
}

impl CombatAdviceMessage {
    pub fn new(state: CombatAdviceState) -> Self {
        CombatAdviceMessage {
            state,
            encounter: None,
            turn: None,
            energy: None,
            hp: None,
            block: None,
            played: None,
            steps: Vec::new(),
            choosing: None,
            ending: CombatEnding::None,
            is_fool_search: false,
            uncertain: false,
            note: String::new(),
            reason: String::new(),
            ms: None,
        }
    }

    pub fn idle() -> Self {
        Self::new(CombatAdviceState::Idle)
    }
}

/// The protocol version of combat lines.
pub const PROTOCOL_VERSION: i64 = 1;

pub(crate) fn str_of<'a>(o: &'a Map<String, Value>, name: &str) -> Option<&'a str> {
    o.get(name).and_then(Value::as_str)
}

/// C# `TryGetInt32`: an integer JSON number in i32 range (`2.0` and `1.5` are not).
pub(crate) fn int_of(o: &Map<String, Value>, name: &str) -> Option<i32> {
    o.get(name)?.as_i64().and_then(|i| i32::try_from(i).ok())
}

pub(crate) fn bool_of(o: &Map<String, Value>, name: &str) -> bool {
    o.get(name) == Some(&Value::Bool(true))
}

fn short(s: &str) -> String {
    if s.chars().count() <= 40 { s.to_string() } else { s.chars().take(40).collect::<String>() + "…" }
}

/// Reads one v1 object (already parsed). `Err` = unusable, with a short reason.
pub fn parse_object(root: &Map<String, Value>) -> Result<CombatAdviceMessage, String> {
    let state = str_of(root, "state");
    let with_reason = |s: CombatAdviceState| CombatAdviceMessage {
        encounter: str_of(root, "encounter").map(str::to_string),
        reason: str_of(root, "reason").unwrap_or("").to_string(),
        ..CombatAdviceMessage::new(s)
    };
    match state {
        Some("idle") => Ok(CombatAdviceMessage::idle()),
        Some("lost") => Ok(with_reason(CombatAdviceState::Lost)),
        Some("unsupported") => Ok(with_reason(CombatAdviceState::Unsupported)),
        Some("advice") => Ok(advice(root)),
        None => Err("no state".into()),
        Some(s) => Err(format!("unknown state '{}'", short(s))),
    }
}

fn picks(v: &Value) -> Option<Vec<CombatPick>> {
    let cards = v.as_object()?.get("cards")?.as_array()?;
    Some(
        cards
            .iter()
            .filter_map(Value::as_object)
            .filter_map(|c| str_of(c, "id").filter(|s| !s.is_empty()).map(|id| CombatPick { id: id.to_string(), upgraded: bool_of(c, "upgraded") }))
            .collect(),
    )
}

fn advice(root: &Map<String, Value>) -> CombatAdviceMessage {
    let mut steps = Vec::new();
    if let Some(arr) = root.get("steps").and_then(Value::as_array) {
        for s in arr.iter().filter_map(Value::as_object) {
            let Some(id) = str_of(s, "id").filter(|x| !x.is_empty()) else { continue };
            let kind = match str_of(s, "kind") {
                Some("card") => CombatStepKind::Card,
                Some("potion") => CombatStepKind::Potion,
                _ => CombatStepKind::Unknown,
            };
            let choices = s.get("choices").and_then(Value::as_array).map(|cs| cs.iter().filter_map(picks).collect()).unwrap_or_default();
            steps.push(CombatAdviceStep {
                kind,
                id: id.to_string(),
                upgraded: bool_of(s, "upgraded"),
                hand: int_of(s, "hand"),
                target: str_of(s, "target").filter(|t| !t.is_empty()).map(str::to_string),
                target_slot: int_of(s, "targetSlot"),
                target_ambiguous: bool_of(s, "targetAmbiguous"),
                choices,
            });
        }
    }
    CombatAdviceMessage {
        encounter: str_of(root, "encounter").map(str::to_string),
        turn: int_of(root, "turn"),
        energy: int_of(root, "energy"),
        hp: int_of(root, "hp"),
        block: int_of(root, "block"),
        played: int_of(root, "played"),
        steps,
        choosing: root.get("choosing").and_then(picks),
        is_fool_search: str_of(root, "policy") == Some("fool_rust"),
        ending: match str_of(root, "ending") {
            Some("end_turn") => CombatEnding::EndTurn,
            Some("win") => CombatEnding::Win,
            Some("draw") => CombatEnding::AdviseAfterPlay,
            Some("choice") => CombatEnding::AdviseAfterChoice,
            Some("observe") => CombatEnding::AwaitObservedAction,
            _ => CombatEnding::None,
        },
        // Only an explicit "exact" is trusted.
        uncertain: str_of(root, "confidence") != Some("exact"),
        note: str_of(root, "note").unwrap_or("").to_string(),
        ms: root.get("ms").and_then(Value::as_f64),
        ..CombatAdviceMessage::new(CombatAdviceState::Advice)
    }
}
