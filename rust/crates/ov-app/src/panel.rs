//! Drawing the panel tabs and the layer marks from [`Live`] (the C# PanelWindow sections and BadgeWindow).

use egui::{Color32, CornerRadius, Pos2, Rect, RichText, Stroke, Vec2};
use ov_core::loc::{self, t};
use ov_live::brain::DecisionView;
use ov_live::offer::PartialVerdict;
use ov_live::present::{self, Section};
use ov_live::recognition::Rect as SRect;
use ov_live::request::{Shown, Target};

use crate::layout::badge::{self, CardMarks, FrameInput, PtRect, PxBox};
use crate::live::Live;
use crate::win::PixelRect;

pub const GOLD: Color32 = Color32::from_rgb(255, 196, 0);
const DIM: Color32 = Color32::from_rgb(170, 170, 180);
const WARN: Color32 = Color32::from_rgb(255, 170, 60);

fn section_ui(ui: &mut egui::Ui, s: &Section) {
    if s.title.is_empty() {
        return;
    }
    ui.label(RichText::new(&s.title).strong().size(15.0));
    if !s.status.is_empty() {
        let r = ui.colored_label(if s.warn { WARN } else { DIM }, &s.status);
        if !s.note.is_empty() {
            r.on_hover_text(&s.note);
        }
    }
    for row in &s.rows {
        ui.horizontal_wrapped(|ui| {
            if row.picked {
                ui.label(RichText::new(t("추천!", "Pick!")).color(Color32::BLACK).background_color(GOLD).strong());
            } else {
                ui.label(RichText::new(format!("{}", row.rank + 1)).color(DIM).monospace());
            }
            ui.label(RichText::new(&row.label).color(if row.picked { Color32::WHITE } else { Color32::LIGHT_GRAY }));
            if !row.sub.is_empty() {
                ui.label(RichText::new(&row.sub).color(DIM).small());
            }
        });
    }
    if !s.ending.is_empty() {
        ui.label(RichText::new(&s.ending).color(GOLD));
    }
    ui.add_space(6.0);
}

/// The 추천 tab: preparation line, helper state, combat order, the open screen's decision.
pub fn recommend_ui(ui: &mut egui::Ui, live: &Live) {
    if let Some(line) = live.prepare_line.lock().ok().and_then(|l| l.clone()) {
        ui.colored_label(WARN, line);
    }
    let brain = &live.brain;
    if !brain.advisor_available() {
        ui.colored_label(WARN, t("AI 도우미(advisor\\sts2-advisor.exe)를 찾지 못해 추천이 꺼져 있습니다", "The AI helper (advisor\\sts2-advisor.exe) was not found: advice is off"));
        return;
    }
    if !brain.enabled() {
        ui.colored_label(DIM, t("AI 추천이 꺼져 있습니다 (상태 탭에서 켜기)", "AI advice is off (turn it on in the Status tab)"));
        return;
    }
    let run = brain.run();
    if run.is_none() {
        ui.label(t("진행 중인 런을 찾지 못했습니다", "No run in progress"));
        return;
    }
    let mut any = false;
    if let Some(m) = brain.combat() {
        section_ui(ui, &present::combat(m, &live.text));
        any = true;
    }
    if let Some(v) = brain.current_offer() {
        let incomplete = brain.verdict() == PartialVerdict::ShowIncomplete;
        section_ui(ui, &present::decision(&v, run, &live.text, incomplete));
        any = true;
    }
    if !any {
        ui.colored_label(DIM, t("카드 보상, 상점, 휴식처, 이벤트에서 AI 추천을 보여 줍니다", "AI advice shows at card rewards, shops, rest sites and events"));
    }
}

pub fn path_ui(ui: &mut egui::Ui, live: &Live) {
    let run = live.brain.run();
    match live.brain.decision("path") {
        Some(v) => section_ui(ui, &present::decision(&v, run, &live.text, false)),
        None => {
            ui.colored_label(DIM, t("지도 화면에서 다음 경로 추천을 보여 줍니다", "The next path is advised on the map screen"));
        }
    }
}

pub fn deck_ui(ui: &mut egui::Ui, live: &Live) {
    let Some(run) = live.brain.run() else {
        ui.label(t("진행 중인 런을 찾지 못했습니다", "No run in progress"));
        return;
    };
    let lang = loc::current();
    let mut groups: Vec<(String, usize)> = Vec::new();
    for c in &run.state.deck {
        let name = live.text.card(lang, &c.card.id, c.card.upgrade);
        let name = match &c.enchantment_id {
            Some(e) => format!("{name} [{}]", ov_live::text::prettify(e)),
            None => name,
        };
        match groups.iter_mut().find(|(n, _)| *n == name) {
            Some(g) => g.1 += 1,
            None => groups.push((name, 1)),
        }
    }
    groups.sort();
    ui.label(RichText::new(loc::tr(format!("덱 {}장", run.state.deck.len()), format!("Deck: {} cards", run.state.deck.len()))).strong());
    if run.deck_approximate {
        ui.colored_label(DIM, t("로그로 추정한 덱입니다 (상점 구매 등은 빠질 수 있음)", "Deck rebuilt from the log (shop buys may be missing)"));
    }
    for (name, n) in groups {
        ui.label(if n > 1 { format!("{name} ×{n}") } else { name });
    }
    ui.add_space(6.0);
    ui.label(RichText::new(t("유물", "Relics")).strong());
    for r in &run.state.relics {
        ui.label(live.text.relic(lang, r));
    }
}

fn px(r: SRect) -> PxBox {
    PxBox::new(r.x, r.y, r.width, r.height)
}

fn to_egui(r: PtRect) -> Rect {
    Rect::from_min_size(Pos2::new(r.x as f32, r.y as f32), Vec2::new(r.w as f32, r.h as f32))
}

fn pill(p: &egui::Painter, r: Rect, text: &str, fill: Color32, fg: Color32, size: f32) {
    p.rect_filled(r, CornerRadius::same((r.height() / 2.0).min(14.0) as u8), fill);
    p.rect_stroke(r, CornerRadius::same((r.height() / 2.0).min(14.0) as u8), Stroke::new(1.5, GOLD), egui::StrokeKind::Inside);
    p.text(r.center(), egui::Align2::CENTER_CENTER, text, egui::FontId::proportional(size), fg);
}

/// Marks on the game: a badge per offered card / shop item (AI rank, "추천!" on the pick), the skip pill, the
/// removal marker, and the advised rest-site button.
pub fn layer_marks(ui: &mut egui::Ui, live: &Live, layer: PixelRect, multiplier: f64, outlines: bool) {
    let p = ui.painter().clone();
    let dpi = f64::from(ui.ctx().pixels_per_point());
    let layer_px = PxBox::new(layer.x, layer.y, layer.w, layer.h);
    let brain = &live.brain;
    if !brain.enabled() {
        return;
    }
    let Some(v) = brain.current_offer() else { return };
    let hold = brain.verdict() == PartialVerdict::Hold;
    match &v.request.shown {
        Shown::Cards(_) | Shown::Shop { .. } => card_badges(&p, &v, layer_px, dpi, multiplier, outlines, hold),
        Shown::Rest(_) => rest_marks(&p, live, &v, layer_px, dpi),
        Shown::Options(_) => event_marks(&p, live, &v, layer_px, dpi),
        _ => {}
    }
}

fn card_badges(p: &egui::Painter, v: &DecisionView, layer: PxBox, dpi: f64, multiplier: f64, outlines: bool, hold: bool) {
    // Every shown item with its target, in one list.
    let mut items: Vec<(Target, SRect, Option<SRect>)> = Vec::new();
    match &v.request.shown {
        Shown::Cards(c) => items.extend(c.iter().enumerate().map(|(i, c)| (Target::Card(i), c.bounds, None))),
        Shown::Shop { cards, relics, potions, .. } => {
            items.extend(cards.iter().enumerate().map(|(i, c)| (Target::ShopCard(i), c.bounds, c.price_bounds)));
            items.extend(relics.iter().enumerate().map(|(i, c)| (Target::ShopRelic(i), c.bounds, None)));
            items.extend(potions.iter().enumerate().map(|(i, c)| (Target::ShopPotion(i), c.bounds, None)));
        }
        _ => return,
    }
    let choice = |t: Target| v.choices.iter().find(|c| c.target == t);
    let boxes: Vec<PxBox> = items.iter().map(|x| px(x.1)).collect();
    let prices: Vec<Option<PxBox>> = items.iter().map(|x| x.2.map(px)).collect();
    let marks: Vec<CardMarks> = items.iter().map(|x| CardMarks { chip_count: 0, ribbon: !hold && choice(x.0).is_some_and(|c| c.picked) }).collect();
    let skip_picked = !hold && v.choices.iter().any(|c| c.target == Target::Skip && c.picked);
    let removal = match (&v.request.shown, v.choices.iter().any(|c| matches!(c.target, Target::Removal(_)) && c.picked)) {
        (Shown::Shop { removal: Some(r), .. }, true) => Some(px(*r)),
        _ => None,
    };
    let frame = badge::compute(&FrameInput { cards: &boxes, prices: &prices, layer, dpi_scale: dpi, multiplier, skip: skip_picked, outlines, marks: &marks, removal });
    for (i, b) in frame.badges.iter().enumerate() {
        let Some(c) = choice(items[i].0) else { continue };
        let r = to_egui(b.rect);
        let text = loc::tr(format!("AI {}위", c.rank + 1), format!("AI #{}", c.rank + 1));
        let fill = if c.picked && !hold { Color32::from_rgba_unmultiplied(60, 46, 0, 235) } else { Color32::from_rgba_unmultiplied(20, 22, 28, 220) };
        pill(p, r, &text, fill, Color32::WHITE, (18.0 * b.scale) as f32);
    }
    for r in frame.ribbons.iter().flatten() {
        pill(p, to_egui(r.rect), t("추천!", "Pick!"), GOLD, Color32::BLACK, (16.0 * r.scale) as f32);
    }
    if let Some(r) = frame.removal {
        pill(p, to_egui(r.rect), t("제거 추천!", "Remove a card!"), GOLD, Color32::BLACK, (16.0 * r.scale) as f32);
    }
    if let Some(s) = frame.skip {
        pill(p, to_egui(s.rect), t("스킵 추천!", "Skip!"), GOLD, Color32::BLACK, (18.0 * s.scale) as f32);
    }
    for o in frame.card_outlines.iter().chain(frame.price_outlines.iter()) {
        p.rect_stroke(to_egui(*o), 0.0, Stroke::new(1.0, Color32::RED), egui::StrokeKind::Inside);
    }
}

fn rest_marks(p: &egui::Painter, live: &Live, v: &DecisionView, layer: PxBox, dpi: f64) {
    let Some(site) = live.brain.rest_site() else { return };
    for c in v.choices.iter().filter(|c| c.picked) {
        let Target::Rest(option, _) = c.target else { continue };
        let Some(b) = site.find(option) else { continue };
        // Rest buttons are in frame pixels: the frame is the game's client area = the layer.
        let bx = PxBox::new(layer.x + b.rect.x as i32, layer.y + b.rect.y as i32, b.rect.width as i32, b.rect.height as i32);
        let r = badge::to_layer_points(bx, layer, dpi);
        let scale = badge::scale_for(r.w);
        let (w, h) = (badge::RIBBON_WIDTH * scale * 1.3, badge::RIBBON_HEIGHT * scale);
        let (x, y) = badge::place(r, w, h, f64::from(layer.w) / dpi, f64::from(layer.h) / dpi, 4.0 * scale);
        pill(p, to_egui(PtRect::new(x, y, w, h)), t("추천!", "Pick!"), GOLD, Color32::BLACK, (16.0 * scale) as f32);
    }
}

fn event_marks(p: &egui::Painter, live: &Live, v: &DecisionView, layer: PxBox, dpi: f64) {
    let buttons = live.brain.event_buttons();
    for c in v.choices.iter().filter(|c| c.picked) {
        let Target::Option(i) = c.target else { continue };
        let Some((b, _)) = buttons.get(i) else { continue };
        let r = badge::to_layer_points(px(*b), layer, dpi);
        // A tag on the button's left end, inside it (the text sits in the middle).
        let scale = (r.h / 84.0 * 1.6).clamp(0.5, 3.0);
        let (w, h) = (badge::RIBBON_WIDTH * scale, badge::RIBBON_HEIGHT * scale);
        let rect = PtRect::new(r.x - w - 6.0 * scale, r.y + (r.h - h) / 2.0, w, h);
        let rect = if rect.x < 2.0 { PtRect::new(r.x + 4.0 * scale, rect.y, w, h) } else { rect };
        pill(p, to_egui(rect), if v.close { t("추천 (박빙)", "Pick (close)") } else { t("추천!", "Pick!") }, GOLD, Color32::BLACK, (15.0 * scale) as f32);
    }
}
