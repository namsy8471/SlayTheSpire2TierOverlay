using System.Text.Json;
using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Tests.Data;

public class DataPackLoaderTests
{
    [Fact]
    public void JsonOptions_AreSharedAndReadOnly()
    {
        var o = DataPackLoader.JsonOptions;
        Assert.Same(o, DataPackLoader.JsonOptions);
        Assert.True(o.IsReadOnly);
        Assert.Equal(JsonCommentHandling.Skip, o.ReadCommentHandling);
        Assert.True(o.AllowTrailingCommas);
        Assert.True(o.PropertyNameCaseInsensitive);
    }

    [Fact]
    public void LoadWeights_EmptyObject_IsDefault()
    {
        var w = DataPackLoader.LoadWeights("{}");
        var d = Weights.Default;
        Assert.Equal(d.WArch, w.WArch);
        Assert.Equal(d.TierValue, w.TierValue);
        Assert.Equal(d.Skip.Margin, w.Skip.Margin);
        Assert.Equal(d.Completion.CardsPerReward, w.Completion.CardsPerReward);
        Assert.Equal(d.WeakRoles, w.WeakRoles);
    }

    [Fact]
    public void LoadWeights_Partial_KeepsDefaultsIncludingNestedObjects()
    {
        var w = DataPackLoader.LoadWeights("""
            {
              "wArch": 30,
              "skip": { "margin": 2.5 },
              "cardOdds": { "eliteReward": { "rare": 0.12 }, "pity": { "cap": 0.5 } },
              "situation": { "lowHpThreshold": 0.4 }
            }
            """);
        var d = Weights.Default;
        Assert.Equal(30, w.WArch);
        Assert.Equal(d.BuildCap, w.BuildCap);

        Assert.Equal(2.5, w.Skip.Margin);
        Assert.Equal(d.Skip.LambdaSize, w.Skip.LambdaSize);
        Assert.Equal(d.Skip.LowConfidence, w.Skip.LowConfidence);
        Assert.Equal(d.Skip.T0, w.Skip.T0);

        Assert.Equal(0.4, w.Situation.LowHpThreshold);
        Assert.Equal(d.Situation.BossRewardBuildBonus, w.Situation.BossRewardBuildBonus);

        Assert.Equal(0.12, w.CardOdds.EliteReward.Rare);
        Assert.Equal(0.40, w.CardOdds.EliteReward.Uncommon);
        Assert.Equal(0.05, w.CardOdds.EliteReward.RareHighAscension);
        Assert.Equal(0.5, w.CardOdds.Pity.Cap);
        Assert.Equal(-0.05, w.CardOdds.Pity.Start);
        Assert.Equal(d.CardOdds.CardReward.Rare, w.CardOdds.CardReward.Rare);
        Assert.Equal(d.Completion.PoolSize, w.Completion.PoolSize);
    }

    [Fact]
    public void LoadWeights_Dictionaries_MergeKeys_ArraysReplace()
    {
        var w = DataPackLoader.LoadWeights("""
            {
              "tierValue": { "S": 90 },
              "skip": { "t0": { "2": 60 } },
              "targets": { "CHARACTER.DEFECT": { "1": { "draw": 2 } } },
              "weakRoles": ["block"]
            }
            """);
        Assert.Equal(90, w.TierValue[Tier.S]);
        Assert.Equal(65, w.TierValue[Tier.A]);
        Assert.Equal(20, w.TierValue[Tier.D]);

        Assert.Equal(45, w.Skip.T0[1]);
        Assert.Equal(60, w.Skip.T0[2]);
        Assert.Equal(58, w.Skip.T0[3]);

        Assert.True(w.Targets.ContainsKey("default"));
        Assert.Equal(2, w.Targets["CHARACTER.DEFECT"][1]["draw"]);

        Assert.Equal(["block"], w.WeakRoles);
    }

    [Fact]
    public void LoadWeights_DoesNotMutateDefaultsOfOtherInstances()
    {
        _ = DataPackLoader.LoadWeights("""{ "tierValue": { "S": 1 }, "skip": { "margin": 9 } }""");
        var fresh = DataPackLoader.LoadWeights("{}");
        Assert.Equal(80, fresh.TierValue[Tier.S]);
        Assert.Equal(0.5, fresh.Skip.Margin);
    }

    [Fact]
    public void LoadWeights_ActKeyedDictionaries()
    {
        var w = DataPackLoader.LoadWeights("""
            { "idealDeckSize": { "1": 12, "4": 30 }, "urgency": { "2": { "block": 2.0 } } }
            """);
        Assert.Equal(12, w.IdealDeckSize[1]);
        Assert.Equal(20, w.IdealDeckSize[2]);
        Assert.Equal(30, w.IdealDeckSize[4]);
        Assert.Equal(2.0, w.Urgency[2]["block"]);
        Assert.Equal(1.4, w.Urgency[1]["frontload"]);
    }

    [Fact]
    public void LoadTierPack_ParsesAllFields_WithCommentsAndTrailingCommas()
    {
        var pack = DataPackLoader.LoadTierPack("""
            // header comment
            {
              "character": "CHARACTER.DEFECT",
              "dataVersion": "v0.107.1",
              "cards": {
                "CARD.BALL_LIGHTNING": {
                  "tier": "B",
                  "actTier": { "1": "A", "3": "c" }, /* lower-case tier is accepted */
                  "upgradedDelta": 5,
                  "roles": { "frontload": 0.6, "aoe": 0.0, },
                  "provides": ["ORB_LIGHTNING"],
                  "dupFactor": 0.8,
                  "cycle": 0.1,
                  "tip": "팁",
                  "confidence": 0.7,
                  "changedIn": null,
                  "source": "curated",
                  "type": "attack", "rarity": "common", "cost": 1,
                },
                "CARD.ZAP": { "tier": "S" },
              },
            }
            """);
        Assert.Equal("CHARACTER.DEFECT", pack.Character);
        Assert.Equal("v0.107.1", pack.DataVersion);
        var e = pack.Cards["CARD.BALL_LIGHTNING"];
        Assert.Equal(Tier.B, e.Tier);
        Assert.Equal(Tier.A, e.ActTier![1]);
        Assert.Equal(Tier.C, e.ActTier[3]);
        Assert.Equal(5, e.UpgradedDelta);
        Assert.Equal(0.6, e.Roles["frontload"]);
        Assert.Equal(["ORB_LIGHTNING"], e.Provides);
        Assert.Equal(0.8, e.DupFactor);
        Assert.Equal(0.1, e.Cycle);
        Assert.Equal(0.7, e.Confidence);
        Assert.Null(e.ChangedIn);
        Assert.Equal("attack", e.Type);
        Assert.Equal(1, e.Cost);

        var zap = pack.Cards["CARD.ZAP"];
        Assert.Equal(Tier.S, zap.Tier);
        Assert.Equal(1.0, zap.DupFactor);
        Assert.Equal(1.0, zap.Confidence);
        Assert.Equal("curated", zap.Source);
        Assert.Null(zap.ActTier);
    }

    [Fact]
    public void LoadTierPack_KeepsDictionaryKeysExact()
    {
        var pack = DataPackLoader.LoadTierPack("""{ "character": "COLORLESS", "cards": { "CARD.X": { "tier": "A", "roles": { "Exhaust_Fodder": 1 } } } }""");
        Assert.True(pack.Cards.ContainsKey("CARD.X"));
        Assert.True(pack.Cards["CARD.X"].Roles.ContainsKey("Exhaust_Fodder"));
        Assert.False(pack.Cards.ContainsKey("cARD.X"));
    }

    [Fact]
    public void LoadTierPack_NumericTier_IsRejected()
        => Assert.ThrowsAny<JsonException>(() => DataPackLoader.LoadTierPack("""{ "cards": { "CARD.X": { "tier": 0 } } }"""));

    [Fact]
    public void LoadArchetypes_ArrayForm()
    {
        var list = DataPackLoader.LoadArchetypes("""
            [{
              "id": "defect.orbs", "character": "CHARACTER.DEFECT",
              "names": { "kor": "구체" },
              "cards": { "core": ["CARD.DEFRAGMENT"], "support": ["CARD.HOLOGRAM"] },
              "roleWeight": { "core": 1.5 },
              "fullWeight": 3,
              "keyRelics": [{ "id": "RELIC.CRACKED_CORE", "w": 0.5 }],
              "needs": [{ "tag": "ORB_FROST", "min": 2 }, { "role": "draw" }],
              "guideHeroCard": "CARD.DEFRAGMENT"
            }]
            """);
        var a = Assert.Single(list);
        Assert.Equal("defect.orbs", a.Id);
        Assert.Equal("구체", a.DisplayName);
        Assert.Equal(["CARD.DEFRAGMENT"], a.Cards.Core);
        Assert.Empty(a.Cards.Enabler);
        Assert.Equal(1.5, a.RoleWeight["core"]);
        Assert.Equal(0.7, a.RoleWeight["enabler"]); // partial roleWeight keeps the other defaults
        Assert.Equal(3, a.FullWeight);
        Assert.Equal(0.7, a.CommitThreshold);
        Assert.Equal(0.5, a.KeyRelics[0].W);
        Assert.Equal("ORB_FROST", a.Needs[0].Tag);
        Assert.Equal(2, a.Needs[0].Min);
        Assert.Equal("draw", a.Needs[1].Role);
        Assert.Equal(1, a.Needs[1].Min);
    }

    [Fact]
    public void LoadArchetypes_ObjectForm_AppliesTopLevelCharacter()
    {
        var list = DataPackLoader.LoadArchetypes("""
            { "character": "CHARACTER.DEFECT", "archetypes": [
                { "id": "a", "cards": { "core": ["CARD.A"] } },
                { "id": "b", "character": "CHARACTER.SILENT", "cards": { "core": ["CARD.B"] } },
            ] }
            """);
        Assert.Equal("CHARACTER.DEFECT", list[0].Character);
        Assert.Equal("CHARACTER.SILENT", list[1].Character);
    }

    [Fact]
    public void LoadCombos_BothForms()
    {
        var arr = DataPackLoader.LoadCombos("""[{ "cards": ["CARD.A", "CARD.B"], "name": "x" }]""");
        Assert.Null(Assert.Single(arr).Character);
        Assert.Equal(6, arr[0].W);

        var obj = DataPackLoader.LoadCombos("""
            { "character": "CHARACTER.DEFECT", "combos": [
                { "cards": ["CARD.A", "CARD.B"], "name": "x", "w": 4 },
                { "character": "CHARACTER.SILENT", "cards": ["CARD.C", "CARD.D"], "name": "y" }
            ] }
            """);
        Assert.Equal("CHARACTER.DEFECT", obj[0].Character);
        Assert.Equal(4, obj[0].W);
        Assert.Equal("CHARACTER.SILENT", obj[1].Character);
    }

    [Fact]
    public void LoadCombos_ObjectWithoutArray_Throws()
        => Assert.ThrowsAny<JsonException>(() => DataPackLoader.LoadCombos("""{ "character": "CHARACTER.DEFECT" }"""));

    [Fact]
    public void LoadCatalog_Parses()
    {
        var c = DataPackLoader.LoadCatalog("""
            { "gameVersion": "v0.107.1", "commit": "abc",
              "cards": [{ "id": "CARD.X", "pool": "CHARACTER.DEFECT", "type": "attack", "rarity": "common", "cost": 1,
                          "costX": false, "multiplayerOnly": true, "upgrade": { "maxLevel": 1 } }] }
            """);
        Assert.Equal("v0.107.1", c.GameVersion);
        var card = Assert.Single(c.Cards);
        Assert.Equal("CHARACTER.DEFECT", card.Pool);
        Assert.True(card.MultiplayerOnly);
    }

    [Fact]
    public void LoadAliases_Parses()
    {
        var a = DataPackLoader.LoadAliases("""{ "CARD.OLD": "CARD.NEW", /* c */ }""");
        Assert.Equal("CARD.NEW", a["CARD.OLD"]);
    }

    [Fact]
    public void LoadDirectory_FullPack()
    {
        using var dir = new TempDir();
        dir.Write("weights.json", """{ "skip": { "margin": 3 } }""");
        dir.Write("tiers/defect.json", """{ "character": "CHARACTER.DEFECT", "cards": { "CARD.HOLOGRAM": { "tier": "A" } } }""");
        dir.Write("tiers/colorless.json", """{ "character": "COLORLESS", "cards": { "CARD.C": { "tier": "D" } } }""");
        dir.Write("archetypes/defect.json", """[{ "id": "d.a", "character": "CHARACTER.DEFECT", "cards": { "core": ["CARD.HOLOGRAM"] } }]""");
        dir.Write("archetypes/silent.json", """{ "character": "CHARACTER.SILENT", "archetypes": [{ "id": "s.a", "cards": { "core": ["CARD.X"] } }] }""");
        dir.Write("combos/defect.json", """{ "character": "CHARACTER.DEFECT", "combos": [{ "cards": ["CARD.HOLOGRAM", "CARD.C"], "name": "c" }] }""");
        dir.Write("aliases.json", """{ "CARD.OLD_HOLOGRAM": "CARD.HOLOGRAM" }""");
        dir.Write("catalog.json", """{ "cards": [{ "id": "CARD.HOLOGRAM", "pool": "CHARACTER.DEFECT", "rarity": "common" }] }""");
        dir.Write("tiers/notes.txt", "ignored");

        var pack = DataPackLoader.LoadDirectory(dir.Path);
        Assert.Equal(3, pack.Weights.Skip.Margin);
        Assert.Equal(52, pack.Weights.Skip.T0[2]);
        Assert.Equal(2, pack.Tiers.Count);
        Assert.Equal(Tier.A, pack.FindTier("CHARACTER.DEFECT", "CARD.OLD_HOLOGRAM")!.Tier);
        Assert.Equal(["d.a", "s.a"], pack.Archetypes.Select(a => a.Id));
        Assert.Equal("CHARACTER.SILENT", pack.Archetypes[1].Character);
        Assert.Equal("CHARACTER.DEFECT", Assert.Single(pack.Combos).Character);
        Assert.Equal("CARD.HOLOGRAM", pack.Canonical("CARD.OLD_HOLOGRAM"));
        Assert.NotNull(pack.Catalog);
        Assert.Equal("common", pack.CardRarity("CHARACTER.DEFECT", "CARD.HOLOGRAM"));
    }

    [Fact]
    public void LoadDirectory_EmptyDirectory_GivesDefaults()
    {
        using var dir = new TempDir();
        var pack = DataPackLoader.LoadDirectory(dir.Path);
        Assert.Equal(Weights.Default.WArch, pack.Weights.WArch);
        Assert.Empty(pack.Tiers);
        Assert.Empty(pack.Archetypes);
        Assert.Empty(pack.Combos);
        Assert.Empty(pack.IdAliases);
        Assert.Null(pack.Catalog);
    }

    [Fact]
    public void LoadDirectory_CatalogArgumentWinsOverFile()
    {
        using var dir = new TempDir();
        dir.Write("catalog.json", """{ "gameVersion": "file", "cards": [] }""");
        var pack = DataPackLoader.LoadDirectory(dir.Path, new CardCatalog { GameVersion = "arg" });
        Assert.Equal("arg", pack.Catalog!.GameVersion);
    }

    [Fact]
    public void LoadDirectory_Missing_Throws()
        => Assert.Throws<DirectoryNotFoundException>(() => DataPackLoader.LoadDirectory(Path.Combine(Path.GetTempPath(), "sts2-no-such-" + Guid.NewGuid())));

    [Theory]
    [InlineData("weights.json", "{ \"wArch\": }")]
    [InlineData("tiers/defect.json", "{ \"character\": \"CHARACTER.DEFECT\", \"cards\": { \"CARD.X\": { \"tier\": \"Z\" } } }")]
    [InlineData("archetypes/defect.json", "[{ \"id\": 1 ")]
    [InlineData("combos/defect.json", "\"just a string\"")]
    [InlineData("aliases.json", "[1, 2]")]
    [InlineData("catalog.json", "null")]
    public void LoadDirectory_MalformedFile_MessageContainsPath(string relative, string content)
    {
        using var dir = new TempDir();
        var path = dir.Write(relative, content);
        var ex = Assert.Throws<InvalidDataException>(() => DataPackLoader.LoadDirectory(dir.Path));
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void LoadDirectory_TierPackWithoutCharacter_Throws()
    {
        using var dir = new TempDir();
        var path = dir.Write("tiers/x.json", """{ "cards": {} }""");
        var ex = Assert.Throws<InvalidDataException>(() => DataPackLoader.LoadDirectory(dir.Path));
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void LoadDirectory_DuplicateTierCharacter_Throws()
    {
        using var dir = new TempDir();
        dir.Write("tiers/a.json", """{ "character": "CHARACTER.DEFECT", "cards": {} }""");
        var second = dir.Write("tiers/b.json", """{ "character": "CHARACTER.DEFECT", "cards": {} }""");
        var ex = Assert.Throws<InvalidDataException>(() => DataPackLoader.LoadDirectory(dir.Path));
        Assert.Contains(second, ex.Message);
    }

    // ---- Optional measured-rate files: events.stats.json, relics.json ----

    private static void WriteSmallPack(TempDir dir)
    {
        dir.Write("weights.json", """{ "skip": { "margin": 3 } }""");
        dir.Write("tiers/defect.json", """{ "character": "CHARACTER.DEFECT", "cards": { "CARD.HOLOGRAM": { "tier": "A" } } }""");
        dir.Write("archetypes/defect.json", """[{ "id": "d.a", "character": "CHARACTER.DEFECT", "cards": { "core": ["CARD.HOLOGRAM"] } }]""");
    }

    private const string EventStatsJsonc = """
    // comment line, as in the real file
    {
      "version": 1, "source": "test",
      "events": { "ABYSSAL_BATHS": { "INITIAL": { "k": 2, "options": {
        "ABSTAIN": { "choiceRate": 0.648, "n": 254, "players": 17, "dirBoot": 0.95, "byAct": { "3": 0.7 }, }, // trailing comma + unknown field
      } } } },
      "ancients": { "NEOW": { "LEAD_PAPERWEIGHT": { "pickRate": 0.62, "offered": 300, "basis": "good" } } },
    }
    """;

    private const string RelicStatsJsonc = """
    /* block comment */
    { "version": 1, "source": "test", "shopMedianPickRate": 0.327, "ancientNeutralPickRate": 0.333,
      "relics": { "RELIC.MINIATURE_TENT": { "signal": "shop", "use": true, "pickRate": 0.645, "tier": "A", "eff_n": 320.2 }, } }
    """;

    [Fact]
    public void LoadDirectory_WithoutStatsFiles_BothNull_AndEverythingElseIdentical()
    {
        using var without = new TempDir();
        WriteSmallPack(without);
        using var with = new TempDir();
        WriteSmallPack(with);
        with.Write("events.stats.json", EventStatsJsonc);
        with.Write("relics.json", RelicStatsJsonc);

        var a = DataPackLoader.LoadDirectory(without.Path);
        var b = DataPackLoader.LoadDirectory(with.Path);
        Assert.Null(a.EventStats);
        Assert.Null(a.RelicStats);
        Assert.NotNull(b.EventStats);
        Assert.NotNull(b.RelicStats);

        var o = DataPackLoader.JsonOptions;
        Assert.Equal(JsonSerializer.Serialize(a.Weights, o), JsonSerializer.Serialize(b.Weights, o));
        Assert.Equal(JsonSerializer.Serialize(a.Tiers, o), JsonSerializer.Serialize(b.Tiers, o));
        Assert.Equal(JsonSerializer.Serialize(a.Archetypes, o), JsonSerializer.Serialize(b.Archetypes, o));
        Assert.DoesNotContain(DataPackValidator.Validate(a), i => i.Severity == ValidationSeverity.Error);
        Assert.DoesNotContain(DataPackValidator.Validate(b), i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void LoadStats_JsoncWithCommentsAndTrailingCommas_Parses()
    {
        var s = DataPackLoader.LoadEventStats(EventStatsJsonc);
        var page = s.Events["ABYSSAL_BATHS"]["INITIAL"];
        Assert.Equal(2, page.K);
        Assert.Equal(0.648, page.Options["ABSTAIN"].ChoiceRate);
        Assert.Equal(0.7, page.Options["ABSTAIN"].ByAct![3]);
        Assert.Equal(0.62, s.Ancients["NEOW"]["LEAD_PAPERWEIGHT"].PickRate);
        Assert.Equal(new MeasuredChoice(0.7, 0.5, true), s.Find("ABYSSAL_BATHS.pages.INITIAL.options.ABSTAIN", act: 3));
        Assert.Equal(new MeasuredChoice(0.648, 0.5, false), s.Find("ABYSSAL_BATHS.pages.INITIAL.options.ABSTAIN", act: 1));
        Assert.Equal(0.62, s.Find("NEOW.pages.INITIAL.options.LEAD_PAPERWEIGHT", act: 1)!.Value.Rate);
        Assert.Null(s.Find("ABYSSAL_BATHS.pages.INITIAL.options.IMMERSE", act: 1));
        Assert.Null(s.Find("not a key", act: 1));

        var r = DataPackLoader.LoadRelicStats(RelicStatsJsonc);
        Assert.Equal(0.327, r.ShopMedianPickRate);
        var tent = r.Relics["RELIC.MINIATURE_TENT"];
        Assert.True(tent.Use);
        Assert.Equal("A", tent.Tier);
        Assert.Equal(0.645, tent.PickRate);
    }

    [Theory]
    [InlineData("events.stats.json", "{ \"events\": ")]
    [InlineData("events.stats.json", "{ \"events\": { \"X\": { \"INITIAL\": { \"k\": \"two\" } } } }")]
    [InlineData("relics.json", "[1, 2]")]
    [InlineData("relics.json", "{ \"relics\": { \"RELIC.X\": { \"use\": \"yes\" } } }")]
    public void LoadDirectory_BrokenStatsFile_ThrowsWithItsPathFirst(string relative, string content)
    {
        using var dir = new TempDir();
        WriteSmallPack(dir);
        var path = dir.Write(relative, content);
        var ex = Assert.Throws<InvalidDataException>(() => DataPackLoader.LoadDirectory(dir.Path));
        Assert.StartsWith(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadWeights_Events_MergesIntoDefaults_AndRoundTrips()
    {
        var w = DataPackLoader.LoadWeights("""{ "events": { "measuredBlend": 0, "relicTier": { "shop": 18 } } }""");
        Assert.Equal(0, w.Events.MeasuredBlend);
        Assert.Equal(20, w.Events.MeasuredScale);                 // untouched default
        Assert.Equal(18, w.Events.RelicTier["shop"]);
        Assert.Equal(13, w.Events.RelicTier["COMMON"]);           // other keys kept, comparer still case-insensitive
        Assert.Equal(2.2, w.Events.MaxHpFactor[1]);

        var again = DataPackLoader.LoadWeights(JsonSerializer.Serialize(w, DataPackLoader.JsonOptions));
        Assert.Equal(0, again.Events.MeasuredBlend);
        Assert.Equal(18, again.Events.RelicTier["shop"]);
        Assert.Equal(JsonSerializer.Serialize(w, DataPackLoader.JsonOptions), JsonSerializer.Serialize(again, DataPackLoader.JsonOptions));
        Assert.DoesNotContain(DataPackValidator.Validate(new DataPack { Weights = w }), i => i.Severity == ValidationSeverity.Error);

        Assert.Equal(0.5, new Weights().Events.MeasuredBlend); // defaults of other instances untouched
    }

    [Theory]
    [InlineData("""{ "events": { "measuredBlend": 1.5 } }""", "weights.events.measuredBlend")]
    [InlineData("""{ "events": { "measuredBlend": -0.1 } }""", "weights.events.measuredBlend")]
    [InlineData("""{ "events": { "measuredScale": -1 } }""", "weights.events.measuredScale")]
    public void Validate_EventsWeightsOutOfRange_IsAnError(string json, string where)
    {
        var issues = DataPackValidator.Validate(new DataPack { Weights = DataPackLoader.LoadWeights(json) });
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == where);
    }

    [Fact]
    public void Validate_StatsFiles_RejectBadRatesKUseTierAndIds()
    {
        var events = DataPackLoader.LoadEventStats("""
        { "events": { "E": { "INITIAL": { "k": 0, "options": { "A": { "choiceRate": 1.2, "byAct": { "0": 0.5, "2": -0.1 } } } } } },
          "ancients": { "NEOW": { "X": { "pickRate": 2 } } } }
        """);
        var relics = DataPackLoader.LoadRelicStats("""
        { "shopMedianPickRate": 0, "ancientNeutralPickRate": 0.333, "relics": {
          "relic.lower": { "signal": "shop", "use": true, "pickRate": 0.5 },
          "RELIC.NO_USE": { "signal": "shop", "pickRate": 0.5 },
          "RELIC.BAD_TIER": { "signal": "ancient:NEOW", "use": false, "pickRate": 0.5, "tier": "E" },
          "RELIC.BAD_RATE": { "signal": "elite", "use": true, "pickRate": 1.5 } } }
        """);
        var errors = DataPackValidator.Validate(new DataPack { EventStats = events, RelicStats = relics })
            .Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.Where).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("events.stats.events[E].INITIAL.k", errors);
        Assert.Contains("events.stats.events[E].INITIAL.options[A].choiceRate", errors);
        Assert.Contains("events.stats.events[E].INITIAL.options[A].byAct", errors);
        Assert.Contains("events.stats.events[E].INITIAL.options[A].byAct.2", errors);
        Assert.Contains("events.stats.ancients[NEOW].X.pickRate", errors);
        Assert.Contains("relics.shopMedianPickRate", errors);
        Assert.Contains("relics[relic.lower]", errors);
        Assert.Contains("relics[RELIC.NO_USE].use", errors);
        Assert.Contains("relics[RELIC.BAD_TIER].tier", errors);
        Assert.Contains("relics[RELIC.BAD_RATE].pickRate", errors);
        Assert.Contains("relics[RELIC.BAD_RATE].signal", errors);
        Assert.Equal(11, errors.Count);
    }

    [Fact]
    public void SamplePack_LoadsAndValidatesClean()
    {
        var pack = DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "sample"));

        Assert.Equal(82, pack.Weights.TierValue[Tier.S]);
        Assert.Equal(65, pack.Weights.TierValue[Tier.A]);
        Assert.Equal(1.0, pack.Weights.Skip.Margin);
        Assert.Equal(Weights.Default.Skip.LambdaSize, pack.Weights.Skip.LambdaSize);
        Assert.True(pack.Weights.Targets.ContainsKey("default"));
        Assert.True(pack.Weights.Targets.ContainsKey("CHARACTER.DEFECT"));

        Assert.Contains("CHARACTER.DEFECT", pack.Tiers.Keys);
        Assert.Contains(DataPack.Colorless, pack.Tiers.Keys);
        Assert.All(pack.Tiers.Values.SelectMany(t => t.Cards.Values), e => Assert.Equal("placeholder", e.Source));
        Assert.Equal(Tier.A, pack.Tiers["CHARACTER.DEFECT"].Cards["CARD.BALL_LIGHTNING"].ActTier![1]);

        var arch = Assert.Single(pack.Archetypes);
        Assert.Equal("CHARACTER.DEFECT", arch.Character);
        Assert.Equal("CHARACTER.DEFECT", Assert.Single(pack.Combos).Character);
        Assert.Equal("CARD.HOLOGRAM", pack.Canonical("CARD.SAMPLE_OLD_HOLOGRAM"));
        Assert.Null(pack.Catalog); // catalog.example.json is not loaded

        Assert.Empty(DataPackValidator.Validate(pack));
    }

    [Fact]
    public void SampleCatalogExample_ParsesAsCatalog()
    {
        var path = Path.Combine(TestPaths.RepoRoot, "data", "packs", "sample", "catalog.example.json");
        var catalog = DataPackLoader.LoadCatalogFile(path);
        Assert.Equal(["CARD.BALL_LIGHTNING", "CARD.SAMPLE_COLORLESS"], catalog.Cards.Select(c => c.Id));
        Assert.Equal(catalog.CardCount, catalog.Cards.Count);
        Assert.All(catalog.Cards, c => Assert.Empty(c.Names));
        Assert.Equal("CHARACTER.DEFECT", catalog.Cards[0].PoolOwner);
        Assert.Equal(DataPack.Colorless, catalog.Cards[1].PoolOwner);
        Assert.Null(catalog.Cards[1].Character);
        Assert.True(catalog.Cards[1].MultiplayerOnly);

        // Same shape as the real catalog: every property of the example card exists on the real one.
        var real = DataPackLoader.LoadCatalogFile(Path.Combine(TestPaths.RepoRoot, DataPackLoader.CatalogRelativePath("v0.107.1")));
        var ball = real.Cards.Single(c => c.Id == "CARD.BALL_LIGHTNING");
        Assert.Equal(ball.Pool, catalog.Cards[0].Pool);
        Assert.Equal(ball.Type, catalog.Cards[0].Type);
        Assert.Equal(ball.Rarity, catalog.Cards[0].Rarity);
    }
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sts2-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Writes a file relative to the directory and returns its full path.</summary>
    public string Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
