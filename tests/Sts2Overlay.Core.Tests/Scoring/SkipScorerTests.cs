using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Scoring;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Scoring;

public class SkipScorerTests
{
    private static readonly DataPack Pack = DefectPack();
    private static readonly Weights W = Pack.Weights;

    /// <summary>Skip score; the orb build owns its core unless <paramref name="coreOwned"/> is false.</summary>
    private static ScoreBreakdown Skip(int act, int deckSize, double commit = 0, bool coreOwned = true)
    {
        var copies = new Dictionary<string, int>(StringComparer.Ordinal);
        if (coreOwned) copies[OrbCore] = 1;
        var profile = new DeckProfile { DeckSize = deckSize, Copies = copies };
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

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void CommitBonus_UsesTheCoreGatedCommit(int act)
    {
        // 코어 먼저: a full commit without the core is at most noCoreMaxUnlock (0.5) × commit < threshold → no "빌드 완성".
        Assert.Equal(0, Skip(act, 10, commit: 1.0, coreOwned: false).ValueOf(ComponentKeys.SkipCommit));
        var withCore = Skip(act, 10, commit: 1.0);
        Assert.Equal(W.Skip.LambdaCommit, withCore.ValueOf(ComponentKeys.SkipCommit));
        Assert.Contains("코어 반영", withCore.Components.Single(c => c.Key == ComponentKeys.SkipCommit).Detail);
    }

    [Fact]
    public void AllTermsTogether()
    {
        var s = Skip(3, 30, commit: 1.0);
        Assert.Equal(W.Skip.T0[3] + W.Skip.LambdaSize * 5 + W.Skip.LambdaCommit, s.Total, 9);
        Assert.Equal(3, s.Components.Count);
    }
}
