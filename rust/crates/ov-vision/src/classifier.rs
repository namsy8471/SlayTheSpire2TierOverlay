//! Port of ScreenClassifier.cs: a few colour probes decide which layouts are worth matching.

use std::fmt::Write;

use crate::geometry::{PixelRect, RectF, RefTransform, ScreenFrame};
use crate::results::ScreenGate;

struct ColourProbe(&'static str, RectF, f32, f32, f32);
struct DarkProbe(&'static str, RectF, f32);

const REWARD_BANNER: [ColourProbe; 3] = [
    ColourProbe("banL", RectF::new(715.0, 250.0, 75.0, 50.0), 161.0, 133.0, 101.0),
    ColourProbe("banR", RectF::new(1135.0, 250.0, 70.0, 50.0), 161.0, 134.0, 104.0),
    ColourProbe("banTop", RectF::new(820.0, 240.0, 280.0, 10.0), 165.0, 140.0, 111.0),
];
const REWARD_BACKDROP: [DarkProbe; 2] = [
    DarkProbe("darkL", RectF::new(540.0, 250.0, 60.0, 50.0), 40.0),
    DarkProbe("darkR", RectF::new(1320.0, 250.0, 60.0, 50.0), 40.0),
];
const SHOP_RUG: [ColourProbe; 3] = [
    ColourProbe("rugL", RectF::new(250.0, 250.0, 70.0, 250.0), 73.0, 106.0, 97.0),
    ColourProbe("rugB", RectF::new(300.0, 940.0, 1000.0, 50.0), 77.0, 112.0, 99.0),
    ColourProbe("rugR", RectF::new(1600.0, 300.0, 100.0, 200.0), 80.0, 114.0, 97.0),
];
const INSPECT_DARK: [RectF; 3] =
    [RectF::new(0.0, 0.0, 1632.0, 76.0), RectF::new(0.0, 270.0, 192.0, 702.0), RectF::new(0.0, 1004.0, 768.0, 76.0)];
const INSPECT_CARD: RectF = RectF::new(691.0, 162.0, 538.0, 756.0);
pub const INSPECT_DARK_MAX: f32 = 14.0;
pub const INSPECT_CARD_MIN: f32 = 50.0;
pub const COLOUR_TOLERANCE: f32 = 32.0;

pub fn evaluate(frame: &ScreenFrame<'_>) -> ScreenGate {
    let t = RefTransform::for_frame(frame.width, frame.height);
    let mut detail = String::new();
    let mut banner = true;
    for p in &REWARD_BANNER {
        banner &= check(frame, &t, p, &mut detail);
    }
    let mut dark = true;
    for p in &REWARD_BACKDROP {
        let (r, g, b) = mean_rgb(frame, t.to_pixel_rect(p.1));
        let v = (r + g + b) / 3.0;
        let _ = write!(detail, "{}={:.0} ", p.0, v);
        dark &= v <= p.2;
    }
    let mut rug = 0;
    for p in &SHOP_RUG {
        if check(frame, &t, p, &mut detail) {
            rug += 1;
        }
    }
    let inspect = is_inspect_view(frame, &t, &mut detail);
    ScreenGate {
        reward_likely: banner && dark && !inspect,
        shop_likely: rug >= 2 && !inspect,
        detail: detail.trim_end().to_string(),
        inspect_likely: inspect,
    }
}

fn is_inspect_view(frame: &ScreenFrame<'_>, t: &RefTransform, detail: &mut String) -> bool {
    let mut darkest = 0f32;
    for b in &INSPECT_DARK {
        let (r, g, bl) = mean_rgb(frame, t.to_pixel_rect(*b));
        darkest = darkest.max((r + g + bl) / 3.0);
        if darkest > INSPECT_DARK_MAX {
            return false;
        }
    }
    let (cr, cg, cb) = mean_rgb(frame, t.to_pixel_rect(INSPECT_CARD));
    let card = (cr + cg + cb) / 3.0;
    let _ = write!(detail, "inspect={darkest:.0}/{card:.0} ");
    card >= INSPECT_CARD_MIN
}

fn check(frame: &ScreenFrame<'_>, t: &RefTransform, p: &ColourProbe, detail: &mut String) -> bool {
    let (r, g, b) = mean_rgb(frame, t.to_pixel_rect(p.1));
    let d = (r - p.2).abs().max((g - p.3).abs().max((b - p.4).abs()));
    let _ = write!(detail, "{}={:.0} ", p.0, d);
    d <= COLOUR_TOLERANCE
}

/// Mean colour of a pixel rectangle, reading every other pixel of large rectangles.
pub fn mean_rgb(frame: &ScreenFrame<'_>, rect: PixelRect) -> (f32, f32, f32) {
    let rect = rect.intersect(frame.width, frame.height);
    if rect.width == 0 || rect.height == 0 {
        return (0.0, 0.0, 0.0);
    }
    let step = if rect.width >= 8 && rect.height >= 8 { 2 } else { 1 };
    let px = frame.bgra;
    let (mut r, mut g, mut b, mut n) = (0i64, 0i64, 0i64, 0i64);
    let mut y = rect.y;
    while y < rect.bottom() {
        let mut p = (y * frame.stride + rect.x * 4) as usize;
        let mut x = rect.x;
        while x < rect.right() {
            b += px[p] as i64;
            g += px[p + 1] as i64;
            r += px[p + 2] as i64;
            n += 1;
            x += step;
            p += 4 * step as usize;
        }
        y += step;
    }
    (r as f32 / n as f32, g as f32 / n as f32, b as f32 / n as f32)
}
