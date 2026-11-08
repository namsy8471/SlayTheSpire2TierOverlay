using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Scoring;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Scoring;

public class SkipScorerTests
{
    private static readonly DataPack Pack = DefectPack();
    private static readonly Weights W = Pack.Weights;

    private static ScoreBreakdown Skip(int act, int deckSize, double commit = 0)
    {
        var profile = new DeckProfile { DeckSize = deckSize };
        if (commit > 0) profile = profile.WithArchetypes(Pack, (OrbArchetype, commit));
        var s = new SkipScorer(Pack).Score(State(act: act), profile);
        ScoreAssert.Consistent(s);
        return s;
    }

    [Theory]
    [InlineData(1, 45)]
    [InlineData(2, 52)]
    [InlineData(3, 58)]
    [InlineData(4, 58)] // falls back to act 3
    public void Base_IsT0ForAct(int act, double expected)
    {
        var s = Skip(act, 10);
        Assert.Equal(expected, s.Total);
        Assert.Single(s.Components);
        Assert.Equal(ComponentKeys.SkipBase, s.Components[0].Key);
    }

    [Fact]
    public void DeckSizeAddsLambdaPerExtraCard()
    {
        var s = Skip(2, 25);
        Assert.Equal(W.Skip.LambdaSize * (25 - W.IdealDeckSize[2]), s.ValueOf(ComponentKeys.SkipDeckSize));
        Assert.Equal(W.Skip.T0[2] + 7.5, s.Total, 9);
    }

    [Fact]
    public void CommitBonusOnlyAtThreshold()
    {
        Assert.Equal(0, Skip(3, 10, commit: 0.69).ValueOf(ComponentKeys.SkipCommit));
        Assert.Equal(W.Skip.LambdaCommit, Skip(3, 10, commit: 0.7).ValueOf(ComponentKeys.SkipCommit));
    }

    [Fact]
    public void AllTermsTogether()
    {
        var s = Skip(3, 30, commit: 1.0);
        Assert.Equal(W.Skip.T0[3] + W.Skip.LambdaSize * 5 + W.Skip.LambdaCommit, s.Total, 9);
        Assert.Equal(3, s.Components.Count);
    }
}
