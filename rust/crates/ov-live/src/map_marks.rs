//! Port of the C# map overlay's pure parts (`MapTarget` in Services/MapScreenWatcher.cs and Map/MapMarkLayout.cs):
//! where the act map's nodes are on the game's 1920x1080 design screen, and the marks drawn on it.
//!
//! Deliberate difference from C#: the advisor helper answers only the next step (which child of the current node),
//! not whole paths to the boss. So the recommended line goes from where we are to the advised node, and the other
//! next steps get faint lines; there are no runner-up paths and no "?" odds chips.

use std::collections::HashMap;

use ov_core::map::{MapCoord, MapGraph, MapNodeType, MapScreenLayout};
use ov_gamestate::tracking::geometry;
use ov_gamestate::tracking::tracked::TrackedRun;
use ov_vision::geometry::ScreenFrame;
use ov_vision::map::{MapMark, MapScrollEstimator};

use crate::recognition::Frame;

/// Visible map area in design px: below the top bar, above the bottom edge (`MapMarkLayout.VisibleTop/Bottom`).
pub const VISIBLE_TOP: f32 = 95.0;
pub const VISIBLE_BOTTOM: f32 = 1075.0;
/// Half-size of a normal node icon plus a small gap, so lines stop short of the icons.
const NODE_GAP: f32 = 34.0;

/// One act map placed on the design screen (`MapTarget`).
#[derive(Clone, Debug)]
pub struct MapTarget {
    /// Changes when another map is shown (other seed or act).
    pub key: String,
    pub map: MapGraph,
    /// Node centres at scroll 0.
    pub layout: MapScreenLayout,
    /// The normal grid nodes the scroll fit looks for (start and boss are left out).
    pub marks: Vec<MapMark>,
}

impl MapTarget {
    /// None when the run has no map or no seed (the node jitter needs the seed).
    pub fn for_run(run: &TrackedRun) -> Option<MapTarget> {
        let map = run.map.as_ref()?;
        let layout = geometry::compute(map, run.seed.as_deref(), run.rng_scheme, map.centered_rows)?;
        let marks = layout
            .centers
            .iter()
            .filter(|(c, _)| *c != map.start && *c != map.boss && Some(*c) != map.second_boss)
            .map(|(_, p)| MapMark { x: p.x, y0: p.y })
            .collect();
        Some(MapTarget { key: Self::key_for(run), map: map.clone(), layout, marks })
    }

    /// Same key = same node positions (they depend on the seed and the act's nodes, not on where we are).
    pub fn key_for(run: &TrackedRun) -> String {
        match &run.map {
            Some(m) => format!("{}|{:?}|{}|{}|{}|{}x{}", run.seed.as_deref().unwrap_or(""), run.rng_scheme, m.act, m.act_id, m.nodes.len(), m.width, m.height),
            None => String::new(),
        }
    }
}

/// One frame looked at for the map (`MapSighting`).
#[derive(Clone, Debug, PartialEq)]
pub struct MapSight {
    pub key: String,
    /// The map screen is open and every check of the fit passed.
    pub is_map: bool,
    /// A node is on screen at `layout.center(c).y + scroll`.
    pub scroll: f32,
}

/// Finds the act map in captured frames (`MapScreenWatcher`; the capture worker owns it).
#[derive(Default)]
pub struct MapWatcher {
    estimator: MapScrollEstimator,
}

impl MapWatcher {
    pub fn analyze(&mut self, frame: &Frame<'_>, target: &MapTarget) -> MapSight {
        let f = ScreenFrame { bgra: frame.bgra, width: frame.width, height: frame.height, stride: frame.stride };
        let fit = self.estimator.estimate(&f, &target.marks, MapScreenLayout::MIN_SCROLL, MapScreenLayout::MAX_SCROLL);
        MapSight { key: target.key.clone(), is_map: fit.is_map, scroll: fit.scroll }
    }
}

/// The scroll moved more than this between two frames (design px): the map is still moving.
pub const STILL_TOLERANCE: f32 = 3.0;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LineKind {
    /// To the advised next node: bold gold.
    Best,
    /// To another next node: thin and faint.
    Other,
}

/// A segment in design px on screen (scroll applied), shortened so it doesn't cover the icons.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct LineMark {
    pub x1: f32,
    pub y1: f32,
    pub x2: f32,
    pub y2: f32,
    pub kind: LineKind,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NodeKind {
    /// The node to click next: ring + "추천!" ribbon.
    Recommended,
    /// A co-op partner's vote.
    Partner,
    /// Co-op: my own vote where no partner voted.
    MyVote,
    /// Co-op: my vote and a partner's on the same node.
    BothVoted,
}

#[derive(Clone, Debug, PartialEq)]
pub struct NodeMark {
    pub coord: MapCoord,
    pub x: f32,
    pub y: f32,
    pub kind: NodeKind,
    /// Partner count on the node (Partner / BothVoted).
    pub count: usize,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct MapPlan {
    pub lines: Vec<LineMark>,
    pub nodes: Vec<NodeMark>,
}

/// The advised next step: where we are, the advised child, and the other children.
#[derive(Clone, Debug, PartialEq)]
pub struct PathChoice {
    pub origin: MapCoord,
    pub best: MapCoord,
    pub others: Vec<MapCoord>,
}

/// The marks for one frame (`MapMarkLayout.Build`). `scroll`: measured map scroll in design px.
pub fn build(map: &MapGraph, layout: &MapScreenLayout, scroll: f32, choice: Option<&PathChoice>, partner_votes: &[MapCoord], my_vote: Option<MapCoord>) -> MapPlan {
    let centers: HashMap<MapCoord, (f32, f32)> = layout.centers.iter().map(|(c, p)| (*c, (p.x, p.y + scroll))).collect();
    let at = |c: MapCoord| centers.get(&c).copied();
    let visible = |y: f32| (VISIBLE_TOP..=VISIBLE_BOTTOM).contains(&y);
    let radius = |c: MapCoord| -> f32 {
        if c == map.boss || Some(c) == map.second_boss {
            130.0
        } else if c == map.start && map.nodes.get(&c).is_some_and(|n| n.kind == MapNodeType::Ancient) {
            95.0
        } else {
            NODE_GAP
        }
    };
    let segment = |a: MapCoord, b: MapCoord, kind: LineKind| -> Option<LineMark> {
        let (x1, y1) = at(a)?;
        let (x2, y2) = at(b)?;
        if y1.max(y2) < VISIBLE_TOP || y1.min(y2) > VISIBLE_BOTTOM {
            return None;
        }
        let (dx, dy) = (x2 - x1, y2 - y1);
        let len = (dx * dx + dy * dy).sqrt();
        let (ra, rb) = (radius(a), radius(b));
        if len <= ra + rb + 4.0 {
            return None;
        }
        let (ux, uy) = (dx / len, dy / len);
        Some(LineMark { x1: x1 + ux * ra, y1: y1 + uy * ra, x2: x2 - ux * rb, y2: y2 - uy * rb, kind })
    };

    let mut plan = MapPlan::default();
    if let Some(c) = choice {
        // Faint lines first so the gold one is drawn over them.
        plan.lines.extend(c.others.iter().filter(|&&o| o != c.best).filter_map(|&o| segment(c.origin, o, LineKind::Other)));
        plan.lines.extend(segment(c.origin, c.best, LineKind::Best));
        if let Some((x, y)) = at(c.best).filter(|p| visible(p.1)) {
            plan.nodes.push(NodeMark { coord: c.best, x, y, kind: NodeKind::Recommended, count: 0 });
        }
    }
    let mut partners: Vec<(MapCoord, usize)> = Vec::new();
    for &v in partner_votes.iter().filter(|&&v| Some(v) != my_vote) {
        match partners.iter_mut().find(|p| p.0 == v) {
            Some(p) => p.1 += 1,
            None => partners.push((v, 1)),
        }
    }
    for (c, n) in partners {
        if let Some((x, y)) = at(c).filter(|p| visible(p.1)) {
            plan.nodes.push(NodeMark { coord: c, x, y, kind: NodeKind::Partner, count: n });
        }
    }
    if let Some(mine) = my_vote
        && let Some((x, y)) = at(mine).filter(|p| visible(p.1))
    {
        let agree = partner_votes.iter().filter(|&&v| v == mine).count();
        plan.nodes.push(NodeMark { coord: mine, x, y, kind: if agree == 0 { NodeKind::MyVote } else { NodeKind::BothVoted }, count: agree });
    }
    plan
}

#[cfg(test)]
mod tests {
    use super::*;
    use ov_core::map::{DesignPoint, MapNode};

    fn c(col: i32, row: i32) -> MapCoord {
        MapCoord { col, row }
    }

    #[test]
    fn lines_go_from_here_to_the_next_steps_and_hidden_rows_are_dropped() {
        let nodes: HashMap<MapCoord, MapNode> = [c(1, 0), c(0, 1), c(2, 1), c(1, 16)]
            .into_iter()
            .map(|k| (k, MapNode { coord: k, kind: MapNodeType::Monster, children: vec![] }))
            .collect();
        let map = MapGraph {
            act_id: "ACT.X".into(),
            act: 1,
            width: 7,
            height: 16,
            is_blitz: false,
            centered_rows: true,
            nodes,
            start: c(1, 0),
            boss: c(1, 16),
            second_boss: None,
            current: Some(c(1, 0)),
            visited: vec![],
            normal_fights_done: None,
            easy_fights: None,
        };
        let p = |x, y| DesignPoint { x, y };
        let layout = MapScreenLayout {
            centers: vec![(c(1, 0), p(900.0, 900.0)), (c(0, 1), p(800.0, 750.0)), (c(2, 1), p(1000.0, 750.0)), (c(1, 16), p(900.0, -1500.0))],
            row_spacing: 150.0,
            default_scroll: 0.0,
            act_index: 0,
            seed: "S".into(),
        };
        let choice = PathChoice { origin: c(1, 0), best: c(2, 1), others: vec![c(0, 1), c(2, 1)] };
        let plan = build(&map, &layout, 100.0, Some(&choice), &[c(0, 1), c(0, 1)], None);
        assert_eq!(plan.lines.iter().map(|l| l.kind).collect::<Vec<_>>(), vec![LineKind::Other, LineKind::Best], "best drawn last, not twice");
        let best = plan.lines[1];
        assert!((best.y1 - (1000.0 - 34.0 * 150.0 / (100.0f32 * 100.0 + 150.0 * 150.0).sqrt())).abs() < 0.01, "starts short of the icon: {best:?}");
        assert_eq!(plan.nodes.iter().map(|n| (n.kind, n.coord, n.count)).collect::<Vec<_>>(), vec![(NodeKind::Recommended, c(2, 1), 0), (NodeKind::Partner, c(0, 1), 2)]);
        // Scrolled so both rows are under the top bar: no ring, no tag, no line.
        let plan = build(&map, &layout, -900.0, Some(&choice), &[], Some(c(2, 1)));
        assert!(plan.nodes.is_empty() && plan.lines.is_empty(), "{plan:?}");
    }
}
