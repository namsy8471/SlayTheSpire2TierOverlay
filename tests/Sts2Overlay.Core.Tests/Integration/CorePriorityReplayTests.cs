using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Tests.Data;
using Xunit.Abstractions;

namespace Sts2Overlay.Core.Tests.Integration;

/// <summary>
/// "코어 먼저" with the real v0.107.1 pack and catalog: an invariant over every recorded offer of the 24 fixture runs
/// (plus counterfactual offers of every core/non-core pair of the leaning build at each of those deck states),
/// and one scenario per character.
/// </summary>
public class CorePriorityReplayTests(ITestOutputHelper output)
{
    private static readonly Lazy<DataPack> RealPack = new(() =>
        DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1"), RealCatalog.Instance));

    private static readonly Lazy<IReadOnlyList<CorePair>> OfferedPairs = new(() => Pairs(RealPack.Value, counterfactual: false));
    private static readonly Lazy<IReadOnlyList<CorePair>> CounterfactualPairs = new(() => Pairs(RealPack.Value, counterfactual: true));

    /// <summary>One (missing core C, same-build non-core N) pair at a replayed deck state.</summary>
    internal sealed record CorePair(
        string Run, int Floor, string Archetype, double Commit, bool Weak, bool OtherExclusive,
        CardEvaluation Core, CardEvaluation Other, string OtherRole)
    {
        public bool CoreFirst => Core.Total > Other.Total;

        /// <summary>Base + build + core components: what the core-first rule controls.</summary>
        public static double RuleScore(CardEvaluation e)
            => e.Score.ValueOf(ComponentKeys.Base) + e.Score.ValueOf(ComponentKeys.Build)
               + e.Score.ValueOf(ComponentKeys.CoreMissing) + e.Score.ValueOf(ComponentKeys.CorePriority);

        public double RuleMargin => RuleScore(Core) - RuleScore(Other);
    }

    /// <summary>
    /// Pairs where build A is leaning (commit ≥ leanMin, top or within hybridMargin of the top) and owns no core card,
    /// C is a missing core card of A without "엔진 없음", and N is a non-core card of A whose base value is at most one
    /// tier step (15) above C's. <paramref name="counterfactual"/>: candidates are all of A's cards instead of the offer.
    /// </summary>
    internal static IReadOnlyList<CorePair> Pairs(DataPack pack, bool counterfactual)
    {
        var scorer = new CardScorer(pack);
        var w = pack.Weights;
        var step = w.TierValue[Tier.A] - w.TierValue[Tier.B];
        var pairs = new List<CorePair>();
        foreach (var file in TestPaths.AllRuns)
        {
            var run = RunHistoryReader.ReadFile(file);
            var player = LocalPlayer.Resolve(run);
            foreach (var d in DecisionExtractor.Extract(run, player.Id))
            {
                var state = d.StateBefore;
                var profile = DeckAnalyzer.Analyze(state, pack);
                if (profile.Archetypes.Count == 0) continue;
                var top = profile.Archetypes.Max(CardScorer.Commit);
                var offered = d.Offered.Distinct().Select(c => scorer.Score(state, profile, c, d.Context)).ToList();
                foreach (var a in profile.Archetypes)
                {
                    var commit = CardScorer.Commit(a);
                    var leaning = commit >= w.CorePriority.LeanMin - 1e-9 && commit >= top - w.HybridMargin - 1e-9;
                    if (!leaning || a.CoreTarget == 0 || a.OwnedCore > 0) continue;
                    var evals = counterfactual
                        ? a.Archetype.Cards.All.Select(pack.Canonical).Distinct(StringComparer.Ordinal)
                            .Select(id => scorer.Score(state, profile, new CardRef(id), d.Context)).ToList()
                        : offered;
                    string? RoleIn(CardEvaluation e) => a.Archetype.RoleOf(pack.Canonical(e.Card.Id));
                    var cores = evals.Where(e => RoleIn(e) == ArchetypeRoles.Core
                                                 && profile.Copies.GetValueOrDefault(pack.Canonical(e.Card.Id)) == 0
                                                 && e.Score.ValueOf(ComponentKeys.EngineMissing) == 0).ToList();
                    foreach (var c in cores)
                    {
                        foreach (var n in evals)
                        {
                            var role = RoleIn(n);
                            if (role is null or ArchetypeRoles.Core) continue;
                            if (n.Score.ValueOf(ComponentKeys.Base) - c.Score.ValueOf(ComponentKeys.Base) > step + 1e-9) continue;
                            var nId = pack.Canonical(n.Card.Id);
                            var exclusive = !profile.Archetypes.Any(o => !ReferenceEquals(o, a) && CardScorer.Commit(o) > 0 && o.Archetype.WeightOf(nId) > 0);
                            pairs.Add(new CorePair(Path.GetFileNameWithoutExtension(file), d.Node.Floor, a.Archetype.Id, commit,
                                profile.IsWeak, exclusive, c, n, role));
                        }
                    }
                }
            }
        }
        return pairs;
    }

    private void Report(string name, IReadOnlyList<CorePair> pairs)
    {
        var lf = RealPack.Value.Weights.CorePriority.LeanFull;
        output.WriteLine($"{name}: {pairs.Count} pairs, core ranks first {pairs.Count(p => p.CoreFirst)} ({Pct(pairs.Count(p => p.CoreFirst), pairs.Count)})");
        foreach (var g in pairs.GroupBy(p => (Full: p.Commit >= lf, p.Weak)).OrderBy(g => g.Key.Full).ThenBy(g => g.Key.Weak))
        {
            output.WriteLine($"  commit≥leanFull {g.Key.Full}, weak {g.Key.Weak}: {g.Count(p => p.CoreFirst)}/{g.Count()}");
        }
        var scope = pairs.Where(InRuleScope).ToList();
        output.WriteLine($"  rule scope: {scope.Count} pairs, core first on total {scope.Count(p => p.CoreFirst)}, "
            + $"min rule margin {(scope.Count > 0 ? scope.Min(p => p.RuleMargin) : 0):0.##}");
    }

    private static string Pct(int a, int b) => b == 0 ? "-" : $"{100.0 * a / b:0.#}%";

    /// <summary>Full lean (commit ≥ leanFull), deck not weak (weak halves the bonus by design), N valued only by build A.</summary>
    private static bool InRuleScope(CorePair p)
        => p.Commit >= RealPack.Value.Weights.CorePriority.LeanFull - 1e-9 && !p.Weak && p.OtherExclusive;

    [Fact]
    public void RecordedOffers_MissingCoreOfTheLeaningBuild_AlwaysRanksAboveSameBuildCardsUpToOneTierBetter()
    {
        var pairs = OfferedPairs.Value;
        Report("recorded offers", pairs);
        foreach (var p in pairs)
        {
            output.WriteLine($"  {(p.CoreFirst ? "ok " : "BAD")} {p.Run} f{p.Floor} {p.Archetype} {p.Commit:0.00} weak {p.Weak} | "
                + $"{p.Core.Card.Id} {p.Core.Score} || {p.Other.Card.Id}({p.OtherRole}) {p.Other.Score}");
        }
        Assert.NotEmpty(pairs);
        Assert.All(pairs, p => Assert.True(p.CoreFirst,
            $"{p.Run} floor {p.Floor} {p.Archetype}: {p.Core.Card.Id} {p.Core.Total:0.#} ≤ {p.Other.Card.Id} {p.Other.Total:0.#}"));
    }

    [Fact]
    public void CounterfactualOffers_RuleScopeHoldsWithoutException()
    {
        var pairs = CounterfactualPairs.Value;
        Report("counterfactual", pairs);
        var scope = pairs.Where(InRuleScope).ToList();
        Assert.True(scope.Count >= 100, $"only {scope.Count} pairs in scope");
        // On what the rule controls (base + build + core_missing + core_priority) the core is ahead in 100% of the pairs.
        Assert.All(scope, p => Assert.True(p.RuleMargin > 0,
            $"{p.Run} floor {p.Floor} {p.Archetype}: {p.Core.Card.Id} vs {p.Other.Card.Id} margin {p.RuleMargin:0.##}"));
        // Total ranking can still flip on deck needs, combos, saturation, dilution and duplicates (weakness first); report only.
        foreach (var g in pairs.Where(p => !p.CoreFirst).GroupBy(Decider).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  total flipped (all pairs) by {g.Key}: {g.Count()}");
        }
    }

    /// <summary>What lets the other card win: "weak/low commit" when the rule itself is behind, else the swing component.</summary>
    private static string Decider(CorePair p)
    {
        if (p.RuleMargin <= 0)
        {
            return p.Weak ? "rule behind (weak deck, bonus halved)"
                : p.Commit < RealPack.Value.Weights.CorePriority.LeanFull ? "rule behind (commit below leanFull)"
                : "rule behind (other build values the card)";
        }
        var keys = p.Core.Score.Components.Concat(p.Other.Score.Components).Select(c => c.Key).Distinct()
            .Where(k => k is not (ComponentKeys.Base or ComponentKeys.Build or ComponentKeys.CoreMissing or ComponentKeys.CorePriority));
        return keys.OrderByDescending(k => p.Other.Score.ValueOf(k) - p.Core.Score.ValueOf(k)).FirstOrDefault() ?? "?";
    }

    public static IEnumerable<object[]> Characters =>
    [
        ["CHARACTER.IRONCLAD"], ["CHARACTER.SILENT"], ["CHARACTER.DEFECT"], ["CHARACTER.NECROBINDER"], ["CHARACTER.REGENT"],
    ];

    /// <summary>
    /// Per character: for every build, a deck of that build's non-core cards that leans into it (commit ≥ leanFull), with role
    /// needs met and no weak role. Every missing core card then ranks above every same-build non-core card that is equal or
    /// one tier better (and not valued by another build); owning a core removes the bonus and this build's discount;
    /// a weak deck halves the bonus.
    /// </summary>
    [Theory]
    [MemberData(nameof(Characters))]
    public void Scenario_PerCharacter_CoreFirstUntilOwned(string character)
    {
        var pack = RealPack.Value;
        var scorer = new CardScorer(pack);
        var lf = pack.Weights.CorePriority.LeanFull;
        var step = pack.Weights.TierValue[Tier.A] - pack.Weights.TierValue[Tier.B];
        var checkedPairs = 0;
        foreach (var archetype in pack.ArchetypesFor(character))
        {
            var cores = archetype.Cards.Core.Select(pack.Canonical).Distinct().ToList();
            if (cores.Count == 0) continue;
            var others = archetype.Cards.All.Select(pack.Canonical).Distinct().Where(id => !cores.Contains(id)).ToList();

            // Deck: enablers/supports first, then payoffs, until the build leans fully; offered non-core cards are the rest.
            var deck = new List<string>();
            foreach (var id in others.OrderBy(id => archetype.RoleOf(id) == ArchetypeRoles.Payoff ? 1 : 0))
            {
                if (Status(pack, character, deck, archetype).Commit >= lf) break;
                deck.Add(id);
            }
            if (Status(pack, character, deck, archetype).Commit < lf) continue;

            var state = new RunState { Character = character, Act = 2, Hp = 70, MaxHp = 70, Deck = deck.Select(id => new DeckCard(new CardRef(id))).ToArray() };
            var profile = Settled(DeckAnalyzer.Analyze(state, pack));
            var mine = profile.Archetypes.First(s => s.Archetype == archetype);
            if (profile.Archetypes.Max(CardScorer.Commit) - CardScorer.Commit(mine) > pack.Weights.HybridMargin) continue;
            Assert.Equal(0, mine.OwnedCore);
            Assert.False(mine.CoreSecured);
            Assert.Equal(cores, mine.MissingCore.Select(m => m.CardId));

            foreach (var c in cores.Where(id => pack.FindTier(character, id)?.Requires.Count is 0 or null))
            {
                var ce = Consistent(scorer.Score(state, profile, new CardRef(c)));
                Assert.True(ce.Score.ValueOf(ComponentKeys.CorePriority) > 0, $"{archetype.Id}: {c} has no core priority");
                foreach (var n in others.Where(id => !deck.Contains(id)))
                {
                    var ne = Consistent(scorer.Score(state, profile, new CardRef(n)));
                    var diff = ne.Score.ValueOf(ComponentKeys.Base) - ce.Score.ValueOf(ComponentKeys.Base);
                    if (diff < -1e-9 || diff > step + 1e-9) continue; // equal or up to one tier better only
                    if (profile.Archetypes.Any(o => o.Archetype != archetype && CardScorer.Commit(o) > 0 && o.Archetype.WeightOf(n) > 0)) continue;
                    Assert.True(CorePair.RuleScore(ce) > CorePair.RuleScore(ne), $"{archetype.Id}: core {c} {ce.Score} vs {n} {ne.Score}");
                    checkedPairs++;
                }

                // Owning a core: no bonus for the other cores, and this build no longer discounts its other cards.
                var owned = state with { Deck = state.Deck.Append(new DeckCard(new CardRef(c))).ToArray() };
                var ownedProfile = Settled(DeckAnalyzer.Analyze(owned, pack));
                if (archetype.CoreTarget <= 1)
                {
                    Assert.True(ownedProfile.Archetypes.First(s => s.Archetype == archetype).CoreSecured);
                    foreach (var other in cores.Where(id => id != c))
                    {
                        Assert.Equal(0, scorer.Score(owned, ownedProfile, new CardRef(other)).Score.ValueOf(ComponentKeys.CorePriority), 9);
                    }
                    foreach (var n in others.Where(id => !profile.Archetypes.Any(o => o.Archetype != archetype && o.Archetype.WeightOf(id) > 0)))
                    {
                        Assert.Equal(0, scorer.Score(owned, ownedProfile, new CardRef(n)).Score.ValueOf(ComponentKeys.CoreMissing), 9);
                    }
                }

                // Weak deck halves it.
                var weak = profile with { WeakRoles = [Roles.Block] };
                Assert.Equal(ce.Score.ValueOf(ComponentKeys.CorePriority) * pack.Weights.CorePriority.WeakFactor,
                    scorer.Score(state, weak, new CardRef(c)).Score.ValueOf(ComponentKeys.CorePriority), 9);
            }
        }
        output.WriteLine($"{character}: {checkedPairs} core/non-core pairs checked");
        Assert.True(checkedPairs > 0, $"{character}: no scenario pair found");
    }

    /// <summary>Needs met and no weak role, so the comparison isolates the core-first rule.</summary>
    private static DeckProfile Settled(DeckProfile p) => p with { WeakRoles = [], RoleGap = new Dictionary<string, double>() };

    private static ArchetypeStatus Status(DataPack pack, string character, IEnumerable<string> deck, Archetype a)
        => ArchetypeDetector.Detect(new RunState { Character = character, Deck = deck.Select(id => new DeckCard(new CardRef(id))).ToArray() }, pack)
            .First(s => s.Archetype == a);

    private static CardEvaluation Consistent(CardEvaluation e)
    {
        Assert.Equal(e.Score.Components.Sum(c => c.Value), e.Total, 9);
        return e;
    }
}
