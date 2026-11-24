//! Where the game draws the map nodes (`MapScreenGeometry`): the map's jitter stream replayed from the run seed.
//! All arithmetic is float32 as in the game.

use std::collections::BTreeSet;

use ov_core::map::{DesignPoint, MapCoord, MapGraph, MapNodeType, MapScreenLayout};

use super::rng::{self, GameRng, RngScheme};

const DESIGN_W: f32 = 1920.0;
const DESIGN_H: f32 = 1080.0;

fn next_float(rng: &mut GameRng, min: f32, max: f32) -> f32 {
    (rng.next_double() * (max - min) as f64 + min as f64) as f32
}

pub fn compute(map: &MapGraph, seed: Option<&str>, scheme: RngScheme, centered_rows: bool) -> Option<MapScreenLayout> {
    let seed = seed.filter(|s| !s.is_empty())?;
    if map.width <= 0 || map.height <= 1 {
        return None;
    }
    let centered = centered_rows || map.centered_rows;
    let act_index = map.act - 1;
    let k: f32 = if map.second_boss.is_some() { 0.9 } else { 1.0 };
    let dist_y = 2325f32 / (map.height - 1) as f32 * k;
    let dist_x = 1050f32 / map.width as f32;
    let mut rng = GameRng::new(rng::run_stream(scheme, seed, &format!("map_jitter_{act_index}")));
    let mut centers: Vec<(MapCoord, DesignPoint)> = Vec::with_capacity(map.nodes.len());
    let mut grid: Vec<MapCoord> =
        map.nodes.keys().copied().filter(|c| *c != map.start && *c != map.boss && Some(*c) != map.second_boss).collect();
    grid.sort_by_key(|c| (c.col, c.row));
    const NODE_HEIGHT: f32 = 56.0;
    let occupied: Vec<i32> = if centered { grid.iter().map(|c| c.row).collect::<BTreeSet<_>>().into_iter().collect() } else { Vec::new() };
    let row_gap = if centered { (2325f32 * k - NODE_HEIGHT * occupied.len() as f32) / (map.height - 1) as f32 } else { dist_y };
    let origin_x: f32 = if map.is_blitz { 530.0 } else { 460.0 };
    let jitter_y: f32 = if centered && map.is_blitz { 12.5 } else { 25.0 };
    for c in &grid {
        let jx = next_float(&mut rng, -21.0, 21.0);
        let jy = next_float(&mut rng, -jitter_y, jitter_y);
        let p = if centered {
            let below = occupied.iter().filter(|r| **r < c.row).count() as f32;
            let row_y = 1280f32 - (c.row as f32 * row_gap + NODE_HEIGHT * below + NODE_HEIGHT / 2.0);
            DesignPoint { x: origin_x + c.col as f32 * dist_x + jx, y: row_y + jy }
        } else {
            DesignPoint {
                x: DESIGN_W / 2.0 + c.col as f32 * dist_x - 500.0 + jx + 28.0,
                y: DESIGN_H / 2.0 + 740.0 - c.row as f32 * dist_y + jy + 28.0,
            }
        };
        centers.push((*c, p));
    }
    let mut set = |c: MapCoord, p: DesignPoint| match centers.iter_mut().find(|(k, _)| *k == c) {
        Some(e) => e.1 = p,
        None => centers.push((c, p)),
    };
    let ancient = map.nodes.get(&map.start).is_some_and(|n| n.kind == MapNodeType::Ancient);
    let start_size: f32 = if ancient { 208.0 } else { 56.0 };
    let start_base: f32 = if ancient { 720.0 } else { 800.0 };
    set(map.start, DesignPoint {
        x: DESIGN_W / 2.0 - 80.0 + start_size / 2.0,
        y: DESIGN_H / 2.0 - map.start.row as f32 * row_gap + start_base + start_size / 2.0,
    });
    set(map.boss, DesignPoint { x: DESIGN_W / 2.0 - 200.0 + 187.0, y: DESIGN_H / 2.0 - 1980.0 * k + 153.0 });
    if let Some(sb) = map.second_boss {
        set(sb, DesignPoint { x: DESIGN_W / 2.0 - 200.0 + 187.0, y: DESIGN_H / 2.0 - 2280.0 * k + 153.0 });
    }
    let current_row = map.current.map_or(map.start.row, |c| c.row);
    let mut default_scroll = -600f32 + current_row as f32 * dist_y;
    if centered && map.current.is_some() {
        let ys: Vec<f32> = centers.iter().filter(|(c, _)| c.row == current_row).map(|(_, p)| p.y).collect();
        default_scroll = if ys.is_empty() {
            MapScreenLayout::MIN_SCROLL
        } else {
            // Enumerable.Average(float): double sum, then float.
            let avg = (ys.iter().map(|y| *y as f64).sum::<f64>() / ys.len() as f64) as f32;
            DESIGN_H / 2.0 + 150.0 - avg
        };
    }
    Some(MapScreenLayout {
        centers,
        row_spacing: dist_y,
        default_scroll: default_scroll.clamp(MapScreenLayout::MIN_SCROLL, MapScreenLayout::MAX_SCROLL),
        act_index,
        seed: seed.to_string(),
    })
}
