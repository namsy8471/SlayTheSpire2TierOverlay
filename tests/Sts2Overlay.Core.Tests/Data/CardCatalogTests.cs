using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Tests.Data;

public class CardCatalogTests
{
    private const string Defect = "CHARACTER.DEFECT";

    [Fact]
    public void RealCatalog_LoadsAsIs()
    {
        var c = RealCatalog.Instance;
        Assert.Equal(578, c.Cards.Count);
        Assert.Equal(578, c.CardCount);
        Assert.Equal("v0.107.1", c.GameVersion);
        Assert.False(string.IsNullOrEmpty(c.Source));
        Assert.False(string.IsNullOrEmpty(c.Note));
        Assert.Equal(578, c.Cards.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(c.Cards, x => Assert.Empty(x.Names)); // names-free
        Assert.All(c.Cards, x => Assert.True(DataPackValidator.IsCardId(x.Id), x.Id));
    }

    [Fact]
    public void RealCatalog_ExtraFields()
    {
        var c = RealCatalog.Instance;
        var anger = c.Cards.Single(x => x.Id == "CARD.ANGER");
        Assert.Equal("CARD_POOL.IRONCLAD_CARD_POOL", anger.Pool);
        Assert.Equal("CHARACTER.IRONCLAD", anger.Character);
        Assert.Equal("Attack", anger.Type);
        Assert.Equal("Common", anger.Rarity);
        Assert.Equal(-1, anger.StarCost);
        Assert.Equal(1, anger.MaxUpgrade);
        Assert.Equal("None", anger.Multiplayer);
        Assert.False(anger.MultiplayerOnly);
        Assert.False(anger.SingleplayerOnly);
        Assert.True(anger.ShowInLibrary);

        Assert.True(c.Cards.Single(x => x.Id == "CARD.IGNITION").MultiplayerOnly);
        Assert.True(c.Cards.Single(x => x.Id == "CARD.WELL_LAID_PLANS").SingleplayerOnly);
        Assert.Equal(21, c.Cards.Count(x => x.MultiplayerOnly));
        Assert.Equal(1, c.Cards.Count(x => x.SingleplayerOnly));
        Assert.Equal(2, c.Cards.Count(x => !x.ShowInLibrary));
        Assert.Contains(c.Cards, x => x.StarCost >= 0); // Regent
    }

    [Fact]
    public void RealCatalog_PoolOwners()
    {
        var c = RealCatalog.Instance;
        Assert.Equal(88, c.CardsOf(Defect).Count());
        Assert.Equal(64, c.CardsOf(DataPack.Colorless).Count());
        var colorless = c.Cards.Single(x => x.Id == "CARD.AUTOMATION");
        Assert.Null(colorless.Character);
        Assert.Equal(DataPack.Colorless, colorless.PoolOwner);
        Assert.All(c.Cards.Where(x => x.Pool == "CARD_POOL.EVENT_CARD_POOL" || x.Pool == "CARD_POOL.CURSE_CARD_POOL"),
            x => Assert.Null(x.PoolOwner));
        // Every card with a character agrees with its pool's mapping.
        Assert.All(c.Cards.Where(x => x.Character is not null), x => Assert.Equal(x.Character, CatalogCard.PoolOwnerOf(x.Pool)));
    }

    public static TheoryData<string, int, int, int, int, int> SinglePlayerPools => new()
    {
        // character, basic, common, uncommon, rare, ancient
        { "CHARACTER.IRONCLAD", 3, 20, 35, 25, 2 },
        { "CHARACTER.SILENT", 4, 20, 35, 25, 2 },
        { "CHARACTER.DEFECT", 4, 20, 35, 25, 2 },
        { "CHARACTER.NECROBINDER", 4, 20, 35, 25, 2 },
        { "CHARACTER.REGENT", 4, 20, 35, 25, 2 },
    };

    [Theory]
    [MemberData(nameof(SinglePlayerPools))]
    public void RealCatalog_PoolSizes_SinglePlayer(string character, int basic, int common, int uncommon, int rare, int ancient)
    {
        var sizes = RealCatalog.Instance.PoolSizes(character, isMultiplayer: false);
        Assert.Equal(basic, sizes["basic"]);
        Assert.Equal(common, sizes["common"]);
        Assert.Equal(uncommon, sizes["uncommon"]);
        Assert.Equal(rare, sizes["rare"]);
        Assert.Equal(ancient, sizes["ancient"]);
        Assert.Equal(5, sizes.Count);
    }

    [Theory]
    [InlineData("CHARACTER.IRONCLAD", 36, 26)]
    [InlineData("CHARACTER.SILENT", 35, 26)] // one multiplayer-only uncommon replaces the single-player-only one
    [InlineData("CHARACTER.DEFECT", 36, 26)]
    [InlineData("CHARACTER.NECROBINDER", 36, 26)]
    [InlineData("CHARACTER.REGENT", 36, 26)]
    public void RealCatalog_PoolSizes_Multiplayer(string character, int uncommon, int rare)
    {
        var sizes = RealCatalog.Instance.PoolSizes(character, isMultiplayer: true);
        Assert.Equal(20, sizes["common"]);
        Assert.Equal(uncommon, sizes["uncommon"]);
        Assert.Equal(rare, sizes["rare"]);
    }

    [Fact]
    public void RealCatalog_ColorlessPoolSizes()
    {
        var sp = RealCatalog.Instance.PoolSizes(DataPack.Colorless, isMultiplayer: false);
        Assert.Equal(32, sp["uncommon"]);
        Assert.Equal(21, sp["rare"]);
        var mp = RealCatalog.Instance.PoolSizes(DataPack.Colorless, isMultiplayer: true);
        Assert.Equal(39, mp["uncommon"]);
        Assert.Equal(25, mp["rare"]);
    }

    [Theory]
    [InlineData("CARD_POOL.DEFECT_CARD_POOL", "CHARACTER.DEFECT")]
    [InlineData("CARD_POOL.NECROBINDER_CARD_POOL", "CHARACTER.NECROBINDER")]
    [InlineData("CARD_POOL.COLORLESS_CARD_POOL", DataPack.Colorless)]
    [InlineData("CARD_POOL.EVENT_CARD_POOL", null)]
    [InlineData("CARD_POOL.CURSE_CARD_POOL", null)]
    [InlineData("CARD_POOL.TOKEN_CARD_POOL", null)]
    [InlineData("CARD_POOL.STATUS_CARD_POOL", null)]
    [InlineData("CARD_POOL.QUEST_CARD_POOL", null)]
    [InlineData("CARD_POOL.DEPRECATED_CARD_POOL", null)]
    [InlineData("CHARACTER.DEFECT", "CHARACTER.DEFECT")] // older dumps
    [InlineData("COLORLESS", DataPack.Colorless)]
    [InlineData("CARD_POOL._CARD_POOL", null)]
    [InlineData("SOMETHING", null)]
    [InlineData(null, null)]
    public void PoolOwnerOf(string? pool, string? owner) => Assert.Equal(owner, CatalogCard.PoolOwnerOf(pool));

    [Fact]
    public void PoolOwner_PrefersCharacter_AndNoneStringIsNull()
    {
        var c = DataPackLoader.LoadCatalog("""
            { "cards": [
                { "id": "CARD.A", "pool": "CARD_POOL.EVENT_CARD_POOL", "character": "CHARACTER.DEFECT" },
                { "id": "CARD.B", "pool": "CARD_POOL.COLORLESS_CARD_POOL", "character": "None" },
                { "id": "CARD.C", "pool": "CARD_POOL.SILENT_CARD_POOL", "character": null }
            ] }
            """);
        Assert.Equal("CHARACTER.DEFECT", c.Cards[0].PoolOwner);
        Assert.Null(c.Cards[1].Character);
        Assert.Equal(DataPack.Colorless, c.Cards[1].PoolOwner);
        Assert.Equal("CHARACTER.SILENT", c.Cards[2].PoolOwner);
    }

    [Fact]
    public void MultiplayerFlags_FromStringOrOldBool()
    {
        var c = DataPackLoader.LoadCatalog("""
            { "cards": [
                { "id": "CARD.A", "multiplayer": "MultiplayerOnly" },
                { "id": "CARD.B", "multiplayer": "SingleplayerOnly" },
                { "id": "CARD.C", "multiplayerOnly": true },
                { "id": "CARD.D", "multiplayer": "None", "showInLibrary": false },
                { "id": "CARD.E", "gainsBlock": null, "costX": null }
            ] }
            """);
        Assert.False(c.Cards[4].GainsBlock);
        Assert.False(c.Cards[4].CostX);
        Assert.True(c.Cards[0].MultiplayerOnly);
        Assert.False(c.Cards[0].IsAvailable(isMultiplayer: false));
        Assert.True(c.Cards[0].IsAvailable(isMultiplayer: true));
        Assert.True(c.Cards[1].SingleplayerOnly);
        Assert.True(c.Cards[1].IsAvailable(isMultiplayer: false));
        Assert.False(c.Cards[1].IsAvailable(isMultiplayer: true));
        Assert.True(c.Cards[2].MultiplayerOnly);
        Assert.False(c.Cards[3].IsAvailable(isMultiplayer: false));
        Assert.False(c.Cards[3].IsAvailable(isMultiplayer: true));
        Assert.True(c.Cards[4].ShowInLibrary);
        Assert.Equal(-1, c.Cards[4].StarCost);
        Assert.True(c.Cards[4].IsAvailable(isMultiplayer: false));
    }

    [Fact]
    public void DataPack_WithRealCatalog_TypeAndRarityAreLowerCase()
    {
        var pack = new DataPack { Catalog = RealCatalog.Instance };
        Assert.Equal("attack", pack.CardType(Defect, "CARD.ANGER"));
        Assert.Equal(CardTypes.Attack, pack.CardType("CHARACTER.IRONCLAD", "CARD.ANGER"));
        Assert.Equal("common", pack.CardRarity("CHARACTER.IRONCLAD", "CARD.ANGER"));
        Assert.Equal(CardTypes.Power, pack.CardType(Defect, "CARD.ECHO_FORM"));
        Assert.Equal("rare", pack.CardRarity(Defect, "CARD.ECHO_FORM"));
        Assert.Equal("basic", pack.CardRarity(Defect, "CARD.STRIKE_DEFECT"));
        Assert.Equal(CardTypes.Curse, pack.CardType(Defect, RealCatalog.Instance.Cards.First(c => c.Type == "Curse").Id));
        Assert.Equal("CHARACTER.IRONCLAD", pack.PoolOwner("CARD.ANGER"));
        Assert.True(pack.IsColorless("CARD.AUTOMATION"));
        Assert.False(pack.IsColorless("CARD.ANGER"));
    }

    [Fact]
    public void DataPack_WithRealCatalog_AliasesStillResolve()
    {
        var pack = new DataPack
        {
            Catalog = RealCatalog.Instance,
            IdAliases = new Dictionary<string, string>(StringComparer.Ordinal) { ["CARD.OLD_ANGER"] = "CARD.ANGER" },
        };
        Assert.Equal("attack", pack.CardType(Defect, "CARD.OLD_ANGER"));
        Assert.Equal("CARD.ANGER", pack.FindCatalog("CARD.OLD_ANGER")!.Id);
        Assert.Equal("CHARACTER.IRONCLAD", pack.PoolOwner("CARD.OLD_ANGER"));
    }

    [Fact]
    public void DataPack_IsColorless_FallsBackToColorlessTierPack()
    {
        var pack = new DataPack
        {
            Tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal)
            {
                [DataPack.Colorless] = new() { Character = DataPack.Colorless, Cards = new(StringComparer.Ordinal) { ["CARD.CL"] = new() } },
                [Defect] = new() { Character = Defect, Cards = new(StringComparer.Ordinal) { ["CARD.D"] = new() } },
            },
        };
        Assert.True(pack.IsColorless("CARD.CL"));
        Assert.False(pack.IsColorless("CARD.D"));
        Assert.Null(pack.PoolOwner("CARD.UNKNOWN"));
    }

    [Fact]
    public void LoadCatalogFile_Missing_Throws()
        => Assert.Throws<FileNotFoundException>(() => DataPackLoader.LoadCatalogFile(Path.Combine(TestPaths.RepoRoot, "no-such-catalog.json")));

    [Fact]
    public void LoadCatalogFile_Malformed_MessageStartsWithPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"catalog-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ \"cards\": 5 }");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => DataPackLoader.LoadCatalogFile(path));
            Assert.StartsWith(path, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadDirectory_WithCatalogPath()
    {
        var pack = DataPackLoader.LoadDirectory(RealCatalog.TestPackDir, catalogPath: RealCatalog.Path);
        Assert.Equal(578, pack.Catalog!.Cards.Count);
        Assert.Equal("rare", pack.CardRarity(Defect, "CARD.ECHO_FORM"));

        // An explicit catalog object still wins over a path.
        var explicitCatalog = new CardCatalog { GameVersion = "arg" };
        Assert.Same(explicitCatalog, DataPackLoader.LoadDirectory(RealCatalog.TestPackDir, explicitCatalog, RealCatalog.Path).Catalog);
        // The original signature keeps working.
        Assert.Null(DataPackLoader.LoadDirectory(RealCatalog.TestPackDir).Catalog);
        Assert.Same(explicitCatalog, DataPackLoader.LoadDirectory(RealCatalog.TestPackDir, explicitCatalog).Catalog);
    }

    [Fact]
    public void Validator_TestPackIdsAreAllInRealCatalog()
    {
        var pack = DataPackLoader.LoadDirectory(RealCatalog.TestPackDir, RealCatalog.Instance);
        var issues = DataPackValidator.Validate(pack);
        Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);
        Assert.DoesNotContain(issues, i => i.Message.Contains("not in the catalog", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_SamplePack_OnlyFakeSampleIdsMissFromRealCatalog()
    {
        var pack = DataPackLoader.LoadDirectory(RealCatalog.SamplePackDir, RealCatalog.Instance);
        var issues = DataPackValidator.Validate(pack);
        Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);
        var missing = issues.Where(i => i.Message.Contains("not in the catalog", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(missing);
        Assert.All(missing, i => Assert.Contains("CARD.SAMPLE_", i.Message, StringComparison.Ordinal));
    }
}
