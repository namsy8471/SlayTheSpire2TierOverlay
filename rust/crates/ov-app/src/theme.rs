//! The overlay's look: a dark-fantasy palette (deep navy, gold trim) and the shapes drawn with it — the panel frame
//! with corner diamonds, dividers, section heads, tabs, rank seals and the plaque used for every mark on the game.
//! Everything is drawn in code; no game art is used.

use egui::{Color32, CornerRadius, FontId, Mesh, Painter, Pos2, Rect, Response, Sense, Shape, Stroke, StrokeKind, Ui, Vec2};

pub const GOLD: Color32 = Color32::from_rgb(233, 196, 106);
pub const GOLD_DEEP: Color32 = Color32::from_rgb(184, 145, 63);
pub const GOLD_DARK: Color32 = Color32::from_rgb(94, 74, 34);
pub const GOLD_PALE: Color32 = Color32::from_rgb(241, 220, 166);
pub const SILVER: Color32 = Color32::from_rgb(206, 211, 222);
pub const TEXT: Color32 = Color32::from_rgb(207, 200, 184);
pub const DIM: Color32 = Color32::from_rgb(150, 143, 126);
pub const WARN: Color32 = Color32::from_rgb(255, 170, 60);
const BG_TOP: Color32 = Color32::from_rgb(28, 32, 48);
const BG_BOTTOM: Color32 = Color32::from_rgb(14, 16, 24);
const PLATE: Color32 = Color32::from_rgb(24, 27, 40);
const PLATE_ON_TOP: Color32 = Color32::from_rgb(58, 47, 22);
const PLATE_ON_BOTTOM: Color32 = Color32::from_rgb(36, 29, 14);

/// Room the frame's border and corner diamonds take on each side; content margins start inside it.
pub const FRAME_INSET: f32 = 9.0;

/// The panel's widgets in the palette (buttons, checkboxes, scroll bars, text) for the Status tab and the rest.
pub fn apply_visuals(ctx: &egui::Context) {
    let mut v = egui::Visuals::dark();
    v.override_text_color = Some(TEXT);
    v.panel_fill = Color32::TRANSPARENT;
    v.window_fill = BG_BOTTOM;
    v.extreme_bg_color = BG_BOTTOM;
    v.faint_bg_color = PLATE;
    v.hyperlink_color = GOLD;
    v.selection.bg_fill = PLATE_ON_TOP;
    v.selection.stroke = Stroke::new(1.0, GOLD);
    let w = &mut v.widgets;
    w.noninteractive.bg_stroke = Stroke::new(1.0, GOLD_DARK);
    w.noninteractive.fg_stroke = Stroke::new(1.0, TEXT);
    for (s, stroke, fill) in [(&mut w.inactive, GOLD_DARK, PLATE), (&mut w.hovered, GOLD_DEEP, Color32::from_rgb(34, 36, 50)), (&mut w.active, GOLD, PLATE_ON_TOP), (&mut w.open, GOLD_DEEP, PLATE)] {
        s.bg_fill = fill;
        s.weak_bg_fill = fill;
        s.bg_stroke = Stroke::new(1.0, stroke);
        s.corner_radius = CornerRadius::same(2);
    }
    w.inactive.fg_stroke = Stroke::new(1.0, GOLD_PALE);
    w.hovered.fg_stroke = Stroke::new(1.5, GOLD);
    w.active.fg_stroke = Stroke::new(1.5, GOLD);
    ctx.set_visuals(v);
}

fn with_alpha(c: Color32, a: u8) -> Color32 {
    Color32::from_rgba_unmultiplied(c.r(), c.g(), c.b(), ((u16::from(c.a()) * u16::from(a)) / 255) as u8)
}

fn vertical_gradient(p: &Painter, r: Rect, top: Color32, bottom: Color32) {
    let mut m = Mesh::default();
    m.colored_vertex(r.left_top(), top);
    m.colored_vertex(r.right_top(), top);
    m.colored_vertex(r.right_bottom(), bottom);
    m.colored_vertex(r.left_bottom(), bottom);
    m.add_triangle(0, 1, 2);
    m.add_triangle(0, 2, 3);
    p.add(Shape::mesh(m));
}

/// A filled diamond (a square standing on a corner) of half-diagonal `r`.
pub fn diamond(p: &Painter, c: Pos2, r: f32, fill: Color32, stroke: Stroke) {
    let pts = vec![Pos2::new(c.x, c.y - r), Pos2::new(c.x + r, c.y), Pos2::new(c.x, c.y + r), Pos2::new(c.x - r, c.y)];
    p.add(Shape::convex_polygon(pts, fill, stroke));
}

/// A four-pointed star, the "pick" sign: two thin crossed diamonds.
pub fn star(p: &Painter, c: Pos2, r: f32, color: Color32) {
    let k = r * 0.3;
    let at = |x: f32, y: f32| Pos2::new(c.x + x, c.y + y);
    p.add(Shape::convex_polygon(vec![at(0.0, -r), at(k, 0.0), at(0.0, r), at(-k, 0.0)], color, Stroke::NONE));
    p.add(Shape::convex_polygon(vec![at(-r, 0.0), at(0.0, -k), at(r, 0.0), at(0.0, k)], color, Stroke::NONE));
}

/// The panel background: navy gradient, a gold outer line, a thin dark-gold inner line and a diamond on each corner.
/// `alpha` is the user's panel opacity.
pub fn panel_frame(p: &Painter, outer: Rect, alpha: u8) {
    let r = outer.shrink(FRAME_INSET);
    vertical_gradient(p, r, with_alpha(BG_TOP, alpha), with_alpha(BG_BOTTOM, alpha));
    p.rect_stroke(r, CornerRadius::same(3), Stroke::new(2.0, GOLD_DEEP), StrokeKind::Inside);
    p.rect_stroke(r.shrink(5.0), CornerRadius::ZERO, Stroke::new(1.0, GOLD_DARK), StrokeKind::Inside);
    for c in [r.left_top(), r.right_top(), r.left_bottom(), r.right_bottom()] {
        diamond(p, c, 8.0, BG_BOTTOM, Stroke::new(2.0, Color32::from_rgb(216, 178, 90)));
        diamond(p, c, 3.5, Color32::from_rgb(216, 178, 90), Stroke::NONE);
    }
}

/// The title: centred gold text between two lines that fade outwards. Dragging it moves the panel.
pub fn title(ui: &mut Ui, text: &str, right_reserve: f32) -> Response {
    let h = 24.0;
    let (rect, resp) = ui.allocate_exact_size(Vec2::new(ui.available_width() - right_reserve, h), Sense::drag());
    let p = ui.painter();
    let font = crate::fonts::title_font(18.0);
    let galley = p.layout_no_wrap(text.to_owned(), font, GOLD);
    let tw = galley.size().x;
    let c = rect.center();
    p.galley(Pos2::new(c.x - tw / 2.0, c.y - galley.size().y / 2.0), galley, GOLD);
    let gap = 10.0;
    for (from, to) in [(rect.left() + 4.0, c.x - tw / 2.0 - gap), (c.x + tw / 2.0 + gap, rect.right() - 4.0)] {
        if to > from {
            let fade_left = from < c.x;
            let mut m = Mesh::default();
            let (a, b) = if fade_left { (Color32::TRANSPARENT, GOLD_DEEP) } else { (GOLD_DEEP, Color32::TRANSPARENT) };
            let y = c.y;
            m.colored_vertex(Pos2::new(from, y - 0.5), a);
            m.colored_vertex(Pos2::new(to, y - 0.5), b);
            m.colored_vertex(Pos2::new(to, y + 0.5), b);
            m.colored_vertex(Pos2::new(from, y + 0.5), a);
            m.add_triangle(0, 1, 2);
            m.add_triangle(0, 2, 3);
            p.add(Shape::mesh(m));
        }
    }
    resp
}

/// A small square button in the title row (collapse/expand).
pub fn small_button(ui: &mut Ui, text: &str) -> Response {
    let (rect, resp) = ui.allocate_exact_size(Vec2::new(20.0, 18.0), Sense::click());
    let p = ui.painter();
    let stroke = if resp.hovered() { GOLD } else { GOLD_DARK };
    p.rect_filled(rect, CornerRadius::same(2), PLATE);
    p.rect_stroke(rect, CornerRadius::same(2), Stroke::new(1.0, stroke), StrokeKind::Inside);
    p.text(rect.center(), egui::Align2::CENTER_CENTER, text, FontId::proportional(11.0), GOLD_DEEP);
    resp
}

/// One row of equal-width tab plates; returns the clicked index.
pub fn tabs(ui: &mut Ui, labels: &[&str], selected: usize) -> Option<usize> {
    let gap = 4.0;
    let n = labels.len() as f32;
    let w = (ui.available_width() - gap * (n - 1.0)) / n;
    let mut clicked = None;
    ui.horizontal(|ui| {
        ui.spacing_mut().item_spacing.x = gap;
        for (i, label) in labels.iter().enumerate() {
            let (rect, resp) = ui.allocate_exact_size(Vec2::new(w, 26.0), Sense::click());
            let p = ui.painter();
            let on = i == selected;
            if on {
                vertical_gradient(p, rect, PLATE_ON_TOP, PLATE_ON_BOTTOM);
            } else {
                p.rect_filled(rect, CornerRadius::same(2), PLATE);
            }
            let stroke = if on { Color32::from_rgb(216, 178, 90) } else if resp.hovered() { GOLD_DEEP } else { Color32::from_rgb(59, 52, 36) };
            p.rect_stroke(rect, CornerRadius::same(2), Stroke::new(1.0, stroke), StrokeKind::Inside);
            let (font, color) = if on { (FontId::proportional(14.5), Color32::from_rgb(255, 226, 160)) } else { (FontId::proportional(14.0), Color32::from_rgb(169, 157, 132)) };
            p.text(rect.center(), egui::Align2::CENTER_CENTER, *label, font, color);
            if resp.clicked() {
                clicked = Some(i);
            }
        }
    });
    clicked
}

/// A horizontal dark-gold line with a gold diamond in the middle.
pub fn divider(ui: &mut Ui) {
    let (rect, _) = ui.allocate_exact_size(Vec2::new(ui.available_width(), 14.0), Sense::hover());
    let p = ui.painter();
    let c = rect.center();
    p.line_segment([Pos2::new(rect.left(), c.y), Pos2::new(c.x - 7.0, c.y)], Stroke::new(1.0, GOLD_DARK));
    p.line_segment([Pos2::new(c.x + 7.0, c.y), Pos2::new(rect.right(), c.y)], Stroke::new(1.0, GOLD_DARK));
    diamond(p, c, 4.0, Color32::from_rgb(216, 178, 90), Stroke::NONE);
}

/// A section head: a small gold diamond, then the text in pale gold.
pub fn section_head(ui: &mut Ui, text: &str) {
    ui.horizontal(|ui| {
        let (rect, _) = ui.allocate_exact_size(Vec2::new(10.0, 16.0), Sense::hover());
        diamond(ui.painter(), rect.center(), 3.5, Color32::from_rgb(216, 178, 90), Stroke::NONE);
        ui.label(egui::RichText::new(text).font(crate::fonts::title_font(16.0)).color(GOLD_PALE));
    });
}

/// A round seal holding the rank (or a star on the pick).
pub fn rank_seal(ui: &mut Ui, rank: Option<usize>) {
    let (rect, _) = ui.allocate_exact_size(Vec2::splat(22.0), Sense::hover());
    let p = ui.painter();
    let c = rect.center();
    match rank {
        None => {
            p.circle_filled(c, 10.5, GOLD);
            star(p, c, 7.0, Color32::from_rgb(26, 20, 8));
        }
        Some(n) => {
            p.circle_stroke(c, 10.0, Stroke::new(1.0, GOLD_DARK));
            p.text(c, egui::Align2::CENTER_CENTER, n.to_string(), FontId::proportional(12.0), Color32::from_rgb(169, 157, 132));
        }
    }
}

/// How a row is framed: the pick gets a gold-tinted plate with a bright bar on the left.
pub fn row_frame(picked: bool) -> egui::Frame {
    let f = egui::Frame::new().inner_margin(egui::Margin::symmetric(8, 5)).corner_radius(CornerRadius::same(2));
    if picked {
        f.fill(Color32::from_rgba_unmultiplied(184, 145, 63, 48)).stroke(Stroke::new(1.0, Color32::from_rgb(122, 98, 48)))
    } else {
        f
    }
}

pub fn row_bar(ui: &Ui, rect: Rect) {
    ui.painter().rect_filled(Rect::from_min_size(rect.min, Vec2::new(3.0, rect.height())), CornerRadius::ZERO, GOLD);
}

/// The kinds of plaque drawn on the game.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Plaque {
    /// The advised choice: gold text, gold border, star.
    Pick,
    /// The advised choice when the call is close: silver.
    Close,
    /// Any other mark (a card's AI rank): pale text, dark-gold border.
    Plain,
}

/// A dark plaque with pointed ends, a double border and the text centred (a star before it on a pick).
pub fn plaque(p: &Painter, r: Rect, text: &str, kind: Plaque, size: f32) {
    let (edge, fg) = match kind {
        Plaque::Pick => (Color32::from_rgb(216, 178, 90), GOLD),
        Plaque::Close => (Color32::from_rgb(170, 176, 191), SILVER),
        Plaque::Plain => (GOLD_DEEP, Color32::from_rgb(222, 214, 196)),
    };
    let tip = (r.height() * 0.3).min(r.width() * 0.15);
    let hex = |r: Rect, tip: f32| {
        vec![
            Pos2::new(r.left() + tip, r.top()),
            Pos2::new(r.right() - tip, r.top()),
            Pos2::new(r.right(), r.center().y),
            Pos2::new(r.right() - tip, r.bottom()),
            Pos2::new(r.left() + tip, r.bottom()),
            Pos2::new(r.left(), r.center().y),
        ]
    };
    let shadow = r.translate(Vec2::new(0.0, 2.0));
    p.add(Shape::convex_polygon(hex(shadow, tip), Color32::from_black_alpha(110), Stroke::NONE));
    p.add(Shape::convex_polygon(hex(r, tip), Color32::from_rgba_unmultiplied(18, 20, 28, 238), Stroke::new(1.5, edge)));
    let inner = r.shrink(3.0);
    if inner.width() > 2.0 * tip && inner.height() > 4.0 {
        p.add(Shape::closed_line(hex(inner, (tip - 1.5).max(1.0)), Stroke::new(1.0, with_alpha(edge, 70))));
    }
    let font = FontId::proportional(size);
    let galley = p.layout_no_wrap(text.to_owned(), font, fg);
    let star_w = if kind == Plaque::Plain { 0.0 } else { size * 0.9 };
    let total = galley.size().x + star_w;
    let x0 = r.center().x - total / 2.0;
    if star_w > 0.0 {
        star(p, Pos2::new(x0 + star_w * 0.4, r.center().y), size * 0.38, fg);
    }
    p.galley(Pos2::new(x0 + star_w, r.center().y - galley.size().y / 2.0), galley, fg);
}
