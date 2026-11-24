//! Port of CtexImage.cs: Godot 4 compressed texture files (".ctex"; layout from Godot 4.5
//! scene/resources/compressed_texture.cpp).

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum CtexPayload {
    Webp,
    Png,
    RawUncompressed,
    VramCompressed,
    BasisUniversal,
}

const FORMAT_NAMES: [&str; 39] = [
    "L8", "LA8", "R8", "RG8", "RGB8", "RGBA8", "RGBA4444", "RGB565", "RF", "RGF", "RGBF", "RGBAF", "RH", "RGH", "RGBH", "RGBAH",
    "RGBE9995", "DXT1(BC1)", "DXT3(BC2)", "DXT5(BC3)", "RGTC_R(BC4)", "RGTC_RG(BC5)", "BPTC_RGBA(BC7)", "BPTC_RF(BC6)",
    "BPTC_RFU(BC6)", "ETC", "ETC2_R11", "ETC2_R11S", "ETC2_RG11", "ETC2_RG11S", "ETC2_RGB8", "ETC2_RGBA8", "ETC2_RGB8A1",
    "ETC2_RA_AS_RG", "DXT5_RA_AS_RG", "ASTC_4x4", "ASTC_4x4_HDR", "ASTC_8x8", "ASTC_8x8_HDR",
];
const FIRST_VRAM_FORMAT: u32 = 17;
const FORMAT_RGBA8: u32 = 5;
const FORMAT_DXT5: u32 = 19;
const FORMAT_BPTC_RGBA: u32 = 22;

#[derive(Clone, Debug)]
pub struct CtexImage {
    pub width: u32,
    pub height: u32,
    pub payload: CtexPayload,
    pub pixel_format: String,
    /// The embedded .png/.webp file (full-size level only).
    pub embedded_file: Option<Vec<u8>>,
    /// Raw payload: all mip levels back to back, full size first.
    pub raw_data: Option<Vec<u8>>,
    image_format: Option<u32>,
}

impl CtexImage {
    pub fn parse(ctex: &[u8]) -> Result<CtexImage, String> {
        let u32_at = |o: usize| -> Result<u32, String> {
            ctex.get(o..o + 4).map(|b| u32::from_le_bytes(b.try_into().unwrap())).ok_or_else(|| "truncated .ctex".to_string())
        };
        if u32_at(0)? != 0x3254_5347 {
            return Err("Not a Godot 4 .ctex file (missing 'GST2' magic).".into());
        }
        let width = u32_at(8)?;
        let height = u32_at(12)?;
        let data_format = u32_at(0x24)?;
        let image_format = u32_at(0x30)?;
        let pixel_format = FORMAT_NAMES.get(image_format as usize).map_or_else(|| format!("format#{image_format}"), |s| s.to_string());
        let mut img = CtexImage {
            width,
            height,
            payload: CtexPayload::RawUncompressed,
            pixel_format,
            embedded_file: None,
            raw_data: None,
            image_format: None,
        };
        match data_format {
            1 | 2 => {
                let size = u32_at(0x34)? as usize;
                let end = (0x38 + size).min(ctex.len());
                img.embedded_file = Some(ctex[0x38..end].to_vec());
                img.payload = if data_format == 1 { CtexPayload::Png } else { CtexPayload::Webp };
            }
            0 => {
                img.payload = if image_format >= FIRST_VRAM_FORMAT { CtexPayload::VramCompressed } else { CtexPayload::RawUncompressed };
                img.image_format = Some(image_format);
                img.raw_data = Some(ctex.get(0x34..).unwrap_or(&[]).to_vec());
            }
            3 => img.payload = CtexPayload::BasisUniversal,
            f => return Err(format!("Unknown .ctex data format {f}.")),
        }
        Ok(img)
    }

    pub fn can_decode_rgba(&self) -> bool {
        self.raw_data.is_some() && matches!(self.image_format, Some(FORMAT_RGBA8 | FORMAT_DXT5 | FORMAT_BPTC_RGBA))
    }

    /// Full-size level as straight RGBA8.
    pub fn decode_rgba(&self) -> Result<Vec<u8>, String> {
        let raw = self.raw_data.as_deref().filter(|_| self.can_decode_rgba()).ok_or_else(|| format!("Cannot decode {} ({:?}).", self.pixel_format, self.payload))?;
        let (w, h) = (self.width as usize, self.height as usize);
        match self.image_format {
            Some(FORMAT_DXT5) => super::bc3::decode_image(raw, w, h),
            Some(FORMAT_BPTC_RGBA) => super::bc7::decode_image(raw, w, h),
            _ => raw.get(..w * h * 4).map(<[u8]>::to_vec).ok_or_else(|| "RGBA8 data too short".to_string()),
        }
    }

    pub fn file_extension(&self) -> Option<&'static str> {
        match self.payload {
            CtexPayload::Webp => Some(".webp"),
            CtexPayload::Png => Some(".png"),
            _ => None,
        }
    }
}
