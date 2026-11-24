//! The current act's map (`Sts2Overlay.Core.Map.MapModel`).

use std::collections::HashMap;

/// Kind of a map node; mirrors `saved_map.points[].type`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum MapNodeType {
    /// "unknown" = "?" room.
    Unknown,
    Monster,
    Elite,
    RestSite,
    Shop,
    Treasure,
    Ancient,
    Boss,
}

impl MapNodeType {
    pub fn parse(save_type: Option<&str>) -> Self {
        match save_type {
            Some("monster") => Self::Monster,
            Some("elite") => Self::Elite,
            Some("rest_site") => Self::RestSite,
            Some("shop") => Self::Shop,
            Some("treasure") => Self::Treasure,
            Some("ancient") => Self::Ancient,
            Some("boss") => Self::Boss,
            _ => Self::Unknown,
        }
    }
}

/// Grid coordinate; row 0 is the act start.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct MapCoord {
    pub col: i32,
    pub row: i32,
}

impl MapCoord {
    pub const fn new(col: i32, row: i32) -> Self {
        Self { col, row }
    }
}

impl std::fmt::Display for MapCoord {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "({},{})", self.col, self.row)
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct MapNode {
    pub coord: MapCoord,
    pub kind: MapNodeType,
    pub children: Vec<MapCoord>,
}

/// The current act's map as saved, plus where the player is (`MapGraph`).
#[derive(Clone, Debug, PartialEq)]
pub struct MapGraph {
    pub act_id: String,
    /// 1-based.
    pub act: i32,
    pub width: i32,
    pub height: i32,
    pub is_blitz: bool,
    /// Game v0.113+ (save schema 21+) map layout.
    pub centered_rows: bool,
    pub nodes: HashMap<MapCoord, MapNode>,
    pub start: MapCoord,
    pub boss: MapCoord,
    pub second_boss: Option<MapCoord>,
    pub current: Option<MapCoord>,
    /// Visited coordinates of this act in order; empty when unknown.
    pub visited: Vec<MapCoord>,
    pub normal_fights_done: Option<i32>,
    pub easy_fights: Option<i32>,
}
