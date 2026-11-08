using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Integration.Fakes;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay;
using Sts2Overlay.Core.Rest;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;
using Sts2Overlay.GameState.Tracking;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Rest;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The controllers' presenters in English: the wording is English and no Korean leaks into what they produce. Card names
/// come from a provider, so the tests use ids or English stand-ins for them.
/// </summary>
public class ControllerEnglishTests
{
    private static readonly Regex Hangul = new("[가-힣]", RegexOptions.Compiled);

    private static void AssertNoHangul(IEnumerable<string?> texts)
    {
        foreach (var t in texts)
        {
            if (t is not null) Assert.False(Hangul.IsMatch(t), $"Korean text in English mode: \"{t}\"");
        }
    }

    /// <summary>Every string property of a view model, recursing into nested view models and lists of them.</summary>
    private static IEnumerable<string?> Texts(object? vm)
    {
        if (vm is null) yield break;
        if (vm is string s)
        {
            yield return s;
            yield break;
        }
        if (vm is IEnumerable list)
        {
            foreach (var item in list)
            {
                foreach (var t in Texts(item)) yield return t;
            }
            yield break;
        }
        var type = vm.GetType();
        if (type.Namespace != typeof(OfferSectionVm).Namespace) yield break;
        foreach (var p in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            if (p.PropertyType == typeof(string) || typeof(IEnumerable).IsAssignableFrom(p.PropertyType) || p.PropertyType.Namespace == type.Namespace)
            {
                foreach (var t in Texts(p.GetValue(vm))) yield return t;
            }
        }
    }

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SlayTheSpire2TierOverlay.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    private static readonly Lazy<DataPack> Pack = new(() => DataPackLoader.LoadDirectory(Path.Combine(RepoRoot, "data", "packs", "v0.107.1")));

    /// <summary>No catalog and an empty pack: names are prettified ids ("CARD.BASH" → "Bash"), never Korean.</summary>
    private static readonly CardInfoProvider IdNames = CardInfoProvider.Load(null, new DataPack());

    // ---- BuildCardsPresenter ---------------------------------------------------------------------------------------

    private static BuildCardVm Tile(string id, string role, bool owned) => new(id, role, null, "?", owned, null);

    [Fact]
    public void Build_cards_role_titles_and_summary_are_English()
    {
        using var _ = Loc.Use(Lang.Eng);
        var st = new ArchetypeStatus
        {
            Archetype = new Archetype { Id = "orbs", Cards = new ArchetypeCards { Core = ["CARD.C1"], Payoff = ["CARD.P1"], Enabler = ["CARD.E1"], Support = ["CARD.S1", "CARD.S2"] } },
            OwnedCards = ["CARD.C1", "CARD.S2"],
        };

        var groups = BuildCardsPresenter.Groups(st, id => id, Tile);

        Assert.Equal(["Core 1/1", "Payoff 0/1", "Enabler 0/1", "Support 1/2"], groups.Select(g => g.Title));
        Assert.Equal("Core", groups[0].Cards[0].RoleText);
        Assert.Equal("Cards 2/5 owned", BuildCardsPresenter.Summary(groups));
        Assert.Equal("Payoff", BuildCardsPresenter.RoleName(ArchetypeRoles.Payoff));
        Assert.Equal("custom", BuildCardsPresenter.RoleName("custom"));
    }

    [Fact]
    public void Build_cards_of_every_pack_archetype_have_no_Korean_in_English()
    {
        using var _ = Loc.Use(Lang.Eng);
        var pack = Pack.Value;
        foreach (var character in pack.Archetypes.Select(a => a.Character).Distinct())
        {
            var state = new RunState { Character = character, Act = 2, Floor = 20 };
            foreach (var st in ArchetypeDetector.Detect(state, pack))
            {
                var groups = BuildCardsPresenter.Groups(st, pack.Canonical, Tile);
                AssertNoHangul(groups.Select(g => g.Title).Append(BuildCardsPresenter.Summary(groups)).Concat(groups.SelectMany(g => g.Cards.Select(c => c.RoleText))));
            }
        }
    }

    [Fact]
    public void Build_cards_stay_Korean_by_default()
    {
        using var _ = Loc.Use(Lang.Kor);
        Assert.Equal("핵심", BuildCardsPresenter.RoleName(ArchetypeRoles.Core));
    }

    // ---- OfferMarkers ------------------------------------------------------------------------------------------------

    private static ShopItemEvaluation Item(ShopItemKind kind, string? target = null) => new()
    {
        Kind = kind,
        Card = kind == ShopItemKind.Card ? new CardRef("CARD.ZAP") : null,
        Price = 75,
        Value = new ScoreBreakdown([new ScoreComponent("v", "Value", 10)]),
        RemovalTarget = target,
        Affordable = true,
    };

    private static ShopEvaluation Shop(params ShopItemEvaluation[] buy) => new() { Items = buy, RecommendedPurchase = buy, Skip = new ScoreBreakdown([]) };

    [Fact]
    public void Offer_marks_are_English()
    {
        using var _ = Loc.Use(Lang.Eng);
        Assert.Equal("Pick!", OfferMarkers.RecommendRibbon);
        Assert.Equal("Buy!", OfferMarkers.BuyRibbon);
        Assert.Equal("Remove: Strike", OfferMarkers.RemovalText(Shop(Item(ShopItemKind.CardRemoval, "CARD.STRIKE_DEFECT")), _ => "Strike"));
        Assert.Equal("Remove a card", OfferMarkers.RemovalText(Shop(Item(ShopItemKind.CardRemoval)), id => id));
        Assert.Equal(["Lacks AoE +24"], OfferMarkers.CardChips([new ReasonChip("need", "Lacks AoE", 24.4)]).Select(c => c.Text));
    }

    [Fact]
    public void Offer_ribbons_follow_the_language_each_time_they_are_read()
    {
        using (Loc.Use(Lang.Kor)) Assert.Equal("추천!", OfferMarkers.RecommendRibbon);
        using (Loc.Use(Lang.Eng)) Assert.Equal("Pick!", OfferMarkers.RecommendRibbon);
        using (Loc.Use(Lang.Kor)) Assert.Equal("구매!", OfferMarkers.BuyRibbon);
    }

    // ---- RestMarks ----------------------------------------------------------------------------------------------------

    private static readonly CardRef Bash = new("CARD.BASH"), Strike = new("CARD.STRIKE");

    private static string Name(CardRef c) => c.Id.Replace("CARD.", "");

    private static UpgradeCandidate Up(CardRef card, int rank) => new()
    {
        Card = card,
        Rank = rank,
        Score = new ScoreBreakdown([new ScoreComponent(UpgradeComponentKeys.Upgrade, "Upgrade", 10 - rank)]),
        Chips = [new ReasonChip("dmg", "Damage +3", 5)],
    };

    private static RestSiteAdvice Advice(RestAction action, RestConfidence confidence, params UpgradeCandidate[] ups) => new()
    {
        Action = action,
        Confidence = confidence,
        Reason = "reason",
        Upgrades = ups,
    };

    private static RestSiteResult Choice() => new(true,
    [
        new RestButton(RestOption.Heal, 0, new RectF(700, 410, 200, 200), new RectF(700, 410, 200, 200), RestButtonState.Normal, true, 10, 50),
        new RestButton(RestOption.Smith, 1, new RectF(1020, 410, 200, 200), new RectF(1020, 410, 200, 200), RestButtonState.Normal, true, 10, 50),
    ], false, "");

    [Fact]
    public void Rest_marks_are_English()
    {
        using var _ = Loc.Use(Lang.Eng);
        var heal = Assert.Single(RestMarks.Choice(Choice(), Advice(RestAction.Heal, RestConfidence.Sure), Name).Rects);
        Assert.Equal("Pick!", heal.Ribbon);

        var smith = Assert.Single(RestMarks.Choice(Choice(), Advice(RestAction.Smith, RestConfidence.Close, Up(Bash, 1)), Name).Rects);
        Assert.Equal("Pick! (close)", smith.Ribbon);
        Assert.Equal("Upgrade: BASH\nreason", smith.Note);

        var grid = RestMarks.Grid([(Strike, new RectF(250, 120, 200, 280)), (Bash, new RectF(500, 120, 200, 280))],
            Advice(RestAction.Smith, RestConfidence.Sure, Up(Strike, 1), Up(Bash, 2)), Name);
        Assert.Equal(["Upgrade!", "#2", "Upgrade picks"], grid.Rects.Select(r => r.Ribbon));
        Assert.Equal("1. STRIKE\n2. BASH", grid.Rects[2].Note);
        AssertNoHangul(grid.Rects.SelectMany(r => new[] { r.Ribbon, r.Note }));
    }

    // ---- ForecastReviewPresenter -------------------------------------------------------------------------------------

    private static HorizonOutcome Outcome(double p) => new() { PComplete = p, PPrimary = p, MeanCompletionPercent = 70 };

    private static ArchetypeForecast Forecast(Archetype a, bool complete, double p) => new()
    {
        Archetype = a,
        IsCompleteNow = complete,
        CurrentCommit = 0.5,
        CurrentCompletionPercent = 40,
        NextBoss = Outcome(p),
        EndOfRun = Outcome(p),
        EndOfRunAll = Outcome(p / 2),
        CalibratedIfSurvive = p,
        CalibratedAll = p / 2,
    };

    private static Archetype Named(string id, string kor, string eng) => new() { Id = id, Names = new() { ["kor"] = kor, ["eng"] = eng }, Color = "#FF8800" };

    [Fact]
    public void Forecast_section_is_English()
    {
        using var _ = Loc.Use(Lang.Eng);
        var result = new ForecastResult
        {
            Archetypes = [Forecast(Named("a", "중독", "Poison"), false, 0.42), Forecast(Named("b", "단도", "Shiv"), true, 1)],
            Simulations = 1500,
            UsedMap = true,
            PoolSize = 70,
            ExpectedUntilEnd = new RewardCounts(10, 2.5, 2, 1.5),
        };
        var vm = ForecastReviewPresenter.Forecast(result);

        Assert.Equal("Build completion odds (if you survive · incl. death)", vm.Title);
        Assert.Equal(["Poison", "Shiv"], vm.Rows.Select(r => r.Name));
        Assert.Equal("42%", vm.Rows[0].EndText);
        Assert.Equal("Complete", vm.Rows[1].EndText);
        Assert.Contains("1500 simulations", vm.Note);
        AssertNoHangul(Texts(vm));

        Assert.Equal("No card pool data: based on the current deck only.", ForecastReviewPresenter.Forecast(result with { PoolSize = 0 }).Note);
    }

    [Fact]
    public void Forecast_line_is_matched_to_the_build_by_archetype_id_not_display_name()
    {
        // The build list was built in Korean; the forecast arrives after a switch to English (different display names).
        BuildSectionVm build;
        using (Loc.Use(Lang.Kor))
        {
            build = new BuildSectionVm { Summary = "x", Archetypes = [new ArchetypeVm { Id = "a", Name = "중독" }] };
        }
        using var _ = Loc.Use(Lang.Eng);
        var result = new ForecastResult { Archetypes = [Forecast(Named("a", "중독", "Poison"), false, 0.3)] };

        var withLine = ForecastReviewPresenter.WithForecast(build, result);

        Assert.Equal("Complete by Act 3 end: 30% if you survive · 15% incl. death [est.]", withLine.Archetypes[0].ProbabilityText);
    }

    [Fact]
    public void Review_of_every_fixture_run_has_no_Korean_in_English()
    {
        var runs = Directory.GetFiles(Path.Combine(RepoRoot, "tests", "fixtures", "runs"), "*.run").OrderBy(p => p, StringComparer.Ordinal).Take(6).ToArray();
        Assert.NotEmpty(runs);
        foreach (var file in runs)
        {
            var run = RunHistoryReader.ReadFile(file);
            var review = RunReviewBuilder.FromReplay(new ReplayAnalyzer(Pack.Value).Replay(run), Pack.Value);
            using var _ = Loc.Use(Lang.Eng);
            var vm = ForecastReviewPresenter.Review(review, IdNames, showBanner: true);
            Assert.StartsWith("Run review", vm.BannerText);
            AssertNoHangul(Texts(vm));
        }
    }

    [Fact]
    public void Character_names_use_the_game_titles()
    {
        using (Loc.Use(Lang.Eng)) Assert.Equal("The Ironclad", ForecastReviewPresenter.CharacterName("CHARACTER.IRONCLAD"));
        using (Loc.Use(Lang.Kor)) Assert.Equal("아이언클래드", ForecastReviewPresenter.CharacterName("CHARACTER.IRONCLAD"));
    }

    // ---- path section -------------------------------------------------------------------------------------------------

    private static PathOption PathOpt(string[] chips, params string[] risks) => new()
    {
        Coords = [new MapCoord(4, 1), new MapCoord(4, 2)],
        Types = [MapNodeType.Elite, MapNodeType.Boss],
        Counts = new Dictionary<MapNodeType, int> { [MapNodeType.Elite] = 2, [MapNodeType.RestSite] = 1 },
        Score = new ScoreBreakdown([new ScoreComponent("p", "p", 60)]),
        ExpectedBossHp = 0.62,
        Chips = chips,
        Risks = risks,
    };

    private static readonly MapGraph SmallMap = new()
    {
        Nodes = new Dictionary<MapCoord, MapNode>
        {
            [new(3, 0)] = new(new(3, 0), MapNodeType.Ancient, [new(4, 1)]),
            [new(4, 1)] = new(new(4, 1), MapNodeType.Elite, [new(4, 2)]),
            [new(4, 2)] = new(new(4, 2), MapNodeType.Boss, []),
        },
        Start = new(3, 0),
        Boss = new(4, 2),
        Current = new(3, 0),
    };

    [Fact]
    public void Path_chips_never_show_raw_risk_keys()
    {
        // PathAdvisor puts one worded chip per known risk at the front of Chips.
        var o = PathOpt(["Risk: Elites in a row", "Elite 2", "Rest Site 1", "Shop 1", "? 2", "HP at Boss 62%"], PathRisks.EliteChain);

        var chips = AppController.PathChips(o);

        Assert.Equal(["Risk: Elites in a row", "Elite 2", "Rest Site 1", "Shop 1", "? 2"], chips);
        Assert.DoesNotContain(PathRisks.EliteChain, chips);
        Assert.Equal(["a", "b"], AppController.PathChips(PathOpt(["a", "b"])));
    }

    [Fact]
    public void Path_view_is_English()
    {
        using var _ = Loc.Use(Lang.Eng);
        var rec = new PathRecommendation
        {
            Origin = new(3, 0),
            Top = [PathOpt(["Elite 2"]), PathOpt(["Elite 2"])],
            TotalPaths = 12,
            DeckStrength = 0.5,
        };

        var view = AppController.ToPathView(rec, SmallMap)!;

        Assert.Equal(["Best", "Alt 1"], view.Options.Select(o => o.Label));
        Assert.Equal("Next: Elite (right)", view.NextStep);
        Assert.StartsWith("Elite 2 · Rest 1 · Shop 0 · ? 0 · HP at Boss", view.Options[0].Summary);
        Assert.Equal("Deck strength 0.50 · compared some of 12 paths [est. weights]", view.Note);
        AssertNoHangul(view.Options.SelectMany(o => o.ReasonChips.Append(o.Label).Append(o.Summary)).Append(view.NextStep).Append(view.Note));
        Assert.Equal("At the Boss, or no path left to take", AppController.ToPathView(new PathRecommendation { Origin = new(3, 0) }, SmallMap)!.Note);
    }

    // ---- integration texts --------------------------------------------------------------------------------------------

    [Fact]
    public void Deck_doubts_are_worded_when_shown_so_they_follow_the_language()
    {
        var doubts = new DeckDoubts(
            new Dictionary<string, IReadOnlyList<UncertainReason>> { ["CARD.BASH"] = [UncertainReason.Inferred, UncertainReason.UpgradeRoll] }, 0, 0, 0);

        using (Loc.Use(Lang.Kor)) Assert.Equal("획득 로그 없이 전투에서 확인됨: 강화 여부 모름 / 보상 카드 무료 강화 여부를 확인하지 못함", doubts.ReasonText("CARD.BASH"));
        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("Seen in combat with no pickup log: Upgrade unknown / Couldn't confirm a free Upgrade on the reward card", doubts.ReasonText("CARD.BASH"));
            AssertNoHangul(Enum.GetValues<UncertainReason>().Select(DeckDoubts.Reason));
        }
        Assert.Null(doubts.ReasonText("CARD.ZAP"));
    }

    [Fact]
    public void Demo_path_is_English()
    {
        using var _ = Loc.Use(Lang.Eng);
        var path = DemoScenario.Path;
        Assert.Equal(["Balanced", "Safe", "Aggressive"], path.Options.Select(o => o.Label));
        AssertNoHangul(path.Options.SelectMany(o => o.ReasonChips.Append(o.Label).Append(o.Summary)).Append(path.NextStep).Append(path.Note));
    }
}
