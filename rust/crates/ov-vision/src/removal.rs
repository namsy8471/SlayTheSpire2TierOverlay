//! Port of CardRemovalProbe.cs: is the shop's card-removal service on screen and still for sale?

use std::fmt;

use crate::geometry::{PixelRect, RectF, RefTransform, ScreenFrame};

#[derive(Clone, Debug, PartialEq)]
pub struct CardRemovalReading {
    pub available: bool,
    pub bounds: PixelRect,
    pub face_gold: f32,
    pub face_red: f32,
    pub price_icon: i32,
}

impl fmt::Display for CardRemovalReading {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "{} gold={:.3} red={:.3} price={}",
            if self.available { "available" } else { "absent" },
            self.face_gold,
            self.face_red,
            self.price_icon
        )
    }
}

pub const CENTER_X: f32 = 1488.0;
pub const CENTER_Y: f32 = 767.0;
pub const RADIUS: f32 = 85.0;
const FACE_RADIUS: f32 = 0.85 * RADIUS;
const PRICE_ROW: RectF = RectF::new(1410.0, 872.0, 160.0, 73.0);
const PRICE_ROW_EXCLUSION: f32 = 1.27 * RADIUS;
pub const MIN_FACE_GOLD: f32 = 0.50;
pub const MAX_FACE_RED: f32 = 0.15;
pub const MIN_PRICE_ICON: i32 = 150;

pub fn coin_ref() -> RectF {
    RectF::from_center(CENTER_X, CENTER_Y, 2.0 * RADIUS, 2.0 * RADIUS)
}

pub fn read(frame: &ScreenFrame<'_>) -> CardRemovalReading {
    let t = RefTransform::for_frame(frame.width, frame.height);
    let (mut face, mut gold, mut red) = (0, 0, 0);
    let mut y = CENTER_Y - FACE_RADIUS;
    while y <= CENTER_Y + FACE_RADIUS {
        let mut x = CENTER_X - FACE_RADIUS;
        while x <= CENTER_X + FACE_RADIUS {
            let (dx, dy) = (x - CENTER_X, y - CENTER_Y);
            if dx * dx + dy * dy <= FACE_RADIUS * FACE_RADIUS
                && let Some((r, g, b)) = try_sample(frame, &t, x, y) {
                    face += 1;
                    if is_gold(r, g, b) {
                        gold += 1;
                    } else if is_red(r, g, b) {
                        red += 1;
                    }
                }
            x += 1.0;
        }
        y += 1.0;
    }
    let mut icon = 0;
    let mut y = PRICE_ROW.y;
    while y < PRICE_ROW.bottom() {
        let mut x = PRICE_ROW.x;
        while x < PRICE_ROW.right() {
            let (dx, dy) = (x - CENTER_X, y - CENTER_Y);
            if dx * dx + dy * dy >= PRICE_ROW_EXCLUSION * PRICE_ROW_EXCLUSION
                && let Some((r, g, b)) = try_sample(frame, &t, x, y)
                    && is_gold(r, g, b) {
                        icon += 1;
                    }
            x += 1.0;
        }
        y += 1.0;
    }
    let gf = if face == 0 { 0.0 } else { gold as f32 / face as f32 };
    let rf = if face == 0 { 0.0 } else { red as f32 / face as f32 };
    CardRemovalReading {
        available: gf >= MIN_FACE_GOLD && rf <= MAX_FACE_RED && icon >= MIN_PRICE_ICON,
        bounds: t.to_pixel_rect(coin_ref()),
        face_gold: gf,
        face_red: rf,
        price_icon: icon,
    }
}

pub fn is_gold(r: u8, g: u8, b: u8) -> bool {
    r > 150 && g > 110 && b < 110 && r as i32 - b as i32 > 80 && r >= g
}

pub fn is_red(r: u8, g: u8, b: u8) -> bool {
    r > 160 && g < 90 && b < 90
}

fn try_sample(frame: &ScreenFrame<'_>, t: &RefTransform, ref_x: f32, ref_y: f32) -> Option<(u8, u8, u8)> {
    let x = t.to_pixel_x(ref_x + 0.5) as i32;
    let y = t.to_pixel_y(ref_y + 0.5) as i32;
    if x < 0 || y < 0 || x >= frame.width || y >= frame.height {
        return None;
    }
    let p = (y * frame.stride + x * 4) as usize;
    Some((frame.bgra[p + 2], frame.bgra[p + 1], frame.bgra[p]))
}
