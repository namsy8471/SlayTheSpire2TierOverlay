//! Port of Geometry.cs and ScreenFrame.cs: frames, rectangles and the 1920x1080 reference transform.

use std::fmt;

/// A captured or decoded image: BGRA bytes, rows `stride` bytes apart.
#[derive(Clone, Copy)]
pub struct ScreenFrame<'a> {
    pub bgra: &'a [u8],
    pub width: i32,
    pub height: i32,
    pub stride: i32,
}

impl<'a> ScreenFrame<'a> {
    pub fn packed(bgra: &'a [u8], width: i32, height: i32) -> Self {
        Self { bgra, width, height, stride: width * 4 }
    }

    pub fn validate(&self) -> Result<(), String> {
        if self.width <= 0 || self.height <= 0 {
            return Err(format!("Empty frame {}x{}.", self.width, self.height));
        }
        if self.stride < self.width * 4 {
            return Err(format!("Stride {} < width*4 ({}).", self.stride, self.width * 4));
        }
        let needed = self.stride as i64 * (self.height as i64 - 1) + self.width as i64 * 4;
        if (self.bgra.len() as i64) < needed {
            return Err(format!("Buffer has {} bytes, frame needs {}.", self.bgra.len(), needed));
        }
        Ok(())
    }
}

/// An owned frame (decoded image).
#[derive(Clone)]
pub struct OwnedFrame {
    pub bgra: Vec<u8>,
    pub width: i32,
    pub height: i32,
    pub stride: i32,
}

impl OwnedFrame {
    pub fn view(&self) -> ScreenFrame<'_> {
        ScreenFrame { bgra: &self.bgra, width: self.width, height: self.height, stride: self.stride }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Default)]
pub struct RectF {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}

impl RectF {
    pub const fn new(x: f32, y: f32, width: f32, height: f32) -> Self {
        Self { x, y, width, height }
    }
    pub fn right(&self) -> f32 {
        self.x + self.width
    }
    pub fn bottom(&self) -> f32 {
        self.y + self.height
    }
    pub fn center_x(&self) -> f32 {
        self.x + self.width / 2.0
    }
    pub fn center_y(&self) -> f32 {
        self.y + self.height / 2.0
    }
    pub fn from_center(cx: f32, cy: f32, w: f32, h: f32) -> Self {
        Self::new(cx - w / 2.0, cy - h / 2.0, w, h)
    }
    pub fn offset(&self, dx: f32, dy: f32) -> Self {
        Self::new(self.x + dx, self.y + dy, self.width, self.height)
    }
    pub fn fraction(&self, fx: f32, fy: f32, fw: f32, fh: f32) -> Self {
        Self::new(self.x + fx * self.width, self.y + fy * self.height, fw * self.width, fh * self.height)
    }
}

impl fmt::Display for RectF {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "({:.1},{:.1} {:.1}x{:.1})", self.x, self.y, self.width, self.height)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default, Hash)]
pub struct PixelRect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

impl PixelRect {
    pub const fn new(x: i32, y: i32, width: i32, height: i32) -> Self {
        Self { x, y, width, height }
    }
    pub fn right(&self) -> i32 {
        self.x + self.width
    }
    pub fn bottom(&self) -> i32 {
        self.y + self.height
    }
    pub fn intersect(&self, width: i32, height: i32) -> PixelRect {
        let x0 = self.x.clamp(0, width);
        let y0 = self.y.clamp(0, height);
        let x1 = self.right().clamp(0, width);
        let y1 = self.bottom().clamp(0, height);
        PixelRect::new(x0, y0, (x1 - x0).max(0), (y1 - y0).max(0))
    }
}

impl fmt::Display for PixelRect {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "({},{} {}x{})", self.x, self.y, self.width, self.height)
    }
}

/// Maps 1920x1080 reference coordinates onto a frame (uniform scale, letterboxed).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RefTransform {
    pub scale: f32,
    pub offset_x: f32,
    pub offset_y: f32,
}

impl RefTransform {
    pub const REF_WIDTH: f32 = 1920.0;
    pub const REF_HEIGHT: f32 = 1080.0;

    pub fn for_frame(frame_width: i32, frame_height: i32) -> Self {
        let s = (frame_width as f32 / Self::REF_WIDTH).min(frame_height as f32 / Self::REF_HEIGHT);
        RefTransform {
            scale: s,
            offset_x: (frame_width as f32 - Self::REF_WIDTH * s) / 2.0,
            offset_y: (frame_height as f32 - Self::REF_HEIGHT * s) / 2.0,
        }
    }
    pub fn to_pixel_x(&self, ref_x: f32) -> f32 {
        self.offset_x + ref_x * self.scale
    }
    pub fn to_pixel_y(&self, ref_y: f32) -> f32 {
        self.offset_y + ref_y * self.scale
    }
    pub fn to_pixels(&self, r: RectF) -> RectF {
        RectF::new(self.to_pixel_x(r.x), self.to_pixel_y(r.y), r.width * self.scale, r.height * self.scale)
    }
    pub fn to_pixel_rect(&self, r: RectF) -> PixelRect {
        let p = self.to_pixels(r);
        let x0 = p.x.floor() as i32;
        let y0 = p.y.floor() as i32;
        let x1 = p.right().ceil() as i32;
        let y1 = p.bottom().ceil() as i32;
        PixelRect::new(x0, y0, x1 - x0, y1 - y0)
    }
    pub fn to_ref(&self, pixels: RectF) -> RectF {
        RectF::new(
            (pixels.x - self.offset_x) / self.scale,
            (pixels.y - self.offset_y) / self.scale,
            pixels.width / self.scale,
            pixels.height / self.scale,
        )
    }
}
