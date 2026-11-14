using Sts2Overlay.Core.Text;

namespace Sts2Overlay.Core.Tests.Text;

// Made-up strings in the game's format; no game text is committed.
public class CardTextComposerTests
{
    private static readonly CardLocalization Loc = new("eng",
        new Dictionary<string, string>
        {
            ["BASH_IT.title"] = "Bash It",
            ["BASH_IT.description"] = "Deal {Damage:diff()} damage.",
            ["CURSED.title"] = "Cursed",
        },
        new Dictionary<string, string>
        {
            ["EXHAUST.title"] = "Exhaust",
            ["INNATE.title"] = "Innate",
            ["ETHEREAL.title"] = "Ethereal",
            ["RETAIN.title"] = "Retain",
            ["UNPLAYABLE.title"] = "Unplayable",
            ["PERIOD"] = ".",
        });

    private static CardVars Vars(decimal dmg, decimal up, int costDelta = 0, string[]? add = null, string[]? remove = null) => new()
    {
        Id = "CARD.BASH_IT",
        Vars = new() { ["Damage"] = dmg },
        Upgrade = new CardUpgrade { Vars = new() { ["Damage"] = up }, CostDelta = costDelta, AddKeywords = [.. add ?? []], RemoveKeywords = [.. remove ?? []] },
    };

    private static string Plain(CardFace f) => CardTextRenderer.ToPlainText(f.Body);

    [Fact]
    public void Upgrade_adds_delta_and_marks_it()
    {
        var t = CardTextComposer.Compose("CARD.BASH_IT", new CardTextFacts(2, false, -1, [], 1), Vars(8, 3), Loc)!;
        Assert.Equal("Deal 8 damage.", Plain(t.Base));
        Assert.Equal("Deal 11 damage.", Plain(t.Upgraded!));
        Assert.Equal("Bash It+", t.Upgraded!.Title);
        Assert.Contains(new CardTextRun("11", CardTextStyle.Changed), t.Upgraded.Body);
        Assert.DoesNotContain(t.Base.Body, r => r.Style == CardTextStyle.Changed);
    }

    [Fact]
    public void Keyword_lines_follow_the_game_order()
    {
        var facts = new CardTextFacts(1, false, -1, ["Exhaust", "Ethereal", "Innate"], 1);
        var t = CardTextComposer.Compose("CARD.BASH_IT", facts, Vars(5, 0), Loc)!;
        Assert.Equal("Innate.\nEthereal.\nDeal 5 damage.\nExhaust.", Plain(t.Base));
    }

    [Fact]
    public void Upgrade_changes_cost_and_keywords()
    {
        var facts = new CardTextFacts(2, false, -1, ["Exhaust"], 1);
        var t = CardTextComposer.Compose("CARD.BASH_IT", facts, Vars(5, 0, costDelta: -1, add: ["Retain"], remove: ["Exhaust"]), Loc)!;
        Assert.Equal("2", t.Base.Cost);
        Assert.Equal("1", t.Upgraded!.Cost);
        Assert.True(t.Upgraded.CostChanged);
        Assert.Equal("Retain.\nDeal 5 damage.", Plain(t.Upgraded));
    }

    [Fact]
    public void Upgrade_applies_star_cost_delta_and_preserves_unknown_base_values()
    {
        var vars = CardVarsFile.Parse("""{"cards":[{"id":"CARD.BASH_IT","upgrade":{"starCostDelta":-1,"vars":{"Damage":2}}}]}""").Cards.Single();
        var t = CardTextComposer.Compose(vars.Id, new CardTextFacts(0, false, 3, [], 1), vars, Loc)!;
        Assert.Equal("3", t.Base.StarCost);
        Assert.Equal("2", t.Upgraded!.StarCost);
        Assert.Equal("Deal ? damage.", Plain(t.Upgraded));
    }

    [Fact]
    public void Card_without_description_or_upgrade()
    {
        var t = CardTextComposer.Compose("CARD.CURSED", new CardTextFacts(-1, false, -1, ["Unplayable"], 0), null, Loc)!;
        Assert.Null(t.Base.Cost);
        Assert.Null(t.Upgraded);
        Assert.Equal("Unplayable.", Plain(t.Base));
    }

    [Fact]
    public void Deck_copy_uses_its_saved_value_and_compares_to_its_next_level()
    {
        var vars = Vars(5, 2);
        vars.SavedProperties["Damage"] = "CurrentDamage";
        var facts = new CardTextFacts(1, false, -1, [], 1);
        var saved = new Dictionary<string, decimal> { ["CurrentDamage"] = 19 };
        var t = CardTextComposer.Compose(vars.Id, facts, vars, Loc, savedProperties: saved)!;
        Assert.True(t.IsDeckCopy);
        Assert.Equal("Deal 19 damage.", Plain(t.Base));
        Assert.Equal("Deal 19 damage.", Plain(t.Upgraded!));
        Assert.Null(CardTextComposer.Compose(vars.Id, facts, vars, Loc, level: 1, savedProperties: saved)!.Upgraded);
        saved.Clear();
        Assert.Equal("Deal ? damage.", Plain(CardTextComposer.Compose(vars.Id, facts, vars, Loc, savedProperties: saved)!.Base));
        Assert.Equal("Deal 5 damage.", Plain(CardTextComposer.Compose(vars.Id, facts, vars, Loc)!.Base));
    }

    [Theory]
    [InlineData(0, "Stage A", "7", "Stage A+1", "11")]
    [InlineData(2, "Stage A+2", "16", "Stage B+3", "22")]
    [InlineData(5, "Stage B+5", "37", "Stage C+6", "46")]
    public void Repeated_upgrade_uses_correct_name_and_increment(int level, string title, string damage, string nextTitle, string nextDamage)
    {
        var loc = Loc with { Cards = new Dictionary<string, string>
        {
            ["DINGY_DAGGER.title.dingy"] = "Stage A", ["DINGY_DAGGER.title.fine"] = "Stage B",
            ["DINGY_DAGGER.title.pristine"] = "Stage C", ["DINGY_DAGGER.description"] = "{Damage:diff()}",
        } };
        var t = CardTextComposer.Compose("CARD.DINGY_DAGGER", new CardTextFacts(0, false, -1, [], 999),
            Vars(7, 4), loc, level: level)!;
        Assert.Equal((title, damage), (t.Base.Title, Plain(t.Base)));
        Assert.Equal((nextTitle, nextDamage), (t.Upgraded!.Title, Plain(t.Upgraded)));
    }

    [Theory]
    [InlineData(1, 2, "A V")]
    [InlineData(2, 1, "S S")]
    [InlineData(3, 0, "P")]
    public void Customized_card_uses_type_and_rider_on_that_copy(int type, int rider, string expected)
    {
        var loc = Loc with { Cards = new Dictionary<string, string>
        {
            ["MAD_SCIENCE.title"] = "Test",
            ["MAD_SCIENCE.description"] = "{CardType:choose(Attack|Skill|Power):A|S|P|?}{HasRider:{Violence: V|}{Sapping: S|}|}",
        } };
        var saved = new Dictionary<string, decimal> { ["TinkerTimeType"] = type };
        if (rider > 0) saved["TinkerTimeRider"] = rider;
        var t = CardTextComposer.Compose("CARD.MAD_SCIENCE", new CardTextFacts(1, false, -1, [], 0), new CardVars(),
            loc, savedProperties: saved)!;
        Assert.Equal(expected, Plain(t.Base));
    }

    [Fact]
    public void Unknown_card_gives_null()
        => Assert.Null(CardTextComposer.Compose("CARD.NOPE", new CardTextFacts(1, false, -1, [], 1), null, Loc));

    [Theory]
    [InlineData("Exhaust", "EXHAUST")]
    [InlineData("CardKeyword.Ethereal", "ETHEREAL")]
    [InlineData("SomeKeyword", "SOME_KEYWORD")]
    [InlineData("RETAIN", "RETAIN")]
    public void Keyword_names_normalize(string raw, string expected) => Assert.Equal(expected, CardTextComposer.Normalize(raw));
}
