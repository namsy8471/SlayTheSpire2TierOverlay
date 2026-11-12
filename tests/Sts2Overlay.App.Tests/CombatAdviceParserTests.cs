using Sts2Overlay.App.Integration.CombatAdvice;

namespace Sts2Overlay.App.Tests;

/// <summary>The overlay side of the combat advisor protocol: one stdout line of the helper → a message (docs/COMBAT_ADVISOR.md).</summary>
public class CombatAdviceParserTests
{
    private const string Advice = """{"v":1,"state":"advice","encounter":"JAW_WORM_WEAK","turn":2,"energy":3,"hp":61,"block":0,"played":1,"steps":[{"kind":"card","id":"BASH","upgraded":false,"hand":2,"target":"JAW_WORM","targetSlot":0},{"kind":"potion","id":"FIRE_POTION","target":"JAW_WORM","targetSlot":0},{"kind":"card","id":"DEFEND_IRONCLAD","upgraded":true,"hand":0}],"ending":"end_turn","confidence":"exact","note":"","ms":2.4}""";

    private static CombatAdviceMessage ParseOk(string line)
    {
        var m = CombatAdviceParser.Parse(line, out var problem);
        Assert.Null(problem);
        return Assert.IsType<CombatAdviceMessage>(m);
    }

    [Fact]
    public void Reads_idle()
        => Assert.Equal(CombatAdviceState.Idle, ParseOk("""{"v":1,"state":"idle"}""").State);

    [Fact]
    public void Reads_the_advice_example_of_the_protocol()
    {
        var m = ParseOk(Advice);

        Assert.Equal(CombatAdviceState.Advice, m.State);
        Assert.Equal("JAW_WORM_WEAK", m.Encounter);
        Assert.Equal(2, m.Turn);
        Assert.Equal(3, m.Energy);
        Assert.Equal(61, m.Hp);
        Assert.Equal(0, m.Block);
        Assert.Equal(1, m.Played);
        Assert.Equal(CombatEnding.EndTurn, m.Ending);
        Assert.False(m.Uncertain);
        Assert.Equal("", m.Note);
        Assert.Equal(2.4, m.Ms);
        Assert.Equal(
            [
                new CombatAdviceStep(CombatStepKind.Card, "BASH", Upgraded: false, Hand: 2, Target: "JAW_WORM", TargetSlot: 0),
                new CombatAdviceStep(CombatStepKind.Potion, "FIRE_POTION", Target: "JAW_WORM", TargetSlot: 0),
                new CombatAdviceStep(CombatStepKind.Card, "DEFEND_IRONCLAD", Upgraded: true, Hand: 0),
            ],
            m.Steps);
    }

    [Fact]
    public void Reads_win_uncertain_and_the_ambiguous_target()
    {
        var m = ParseOk("""{"v":1,"state":"advice","turn":5,"steps":[{"kind":"card","id":"STRIKE_IRONCLAD","target":"JAW_WORM","targetSlot":1,"targetAmbiguous":true}],"ending":"win","confidence":"uncertain","note":"why"}""");

        Assert.Equal(CombatEnding.Win, m.Ending);
        Assert.True(m.Uncertain);
        Assert.Equal("why", m.Note);
        Assert.True(m.Steps[0].TargetAmbiguous);
        Assert.Equal(1, m.Steps[0].TargetSlot);
    }

    [Theory]
    [InlineData("lost", CombatAdviceState.Lost)]
    [InlineData("unsupported", CombatAdviceState.Unsupported)]
    public void Reads_lost_and_unsupported_with_their_reason(string state, CombatAdviceState expected)
    {
        var m = ParseOk($$"""{"v":1,"state":"{{state}}","encounter":"X_ENCOUNTER","reason":"made-up reason"}""");

        Assert.Equal(expected, m.State);
        Assert.Equal("made-up reason", m.Reason);
        Assert.Equal("X_ENCOUNTER", m.Encounter);
    }

    [Fact]
    public void Ignores_unknown_fields()
    {
        var m = ParseOk("""{"v":1,"state":"advice","future":{"a":[1,2]},"turn":1,"steps":[{"kind":"card","id":"BASH","newField":7}],"ending":"end_turn","confidence":"exact","extra":"x"}""");

        Assert.Equal(1, m.Turn);
        Assert.Equal("BASH", Assert.Single(m.Steps).Id);
        Assert.Equal(CombatEnding.EndTurn, m.Ending);
    }

    [Fact]
    public void Unknown_kind_keeps_the_raw_id_and_steps_without_an_id_are_dropped()
    {
        var m = ParseOk("""{"v":1,"state":"advice","steps":[{"kind":"orb","id":"SOMETHING_NEW"},{"kind":"card"},{"kind":"card","id":""},3,{"id":"NO_KIND"}],"ending":"later","confidence":"exact"}""");

        Assert.Equal([CombatStepKind.Unknown, CombatStepKind.Unknown], m.Steps.Select(s => s.Kind));
        Assert.Equal(["SOMETHING_NEW", "NO_KIND"], m.Steps.Select(s => s.Id));
        Assert.Equal(CombatEnding.None, m.Ending); // unknown ending: no closing row
    }

    [Theory]
    [InlineData("draw", CombatEnding.AdviseAfterPlay)]
    [InlineData("end_turn", CombatEnding.EndTurn)]
    [InlineData("win", CombatEnding.Win)]
    [InlineData("", CombatEnding.None)]
    [InlineData("Draw", CombatEnding.None)] // values are case-sensitive, like every other protocol word
    [InlineData("draw_more", CombatEnding.None)]
    public void Reads_the_ending(string ending, CombatEnding expected)
    {
        var m = ParseOk($$"""{"v":1,"state":"advice","steps":[{"kind":"card","id":"BASH"}],"ending":"{{ending}}","confidence":"exact","note":"next steps depend on the cards drawn"}""");

        Assert.Equal(expected, m.Ending);
        Assert.False(m.Uncertain); // a draw stop is exact advice, not an uncertain one
        Assert.Equal("next steps depend on the cards drawn", m.Note);
    }

    [Fact]
    public void Missing_confidence_counts_as_uncertain_and_missing_numbers_are_null()
    {
        var m = ParseOk("""{"v":1,"state":"advice","turn":"two","steps":[]}""");

        Assert.True(m.Uncertain);
        Assert.Null(m.Turn);
        Assert.Null(m.Energy);
        Assert.Null(m.Ms);
        Assert.Empty(m.Steps);
    }

    [Theory]
    [InlineData("""{"v":2,"state":"idle"}""", "protocol version 2")]
    [InlineData("""{"v":7,"state":"advice","steps":[]}""", "protocol version 7")]
    [InlineData("""{"v":0,"state":"idle"}""", "protocol version 0")]
    public void Ignores_other_protocol_versions(string line, string problem)
    {
        Assert.Null(CombatAdviceParser.Parse(line, out var p));
        Assert.StartsWith(problem, p);
    }

    [Theory]
    [InlineData("not json at all", "not JSON")]
    [InlineData("""{"v":1,"state":"idle" """, "not JSON")]
    [InlineData("[1,2,3]", "not a JSON object")]
    [InlineData("\"idle\"", "not a JSON object")]
    [InlineData("""{"state":"idle"}""", "no protocol version (v)")]
    [InlineData("""{"v":"1","state":"idle"}""", "no protocol version (v)")]
    [InlineData("""{"v":1}""", "no state")]
    [InlineData("""{"v":1,"state":"dancing"}""", "unknown state 'dancing'")]
    public void Bad_lines_give_a_problem_and_no_message(string line, string problem)
    {
        Assert.Null(CombatAdviceParser.Parse(line, out var p));
        Assert.Equal(problem, p);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_lines_are_skipped_without_a_problem(string? line)
    {
        Assert.Null(CombatAdviceParser.Parse(line, out var p));
        Assert.Null(p);
    }
}
