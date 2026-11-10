using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Events;

/// <summary>
/// An option nothing is known about scores 0 because of missing data, not because it is neutral: it can never be the
/// "추천!" pick, a tie at the top gives no pick, and the texts never show events.json's raw types or English developer notes
/// in Korean.
/// </summary>
public class EventNoDataTests
{
    private const string Json = """
    {
      "events": [
        {
          "id": "EVENT.T_MYSTERY",
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "T_MYSTERY.pages.INITIAL.options.ODD", "effects": [ { "type": "other", "note": "+1 max energy" } ] },
              { "key": "T_MYSTERY.pages.INITIAL.options.HURT", "effects": [ { "type": "hpLoss", "amount": 10 } ] }
            ]}
          ]
        },
        {
          "id": "EVENT.T_GOLD_OR_ODD",
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "T_GOLD_OR_ODD.pages.INITIAL.options.ODD", "effects": [ { "type": "other", "note": "+1 max energy" } ] },
              { "key": "T_GOLD_OR_ODD.pages.INITIAL.options.GOLD", "effects": [ { "type": "gold", "amount": 50 } ] }
            ]}
          ]
        },
        {
          "id": "EVENT.T_TWO_DOORS",
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "T_TWO_DOORS.pages.INITIAL.options.LEFT", "effects": [], "leave": true },
              { "key": "T_TWO_DOORS.pages.INITIAL.options.RIGHT", "effects": [], "leave": true }
            ]}
          ]
        },
        {
          "id": "EVENT.T_TEXTS",
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "T_TEXTS.pages.INITIAL.options.ROLL", "effects": [ { "type": "gold", "amount": "rng.NextInt(1,50)", "note": "amount from code: rng" } ] },
              { "key": "T_TEXTS.pages.INITIAL.options.SMITH", "effects": [ { "type": "cardUpgrade", "count": 1, "how": "choose", "filter": "Attacks" } ] },
              { "key": "T_TEXTS.pages.INITIAL.options.ODD", "effects": [ { "type": "cardRemove", "count": 1, "how": "choose", "filter": "cards Souls Power can enchant" } ] }
            ]}
          ]
        }
      ]
    }
    """;

    private static readonly EventScorer Scorer = new(new DataPack(), EventCatalog.Parse(Json));

    private static RunState State() => new()
    {
        Character = "CHARACTER.IRONCLAD", Hp = 80, MaxHp = 80, Gold = 99,
        Deck = Enumerable.Repeat(new DeckCard(new CardRef("CARD.STRIKE_IRONCLAD")), 5).ToArray(),
    };

    [Fact]
    public void Unknown_option_on_top_is_not_the_pick()
    {
        var r = Scorer.Evaluate(State(), "EVENT.T_MYSTERY")!;
        var odd = r.Options[0];
        Assert.True(odd.HasNoData);
        Assert.Equal(1, odd.Rank); // 0 beats −HP: still ranked, just not recommended
        Assert.Null(r.Best);
        Assert.All(r.Options, o => Assert.False(o.IsBest));
        Assert.Equal(RecommendationConfidence.Estimate, r.Confidence);
    }

    [Fact]
    public void Known_option_with_a_real_lead_is_the_pick_but_flagged_as_estimate()
    {
        var r = Scorer.Evaluate(State(), "EVENT.T_GOLD_OR_ODD")!;
        Assert.Equal("GOLD", r.Best!.Option.ShortName);
        Assert.False(r.Best.HasNoData);
        Assert.Equal(RecommendationConfidence.Estimate, r.Confidence); // the unknown option could still be better
    }

    [Fact]
    public void Tie_at_the_top_gives_no_pick()
    {
        var r = Scorer.Evaluate(State(), "EVENT.T_TWO_DOORS")!;
        Assert.All(r.Options, o => Assert.False(o.HasNoData));
        Assert.Null(r.Best);
        Assert.Equal(RecommendationConfidence.Estimate, r.Confidence);
    }

    [Fact]
    public void Logged_options_of_an_event_missing_from_the_data_give_no_pick()
    {
        var r = Scorer.Evaluate(State(), "EVENT.T_NEW", ["T_NEW.pages.INITIAL.options.A", "T_NEW.pages.INITIAL.options.B"])!;
        Assert.All(r.Options, o => Assert.True(o.HasNoData));
        Assert.Null(r.Best);
        Assert.Null(Scorer.Evaluate(State(), "EVENT.T_NEW")); // no keys, no data: the app says so instead
    }

    [Fact]
    public void Korean_texts_show_no_raw_types_or_english_notes()
    {
        using var _ = Loc.Use(Lang.Kor);
        var r = Scorer.Evaluate(State(), "EVENT.T_TEXTS")!;
        Assert.Equal("골드 변화 (수치 미확인)", r.Options[0].EffectTexts[0]);
        Assert.Equal("카드 1장 강화 (공격 카드)", r.Options[1].EffectTexts[0]);
        Assert.Equal("카드 1장 제거 (대상 제한)", r.Options[2].EffectTexts[0]);

        var odd = Scorer.Evaluate(State(), "EVENT.T_MYSTERY")!.Options[0];
        Assert.Equal("특수 효과 (점수 미반영)", odd.EffectTexts[0]);
    }

    [Fact]
    public void English_texts_keep_the_english_notes_but_not_the_raw_types()
    {
        using var _ = Loc.Use(Lang.Eng);
        var r = Scorer.Evaluate(State(), "EVENT.T_TEXTS")!;
        Assert.Equal("Gold change (amount unknown)", r.Options[0].EffectTexts[0]);
        Assert.Equal("Upgrade 1 card (Attacks)", r.Options[1].EffectTexts[0]);
        Assert.Equal("Remove 1 card (cards Souls Power can enchant)", r.Options[2].EffectTexts[0]);
        Assert.Equal("+1 max energy", Scorer.Evaluate(State(), "EVENT.T_MYSTERY")!.Options[0].EffectTexts[0]);
        Assert.Equal("Special effect (not scored)", EventScorer.UnmodelledText(new EventEffect { Type = EventEffectTypes.Other }));
        Assert.Equal("Unknown effect (amount unknown)", EventScorer.UnmodelledText(new EventEffect { Type = "newType" }));
    }

    [Theory]
    [InlineData("ORB_LIGHTNING", "전기 구체", "Lightning Orbs")]
    [InlineData("FOCUS", "밀집", "Focus")]
    [InlineData("NEW_AXIS", "new axis", "new axis")] // not in the table: humanised, never the raw key
    public void Build_need_tags_have_display_names(string tag, string kor, string eng)
    {
        using (Loc.Use(Lang.Kor)) Assert.Equal(kor, ScoringLabels.Tag(tag));
        using (Loc.Use(Lang.Eng)) Assert.Equal(eng, ScoringLabels.Tag(tag));
    }

    [Fact]
    public void Every_tag_in_the_pack_has_a_display_name()
    {
        var pack = DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1"));
        var tags = pack.Archetypes.SelectMany(a => a.Needs).Select(n => n.Tag).OfType<string>()
            .Concat(pack.Tiers.Values.SelectMany(t => t.Cards.Values).SelectMany(e => e.Provides.Concat(e.Consumes).Concat(e.Requires)))
            .Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(tags);
        using var _ = Loc.Use(Lang.Eng);
        Assert.All(tags, t => Assert.NotEqual(t.Replace('_', ' ').ToLowerInvariant(), ScoringLabels.Tag(t)));
    }
}
