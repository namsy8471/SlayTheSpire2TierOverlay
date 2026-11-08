using System.Text;

namespace Sts2Overlay.Resources.Pck;

/// <summary>One file stored inside the pack.</summary>
/// <param name="Path">Resource path without the "res://" prefix, e.g. "localization/kor/cards.json".</param>
/// <param name="Offset">Absolute byte offset of the file's data inside the .pck.</param>
/// <param name="Size">Size of the file's data in bytes.</param>
/// <param name="Encrypted">True when per-file flag bit 0 is set. We cannot read those (no key).</param>
public sealed record PckEntry(string Path, long Offset, long Size, bool Encrypted);

/// <summary>
/// Reads the directory (table of contents) of a Godot 4 ".pck" resource pack.
///
/// Layout, as read by Godot 4.5 core/io/file_access_pack.cpp (PackedSourcePCK::try_open_pack).
/// All integers are little-endian.
///
///   Header (pack format version 3):
///     0x00  char[4]  magic "GDPC"
///     0x04  uint32   pack format version (3)
///     0x08  uint32   Godot major  (4)
///     0x0C  uint32   Godot minor  (5)
///     0x10  uint32   Godot patch  (1)
///     0x14  uint32   pack_flags   bit0 = directory encrypted, bit1 = REL_FILEBASE, bit2 = sparse bundle
///     0x18  uint64   file_base    where file data starts (relative to the pack start in v3)
///     0x20  uint64   dir_offset   where the directory starts (relative to the pack start)
///     ...   reserved bytes up to file_base
///
///   Directory (at dir_offset):
///     uint32 file_count
///     file_count times:
///       uint32   path_len       byte length of the path, INCLUDING zero padding to a multiple of 4
///       byte[]   path           UTF-8, e.g. "res://..." or just "localization/kor/cards.json"
///       uint64   offset         data offset, relative to file_base
///       uint64   size           data size in bytes
///       byte[16] md5            md5 of the file data (we ignore it)
///       uint32   flags          bit0 = encrypted, bit1 = "removal" (a patch pack deleting a file)
///
/// The file data itself is stored raw (not compressed), so extracting is just "seek + copy".
/// </summary>
public sealed class PckReader : IDisposable
{
    private const uint Magic = 0x43504447; // "GDPC" read as a little-endian uint32 ('G'=0x47 is the lowest byte)
    private const uint PackDirEncrypted = 1 << 0;
    private const uint FileEncrypted = 1 << 0;
    private const uint FileRemoval = 1 << 1;

    private readonly FileStream _stream;

    public IReadOnlyList<PckEntry> Entries { get; }
    public string GodotVersion { get; }

    public PckReader(string pckPath)
    {
        // FileAccess.Read: we never write to the game's files.
        // FileShare.ReadWrite: lets us open the pack even while the game itself has it open.
        _stream = new FileStream(pckPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true); // BinaryReader is always little-endian

        long packStart = 0; // standalone .pck: header is at byte 0 (an exe with an embedded pck would differ)
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a Godot pack: missing 'GDPC' magic at offset 0.");

        uint formatVersion = r.ReadUInt32();
        uint major = r.ReadUInt32(), minor = r.ReadUInt32(), patch = r.ReadUInt32();
        GodotVersion = $"{major}.{minor}.{patch} (pack format v{formatVersion})";
        if (formatVersion != 3)
            throw new NotSupportedException($"Only pack format v3 (Godot 4.4+) is supported, got v{formatVersion}.");

        uint packFlags = r.ReadUInt32();
        if ((packFlags & PackDirEncrypted) != 0)
            throw new NotSupportedException("The pack directory is encrypted; cannot read it without the key.");

        // In format v3 both values are relative to where the header starts (packStart).
        long fileBase = packStart + (long)r.ReadUInt64();
        long dirOffset = packStart + (long)r.ReadUInt64();

        _stream.Seek(dirOffset, SeekOrigin.Begin);
        uint fileCount = r.ReadUInt32();

        // Later entries for the same path win (that is how Godot treats patch packs too).
        var byPath = new Dictionary<string, PckEntry>(StringComparer.Ordinal);
        for (uint i = 0; i < fileCount; i++)
        {
            int pathLen = (int)r.ReadUInt32();
            byte[] pathBytes = r.ReadBytes(pathLen);
            // The path is padded with '\0' bytes up to a multiple of 4; strip them.
            string path = Encoding.UTF8.GetString(pathBytes).TrimEnd('\0');
            if (path.StartsWith("res://", StringComparison.Ordinal))
                path = path["res://".Length..];

            long offset = (long)r.ReadUInt64();
            long size = (long)r.ReadUInt64();
            r.ReadBytes(16); // md5, unused
            uint flags = r.ReadUInt32();

            if ((flags & FileRemoval) != 0)
            {
                byPath.Remove(path);
                continue;
            }
            byPath[path] = new PckEntry(path, fileBase + offset, size, (flags & FileEncrypted) != 0);
        }

        Entries = byPath.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Reads a whole file from the pack into memory.</summary>
    public byte[] ReadAllBytes(PckEntry entry)
    {
        if (entry.Encrypted)
            throw new NotSupportedException($"'{entry.Path}' is encrypted.");

        var buffer = new byte[entry.Size];
        _stream.Seek(entry.Offset, SeekOrigin.Begin);
        _stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>Copies a file from the pack to disk without loading it all into memory.</summary>
    public void ExtractTo(PckEntry entry, string outputPath)
    {
        if (entry.Encrypted)
            throw new NotSupportedException($"'{entry.Path}' is encrypted.");

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);
        _stream.Seek(entry.Offset, SeekOrigin.Begin);

        using var output = File.Create(outputPath);
        var buffer = new byte[81920];
        long remaining = entry.Size;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            _stream.ReadExactly(buffer, 0, toRead);
            output.Write(buffer, 0, toRead);
            remaining -= toRead;
        }
    }

    public void Dispose() => _stream.Dispose();
}
