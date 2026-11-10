using System.Reflection;
using System.Text.RegularExpressions;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;
using Sts2Overlay.Core.Tests.Data;
using Xunit.Abstractions;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Scoring;

/// <summary>
/// English output of the scoring area (card, skip, shop and upgrade reasons, build roles, flip hints): every text these
/// classes produce follows <see cref="Loc"/>, and Korean stays the default.
/// </summary>
public partial class ScoringEnglishTests(ITestOutputHelper output)
{
    [GeneratedRegex(@"[가-힣]")]
    private static partial Regex Hangul();

    private static bool HasHangul(string? s) => s is not null && Hangul().IsMatch(s);

    private static readonly Lazy<DataPack> RealPack = new(() =>
        DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1"), RealCatalog.Instance));

    private static IEnumerable<PropertyInfo> LabelProperties()
        => typeof(ScoringLabels).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string));

    [Fact]
    public void Labels_FollowTheLanguage()
    {
        Assert.NotEmpty(LabelProperties());
        using (Loc.Use(Lang.Eng))
        {
            foreach (var p in LabelProperties())
            {
                var text = (string)p.GetValue(null)!;
                Assert.False(string.IsNullOrWhiteSpace(text), p.Name);
                Assert.False(HasHangul(text), $"{p.Name}: {text}");
            }
            foreach (var role in Roles.All)
            {
                Assert.False(HasHangul(ScoringLabels.Role(role)), role);
                Assert.False(HasHangul(ScoringLabels.NeedLabel(role)), role);
            }
            foreach (var kind in Enum.GetValues<BuildRoleKind>()) Assert.False(HasHangul(ScoringLabels.BuildRoleKind(kind)));

            Assert.Equal("Lacks AoE", ScoringLabels.NeedLabel(Roles.Aoe));
            Assert.Equal("Get core", ScoringLabels.CorePriority);
            Assert.Equal("Orb core · Power key synergy",
                ScoringLabels.BuildRoleText([new BuildRole("a", "Orb", BuildRoleKind.Core), new BuildRole("b", "Power", BuildRoleKind.Essential)]));
        }

        using (Loc.Use(Lang.Kor))
        {
            Assert.Equal("광역 부족", ScoringLabels.NeedLabel(Roles.Aoe));
            Assert.Equal("코어 확보", ScoringLabels.CorePriority);
            Assert.Equal("소멸 재료", ScoringLabels.Role(Roles.ExhaustFodder));
        }
    }

    [Fact]
    public void CardScore_English_NeedLabelAndDetails()
    {
        var pack = DefectPack();
        using var _ = Loc.Use(Lang.Eng);
        var e = new CardScorer(pack).Score(State(act: 1), new DeckProfile { RoleGap = new Dictionary<string, double> { [Roles.Aoe] = 0.8 } },
            Parse(AoeHit));
        var need = e.Score.Components.Single(c => c.Key == ComponentKeys.Need);
        Assert.Equal("Lacks AoE", need.Label);
        Assert.Equal(Roles.Aoe, need.Role);
        Assert.Equal("Base A", e.Score.Components.Single(c => c.Key == ComponentKeys.Base).Label);
        Assert.Equal("Act 1 tier A", e.Score.Components.Single(c => c.Key == ComponentKeys.Base).Detail);
        Assert.All(e.Score.Components, c => Assert.False(HasHangul(c.Label + c.Detail), c.Label + c.Detail));
    }

    [Fact]
    public void Combo_UsesLocalName()
    {
        var combos = new List<Combo>
        {
            new() { Character = Defect, Cards = [ComboA, ComboB], Name = "짝 콤보", NameEng = "Pair combo", W = 6 },
            new() { Character = Defect, Cards = [TrioA, TrioB, TrioC], Name = "세 장 콤보", NameEng = "Trio combo", W = 6 },
        };
        var pack = DefectPack(combos: combos);
        var scorer = new CardScorer(pack);
        string ComboDetail(string card, params string[] owned)
            => scorer.Score(State(), new DeckProfile { Copies = owned.ToDictionary(c => c, _ => 1) }, Parse(card))
                .Score.Components.Single(c => c.Key == ComponentKeys.Combo).Detail!;

        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("Pair combo", ComboDetail(ComboA, ComboB));
            Assert.Equal("Trio combo (partial)", ComboDetail(TrioA, TrioB));
        }
        using (Loc.Use(Lang.Kor))
        {
            Assert.Equal("짝 콤보", ComboDetail(ComboA, ComboB));
            Assert.Equal("세 장 콤보 (일부)", ComboDetail(TrioA, TrioB));
        }
    }

    [Fact]
    public void FlipHint_Need_UsesRoleKeyNotLabelText()
    {
        // The label is deliberately not a need label of any language: the hint must come from Role.
        var a = new ScoreBreakdown([new(ComponentKeys.Base, "x", 50), new(ComponentKeys.Need, "whatever", 8) { Role = Roles.Draw }]);
        var b = new ScoreBreakdown([new(ComponentKeys.Base, "x", 52), new(ComponentKeys.Need, "whatever", 1) { Role = Roles.Draw }]);
        using (Loc.Use(Lang.Eng)) Assert.Equal("More Draw cards in the deck would flip the order.", RecommendationEngine.FlipHint(a, false, b, false));
        using (Loc.Use(Lang.Kor)) Assert.Equal("덱에 드로우 카드가 더 있으면 순위가 바뀝니다.", RecommendationEngine.FlipHint(a, false, b, false));
    }

    [Fact]
    public void UpgradeAdvisor_KeepsNeedRole()
    {
        var pack = DefectPack();
        using var _ = Loc.Use(Lang.Eng);
        var state = State(act: 1, deck: [AoeHit, Strike, Defend]);
        var ranked = new UpgradeAdvisor(pack).Rank(state, new DeckProfile
        {
            RoleGap = new Dictionary<string, double> { [Roles.Aoe] = 0.8 },
            Copies = new Dictionary<string, int> { [AoeHit] = 1 },
        });
        var aoe = ranked.Single(c => c.Card.Id == AoeHit);
        Assert.StartsWith("Upgrade +", aoe.Score.Components.Single(c => c.Key == UpgradeComponentKeys.Upgrade).Label);
        Assert.Equal(Roles.Aoe, aoe.Score.Components.Single(c => c.Key == ComponentKeys.Need).Role);
        Assert.All(ranked.SelectMany(c => c.Score.Components), c => Assert.False(HasHangul(c.Label + c.Detail), c.Label + c.Detail));
    }

    /// <summary>
    /// Every recorded decision of the fixture runs with the real v0.107.1 pack, in English: card offers (recorded context, plus a
    /// low-HP and a boss-reward variant over every card of the profile's builds), the shop over the same cards, smith upgrades and
    /// flip hints for every pair of candidates. Asserts no Hangul in any label, detail, chip, build-role text or hint.
    /// <para>
    /// Scope: text this area produces. Combo names come from the pack (<see cref="Combo.LocalName"/>, English from
    /// <c>text.eng.json</c>), so they are removed from the combo detail before the check; archetype names
    /// (<see cref="Archetype.DisplayName"/>) are pack data too but already have English names, so they stay in.
    /// </para>
    /// </summary>
    [Fact]
    public void Sweep_RealPackFixtures_EnglishHasNoHangul()
    {
        var pack = RealPack.Value;
        using var _ = Loc.Use(Lang.Eng);
        var engine = new RecommendationEngine(pack);
        var scorer = new CardScorer(pack);
        var shop = new ShopScorer(pack);
        var upgrades = new UpgradeAdvisor(pack);
        var bad = new List<string>();
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        int texts = 0, hints = 0, decisions = 0;

        void Check(string where, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            texts++;
            if (HasHangul(text) && bad.Count < 50) bad.Add($"{where}: {text}");
        }

        void CheckBreakdown(string where, string character, ScoreBreakdown score)
        {
            foreach (var c in score.Components)
            {
                keys.Add(c.Key);
                Check($"{where} label", c.Label);
                var detail = c.Detail;
                if (c.Key == ComponentKeys.Combo && detail is not null)
                {
                    foreach (var name in pack.CombosFor(character).Select(x => x.LocalName).OrderByDescending(n => n.Length))
                    {
                        detail = detail.Replace(name, "", StringComparison.Ordinal);
                    }
                }
                Check($"{where} detail [{c.Key}]", detail);
            }
        }

        void CheckCard(string where, string character, CardEvaluation e)
        {
            CheckBreakdown(where, character, e.Score);
            foreach (var chip in e.Chips) Check($"{where} chip", chip.Label);
            Check($"{where} build role", ScoringLabels.BuildRoleText(e.BuildRoles));
        }

        void CheckHints(string where, IReadOnlyList<CardEvaluation> cards, ScoreBreakdown skip)
        {
            var all = cards.Select(c => (Score: c.Score, IsSkip: false)).Append((Score: skip, IsSkip: true)).ToList();
            foreach (var x in all)
            {
                foreach (var y in all)
                {
                    if (ReferenceEquals(x.Score, y.Score) || (x.IsSkip && y.IsSkip)) continue;
                    hints++;
                    Check($"{where} flip", RecommendationEngine.FlipHint(x.Score, x.IsSkip, y.Score, y.IsSkip));
                }
            }
        }

        foreach (var file in TestPaths.AllRuns)
        {
            var run = RunHistoryReader.ReadFile(file);
            var player = LocalPlayer.Resolve(run);
            var name = Path.GetFileNameWithoutExtension(file);
            foreach (var d in DecisionExtractor.Extract(run, player.Id))
            {
                decisions++;
                var state = d.StateBefore;
                var where = $"{name} floor {d.Node.Floor}";
                var profile = engine.Analyze(state);

                var offer = engine.EvaluateOffer(state, profile, d.Offered, d.Context);
                foreach (var e in offer.Cards) CheckCard(where, state.Character, e);
                CheckBreakdown($"{where} skip", state.Character, offer.Skip);
                // The learned skip shows a sign-flipped engine part inside "skip tendency": reach every engine template too.
                CheckBreakdown($"{where} engine skip", state.Character, new SkipScorer(pack).Score(state, profile));
                Check($"{where} offer flip", offer.FlipHint);
                CheckHints(where, offer.Cards, offer.Skip);

                // Counterfactual: every card of the profile's builds as a boss reward at low HP (core priority, core missing,
                // boss bonus, low-HP block, engine missing …).
                var lowHp = state with { Hp = Math.Max(1, state.MaxHp / 5) };
                var lowProfile = engine.Analyze(lowHp);
                var buildCards = lowProfile.Archetypes.SelectMany(a => a.Archetype.Cards.All).Select(pack.Canonical)
                    .Distinct(StringComparer.Ordinal).Select(id => new CardRef(id)).ToList();
                var boss = buildCards.Select(c => scorer.Score(lowHp, lowProfile, c, OfferContext.BossReward)).ToList();
                foreach (var e in boss) CheckCard($"{where} boss", state.Character, e);

                var shopEval = shop.Evaluate(state, profile,
                    new ShopOffer(d.Offered.Concat(buildCards.Take(5)).Select(c => new ShopCardOffer(c, 75)).ToArray()));
                foreach (var item in shopEval.Items)
                {
                    CheckBreakdown($"{where} shop", state.Character, item.Value);
                    if (item.CardEvaluation is { } ce) CheckCard($"{where} shop card", state.Character, ce);
                }

                foreach (var u in upgrades.Rank(state, profile))
                {
                    CheckBreakdown($"{where} upgrade", state.Character, u.Score);
                    foreach (var chip in u.Chips) Check($"{where} upgrade chip", chip.Label);
                }
            }
        }

        output.WriteLine($"{decisions} decisions, {texts} texts, {hints} flip hints; keys: {string.Join(", ", keys)}");
        Assert.True(decisions > 100, $"only {decisions} decisions");
        Assert.True(hints > 100, $"only {hints} flip hints");
        // The sweep must actually reach the English template of every component.
        foreach (var key in new[]
                 {
                     ComponentKeys.Base, ComponentKeys.Build, ComponentKeys.Need, ComponentKeys.Situation, ComponentKeys.Duplicate,
                     ComponentKeys.Dilution, ComponentKeys.CorePriority, ComponentKeys.CoreMissing, ComponentKeys.Combo,
                     ComponentKeys.EngineMissing, ComponentKeys.OffBuild, ComponentKeys.Saturation, ComponentKeys.PowerSaturation,
                     ComponentKeys.SkipBase, ComponentKeys.SkipDeckSize, ComponentKeys.SkipCommit, ComponentKeys.SkipBasics,
                     ComponentKeys.SkipRelative, ComponentKeys.RemovalValue, UpgradeComponentKeys.Upgrade,
                 })
        {
            Assert.Contains(key, keys);
        }
        Assert.True(bad.Count == 0, "Hangul in English output:\n" + string.Join("\n", bad));
    }
}
