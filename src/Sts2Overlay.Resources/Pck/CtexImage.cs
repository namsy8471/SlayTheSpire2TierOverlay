namespace Sts2Overlay.Resources.Pck;

/// <summary>What kind of pixel data a .ctex file holds.</summary>
public enum CtexPayload
{
    Webp,            // a complete .webp file is embedded: we can write it out unchanged
    Png,             // a complete .png file is embedded: same
    RawUncompressed, // raw pixels (e.g. RGBA8) with no file header - would need a PNG encoder, skipped
    VramCompressed,  // GPU block compression (BC1/BC3/BC7, ETC2, ASTC...) - would need a decoder, skipped
    BasisUniversal,  // Basis Universal supercompressed - skipped
}

/// <summary>
/// Reads Godot 4 "compressed texture" files (.ctex), the format the editor converts every imported
/// .png into. In an exported game the original .png is gone; only .godot/imported/*.ctex remains.
///
/// Layout, as read by Godot 4.5 scene/resources/compressed_texture.cpp
/// (CompressedTexture2D::_load_data and load_image_from_file). Little-endian.
///
///   0x00  char[4]  magic "GST2"
///   0x04  uint32   version (1)
///   0x08  uint32   width
///   0x0C  uint32   height
///   0x10  uint32   flags (stream / has mipmaps / detect-3D ...)
///   0x14  int32    mipmap limit
///   0x18  uint32[3] reserved
///   0x24  uint32   data_format   0 = raw Image data, 1 = PNG, 2 = WebP, 3 = Basis Universal
///   0x28  uint16   width
///   0x2A  uint16   height
///   0x2C  uint32   mipmap count (not counting the full-size image)
///   0x30  uint32   Image::Format (pixel format, e.g. 4 = RGB8, 5 = RGBA8, 17+ = VRAM-compressed)
///   0x34  ...      payload:
///                    PNG / WebP: (mipmaps + 1) times { uint32 size, byte[size] complete image file }
///                    raw Image : the pixel bytes of all mip levels back to back
///
/// Only the PNG/WebP case can be "converted" by simply cutting out the embedded file.
/// </summary>
public sealed class CtexImage
{
    private const int DataFormatImage = 0, DataFormatPng = 1, DataFormatWebp = 2, DataFormatBasis = 3;

    // Image::Format values, in the order of the enum in Godot 4.5 core/io/image.h.
    // 0..16 are plain (uncompressed) pixel layouts; DXT1 (17) and everything after it is GPU block compression.
    private const int FirstVramFormat = 17;
    private static readonly string[] FormatNames =
    [
        "L8", "LA8", "R8", "RG8", "RGB8", "RGBA8", "RGBA4444", "RGB565", "RF", "RGF", "RGBF", "RGBAF",
        "RH", "RGH", "RGBH", "RGBAH", "RGBE9995", "DXT1(BC1)", "DXT3(BC2)", "DXT5(BC3)", "RGTC_R(BC4)",
        "RGTC_RG(BC5)", "BPTC_RGBA(BC7)", "BPTC_RF(BC6)", "BPTC_RFU(BC6)", "ETC", "ETC2_R11", "ETC2_R11S",
        "ETC2_RG11", "ETC2_RG11S", "ETC2_RGB8", "ETC2_RGBA8", "ETC2_RGB8A1", "ETC2_RA_AS_RG",
        "DXT5_RA_AS_RG", "ASTC_4x4", "ASTC_4x4_HDR", "ASTC_8x8", "ASTC_8x8_HDR",
    ];

    public int Width { get; }
    public int Height { get; }
    public CtexPayload Payload { get; }
    public string PixelFormat { get; }

    /// <summary>The embedded .png/.webp file (full-size level only), or null for skipped payloads.</summary>
    public byte[]? EmbeddedFile { get; }

    /// <summary>Raw (data format 0) payload: all mip levels back to back, full size first. Null for embedded files.</summary>
    public byte[]? RawData { get; }

    private readonly int _imageFormat = -1;
    private const int FormatRgba8 = 5, FormatBptcRgba = 22;

    /// <summary>The image can be turned into RGBA8 pixels by <see cref="DecodeRgba"/> (RGBA8 or BC7 raw data).</summary>
    public bool CanDecodeRgba => RawData != null && _imageFormat is FormatRgba8 or FormatBptcRgba;

    /// <summary>
    /// Full-size level as straight RGBA8 (<see cref="Width"/> x <see cref="Height"/>): RGBA8 is copied, BC7 decoded
    /// with <see cref="Bc7Decoder"/>. Used for the relic atlas, which the game ships only as a BC7 VRAM texture.
    /// </summary>
    public byte[] DecodeRgba()
    {
        if (!CanDecodeRgba) throw new NotSupportedException($"Cannot decode {PixelFormat} ({Payload}).");
        return _imageFormat == FormatRgba8
            ? RawData.AsSpan(0, Width * Height * 4).ToArray()
            : Bc7Decoder.DecodeImage(RawData, Width, Height);
    }

    public string? FileExtension => Payload switch
    {
        CtexPayload.Webp => ".webp",
        CtexPayload.Png => ".png",
        _ => null,
    };

    public CtexImage(byte[] ctex)
    {
        using var r = new BinaryReader(new MemoryStream(ctex));

        if (r.ReadUInt32() != 0x32545347) // "GST2" as a little-endian uint32
            throw new InvalidDataException("Not a Godot 4 .ctex file (missing 'GST2' magic).");

        r.ReadUInt32();                 // version
        Width = (int)r.ReadUInt32();
        Height = (int)r.ReadUInt32();
        r.ReadUInt32();                 // flags
        r.ReadInt32();                  // mipmap limit
        r.ReadBytes(12);                // 3 reserved uint32s

        uint dataFormat = r.ReadUInt32();
        r.ReadUInt16();                 // width again (16-bit)
        r.ReadUInt16();                 // height again (16-bit)
        r.ReadUInt32();                 // mipmap count - we only want the first (full-size) image
        int imageFormat = (int)r.ReadUInt32();
        PixelFormat = imageFormat < FormatNames.Length ? FormatNames[imageFormat] : $"format#{imageFormat}";

        switch (dataFormat)
        {
            case DataFormatPng:
            case DataFormatWebp:
                // Payload = [uint32 size][complete image file] for the full-size level, then smaller mips.
                int size = (int)r.ReadUInt32();
                EmbeddedFile = r.ReadBytes(size);
                Payload = dataFormat == DataFormatPng ? CtexPayload.Png : CtexPayload.Webp;
                break;
            case DataFormatImage:
                Payload = imageFormat >= FirstVramFormat ? CtexPayload.VramCompressed : CtexPayload.RawUncompressed;
                _imageFormat = imageFormat;
                // The full-size level comes first; smaller mips follow and are not needed.
                RawData = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position));
                break;
            case DataFormatBasis:
                Payload = CtexPayload.BasisUniversal;
                break;
            default:
                throw new InvalidDataException($"Unknown .ctex data format {dataFormat}.");
        }
    }
}
