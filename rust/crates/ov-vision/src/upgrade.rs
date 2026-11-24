//! Port of UpgradeDetector (CardRecognizer.cs): upgrade-green title pixels and tooltip slate over the title.

use crate::geometry::RefTransform;
use crate::integral::{IntegralImage, RowSink};
use crate::layout::card_geometry;

pub fn is_upgrade_green(r: f32, g: f32, b: f32) -> bool {
    g >= 50.0 && r <= 0.62 * g && b <= 0.35 * g
}

pub fn is_tooltip_slate(r: f32, g: f32, b: f32) -> bool {
    (18.0..=56.0).contains(&r) && (3.0..=20.0).contains(&(g - r)) && (8.0..=28.0).contains(&(b - r))
}

struct TitleCounts {
    green: i32,
    slate: i32,
}

impl RowSink for TitleCounts {
    fn row(&mut self, rgb: &[f32]) {
        for px in rgb.as_chunks::<3>().0 {
            if is_upgrade_green(px[0], px[1], px[2]) {
                self.green += 1;
            } else if is_tooltip_slate(px[0], px[1], px[2]) {
                self.slate += 1;
            }
        }
    }
}

/// Share of the title box in upgrade green, and share in tooltip slate.
pub fn title_evidence(img: &IntegralImage, t: &RefTransform, cx: f32, cy: f32, scale: f32) -> (f32, f32) {
    let title = t.to_pixels(card_geometry::place(card_geometry::TITLE, cx, cy, scale));
    let gw = ((title.width / img.step as f32) as i32).clamp(8, 400);
    let gh = ((title.height / img.step as f32) as i32).clamp(4, 120);
    let mut row = vec![0f32; gw as usize * 3];
    let mut counts = TitleCounts { green: 0, slate: 0 };
    img.sample_grid_rows(title, gw, gh, &mut row, &mut counts);
    let n = (gw * gh) as f32;
    (counts.green as f32 / n, counts.slate as f32 / n)
}
