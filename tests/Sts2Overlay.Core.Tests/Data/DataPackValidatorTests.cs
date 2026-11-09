using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Tests.Data;

public class DataPackValidatorTests
{
    private const string Defect = "CHARACTER.DEFECT";

    private static TierPack DefectTiers(params string[] ids) => new()
    {
        Character = Defect,
        Cards = ids.ToDictionary(id => id, _ => new CardTierEntry { Tier = Tier.B }, StringComparer.Ordinal),
    };

    private static Archetype Arch(string id, params string[] core) => new()
    {
        Id = id,
        Character = Defect,
        Cards = new ArchetypeCards { Core = [.. core] },
    };

    private static DataPack Pack(
        TierPack? tiers = null,
        IReadOnlyList<Archetype>? archetypes = null,
        IReadOnlyList<Combo>? combos = null,
        CardCatalog? catalog = null,
        Dictionary<string, string>? aliases = null,
        Weights? weights = null)
    {
        var t = tiers ?? DefectTiers("CARD.A", "CARD.B");
        return new DataPack
        {
            Weights = weights ?? new Weights(),
            Tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal) { [t.Character] = t },
            Archetypes = archetypes ?? [Arch("d.a", "CARD.A", "CARD.B")],
            Combos = combos ?? [new Combo { Character = Defect, Cards = ["CARD.A", "CARD.B"], Name = "ab" }],
            Catalog = catalog,
            IdAliases = aliases ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private static IEnumerable<ValidationIssue> Errors(DataPack p) => DataPackValidator.Validate(p).Where(i => i.Severity == ValidationSeverity.Error);

    private static IEnumerable<ValidationIssue> Warnings(DataPack p) => DataPackValidator.Validate(p).Where(i => i.Severity == ValidationSeverity.Warning);

    [Fact]
    public void ValidPack_HasNoIssues() => Assert.Empty(DataPackValidator.Validate(Pack()));

    [Theory]
    [InlineData("CARD.BALL_LIGHTNING", true)]
    [InlineData("CARD.X2_Y", true)]
    [InlineData("card.ball", false)]
    [InlineData("CARD.", false)]
    [InlineData("CARD.BALL LIGHTNING", false)]
    [InlineData("RELIC.X", false)]
    public void IsCardId(string id, bool expected) => Assert.Equal(expected, DataPackValidator.IsCardId(id));

    [Theory]
    [InlineData("CHARACTER.DEFECT", true)]
    [InlineData("COLORLESS", true)]
    [InlineData("DEFECT", false)]
    [InlineData("CHARACTER.defect", false)]
    public void IsCharacterId(string id, bool expected) => Assert.Equal(expected, DataPackValidator.IsCharacterId(id));

    [Fact]
    public void MalformedTierCardId_IsError()
    {
        var p = Pack(tiers: DefectTiers("CARD.A", "CARD.B", "card.bad"));
        Assert.Contains(Errors(p), i => i.Where.Contains("card.bad"));
    }

    [Fact]
    public void MalformedTierCharacter_IsError()
    {
        var t = DefectTiers("CARD.A", "CARD.B");
        t.Character = "DEFECT";
        Assert.Contains(Errors(Pack(tiers: t)), i => i.Message.Contains("Malformed character"));
    }

    [Theory]
    [InlineData("roles", 1.2)]
    [InlineData("roles", -0.1)]
    [InlineData("confidence", 1.5)]
    [InlineData("dupFactor", -1)]
    [InlineData("dupFactor", 4)]
    [InlineData("cycle", 2)]
    [InlineData("baseAdjust", 16)]
    [InlineData("baseAdjust", -15.5)]
    public void OutOfRangeNumbers_AreErrors(string field, double value)
    {
        var t = DefectTiers("CARD.A", "CARD.B");
        var e = t.Cards["CARD.A"];
        switch (field)
        {
            case "roles": e.Roles["block"] = value; break;
            case "confidence": e.Confidence = value; break;
            case "dupFactor": e.DupFactor = value; break;
            case "cycle": e.Cycle = value; break;
            case "baseAdjust": e.BaseAdjust = value; break;
        }
        var err = Assert.Single(Errors(Pack(tiers: t)));
        Assert.Contains(field, err.Where);
        Assert.Contains("CARD.A", err.Where);
    }

    [Fact]
    public void UnknownRoleKey_IsWarning()
    {
        var t = DefectTiers("CARD.A", "CARD.B");
        t.Cards["CARD.A"].Roles["blok"] = 0.5;
        Assert.Empty(Errors(Pack(tiers: t)));
        Assert.Contains(Warnings(Pack(tiers: t)), i => i.Message.Contains("blok"));
    }

    [Fact]
    public void DuplicateArchetypeId_IsError()
    {
        var p = Pack(archetypes: [Arch("d.a", "CARD.A"), Arch("d.a", "CARD.B")]);
        Assert.Contains(Errors(p), i => i.Message.Contains("Duplicate archetype id"));
    }

    [Fact]
    public void ArchetypeWithoutCards_IsError()
        => Assert.Contains(Errors(Pack(archetypes: [Arch("d.empty")])), i => i.Message.Contains("no cards"));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveFullWeight_IsError(double fullWeight)
    {
        var a = Arch("d.a", "CARD.A");
        a.FullWeight = fullWeight;
        Assert.Contains(Errors(Pack(archetypes: [a])), i => i.Where.EndsWith("fullWeight"));
    }

    [Fact]
    public void MalformedArchetypeIds_AreErrors()
    {
        var a = Arch("d.a", "CARD.A", "BALL_LIGHTNING");
        a.KeyRelics = [new KeyRelic { Id = "CRACKED_CORE" }];
        a.Character = "Defect";
        var errors = Errors(Pack(archetypes: [a])).ToList();
        Assert.Contains(errors, i => i.Message.Contains("'BALL_LIGHTNING'"));
        Assert.Contains(errors, i => i.Message.Contains("'CRACKED_CORE'"));
        Assert.Contains(errors, i => i.Message.Contains("'Defect'"));
    }

    [Fact]
    public void ArchetypeCardWithoutTier_IsWarning()
    {
        var p = Pack(archetypes: [Arch("d.a", "CARD.A", "CARD.UNRATED")]);
        Assert.Empty(Errors(p));
        Assert.Contains(Warnings(p), i => i.Where.Contains("CARD.UNRATED") && i.Message.Contains("no tier entry"));
    }

    [Fact]
    public void ArchetypeCharacterWithoutTierPack_IsWarning()
    {
        var a = Arch("s.a", "CARD.A");
        a.Character = "CHARACTER.SILENT";
        var p = Pack(archetypes: [a]);
        Assert.Empty(Errors(p));
        Assert.Contains(Warnings(p), i => i.Message.Contains("No tier pack for character 'CHARACTER.SILENT'"));
    }

    [Fact]
    public void NeedWithoutTagOrRole_IsError()
    {
        var a = Arch("d.a", "CARD.A");
        a.Needs = [new ArchetypeNeed { Min = 2 }];
        Assert.Contains(Errors(Pack(archetypes: [a])), i => i.Where.Contains("needs[0]"));
    }

    [Fact]
    public void ComboWithFewerThanTwoCards_IsError()
    {
        var p = Pack(combos: [new Combo { Cards = ["CARD.A"], Name = "solo" }, new Combo { Cards = ["CARD.A", "CARD.A"], Name = "same" }]);
        var errors = Errors(p).ToList();
        Assert.Contains(errors, i => i.Where.Contains("solo"));
        Assert.Contains(errors, i => i.Where.Contains("same"));
    }

    [Fact]
    public void ComboCardWithoutTier_IsWarning()
    {
        var p = Pack(combos: [new Combo { Cards = ["CARD.A", "CARD.NOPE"], Name = "x" }]);
        Assert.Empty(Errors(p));
        Assert.Contains(Warnings(p), i => i.Where.Contains("CARD.NOPE"));
    }

    [Fact]
    public void CatalogMissingIds_AreWarnings()
    {
        var catalog = new CardCatalog { Cards = [new CatalogCard { Id = "CARD.A", Pool = Defect, Rarity = "common" }] };
        var p = Pack(catalog: catalog);
        Assert.Empty(Errors(p));
        var warnings = Warnings(p).ToList();
        Assert.Contains(warnings, i => i.Where.StartsWith("tiers") && i.Message.Contains("'CARD.B' is not in the catalog"));
        Assert.Contains(warnings, i => i.Where.StartsWith("archetypes") && i.Message.Contains("'CARD.B' is not in the catalog"));
        Assert.DoesNotContain(warnings, i => i.Message.Contains("'CARD.A' is not in the catalog"));
    }

    [Fact]
    public void Aliases_MalformedIsError_ChainAndAliasUseAreWarnings()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bad"] = "CARD.A",
            ["CARD.OLD_A"] = "CARD.A",
            ["CARD.OLDER"] = "CARD.OLD_A",
        };
        var p = Pack(aliases: aliases, archetypes: [Arch("d.a", "CARD.OLD_A", "CARD.B")]);
        Assert.Contains(Errors(p), i => i.Where == "aliases[bad]");
        var warnings = Warnings(p).ToList();
        Assert.Contains(warnings, i => i.Where == "aliases[CARD.OLDER]" && i.Message.Contains("chains"));
        Assert.Contains(warnings, i => i.Where.Contains("CARD.OLD_A") && i.Message.Contains("alias of 'CARD.A'"));
    }

    [Fact]
    public void InvalidWeights_AreErrors()
    {
        var w = new Weights { HybridMargin = 2 };
        w.Completion.CardsPerReward = 0;
        w.Completion.PoolSize["rare"] = 0;
        var errors = Errors(Pack(weights: w)).ToList();
        Assert.Contains(errors, i => i.Where == "weights.hybridMargin");
        Assert.Contains(errors, i => i.Where == "weights.completion.cardsPerReward");
        Assert.Contains(errors, i => i.Where == "weights.completion.poolSize.rare");
    }
}
