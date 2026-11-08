using Sts2Overlay.Core.Text;

namespace Sts2Overlay.Core.Tests.Text;

// Templates here are written for the tests in the game's syntax; no game text is committed.
public class CardTextRendererTests
{
    private static CardTextContext Ctx(bool upgraded = false, string lang = "eng", params (string, decimal)[] vars)
        => new(vars.ToDictionary(v => v.Item1, v => v.Item2), null, upgraded, lang);

    private static string Plain(string template, CardTextContext ctx) => CardTextRenderer.ToPlainText(CardTextRenderer.Render(template, ctx));

    [Fact]
    public void Diff_prints_the_value()
        => Assert.Equal("Hit for 6.", Plain("Hit for {Damage:diff()}.", Ctx(vars: ("Damage", 6))));

    [Fact]
    public void Gold_tag_becomes_keyword_run()
    {
        var runs = CardTextRenderer.Render("Gain [gold]Block[/gold] now", Ctx());
        Assert.Equal([new("Gain ", CardTextStyle.Plain), new("Block", CardTextStyle.Keyword), new(" now", CardTextStyle.Plain)], runs);
    }

    [Fact]
    public void Changed_value_is_marked_when_base_differs()
    {
        var ctx = new CardTextContext(new Dictionary<string, decimal> { ["Damage"] = 8, ["Hits"] = 2 },
            new Dictionary<string, decimal> { ["Damage"] = 6, ["Hits"] = 2 }, true, "eng");
        var runs = CardTextRenderer.Render("{Damage:diff()} x{Hits:diff()}", ctx);
        Assert.Equal([new("8", CardTextStyle.Changed), new(" x2", CardTextStyle.Plain)], runs);
    }

    [Theory]
    [InlineData(false, "a card")]
    [InlineData(true, "ALL cards")]
    public void IfUpgraded_show_picks_branch(bool upgraded, string expected)
        => Assert.Equal(expected, Plain("{IfUpgraded:show:ALL cards|a card}", Ctx(upgraded)));

    [Theory]
    [InlineData(1, "Draw 1 card.")]
    [InlineData(3, "Draw 3 cards.")]
    public void Plural_uses_count(int n, string expected)
        => Assert.Equal(expected, Plain("Draw {Cards:diff()} {Cards:plural:card|cards}.", Ctx(vars: ("Cards", n))));

    [Theory]
    [InlineData(1, "next time")]
    [InlineData(2, "next 2 times")]
    public void Plural_option_can_nest_current_value(int n, string expected)
        => Assert.Equal(expected, Plain("next {Buffer:plural:time|{:diff()} times}", Ctx(vars: ("Buffer", n))));

    [Theory]
    [InlineData(1, "막기")]
    [InlineData(2, "2회 막기")]
    public void Choose_matches_argument_else_last(int n, string expected)
        => Assert.Equal(expected, Plain("{Buffer:choose(1):|{Buffer:diff()}회 }막기", Ctx(lang: "kor", vars: ("Buffer", n))));

    [Fact]
    public void InCombat_branch_is_hidden_outside_combat()
        => Assert.Equal("Deal damage.", Plain("Deal damage.{InCombat:\n(Deals {CalculatedDamage:diff()})|}", Ctx()));

    [Fact]
    public void Energy_icons_count_from_value_or_argument()
    {
        var runs = CardTextRenderer.Render("Gain {Energy:energyIcons()}. Cost 0{energyPrefix:energyIcons(1)}", Ctx(vars: ("Energy", 2)));
        Assert.Contains(new CardTextRun("2", CardTextStyle.Energy), runs);
        Assert.Contains(new CardTextRun("1", CardTextStyle.Energy), runs);
    }

    [Fact]
    public void Tags_may_close_inside_an_option()
        => Assert.Equal([new("Minion+", CardTextStyle.Keyword), new(" x", CardTextStyle.Plain)],
            CardTextRenderer.Render("[gold]{N:plural:{IfUpgraded:show:Minion+|Minion}[/gold]|Minions[/gold]} x", Ctx(true, vars: ("N", 1))));

    [Fact]
    public void Unknown_variable_is_reported()
    {
        var missing = new List<string>();
        var runs = CardTextRenderer.Render("Deal {Nope:diff()}.", Ctx(), missing);
        Assert.Equal(["Nope"], missing);
        Assert.Equal("Deal ?.", CardTextRenderer.ToPlainText(runs));
    }

    [Fact]
    public void Square_brackets_that_are_not_tags_stay_text()
        => Assert.Equal("a [x y] b", Plain("a [x y] b", Ctx()));

    [Theory]
    [InlineData(1, "next Attack is")]
    [InlineData(2, "next 2 Attacks are")]
    public void Comparison_conditional_picks_matching_branch(int n, string expected)
        => Assert.Equal(expected, Plain("next {Attacks:cond:>1?{Attacks:diff()} Attacks are|Attack is}", Ctx(vars: ("Attacks", n))));

    [Fact]
    public void One_option_switch_that_is_off_prints_nothing()
        => Assert.Equal("Deal.", Plain("Deal.{IfUpgraded:show: Twice.}", Ctx(upgraded: false)));

    [Fact]
    public void Korean_plural_always_takes_the_first_form()
        => Assert.Equal("3 a", Plain("{N:diff()} {N:plural:a|b}", Ctx(lang: "kor", vars: ("N", 3))));

    [Fact]
    public void Fractions_are_truncated_like_the_game()
        => Assert.Equal("2", Plain("{X:diff()}", Ctx(vars: ("X", 2.75m))));

    [Fact]
    public void Upgraded_show_branch_is_marked_in_the_upgrade_preview()
    {
        var ctx = new CardTextContext(new Dictionary<string, decimal>(), new Dictionary<string, decimal>(), true, "eng");
        Assert.Equal([new("Upgrade ", CardTextStyle.Plain), new("ALL", CardTextStyle.Changed)],
            CardTextRenderer.Render("Upgrade {IfUpgraded:show:ALL|one}", ctx));
    }

    [Theory]
    [InlineData("AllEnemies", "Hit ALL.")]
    [InlineData("AnyEnemy", "Hit.")]
    public void TargetType_is_chosen_by_name(string target, string expected)
        => Assert.Equal(expected, Plain("Hit{TargetType:choose(AllEnemies): ALL|}.", Ctx() with { TargetType = target }));

    [Fact]
    public void Runtime_only_switch_takes_the_otherwise_branch_without_reporting()
    {
        var missing = new List<string>();
        var ctx = Ctx() with { Unresolved = ["CardType"] };
        var runs = CardTextRenderer.Render("{CardType:choose(Attack|Skill):hit|guard|???}", ctx, missing);
        Assert.Empty(missing);
        Assert.Equal("???", CardTextRenderer.ToPlainText(runs));
    }
}
