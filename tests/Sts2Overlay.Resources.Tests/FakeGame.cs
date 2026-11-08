using System.Security.Cryptography;
using System.Text;
using Sts2Overlay.Resources;
using Sts2Overlay.Resources.Catalog;

namespace Sts2Overlay.Resources.Tests;

/// <summary>
/// A tiny fake game install in a temp folder: a real Godot GDPC v3 pack (written by <see cref="PckWriter"/>) with two
/// localization files and two card portraits, a random "sts2.dll" and a matching release_info.json.
/// No real game files are needed, so these tests run anywhere (CI included).
/// </summary>
internal sealed class FakeGame : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sts2-res-tests", Guid.NewGuid().ToString("N"));
    public GameInstall Install { get; }
    public LocalLayout Layout { get; }

    public FakeGame(string version = "v0.107.1")
    {
        Install = new GameInstall(Path.Combine(Root, "game"));
        Layout = new LocalLayout(Path.Combine(Root, "local"));
        Directory.CreateDirectory(Install.DataDir);
        WritePck();
        WriteDll(seed: 1);
        WriteReleaseInfo(version);
    }

    public InstalledGame Detect() => InstalledGame.Detect(Install);

    public void WriteDll(int seed)
    {
        var bytes = new byte[4096];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(Install.DllPath, bytes);
    }

    public void WriteReleaseInfo(string version, int? hash = null)
    {
        hash ??= ReleaseInfo.ComputeMainAssemblyHash(Install.DllPath);
        File.WriteAllText(Install.ReleaseInfoPath,
            $$"""{"commit":"abc12345","version":"{{version}}","date":"2026-06-18T15:43:56-07:00","branch":"{{version}}","main_assembly_hash":{{hash}}}""");
    }

    public void WritePck()
    {
        var w = new PckWriter();
        w.Add("localization/kor/cards.json", """{"BASH.title":"가나다","BASH.description":"설명"}""");
        w.Add("localization/eng/cards.json", """{"BASH.title":"Abc"}""");
        w.Add("localization/kor/relics.json", "{}");
        w.AddTexture("images/packed/card_portraits/ironclad/bash.png", FakeWebp(1));
        w.AddTexture("images/packed/card_portraits/ironclad/beta/anger.png", FakeWebp(2));
        w.AddTexture("images/ui/other.png", FakeWebp(3)); // not a card portrait: must not be extracted
        w.Save(Install.PckPath);
    }

    /// <summary>Bytes that look like a WebP file header; the preparer never decodes them.</summary>
    private static byte[] FakeWebp(byte tag) => [.. "RIFF"u8, 8, 0, 0, 0, .. "WEBP"u8, tag];

    /// <summary>Stands in for the Mono.Cecil step (which needs the real sts2.dll).</summary>
    public static CatalogFile FakeCatalog(CatalogOptions o)
    {
        // Names come from the extracted localization, just like the real generator.
        var kor = File.ReadAllText(Path.Combine(o.LocalizationDir!, "kor", "cards.json"));
        return new CatalogFile(o.GameVersion, "fake", [Row("CARD.BASH", kor.Contains("가나다", StringComparison.Ordinal) ? "가나다" : null)]);
    }

    public static CardRow Row(string id, string? kor = null, string pool = "CARD_POOL.IRONCLAD_CARD_POOL", string rarity = "Common")
        => new(id, "X", pool, "CHARACTER.IRONCLAD", "Attack", rarity, 1, false, null, null, "AnyEnemy", false, 1, null, true,
            [], [], new() { ["kor"] = kor }, new() { ["kor"] = null }, [], []);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Writes a minimal Godot 4 GDPC v3 pack (the layout documented on PckReader).</summary>
internal sealed class PckWriter
{
    private readonly List<(string Path, byte[] Data)> _files = [];

    public void Add(string path, string text) => _files.Add((path, Encoding.UTF8.GetBytes(text)));

    /// <summary>Adds "&lt;path&gt;.import" pointing to a GST2 .ctex that embeds <paramref name="webp"/>.</summary>
    public void AddTexture(string logicalPath, byte[] webp)
    {
        string name = Path.GetFileName(logicalPath);
        string ctex = $".godot/imported/{name}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(logicalPath)))[..32]}.ctex";
        _files.Add((logicalPath + ".import", Encoding.UTF8.GetBytes($"[remap]\n\nimporter=\"texture\"\npath=\"res://{ctex}\"\n")));

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write("GST2"u8);
            bw.Write(1u);             // version
            bw.Write(10u);            // width
            bw.Write(8u);             // height
            bw.Write(0u);             // flags
            bw.Write(0);              // mipmap limit
            bw.Write(new byte[12]);   // reserved
            bw.Write(2u);             // data format: WebP
            bw.Write((ushort)10);
            bw.Write((ushort)8);
            bw.Write(0u);             // mipmaps
            bw.Write(5u);             // RGBA8
            bw.Write((uint)webp.Length);
            bw.Write(webp);
        }
        _files.Add((ctex, ms.ToArray()));
    }

    public void Save(string path)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        const long fileBase = 0x60;
        bw.Write("GDPC"u8);
        bw.Write(3u);
        bw.Write(4u);
        bw.Write(5u);
        bw.Write(1u);
        bw.Write(2u);                 // REL_FILEBASE
        bw.Write((ulong)fileBase);
        long dirOffsetPos = fs.Position;
        bw.Write(0UL);                // dir offset, patched below
        while (fs.Position < fileBase) bw.Write((byte)0);

        var offsets = new List<long>();
        foreach (var (_, data) in _files)
        {
            offsets.Add(fs.Position - fileBase);
            bw.Write(data);
        }

        long dirOffset = fs.Position;
        bw.Write((uint)_files.Count);
        for (int i = 0; i < _files.Count; i++)
        {
            var pathBytes = Encoding.UTF8.GetBytes("res://" + _files[i].Path);
            int padded = (pathBytes.Length + 3) / 4 * 4;
            bw.Write((uint)padded);
            bw.Write(pathBytes);
            bw.Write(new byte[padded - pathBytes.Length]);
            bw.Write((ulong)offsets[i]);
            bw.Write((ulong)_files[i].Data.Length);
            bw.Write(new byte[16]);   // md5 (unused)
            bw.Write(0u);             // flags
        }
        fs.Position = dirOffsetPos;
        bw.Write((ulong)dirOffset);
    }
}
