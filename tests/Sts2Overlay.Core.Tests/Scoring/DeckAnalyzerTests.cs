using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Scoring;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Scoring;

public class DeckAnalyzerTests
{
    private static readonly DataPack Pack = DefectPack();
    private static readonly Weights W = Pack.Weights;

    [Fact]
    public void RoleSupply_SumsStrengths_WithUpgradeMultiplier()
    {
        var p = DeckAnalyzer.AnalyzeDeck(State(deck: [Blocker, Blocker + "+1", Defend, OrbEnabler]), Pack);
        Assert.Equal(1.0 + 1.0 * W.UpgradedSupplyMultiplier + 0.3 + 0.4, p.RoleSupply[Roles.Block], 9);
        Assert.Equal(0, p.RoleSupply[Roles.Aoe]);
        Assert.Equal(4, p.DeckSize);
    }

    [Fact]
    public void Targets_DefaultForAct_AndGap()
    {
        var p = DeckAnalyzer.AnalyzeDeck(State(act: 2, deck: [Blocker, AoeHit]), Pack);
        var t = W.Targets["default"][2];
        Assert.Equal(t[Roles.Block], p.RoleTarget[Roles.Block]);
        Assert.Equal((t[Roles.Block] - 1.0) / t[Roles.Block], p.RoleGap[Roles.Block], 9);
        Assert.Equal((t[Roles.Aoe] - 1.0) / t[Roles.Aoe], p.RoleGap[Roles.Aoe], 9);
        Assert.Equal(1.0, p.RoleGap[Roles.Draw]);
        Assert.False(p.RoleGap.ContainsKey(Roles.Energy));
    }

    [Fact]
    public void Gap_IsZeroWhenSupplyExceedsTarget()
    {
        var p = DeckAnalyzer.AnalyzeDeck(State(act: 1, deck: [AoeHit, AoeHit]), Pack);
        Assert.Equal(0, p.RoleGap[Roles.Aoe]);
    }

    [Fact]
    public void Targets_CharacterSpecificOverridesDefault_AndActFallsBack()
    {
        var weights = new Weights();
        weights.Targets[Defect] = new() { [1] = new() { [Roles.Block] = 2.0 } };
        var pack = DefectPack(weights);
        var p = DeckAnalyzer.AnalyzeDeck(State(act: 3, deck: [Blocker]), pack);
        Assert.Single(p.RoleTarget);
        Assert.Equal(0.5, p.RoleGap[Roles.Block], 9);
    }

    [Fact]
    public void Axes_Copies_Powers_Basics_Curses()
    {
        var deck = new[] { OrbCore, OrbCore, OrbEnabler, OrbPayoff, Strike, Strike, Strike, Defend, Curse, CatalogCurse, OldId, Blocker, PowerCore };
        var p = DeckAnalyzer.AnalyzeDeck(State(deck: deck), Pack);
        Assert.Equal(3, p.Provides["ORB"]);
        Assert.Equal(1, p.Consumes["ORB"]);
        Assert.Equal(2, p.Copies[OrbCore]);
        Assert.Equal(2, p.Copies[Blocker]); // alias counted under the canonical id
        Assert.False(p.Copies.ContainsKey(OldId));
        Assert.Equal(3, p.PowerCount);
        Assert.Equal(3, p.BasicStrikes);
        Assert.Equal(1, p.BasicDefends);
        Assert.Equal(2, p.Curses);
        Assert.Empty(p.Archetypes);
    }

    [Fact]
    public void WeakRoles_BelowThresholdOfTarget()
    {
        // Act 1 block target 2.5: 1.0 supply < 1.25 → weak; draw 1.0 target, supply 0 → weak.
        var weak = DeckAnalyzer.AnalyzeDeck(State(act: 1, deck: [Blocker]), Pack);
        Assert.Equal(new[] { Roles.Block, Roles.Draw }, weak.WeakRoles);
        Assert.True(weak.IsWeak);

        var fine = DeckAnalyzer.AnalyzeDeck(State(act: 1, deck: [Blocker, Blocker, Drawer]), Pack);
        Assert.Empty(fine.WeakRoles);
        Assert.False(fine.IsWeak);
    }

    [Fact]
    public void EmptyDeck_IsWellFormed()
    {
        var p = DeckAnalyzer.AnalyzeDeck(State(deck: []), Pack);
        Assert.Equal(0, p.DeckSize);
        Assert.All(p.RoleGap.Values, g => Assert.Equal(1.0, g));
        Assert.Equal(0, p.MaxCommit);
    }

    [Fact]
    [Trait("Category", "NeedsArchetypes")]
    public void Analyze_FillsArchetypesPerDetectorContract()
    {
        var state = State(deck: [OrbCore, OrbEnabler, Strike]);
        var p = DeckAnalyzer.Analyze(state, Pack);
        var expected = ContractStatuses(state, Pack);
        Assert.Equal(expected.Select(s => s.Archetype.Id), p.Archetypes.Select(s => s.Archetype.Id));
        Assert.Equal((1.0 + 0.7) / 4.0, p.Primary!.Commit, 9);
        Assert.Equal(OrbArchetype, p.Primary.Archetype.Id);
    }
}
