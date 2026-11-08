namespace Sts2Overlay.Vision;

/// <summary>One portrait file that represents a card in the art database.</summary>
public sealed record ArtFile(string CardId, string Pool, string? Character, string RelativePath, bool IsBeta);

/// <summary>
/// Maps the extracted portraits (<c>data/local/pck/card_art/&lt;pool&gt;/[beta/]&lt;id&gt;.webp</c>, see
/// tools/PckTool) to card ids, mirroring how the game picks a portrait (CardModel.PortraitPath):
/// <c>&lt;pool title&gt;/&lt;id lower&gt;</c>, falling back to the old beta art when a card has no main art.
/// </summary>
public static class CardArtSource
{
    /// <summary>
    /// With a catalog: exactly one entry per catalog card that has art (main, else beta, else per-type variants
    /// such as event/mad_science_attack.webp for CARD.MAD_SCIENCE). Art files of cards that no longer exist in
    /// this game version are ignored, which also removes stale look-alikes (e.g. BLACKENED_BLADE, which shares
    /// BLADE_OF_INK's picture but is not in the v0.107.1 card list).
    /// Without a catalog: every portrait, main art preferred over beta (the M0 VisionSpike rule).
    /// </summary>
    public static IReadOnlyList<ArtFile> Enumerate(string artRoot, VisionCatalog? catalog)
    {
        if (!Directory.Exists(artRoot)) throw new DirectoryNotFoundException($"Card art folder not found: {artRoot}");
        return catalog is null ? EnumerateAll(artRoot) : EnumerateCatalog(artRoot, catalog);
    }

    private static List<ArtFile> EnumerateCatalog(string artRoot, VisionCatalog catalog)
    {
        var result = new List<ArtFile>();
        foreach (CatalogCard card in catalog.Cards)
        {
            string folder = card.PoolFolder;
            string name = card.Id.StartsWith("CARD.", StringComparison.Ordinal) ? card.Id[5..].ToLowerInvariant() : card.Id.ToLowerInvariant();
            string main = Path.Combine(artRoot, folder, name + ".webp");
            string beta = Path.Combine(artRoot, folder, "beta", name + ".webp");
            if (File.Exists(main))
            {
                result.Add(new ArtFile(card.Id, card.Pool, card.Character, Rel(artRoot, main), false));
            }
            else if (File.Exists(beta))
            {
                result.Add(new ArtFile(card.Id, card.Pool, card.Character, Rel(artRoot, beta), true));
            }
            else if (Directory.Exists(Path.Combine(artRoot, folder)))
            {
                foreach (string v in Directory.EnumerateFiles(Path.Combine(artRoot, folder), name + "_*.webp").Order(StringComparer.Ordinal))
                    result.Add(new ArtFile(card.Id, card.Pool, card.Character, Rel(artRoot, v), false));
            }
        }
        return result;
    }

    private static List<ArtFile> EnumerateAll(string artRoot)
    {
        var byId = new Dictionary<string, ArtFile>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(artRoot, "*.webp", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string rel = Rel(artRoot, file);
            string[] parts = rel.Split('/');
            if (parts.Length < 2) continue; // root-level placeholders (beta.webp, ancient_beta.webp)
            bool isBeta = parts.Contains("beta");
            string id = "CARD." + Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
            string pool = $"CARD_POOL.{parts[0].ToUpperInvariant()}_CARD_POOL";
            var entry = new ArtFile(id, pool, null, rel, isBeta);
            if (!byId.TryGetValue(id, out ArtFile? existing) || (existing.IsBeta && !isBeta)) byId[id] = entry;
        }
        return byId.Values.OrderBy(e => e.CardId, StringComparer.Ordinal).ToList();
    }

    private static string Rel(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
}
