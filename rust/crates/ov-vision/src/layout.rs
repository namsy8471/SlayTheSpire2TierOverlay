//! Port of Layout.cs: where the game puts cards on each screen, in 1920x1080 reference units.

use crate::geometry::RectF;
use crate::results::CardSlotKind;

pub mod card_geometry {
    use crate::geometry::RectF;
    /// NCard's whole card, art and title boxes around the node centre at scale 1.
    pub const CARD: RectF = RectF::new(-150.0, -211.0, 300.0, 422.0);
    pub const ART: RectF = RectF::new(-125.0, -168.0, 250.0, 190.0);
    pub const TITLE: RectF = RectF::new(-105.0, -204.0, 210.0, 54.0);

    pub fn place(local: RectF, cx: f32, cy: f32, scale: f32) -> RectF {
        RectF::new(cx + local.x * scale, cy + local.y * scale, local.width * scale, local.height * scale)
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SlotLayout {
    pub index: i32,
    pub kind: CardSlotKind,
    pub center_x: f32,
    pub center_y: f32,
    pub small_scale: f32,
    pub hover_scale: f32,
}

pub const REWARD_ROW_X: f32 = 960.0;
pub const REWARD_ROW_Y: f32 = 616.0;
pub const REWARD_SPACING: f32 = 350.0;
pub const REWARD_SMALL_SCALE: f32 = 0.8;
pub const REWARD_HOVER_SCALE: f32 = 1.0;

pub fn reward(count: i32, spread: f32) -> Vec<SlotLayout> {
    (0..count)
        .map(|i| {
            let x = REWARD_ROW_X + (i as f32 - (count - 1) as f32 / 2.0) * REWARD_SPACING * spread;
            SlotLayout { index: i, kind: CardSlotKind::Reward, center_x: x, center_y: REWARD_ROW_Y, small_scale: REWARD_SMALL_SCALE, hover_scale: REWARD_HOVER_SCALE }
        })
        .collect()
}

pub const CHOICE_ROW_Y: f32 = 600.0;
pub const CHOICE_SPACING: f32 = 340.0;

pub fn choice(count: i32, spread: f32) -> Vec<SlotLayout> {
    (0..count)
        .map(|i| {
            let x = REWARD_ROW_X + (i as f32 - (count - 1) as f32 / 2.0) * CHOICE_SPACING * spread;
            SlotLayout { index: i, kind: CardSlotKind::Choice, center_x: x, center_y: CHOICE_ROW_Y, small_scale: REWARD_SMALL_SCALE, hover_scale: REWARD_HOVER_SCALE }
        })
        .collect()
}

pub const GRID_X0: f32 = 375.0;
pub const GRID_COLUMN_STEP: f32 = 280.0;
pub const GRID_ROW_STEP: f32 = 377.6;
pub const GRID_ONE_ROW_Y: f32 = 460.0;
pub const GRID_FIRST_ROW_Y: f32 = 328.8;
pub const GRID_COLUMNS: i32 = 5;

pub fn grid(two_rows: bool) -> Vec<SlotLayout> {
    let rows = if two_rows { 2 } else { 1 };
    let mut slots = Vec::new();
    for r in 0..rows {
        for c in 0..GRID_COLUMNS {
            let y = if two_rows { GRID_FIRST_ROW_Y + GRID_ROW_STEP * r as f32 } else { GRID_ONE_ROW_Y };
            slots.push(SlotLayout {
                index: r * GRID_COLUMNS + c,
                kind: CardSlotKind::Grid,
                center_x: GRID_X0 + GRID_COLUMN_STEP * c as f32,
                center_y: y,
                small_scale: REWARD_SMALL_SCALE,
                hover_scale: REWARD_HOVER_SCALE,
            });
        }
    }
    slots
}

pub const GRID_FOCUS_SCROLL: f32 = -126.4;
pub const GRID_END_SCROLL: f32 = -117.6;
pub const GRID_SCROLLED_SLOT_BASE: i32 = 10;

pub fn grid_scrolled(scroll: f32) -> Vec<SlotLayout> {
    let mut slots: Vec<SlotLayout> = Vec::new();
    for r in 0..4 {
        let y = GRID_FIRST_ROW_Y + scroll + GRID_ROW_STEP * r as f32;
        if y + card_geometry::ART.y * REWARD_SMALL_SCALE < 80.0 {
            continue;
        }
        if y + card_geometry::ART.bottom() * REWARD_HOVER_SCALE > 1080.0 {
            break;
        }
        for c in 0..GRID_COLUMNS {
            slots.push(SlotLayout {
                index: GRID_SCROLLED_SLOT_BASE + slots.len() as i32,
                kind: CardSlotKind::Grid,
                center_x: GRID_X0 + GRID_COLUMN_STEP * c as f32,
                center_y: y,
                small_scale: REWARD_SMALL_SCALE,
                hover_scale: REWARD_HOVER_SCALE,
            });
        }
    }
    slots
}

pub const SHOP_SMALL_SCALE: f32 = 0.65;
pub const SHOP_HOVER_SCALE: f32 = 0.8;
const SLOTS_X: f32 = 960.0 - 842.0;
const SLOTS_Y: f32 = 80.0;
const CHARACTER_OFFSETS: [(f32, f32); 5] = [(-13.0, 0.0), (250.0, 0.0), (511.0, 0.0), (770.0, 0.0), (1036.0, -3.0)];
const COLORLESS_OFFSETS: [(f32, f32); 2] = [(-13.0, 0.0), (261.0, 0.0)];

pub fn shop() -> Vec<SlotLayout> {
    let mut slots = Vec::new();
    for (x, y) in CHARACTER_OFFSETS {
        slots.push(SlotLayout {
            index: slots.len() as i32,
            kind: CardSlotKind::ShopCharacter,
            center_x: SLOTS_X + 332.0 + x,
            center_y: SLOTS_Y + 303.0 + y,
            small_scale: SHOP_SMALL_SCALE,
            hover_scale: SHOP_HOVER_SCALE,
        });
    }
    for (x, y) in COLORLESS_OFFSETS {
        slots.push(SlotLayout {
            index: slots.len() as i32,
            kind: CardSlotKind::ShopColorless,
            center_x: SLOTS_X + 397.0 + x,
            center_y: SLOTS_Y + 676.0 + y,
            small_scale: SHOP_SMALL_SCALE,
            hover_scale: SHOP_HOVER_SCALE,
        });
    }
    slots
}

pub fn shop_price(cx: f32, cy: f32, scale: f32) -> RectF {
    card_geometry::place(RectF::new(-148.0, 220.0, 297.0, 58.0), cx, cy, scale)
}
