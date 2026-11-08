using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Tests.Data;
using Xunit.Abstractions;

namespace Sts2Overlay.Core.Tests.Integration;

/// <summary>
/// "After the core, the build's essentials first" (<c>secured_essential</c>) with the real v0.107.1 pack over every decision of the
/// 24 fixture runs and the Silent poison regression run (tests/fixtures/regression). Written against archetype roles only (no card ids
/// in assertions), so a rebuilt archetype list drops in.
/// </summary>
public class SecuredBuildReplayTests(ITestOutputHelper output)
{
    private static readonly Lazy<DataPack> RealPack = new(() =>
        DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1"), RealCatalog.Instance));

    /// <summary>The user's Silent run that went all-in poison while the overlay kept recommending draw cards (single player, no ids).</summary>
    private static string PoisonRun => Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "regression", "silent-poison-1794163190.run");

    private sealed record Replayed(string Run, int Floor, RunState State, DeckProfile Profile, IReadOnlyList<CardEvaluation> Offered, OfferContext Context);

    private static IReadOnlyList<Replayed> Replay(IEnumerable<string> files)
    {
        var pack = RealPack.Value;
        var scorer = new CardScorer(pack);
        var list = new List<Replayed>();
        foreach (var file in files)
        {
            var run = RunHistoryReader.ReadFile(file);
            var player = LocalPlayer.Resolve(run);
            foreach (var d in DecisionExtractor.Extract(run, player.Id))
            {
                var profile = DeckAnalyzer.Analyze(d.StateBefore, pack);
                var offered = d.Offered.Distinct().Select(c => scorer.Score(d.StateBefore, profile, c, d.Context)).ToList();
                list.Add(new Replayed(Path.GetFileNameWithoutExtension(file), d.Node.Floor, d.StateBefore, profile, offered, d.Context));
            }
        }
        return list;
    }

    private static readonly Lazy<IReadOnlyList<Replayed>> All = new(() => Replay(TestPaths.AllRuns.Append(PoisonRun)));

    /// <summary>Builds with core cards that own their core and lean (commit ≥ leanMin, top or within hybridMargin of the top).</summary>
    private static IEnumerable<ArchetypeStatus> SecuredLeaning(DeckProfile p, Weights w)
    {
        var top = p.Archetypes.Select(CardScorer.Commit).DefaultIfEmpty(0).Max();
        return p.Archetypes.Where(a => a.CoreTarget > 0 && a.CoreSecured
            && CardScorer.Commit(a) >= w.CorePriority.LeanMin - 1e-9 && CardScorer.Commit(a) >= top - w.HybridMargin - 1e-9);
    }

    /// <summary>The documented floor for build <paramref name="a"/>: <c>floor × clamp((commit − leanMin)/(leanFull − leanMin)) × taper × emergency</c>.</summary>
    private static double ExpectedFloor(ArchetypeStatus a, Replayed r, Weights w)
    {
        var cp = w.CorePriority;
        var ramp = Math.Clamp((CardScorer.Commit(a) - cp.LeanMin) / (cp.LeanFull - cp.LeanMin), 0, 1);
        var excess = a.OwnedEssential - (a.EssentialTarget + cp.SecuredEssentialExtra);
        var taper = excess <= 0 ? 1 : Math.Max(0, 1 - excess * cp.SecuredEssentialTaper);
        var emergency = CardScorer.IsEmergency(r.State, r.Profile, w) ? cp.EmergencyFactor : 1;
        return cp.SecuredEssentialFloor * ramp * taper * emergency;
    }

    private static double Synergy(CardEvaluation e)
        => e.Score.ValueOf(ComponentKeys.Build) + e.Score.ValueOf(ComponentKeys.CoreMissing) + e.Score.ValueOf(ComponentKeys.SecuredEssential);

    private static bool IsEssential(string? role) => role is ArchetypeRoles.Payoff or ArchetypeRoles.Enabler;

    /// <summary>
    /// Invariant (d), counterfactual: at every replayed state, every essential card of a secured leaning build (not behind the requires gate)
    /// counts at least that build's documented floor of synergy, and never more than max(buildCap, floor) — below two tier steps, so a
    /// generic card two tiers higher keeps winning on base alone.
    /// </summary>
    [Fact]
    public void Counterfactual_SecuredEssentialSynergy_IsAtLeastTheFloor_AndBelowTwoTierSteps()
    {
        var pack = RealPack.Value;
        var w = pack.Weights;
        var scorer = new CardScorer(pack);
        var twoSteps = 2 * (w.TierValue[Tier.A] - w.TierValue[Tier.B]);
        int checks = 0, states = 0, tapered = 0, emergencies = 0;
        foreach (var r in All.Value)
        {
            foreach (var a in SecuredLeaning(r.Profile, w))
            {
                states++;
                var floor = ExpectedFloor(a, r, w);
                if (floor < w.CorePriority.SecuredEssentialFloor * CardScorer.SecuredRamp(CardScorer.Commit(a), w.CorePriority) - 1e-9)
                {
                    if (CardScorer.IsEmergency(r.State, r.Profile, w)) emergencies++; else tapered++;
                }
                foreach (var id in a.Archetype.Cards.Payoff.Concat(a.Archetype.Cards.Enabler).Select(pack.Canonical).Distinct()
                             .Where(id => IsEssential(a.Archetype.RoleOf(id))))
                {
                    var e = scorer.Score(r.State, r.Profile, new CardRef(id), r.Context);
                    if (e.Score.ValueOf(ComponentKeys.EngineMissing) != 0) continue;
                    checks++;
                    Assert.True(Synergy(e) >= floor - 1e-9, $"{r.Run} f{r.Floor} {a.Archetype.Id} {id}: synergy {Synergy(e):0.##} < floor {floor:0.##} ({e.Score})");
                    Assert.True(Synergy(e) <= Math.Max(w.BuildCap, w.CorePriority.SecuredEssentialFloor) + 1e-9);
                    Assert.True(Synergy(e) < twoSteps, $"{r.Run} f{r.Floor} {id}: synergy {Synergy(e):0.##} reaches two tier steps");
                }
            }
        }
        output.WriteLine($"(d) {states} secured leaning build states, {checks} essential checks; floor tapered in {tapered}, halved by emergency in {emergencies}");
        Assert.True(checks > 100, $"only {checks} checks");
    }

    /// <summary>
    /// Invariant (d), recorded offers: when a secured build leans fully (commit ≥ leanFull) and there is no emergency, an offered essential
    /// of it ranks above every offered card that no build lists and that is not on-theme, with a base at most one tier step higher —
    /// on base + synergy + need (dilution, duplicates and saturation are reported only). The total is reported too.
    /// </summary>
    [Fact]
    public void RecordedOffers_SecuredEssential_OutranksGenericCardsUpToOneTierHigher()
    {
        var pack = RealPack.Value;
        var w = pack.Weights;
        var step = w.TierValue[Tier.A] - w.TierValue[Tier.B];
        int pairs = 0, totalFirst = 0;
        var flips = new List<string>();
        foreach (var r in All.Value)
        {
            if (CardScorer.IsEmergency(r.State, r.Profile, w)) continue;
            foreach (var a in SecuredLeaning(r.Profile, w).Where(a => CardScorer.Commit(a) >= w.CorePriority.LeanFull - 1e-9))
            {
                var excess = a.OwnedEssential - (a.EssentialTarget + w.CorePriority.SecuredEssentialExtra);
                if (excess > 0) continue; // tapered: the build is done, needs may win again
                foreach (var e in r.Offered.Where(e => IsEssential(a.Archetype.RoleOf(pack.Canonical(e.Card.Id)))
                                                       && e.Score.ValueOf(ComponentKeys.EngineMissing) == 0
                                                       && e.Score.ValueOf(ComponentKeys.Duplicate) == 0))
                {
                    foreach (var x in r.Offered)
                    {
                        var xid = pack.Canonical(x.Card.Id);
                        if (r.Profile.Archetypes.Any(o => o.Archetype.RoleOf(xid) is not null)) continue;
                        if (a.Archetype.IsOnTheme(xid, pack.FindTier(r.State.Character, xid), pack.Canonical)) continue;
                        if (x.Score.ValueOf(ComponentKeys.Base) - e.Score.ValueOf(ComponentKeys.Base) > step + 1e-9) continue;
                        pairs++;
                        double Rule(CardEvaluation c) => c.Score.ValueOf(ComponentKeys.Base) + Synergy(c) + c.Score.ValueOf(ComponentKeys.Need);
                        Assert.True(Rule(e) > Rule(x), $"{r.Run} f{r.Floor} {a.Archetype.Id}: {e.Card.Id} {e.Score} ≤ {x.Card.Id} {x.Score}");
                        if (e.Total > x.Total) totalFirst++;
                        else flips.Add($"{r.Run} f{r.Floor}: {e.Card.Id} {e.Score} ≤ {x.Card.Id} {x.Score}");
                    }
                }
            }
        }
        output.WriteLine($"(d) recorded: {pairs} essential/generic pairs, rule score 100%, total {totalFirst}/{pairs}");
        foreach (var f in flips) output.WriteLine("  total flipped: " + f);
        Assert.True(pairs > 0, "no pair in scope");
    }

    /// <summary>
    /// The poison regression run: once the poison core is in, the build's essentials and on-theme poison cards are no longer pushed below
    /// generic draw cards. Role-based (whatever cards the pack lists as core/essential), so it holds with a rebuilt Silent list.
    /// </summary>
    [Fact]
    public void PoisonRun_AfterTheCore_NoPoisonCardTakesTheOffBuildPenalty_AndEssentialsBeatTheDrawCardsOfferedWithThem()
    {
        var pack = RealPack.Value;
        var w = pack.Weights;
        var step = w.TierValue[Tier.A] - w.TierValue[Tier.B];
        var decisions = Replay([PoisonRun]);
        Assert.True(decisions.Count >= 20);
        int themeChecks = 0, essentialWins = 0;
        foreach (var r in decisions)
        {
            var poison = r.Profile.Archetypes.Single(a => a.Archetype.Needs.Any(n => n.Tag == "POISON") && a.Archetype.Id.Contains("poison"));
            if (!poison.CoreSecured) continue;
            foreach (var e in r.Offered)
            {
                var id = pack.Canonical(e.Card.Id);
                var entry = pack.FindTier(r.State.Character, id);
                var fits = poison.Archetype.RoleOf(id) is not null || poison.Archetype.IsOnTheme(id, entry, pack.Canonical);
                if (!fits) continue;
                themeChecks++;
                Assert.True(e.Score.ValueOf(ComponentKeys.OffBuild) == 0, $"f{r.Floor} {id}: {e.Score}");
            }
            if (CardScorer.IsEmergency(r.State, r.Profile, w) || CardScorer.Commit(poison) < w.CorePriority.LeanFull) continue;
            foreach (var e in r.Offered.Where(e => IsEssential(poison.Archetype.RoleOf(pack.Canonical(e.Card.Id)))
                                                   && e.Score.ValueOf(ComponentKeys.Duplicate) == 0))
            {
                foreach (var d in r.Offered.Where(d => pack.FindTier(r.State.Character, pack.Canonical(d.Card.Id))?.Roles.GetValueOrDefault(Roles.Draw) > 0
                                                       && r.Profile.Archetypes.All(o => o.Archetype.RoleOf(pack.Canonical(d.Card.Id)) is null)
                                                       && d.Score.ValueOf(ComponentKeys.Base) - e.Score.ValueOf(ComponentKeys.Base) <= step + 1e-9))
                {
                    output.WriteLine($"f{r.Floor}: {e.Card.Id} {e.Total:0.#} vs {d.Card.Id} {d.Total:0.#}");
                    Assert.True(e.Total > d.Total, $"f{r.Floor}: {e.Card.Id} {e.Score} ≤ {d.Card.Id} {d.Score}");
                    essentialWins++;
                }
            }
        }
        output.WriteLine($"poison run: {themeChecks} poison-fitting offers checked for off-build, {essentialWins} essential-vs-draw pairs");
        Assert.True(themeChecks >= 10);
        Assert.True(essentialWins > 0, "no essential-vs-draw pair in scope");
    }
}
