using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Events;

namespace Sts2Overlay.Core.Tests.Data;

/// <summary>
/// Checks the real (draft) data pack <c>data/packs/v0.107.1</c> against the committed, names-free catalog
/// <c>data/catalog/cards.v0.107.1.json</c>.
/// </summary>
public abstract class RealPackTests
{
    /// <summary>The pack and catalog version under test; one subclass per shipped data version.</summary>
    protected abstract string Version { get; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> s_cache = new(StringComparer.Ordinal);

    /// <summary>Loads once per version and key (the pack is big; xUnit makes a new instance per test).</summary>
    private T Cached<T>(string key, Func<T> load) where T : class =>
        ((Lazy<T>)s_cache.GetOrAdd(Version + "|" + key, _ => new Lazy<T>(load))).Value;

    private string PackDir => Path.Combine(TestPaths.RepoRoot, "data", "packs", Version);

    private string CatalogPath => Path.Combine(TestPaths.RepoRoot, "data", "catalog", $"cards.{Version}.json");

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

    private Lazy<CardCatalog> Catalog => new(() => Cached("catalog", () => ReadCommittedCatalog(CatalogPath)));

    private Lazy<DataPack> Pack => new(() => Cached("pack", () => DataPackLoader.LoadDirectory(PackDir, Catalog.Value)));

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

    /// <summary>
    /// Builds whose core list is empty on purpose: no card passed the core test in the top-player win data
    /// (docs/research/build-sources, v2 = OP.GG top players 2026-11). For these the core-first rule is off; the validator
    /// warns about each one. v2: defect.orb_focus (CAPACITOR), defect.zero_cost (FERAL), ironclad.block (BARRICADE) and
    /// regent.creation (PILLAR_OF_CREATION) got a core; defect.orb_variety lost SYNCHRONIZE as its core.
    /// </summary>
    protected abstract string[] IntentionallyCoreless { get; }

    /// <summary>v0.107.1 core-less builds.</summary>
    protected static readonly string[] IntentionallyCorelessV0107 =
    [
        "defect.dark_orbs", "defect.orb_variety",
        "ironclad.attack_replay", "ironclad.strength", "ironclad.strike_tempo", "ironclad.vulnerable",
        "necrobinder.ethereal",
        "silent.sly_discard",
    ];

    [Fact]
    public void Validator_ReportsNoErrorsAndOnlyTheIntentionalEmptyCoreWarnings()
    {
        var issues = DataPackValidator.Validate(Pack.Value);
        var expected = IntentionallyCoreless.Select(id => $"archetypes[{id}].cards.core").ToHashSet(StringComparer.Ordinal);
        bool IsExpected(ValidationIssue i)
            => i.Severity == ValidationSeverity.Warning && expected.Contains(i.Where) && i.Message.Contains("no core card", StringComparison.Ordinal);

        var unexpected = issues.Where(i => !IsExpected(i)).ToList();
        Assert.True(unexpected.Count == 0, string.Join(Environment.NewLine, unexpected.Select(i => $"[{i.Severity}] {i.Where}: {i.Message}")));
        // Every listed build really is core-less (so the list cannot hide a build that got a core back).
        Assert.Equal(expected.Order(StringComparer.Ordinal), issues.Where(IsExpected).Select(i => i.Where).Order(StringComparer.Ordinal));
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

    /// <summary>
    /// Source tags a tier entry may carry: the hand-written draft (colorless and every entry the v2 data left
    /// "불확실"), or the v2 OP.GG top-player pick rates (docs/research/v2-stats/tiers.md).
    /// </summary>
    private static readonly string[] TierSourcePrefixes = ["claude-draft-v1", "opgg-top-a10-2026-11"];

    [Fact]
    public void DraftEntries_AreMarkedAsDraftsWithHonestConfidence()
    {
        foreach (var (character, tiers) in Pack.Value.Tiers)
        {
            foreach (var (id, e) in tiers.Cards)
            {
                Assert.True(TierSourcePrefixes.Any(p => e.Source.StartsWith(p, StringComparison.Ordinal)), $"{character} {id}: source '{e.Source}'");
                Assert.InRange(e.Confidence, 0.3, 0.7);
                Assert.DoesNotContain("CARD.", e.Tip ?? "", StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Archetype count per character, as documented in data/packs/v0.107.1/README.md (top-player build sources).</summary>
    [Theory]
    [InlineData("CHARACTER.IRONCLAD", 8)]
    [InlineData("CHARACTER.SILENT", 3)]
    [InlineData("CHARACTER.DEFECT", 6)]
    [InlineData("CHARACTER.NECROBINDER", 3)]
    [InlineData("CHARACTER.REGENT", 4)]
    public void EveryCharacter_HasTheDocumentedArchetypeCount(string character, int expected)
    {
        Assert.Equal(expected, Pack.Value.Archetypes.Count(a => a.Character == character));
    }

    // ---- v2 measured rates: events.stats.json, relics.json ----

    private const string StatsSourcePrefix = "opgg-top-a10-2026-11";

    private Lazy<EventCatalog> EventsJson => new(() => Cached("events", () => EventCatalog.LoadFile(Path.Combine(PackDir, "events.json"))));

    private Lazy<HashSet<string>> KnownRelics => new(() => Cached("relics", () =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "data", "catalog", $"relics.{Version}.json")));
        return doc.RootElement.GetProperty("relics").EnumerateArray().Select(r => r.GetProperty("id").GetString() ?? "").ToHashSet(StringComparer.Ordinal);
    }));

    /// <summary>
    /// Ids in the v2 data that the v0.107.1 game does not have: the sample pools v0.107.x with the v0.108–v0.111 beta.
    /// NEOWS_SACRIFICE / DOWSING_ROD are not in the v0.107.1 relic catalog; the OROBAS ids are character ids, not v0.107.1 OROBAS buttons.
    /// They never match a key at runtime (no v0.107.1 option has these ids), so they are inert; listed so a new unknown id still fails.
    /// </summary>
    protected abstract string[] NewerAncientOptions { get; }

    protected abstract string[] NewerRelics { get; }

    /// <summary>Options in events.stats.json (docs/research/v2-stats/events.md).</summary>
    protected abstract int EventStatsOptionCount { get; }

    protected static readonly string[] NewerAncientOptionsV0107 = ["NEOW.NEOWS_SACRIFICE", "OROBAS.IRONCLAD", "OROBAS.REGENT", "OROBAS.SILENT"];

    protected static readonly string[] NewerRelicsV0107 = ["RELIC.DOWSING_ROD", "RELIC.NEOWS_SACRIFICE"];

    [Fact]
    public void StatsFiles_AreLoaded_WithTheV2Source()
    {
        var events = Assert.IsType<EventStats>(Pack.Value.EventStats);
        var relics = Assert.IsType<RelicStats>(Pack.Value.RelicStats);
        Assert.StartsWith(StatsSourcePrefix, events.Source, StringComparison.Ordinal);
        Assert.StartsWith(StatsSourcePrefix, relics.Source, StringComparison.Ordinal);
        Assert.NotEmpty(events.Events);
        Assert.NotEmpty(events.Ancients);
        Assert.Contains(relics.Relics.Values, r => r.Use == true);
    }

    [Fact]
    public void EveryEventStatsOption_ExistsInEventsJson()
    {
        var missing = new List<string>();
        var count = 0;
        foreach (var (eventId, pages) in Pack.Value.EventStats!.Events)
        {
            Assert.NotNull(EventsJson.Value.Find($"EVENT.{eventId}"));
            foreach (var (page, p) in pages)
            {
                Assert.True(p.Options.Count <= p.K, $"{eventId}.{page}: {p.Options.Count} options listed but k = {p.K}");
                foreach (var option in p.Options.Keys)
                {
                    count++;
                    var key = $"{eventId}.pages.{page}.options.{option}";
                    if (EventsJson.Value.FindOption(key) is null) missing.Add(key);
                }
            }
        }
        Assert.True(missing.Count == 0, "Not in events.json:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
        Assert.Equal(EventStatsOptionCount, count);
    }

    [Fact]
    public void EveryAncientStatsOption_IsAnAncientOptionInEventsJson_OrADocumentedNewerId()
    {
        var unmatched = new List<string>();
        foreach (var (ancient, options) in Pack.Value.EventStats!.Ancients)
        {
            var def = EventsJson.Value.Find($"EVENT.{ancient}");
            Assert.True(def is { IsAncient: true }, $"{ancient} is not an ancient in events.json");
            foreach (var option in options.Keys)
            {
                // The key is built the way NeowOptions / the log name an ancient's buttons.
                if (EventsJson.Value.FindOption($"{ancient}.pages.INITIAL.options.{option}") is null) unmatched.Add($"{ancient}.{option}");
            }
        }
        Assert.Equal(NewerAncientOptions.Order(StringComparer.Ordinal), unmatched.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryRelicStatsId_IsAKnownRelic_OrADocumentedNewerId()
    {
        var unknown = Pack.Value.RelicStats!.Relics.Keys.Where(id => !KnownRelics.Value.Contains(id)).Order(StringComparer.Ordinal);
        Assert.Equal(NewerRelics.Order(StringComparer.Ordinal), unknown);
    }

    [Fact]
    public void RelicFit_HoldsOnlyUsableEntries_ForPackBuilds_WithKnownRelics()
    {
        var fit = Assert.IsType<RelicFit>(Pack.Value.RelicFit);
        var builds = Pack.Value.Archetypes.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(fit.Archetypes);
        foreach (var (build, relics) in fit.Archetypes)
        {
            Assert.Contains(build, builds);
            foreach (var (id, stat) in relics)
            {
                Assert.True(stat.Use, $"{build} {id}: the pack file keeps use:true entries only");
                Assert.True(stat.FitRatio is >= 1.5 or <= 0.67, $"{build} {id}: fitRatio {stat.FitRatio} inside the dead band");
                Assert.True(KnownRelics.Value.Contains(id) || NewerRelics.Contains(id), $"{build} {id}: unknown relic");
            }
        }
    }
}

/// <summary>The v0.107.1 pack against the v0.107.1 catalogs.</summary>
public sealed class RealPackTestsV0107 : RealPackTests
{
    protected override string Version => "v0.107.1";
    protected override string[] IntentionallyCoreless => IntentionallyCorelessV0107;
    protected override string[] NewerAncientOptions => NewerAncientOptionsV0107;
    protected override string[] NewerRelics => NewerRelicsV0107;
    protected override int EventStatsOptionCount => 115;
}

/// <summary>The v0.111.0 pack against the v0.111.0 catalogs.</summary>
public sealed class RealPackTestsV0111 : RealPackTests
{
    protected override string Version => "v0.111.0";
    /// <summary>
    /// v0.111.0: the v0.107.1 list plus the builds whose core moved to payoff after the expert tier check
    /// (docs/research/build-sources/v0111/EXPERT_TIERS.md): BARRICADE, RUPTURE, HELLRAISER, CALCIFY, FURNACE.
    /// </summary>
    protected override string[] IntentionallyCoreless =>
    [
        .. IntentionallyCorelessV0107,
        "ironclad.block", "ironclad.hellraiser", "ironclad.self_damage",
        "necrobinder.osty",
        "regent.forge",
    ];
    // v0.111 has Neow's Sacrifice; the OROBAS ids are still character ids, not OROBAS buttons.
    protected override string[] NewerAncientOptions => ["OROBAS.IRONCLAD", "OROBAS.REGENT", "OROBAS.SILENT"];
    protected override string[] NewerRelics => [];
    protected override int EventStatsOptionCount => 115;
}
