using System.Text.RegularExpressions;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Rest;
using Sts2Overlay.Core.Tests.Map;
using Sts2Overlay.Core.Tests.Scoring;
using static Sts2Overlay.Core.Map.MapNodeType;

namespace Sts2Overlay.Core.Tests.Rest;

/// <summary>Rest-site advice in English (<see cref="Loc"/>): the reason line and the score labels carry no Korean.</summary>
public class RestSiteEnglishTests
{
    private static readonly Regex Hangul = new("[가-힣]", RegexOptions.Compiled);
    private static readonly RestSiteAdvisor Advisor = new(TestMaps.Pack);

    private static IEnumerable<string> Texts(RestSiteAdvice a)
        => new[] { a.Reason }
            .Concat(a.HealScore?.Components.Select(c => c.Label) ?? [])
            .Concat(a.SmithScore?.Components.Select(c => c.Label) ?? []);

    [Fact]
    public void Rest_advice_over_paths_maps_and_fallbacks_has_no_Korean_in_English()
    {
        IReadOnlyList<MapNodeType>[] paths =
        [
            [Boss], [Monster, Boss], [Elite, Boss], [Elite, Elite, RestSite, Boss], [Unknown, MapNodeType.Shop, Treasure, Monster, Boss],
        ];
        var fixture = MapFixture.Load("map_act2.json");
        var texts = new List<string>();
        using (Loc.Use(Lang.Eng))
        {
            foreach (var hp in new[] { 1.0, 0.9, 0.6, 0.4, 0.15 })
            {
                var state = TestMaps.State(hpRatio: hp);
                foreach (var path in paths) texts.AddRange(Texts(Advisor.Advise(state, path)));
                texts.AddRange(Texts(Advisor.Advise(state, (MapGraph?)null)));
                texts.AddRange(Texts(Advisor.Advise(state with { Act = fixture.Act }, fixture)));
            }
            var nothingToUpgrade = TestMaps.State(0.5) with { Deck = [new DeckCard(new CardRef(TestPacks.Strike, 1))] };
            texts.AddRange(Texts(Advisor.Advise(nothingToUpgrade, [Monster, Boss])));
        }

        var korean = texts.Where(t => Hangul.IsMatch(t)).Distinct().ToList();
        Assert.True(korean.Count == 0, "Korean in English rest texts: " + string.Join(" | ", korean.Take(10)));
        Assert.Contains("HP is full → Smith", texts);
        Assert.Contains("No card to upgrade → Rest", texts);
        Assert.Contains(texts, t => t.StartsWith("No map: ", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("Boss is next", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("rooms to Boss", StringComparison.Ordinal));
    }

    [Fact]
    public void Same_advice_in_both_languages()
    {
        var kor = Advisor.Advise(TestMaps.State(0.2), [Boss]);
        RestSiteAdvice eng;
        using (Loc.Use(Lang.Eng)) eng = Advisor.Advise(TestMaps.State(0.2), [Boss]);

        Assert.Equal(kor.Action, eng.Action);
        Assert.Equal(kor.HealScore!.Total, eng.HealScore!.Total);
        Assert.StartsWith("HP 20%, 보스 직전 → 휴식이 더 안전", kor.Reason, StringComparison.Ordinal);
        Assert.StartsWith("HP 20%, Boss is next → Rest is safer", eng.Reason, StringComparison.Ordinal);
    }
}
