using System.Text.Json;
using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Tests.Data;

/// <summary>
/// Checks the real (draft) data pack <c>data/packs/v0.107.1</c> against the committed, names-free catalog
/// <c>data/catalog/cards.v0.107.1.json</c>.
/// </summary>
public class RealPackTests
{
    private const string Version = "v0.107.1";

    private static string PackDir => Path.Combine(TestPaths.RepoRoot, "data", "packs", Version);

    private static string CatalogPath => Path.Combine(TestPaths.RepoRoot, "data", "catalog", $"cards.{Version}.json");

    /// <summary>Card pool id in the committed catalog → tier-pack character.</summary>
    private static readonly IReadOnlyDictionary<string, string> PoolToCharacter = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CARD_POOL.IRONCLAD_CARD_POOL"] = "CHARACTER.IRONCLAD",
        ["CARD_POOL.SILENT_CARD_POOL"] = "CHARACTER.SILENT",
        ["CARD_POOL.DEFECT_CARD_POOL"] = "CHARACTER.DEFECT",
        ["CARD_POOL.NECROBINDER_CARD_POOL"] = "CHARACTER.NECROBINDER",
        ["CARD_POOL.REGENT_CARD_POOL"] = "CHARACTER.REGENT",
        ["CARD_POOL.COLORLESS_CARD_POOL"] = DataPack.Colorless,
    };

    private static readonly Lazy<CardCatalog> Catalog = new(() => ReadCommittedCatalog(CatalogPath));

    private static readonly Lazy<DataPack> Pack = new(() => DataPackLoader.LoadDirectory(PackDir, Catalog.Value));

    /// <summary>
    /// The committed catalog (tools/CatalogBuilder) differs from <see cref="CardCatalog"/>: <c>pool</c> is a
    /// <c>CARD_POOL.*</c> id (the character is in <c>character</c>), type/rarity are PascalCase and multiplayer-only is
    /// <c>"multiplayer": "MultiplayerOnly"</c>. Converted here so this test does not depend on loader changes made elsewhere.
    /// </summary>
    private static CardCatalog ReadCommittedCatalog(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var catalog = new CardCatalog
        {
            GameVersion = root.TryGetProperty("gameVersion", out var gv) ? gv.GetString() : null,
        };
        foreach (var c in root.GetProperty("cards").EnumerateArray())
        {
            var poolId = c.GetProperty("pool").GetString() ?? "";
            catalog.Cards.Add(new CatalogCard
            {
                Id = c.GetProperty("id").GetString() ?? "",
                Pool = PoolToCharacter.TryGetValue(poolId, out var character) ? character : poolId,
                Type = c.GetProperty("type").GetString()?.ToLowerInvariant(),
                Rarity = c.GetProperty("rarity").GetString()?.ToLowerInvariant(),
                Cost = c.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetInt32() : null,
                CostX = c.TryGetProperty("costX", out var x) && x.ValueKind == JsonValueKind.True,
                Target = c.TryGetProperty("target", out var t) ? t.GetString() : null,
                GainsBlock = c.TryGetProperty("gainsBlock", out var gb) && gb.ValueKind == JsonValueKind.True,
                MultiplayerOnly = c.TryGetProperty("multiplayer", out var mp) && mp.GetString() == "MultiplayerOnly",
                Keywords = c.TryGetProperty("keywords", out var kw) ? kw.EnumerateArray().Select(k => k.GetString() ?? "").ToList() : [],
                Tags = c.TryGetProperty("tags", out var tg) ? tg.EnumerateArray().Select(k => k.GetString() ?? "").ToList() : [],
            });
        }
        return catalog;
    }

    [Fact]
    public void Pack_LoadsWithSixTierPacks()
    {
        var pack = Pack.Value;
        Assert.Equal(PoolToCharacter.Values.Order(StringComparer.Ordinal), pack.Tiers.Keys.Order(StringComparer.Ordinal));
        Assert.All(pack.Tiers.Values, t => Assert.Equal(Version, t.DataVersion));
        Assert.NotEmpty(pack.Archetypes);
        Assert.NotEmpty(pack.Combos);
    }

    [Fact]
    public void Validator_ReportsNoErrorsOrWarnings()
    {
        var issues = DataPackValidator.Validate(Pack.Value);
        Assert.True(issues.Count == 0, string.Join(Environment.NewLine, issues.Select(i => $"[{i.Severity}] {i.Where}: {i.Message}")));
    }

    [Fact]
    public void EveryPoolCard_HasATierEntryInItsOwnPack()
    {
        var pack = Pack.Value;
        var missing = Catalog.Value.Cards
            .Where(c => c.Pool is not null && PoolToCharacter.Values.Contains(c.Pool))
            .Where(c => !pack.Tiers[c.Pool!].Cards.ContainsKey(c.Id))
            .Select(c => $"{c.Pool}: {c.Id}")
            .ToList();
        Assert.True(missing.Count == 0, "Unrated pool cards:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryReferencedId_ExistsInCatalogAndPool()
    {
        var pack = Pack.Value;
        var byId = Catalog.Value.Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var (character, tiers) in pack.Tiers)
        {
            foreach (var id in tiers.Cards.Keys)
            {
                if (!byId.TryGetValue(id, out var card)) problems.Add($"tiers[{character}]: {id} not in catalog");
                else if (card.Pool != character) problems.Add($"tiers[{character}]: {id} belongs to {card.Pool}");
            }
        }

        foreach (var a in pack.Archetypes)
        {
            foreach (var id in a.Cards.Core.Concat(a.Cards.Enabler).Concat(a.Cards.Payoff).Concat(a.Cards.Support))
            {
                if (!byId.TryGetValue(id, out var card)) problems.Add($"archetype {a.Id}: {id} not in catalog");
                else if (card.Pool != a.Character && card.Pool != DataPack.Colorless) problems.Add($"archetype {a.Id}: {id} belongs to {card.Pool}");
            }
        }

        foreach (var c in pack.Combos)
        {
            foreach (var id in c.Cards)
            {
                if (!byId.TryGetValue(id, out var card)) problems.Add($"combo {c.Name}: {id} not in catalog");
                else if (c.Character is not null && card.Pool != c.Character && card.Pool != DataPack.Colorless) problems.Add($"combo {c.Name}: {id} belongs to {card.Pool}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void DraftEntries_AreMarkedAsDraftsWithHonestConfidence()
    {
        foreach (var (character, tiers) in Pack.Value.Tiers)
        {
            foreach (var (id, e) in tiers.Cards)
            {
                Assert.True(e.Source.StartsWith("claude-draft-v1", StringComparison.Ordinal), $"{character} {id}: source '{e.Source}'");
                Assert.InRange(e.Confidence, 0.3, 0.7);
                Assert.DoesNotContain("CARD.", e.Tip ?? "", StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void EveryCharacter_HasThreeToFiveArchetypes()
    {
        foreach (var character in PoolToCharacter.Values.Where(c => c != DataPack.Colorless))
        {
            var count = Pack.Value.Archetypes.Count(a => a.Character == character);
            Assert.InRange(count, 3, 5);
        }
    }
}
