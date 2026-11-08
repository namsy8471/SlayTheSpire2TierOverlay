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
    public void Card_without_description_or_upgrade()
    {
        var t = CardTextComposer.Compose("CARD.CURSED", new CardTextFacts(-1, false, -1, ["Unplayable"], 0), null, Loc)!;
        Assert.Null(t.Base.Cost);
        Assert.Null(t.Upgraded);
        Assert.Equal("Unplayable.", Plain(t.Base));
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
