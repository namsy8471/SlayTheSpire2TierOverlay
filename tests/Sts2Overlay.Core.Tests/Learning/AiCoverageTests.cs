using Sts2Overlay.Core.Learning;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.Learning;

public class AiCoverageTests
{
    [Theory]
    [InlineData("CHARACTER.SILENT", 0, false, true, true)]
    [InlineData("CHARACTER.SILENT", 8, false, true, true)]
    [InlineData("CHARACTER.SILENT", 10, false, true, true)]
    [InlineData("CHARACTER.SILENT", 4, false, true, false)]
    [InlineData("CHARACTER.SILENT", 0, true, true, false)]   // co-op
    [InlineData("CHARACTER.SILENT", 0, false, false, false)] // custom / daily
    [InlineData("CHARACTER.IRONCLAD", 8, false, true, false)]
    [InlineData("CHARACTER.IRONCLAD", 10, false, true, true)]
    [InlineData("CHARACTER.FOOL", 0, false, true, true)]
    public void Covers_only_the_measured_solo_runs(string character, int ascension, bool coop, bool standard, bool covered)
    {
        var run = new RunState { Character = character, Ascension = ascension, IsMultiplayer = coop, IsStandardRun = standard };
        Assert.Equal(covered, AiCoverage.Covers(run));
        Assert.Equal(!covered, AiCoverage.Excludes(run));
        // The learned run layers never apply where the AI was not measured.
        if (!covered) Assert.False(RunBaselinePolicy.Supports(run with { VitalsKnown = true }));
    }

    [Fact]
    public void An_unknown_run_is_not_excluded()
    {
        Assert.False(AiCoverage.Excludes(null));
        Assert.False(AiCoverage.Excludes(new RunState { Character = "CHARACTER.UNKNOWN", IsMultiplayer = true }));
    }
}
