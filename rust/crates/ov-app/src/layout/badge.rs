//! Badge placement (C# `Views/BadgeLayout.cs`). Every mark is sized from something the GAME draws (a card's width,
//! the removal coin's width), not from the Windows DPI, so it keeps its size relative to the game at FHD and 4K. The
//! DPI only converts physical pixels to layer points at the end (`to_layer_points`). egui points = WPF DIPs.

/// A rectangle in physical pixels (screen or captured frame).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PxBox {
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
}

impl PxBox {
    pub const fn new(x: i32, y: i32, w: i32, h: i32) -> Self {
        Self { x, y, w, h }
    }
    pub fn right(&self) -> i32 {
        self.x + self.w
    }
}

/// A rectangle in points relative to the layer's top-left.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct PtRect {
    pub x: f64,
    pub y: f64,
    pub w: f64,
    pub h: f64,
}

impl PtRect {
    pub const fn new(x: f64, y: f64, w: f64, h: f64) -> Self {
        Self { x, y, w, h }
    }
    pub fn right(&self) -> f64 {
        self.x + self.w
    }
    pub fn bottom(&self) -> f64 {
        self.y + self.h
    }
    pub fn intersects(&self, o: &PtRect) -> bool {
        self.x < o.right() && o.x < self.right() && self.y < o.bottom() && o.y < self.bottom()
    }
    /// Fully inside a `width` x `height` area, with a margin.
    pub fn inside_area(&self, width: f64, height: f64, margin: f64) -> bool {
        self.x >= margin && self.y >= margin && self.right() <= width - margin && self.bottom() <= height - margin
    }
}

/// One placed badge: its box and the scale of its design (the 170x40 badge).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlacedBadge {
    pub rect: PtRect,
    pub scale: f64,
}

/// A stack of reason chips under a card.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlacedChips {
    pub rect: PtRect,
    pub scale: f64,
    pub count: usize,
}

/// What to draw for one card besides its badge.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct CardMarks {
    /// Reason chips (0..2) under the card.
    pub chip_count: usize,
    /// A gold "추천!" / "구매!" ribbon on the badge.
    pub ribbon: bool,
}

/// Everything the badge layer draws for one frame, in layer points.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct BadgeFrame {
    /// One per input card, same order.
    pub badges: Vec<PlacedBadge>,
    /// The "스킵 추천!" pill under the card row.
    pub skip: Option<PlacedBadge>,
    /// Debug outlines of the recognised cards (empty unless asked for).
    pub card_outlines: Vec<PtRect>,
    /// Debug outlines of the shop price labels.
    pub price_outlines: Vec<PtRect>,
    /// One per input card.
    pub chips: Vec<Option<PlacedChips>>,
    /// One per input card.
    pub ribbons: Vec<Option<PlacedBadge>>,
    /// The "제거 추천: ..." marker over the shop's card-removal service.
    pub removal: Option<PlacedBadge>,
}

pub const BASE_WIDTH: f64 = 170.0;
pub const BASE_HEIGHT: f64 = 40.0;
pub const RIBBON_WIDTH: f64 = 84.0;
pub const RIBBON_HEIGHT: f64 = 30.0;
pub const CHIP_HEIGHT: f64 = 22.0;
pub const CHIP_GAP: f64 = 3.0;
/// Chips use at most this fraction of the card width, so stacks of neighbouring cards never touch.
pub const CHIP_WIDTH_OF_CARD: f64 = 0.96;
pub const SKIP_WIDTH: f64 = 300.0;
pub const SKIP_HEIGHT: f64 = 40.0;
pub const REMOVAL_WIDTH: f64 = 230.0;
pub const REMOVAL_HEIGHT: f64 = 34.0;
pub const REMOVAL_COIN_DESIGN_WIDTH: f64 = 170.0;
/// Bottom of the reward screen's "넘기기" button in the game's 1920x1080 reference space (y 884..957).
pub const REWARD_SKIP_BUTTON_BOTTOM_REF: f64 = 957.0;
const MARGIN: f64 = 2.0;

/// Physical screen rect -> points relative to the layer (subtract the layer origin, divide by the DPI scale).
pub fn to_layer_points(screen: PxBox, layer: PxBox, dpi_scale: f64) -> PtRect {
    PtRect::new(
        f64::from(screen.x - layer.x) / dpi_scale,
        f64::from(screen.y - layer.y) / dpi_scale,
        f64::from(screen.w) / dpi_scale,
        f64::from(screen.h) / dpi_scale,
    )
}

/// Badge scale for a card: the badge is about 72 % of the card's width.
pub fn scale_for(card_width: f64) -> f64 {
    (card_width * 0.72 / BASE_WIDTH).clamp(0.5, 3.0)
}

/// A y of the game's 1920x1080 reference space in layer points (uniform scale `min(W/1920, H/1080)`, centred).
pub fn ref_y_to_layer_points(ref_y: f64, layer: PxBox, dpi_scale: f64) -> f64 {
    let s = (f64::from(layer.w) / 1920.0).min(f64::from(layer.h) / 1080.0);
    let offset_y = (f64::from(layer.h) - 1080.0 * s) / 2.0;
    (offset_y + ref_y * s) / dpi_scale
}

fn clamp_x(left: f64, width: f64, area_width: f64) -> f64 {
    left.clamp(MARGIN, MARGIN.max(area_width - width - MARGIN))
}

/// Top-left of a badge: centred above the card; below it when there is no room above; inside its top edge last.
pub fn place(card: PtRect, width: f64, height: f64, area_width: f64, area_height: f64, gap: f64) -> (f64, f64) {
    let left = clamp_x(card.x + (card.w - width) / 2.0, width, area_width);
    let mut top = card.y - height - gap;
    if top < MARGIN {
        top = card.bottom() + gap;
        if top + height > area_height - MARGIN {
            top = MARGIN.max(card.y + MARGIN);
        }
    }
    (left, top)
}

/// The ribbon: on the badge's right end sticking out above it; fully inside that end when that would leave the screen
/// or touch something in `avoid`.
pub fn place_ribbon(badge: PtRect, scale: f64, avoid: &[PtRect], area_width: f64, area_height: f64) -> PtRect {
    let (w, h) = (RIBBON_WIDTH * scale, RIBBON_HEIGHT * scale);
    let tag = PtRect::new(badge.right() - w + 4.0 * scale, badge.y - h / 2.0, w, h);
    if tag.inside_area(area_width, area_height, MARGIN) && !avoid.iter().any(|a| tag.intersects(a)) {
        return tag;
    }
    PtRect::new(badge.right() - w - 3.0 * scale, badge.y + (badge.h - h) / 2.0, w, h)
}

/// The reason chips: centred just below the card; inside its bottom edge when below leaves the screen or hits `avoid`.
pub fn place_chips(card: PtRect, count: usize, scale: f64, avoid: &[PtRect], area_width: f64, area_height: f64) -> PtRect {
    let w = card.w * CHIP_WIDTH_OF_CARD;
    let n = count as f64;
    let h = n * CHIP_HEIGHT * scale + (n - 1.0).max(0.0) * CHIP_GAP * scale;
    let left = clamp_x(card.x + (card.w - w) / 2.0, w, area_width);
    let below = PtRect::new(left, card.bottom() + 7.0 * scale, w, h);
    if below.inside_area(area_width, area_height, MARGIN) && !avoid.iter().any(|a| below.intersects(a)) {
        return below;
    }
    PtRect::new(left, MARGIN.max(card.bottom() - h - 6.0 * scale), w, h)
}

/// "제거 추천" marker: above the removal coin (room for its ~20 % hover growth), else below its price label.
pub fn place_removal(coin: PtRect, multiplier: f64, area_width: f64, area_height: f64) -> PlacedBadge {
    let scale = (coin.w / REMOVAL_COIN_DESIGN_WIDTH).clamp(0.5, 3.0) * multiplier;
    let (w, h) = (REMOVAL_WIDTH * scale, REMOVAL_HEIGHT * scale);
    let left = clamp_x(coin.x + (coin.w - w) / 2.0, w, area_width);
    let mut top = coin.y - h - (0.15 * coin.h + 4.0 * scale);
    if top < MARGIN {
        top = (coin.bottom() + 0.55 * coin.h).min(area_height - h - MARGIN);
    }
    PlacedBadge { rect: PtRect::new(left, top, w, h), scale }
}

/// Inputs of one frame (C# `BadgeLayout.Compute` parameters).
#[derive(Debug, Clone, Default)]
pub struct FrameInput<'a> {
    /// Full card bounds, physical screen pixels.
    pub cards: &'a [PxBox],
    /// Price-label bounds per card (None: none / not a shop); may be shorter than `cards`.
    pub prices: &'a [Option<PxBox>],
    /// The layer = the game's client area, physical screen pixels.
    pub layer: PxBox,
    /// Monitor DPI / 96.
    pub dpi_scale: f64,
    /// User UI scale relative to auto (1 = auto).
    pub multiplier: f64,
    pub skip: bool,
    pub outlines: bool,
    /// Per card: chips and ribbon (shorter: nothing extra).
    pub marks: &'a [CardMarks],
    /// The shop's removal coin, physical screen pixels, when its marker should show.
    pub removal: Option<PxBox>,
}

/// Lays out one frame: a badge per card, ribbons, chip stacks, the skip pill, the removal marker and debug outlines.
pub fn compute(inp: &FrameInput<'_>) -> BadgeFrame {
    let (layer, dpi) = (inp.layer, inp.dpi_scale);
    let area_w = f64::from(layer.w) / dpi;
    let area_h = f64::from(layer.h) / dpi;
    let n = inp.cards.len();
    let cards: Vec<PtRect> = inp.cards.iter().map(|c| to_layer_points(*c, layer, dpi)).collect();
    let prices: Vec<Option<PtRect>> = (0..n).map(|i| inp.prices.get(i).copied().flatten().map(|p| to_layer_points(p, layer, dpi))).collect();
    let marks_of = |i: usize| inp.marks.get(i).copied().unwrap_or_default();

    let badges: Vec<PlacedBadge> = cards
        .iter()
        .map(|card| {
            let scale = scale_for(card.w) * inp.multiplier;
            let (w, h) = (BASE_WIDTH * scale, BASE_HEIGHT * scale);
            let (left, top) = place(*card, w, h, area_w, area_h, 6.0 * scale);
            PlacedBadge { rect: PtRect::new(left, top, w, h), scale }
        })
        .collect();

    // What a card's extra marks must never cover: every OTHER card, badge and price label.
    let others_of = |i: usize| -> Vec<PtRect> {
        let mut v = Vec::new();
        for j in (0..n).filter(|&j| j != i) {
            v.push(cards[j]);
            v.push(badges[j].rect);
            if let Some(p) = prices[j] {
                v.push(p);
            }
        }
        v
    };

    let mut ribbons: Vec<Option<PlacedBadge>> = vec![None; n];
    for i in (0..n).filter(|&i| marks_of(i).ribbon) {
        let mut avoid = others_of(i);
        avoid.extend(prices[i]); // its own price must stay readable
        let r = place_ribbon(badges[i].rect, badges[i].scale, &avoid, area_w, area_h);
        ribbons[i] = Some(PlacedBadge { rect: r, scale: badges[i].scale });
    }

    // One chip scale for the row (the smallest badge scale): a hovered card is bigger and sits lower.
    let mut chips: Vec<Option<PlacedChips>> = vec![None; n];
    let with_chips: Vec<usize> = (0..n).filter(|&i| marks_of(i).chip_count > 0).collect();
    let chip_scale = with_chips.iter().map(|&i| badges[i].scale).fold(f64::INFINITY, f64::min);
    let chip_scale = if chip_scale.is_finite() { chip_scale } else { 1.0 };
    let mut placed: Vec<PtRect> = Vec::new();
    for &i in &with_chips {
        let count = marks_of(i).chip_count.min(2);
        let mut avoid = others_of(i);
        avoid.extend(ribbons.iter().flatten().map(|r| r.rect));
        avoid.extend(placed.iter().copied());
        let rect = place_chips(cards[i], count, chip_scale, &avoid, area_w, area_h);
        chips[i] = Some(PlacedChips { rect, scale: chip_scale, count });
        placed.push(rect);
    }

    let skip = (inp.skip && n > 0).then(|| {
        let l = cards.iter().map(|c| c.x).fold(f64::INFINITY, f64::min);
        let r = cards.iter().map(|c| c.right()).fold(f64::NEG_INFINITY, f64::max);
        let b = cards.iter().map(|c| c.bottom()).fold(f64::NEG_INFINITY, f64::max);
        let scale = badges.iter().map(|x| x.scale).fold(f64::INFINITY, f64::min);
        let (w, h) = (SKIP_WIDTH * scale, SKIP_HEIGHT * scale);
        let left = clamp_x((l + r - w) / 2.0, w, area_w);
        // Under the cards, their chips and the game's own skip button: whichever is lowest.
        let mut top = (b + 12.0 * scale).max(ref_y_to_layer_points(REWARD_SKIP_BUTTON_BOTTOM_REF, layer, dpi) + 8.0 * scale);
        for c in chips.iter().flatten() {
            top = top.max(c.rect.bottom() + 6.0 * scale);
        }
        top = top.min(area_h - h - MARGIN);
        PlacedBadge { rect: PtRect::new(left, top, w, h), scale }
    });

    let removal = inp.removal.map(|coin| place_removal(to_layer_points(coin, layer, dpi), inp.multiplier, area_w, area_h));

    BadgeFrame {
        badges,
        skip,
        card_outlines: if inp.outlines { cards.clone() } else { Vec::new() },
        price_outlines: if inp.outlines { prices.iter().flatten().copied().collect() } else { Vec::new() },
        chips,
        ribbons,
        removal,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn close(a: f64, b: f64, what: &str) {
        assert!((a - b).abs() < 1e-6, "{what}: {a} != {b}");
    }

    const REWARD_4K: [PxBox; 3] = [PxBox::new(985, 893, 474, 670), PxBox::new(1683, 893, 474, 670), PxBox::new(2381, 893, 474, 670)];
    const LAYER_4K: PxBox = PxBox::new(0, 0, 3840, 2160);

    fn frame(dpi: f64, mult: f64, skip: bool, outlines: bool, prices: &[Option<PxBox>]) -> BadgeFrame {
        compute(&FrameInput { cards: &REWARD_4K, prices, layer: LAYER_4K, dpi_scale: dpi, multiplier: mult, skip, outlines, ..Default::default() })
    }

    #[test]
    fn single_badge_placement() {
        assert_eq!(to_layer_points(PxBox::new(400, 350, 300, 450), PxBox::new(100, 50, 3840, 2160), 1.5), PtRect::new(200.0, 200.0, 200.0, 300.0));
        for (w, want) in [(170.0 / 0.72, 1.0), (10.0, 0.5), (5000.0, 3.0)] {
            close(scale_for(w), want, "scale");
        }
        assert_eq!(place(PtRect::new(500.0, 300.0, 200.0, 300.0), 170.0, 40.0, 1920.0, 1080.0, 6.0), (515.0, 254.0), "above");
        assert_eq!(place(PtRect::new(500.0, 10.0, 200.0, 300.0), 170.0, 40.0, 1920.0, 1080.0, 6.0).1, 316.0, "below");
        assert_eq!(place(PtRect::new(500.0, 10.0, 200.0, 1060.0), 170.0, 40.0, 1920.0, 1080.0, 6.0).1, 12.0, "inside");
        assert_eq!(place(PtRect::new(1850.0, 300.0, 200.0, 300.0), 170.0, 40.0, 1920.0, 1080.0, 6.0).0, 1920.0 - 170.0 - 2.0, "right edge");
        assert_eq!(place(PtRect::new(-150.0, 300.0, 200.0, 300.0), 170.0, 40.0, 1920.0, 1080.0, 6.0).0, 2.0, "left edge");
    }

    #[test]
    fn reward_badges_sit_above_their_cards_at_any_dpi() {
        for dpi in [1.0, 1.5, 2.0] {
            let f = frame(dpi, 1.0, false, false, &[]);
            assert_eq!(f.badges.len(), 3);
            for i in 0..3 {
                let (b, card) = (f.badges[i].rect, to_layer_points(REWARD_4K[i], LAYER_4K, dpi));
                assert!(b.bottom() <= card.y, "dpi {dpi} badge {i} not above its card");
                close(b.x + b.w / 2.0, card.x + card.w / 2.0, "centred");
                for j in (0..3).filter(|&j| j != i) {
                    assert!(!b.intersects(&f.badges[j].rect), "dpi {dpi}: badge {i} overlaps {j}");
                }
                close(b.w * dpi, 0.72 * f64::from(REWARD_4K[i].w), "same physical size");
            }
            assert!(f.card_outlines.is_empty() && f.skip.is_none());
        }
        let (auto, big) = (frame(1.5, 1.0, false, false, &[]), frame(1.5, 1.25, false, false, &[]));
        close(big.badges[0].scale, auto.badges[0].scale * 1.25, "multiplier scale");
        close(big.badges[0].rect.w, auto.badges[0].rect.w * 1.25, "multiplier width");
    }

    #[test]
    fn skip_pill_sits_below_the_games_skip_button() {
        let f = frame(1.5, 1.0, true, false, &[]);
        let skip = f.skip.expect("skip pill");
        let min_scale = f.badges.iter().map(|b| b.scale).fold(f64::INFINITY, f64::min);
        close(skip.scale, min_scale, "smallest scale");
        // "넘기기" ends at reference y 957 = 1914 px at 4K = 1276 points at 150 %.
        close(skip.rect.y, 1914.0 / 1.5 + 8.0 * skip.scale, "top");
        close(skip.rect.x + skip.rect.w / 2.0, f64::from(REWARD_4K[0].x + REWARD_4K[2].right()) / 2.0 / 1.5, "centre");
        assert!(skip.rect.inside_area(3840.0 / 1.5, 2160.0 / 1.5, 0.0));
        let none = compute(&FrameInput { layer: LAYER_4K, dpi_scale: 1.5, multiplier: 1.0, skip: true, outlines: true, ..Default::default() });
        assert!(none.badges.is_empty() && none.skip.is_none() && none.card_outlines.is_empty());
    }

    #[test]
    fn outlines_cover_cards_and_present_prices_without_moving_badges() {
        let prices = [Some(PxBox::new(1000, 1580, 400, 60)), None, Some(PxBox::new(2400, 1580, 400, 60))];
        let f = frame(1.5, 1.0, false, true, &prices);
        let want: Vec<PtRect> = REWARD_4K.iter().map(|c| to_layer_points(*c, LAYER_4K, 1.5)).collect();
        assert_eq!(f.card_outlines, want);
        assert_eq!(f.price_outlines, vec![to_layer_points(prices[0].unwrap(), LAYER_4K, 1.5), to_layer_points(prices[2].unwrap(), LAYER_4K, 1.5)]);
        assert_eq!(f.badges, frame(1.5, 1.0, false, false, &prices).badges);
    }
}
