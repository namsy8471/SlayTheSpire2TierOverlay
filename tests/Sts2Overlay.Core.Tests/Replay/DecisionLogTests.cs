using System.Text.RegularExpressions;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Replay;

/// <summary>The play-time decision log: key, (de)serialization, privacy, matching to a finished run, and the review built on it.</summary>
public class DecisionLogTests
{
    private static readonly Lazy<DataPack> Pack = new(() =>
        DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1")));

    private static DecisionHeader Header(int floor = 3) =>
        new("NTNV7N8F97", "CHARACTER.IRONCLAD", 5, 1, floor, "learned", "0.1.7", "v0.107.1", new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Run_key_is_seed_character_ascension_and_safe_as_a_file_name()
    {
        Assert.Equal("NTNV7N8F97_IRONCLAD_a5", DecisionLog.RunKey("NTNV7N8F97", "CHARACTER.IRONCLAD", 5));
        Assert.Equal("AB-CD_X_a0", DecisionLog.RunKey(" AB\\CD ", null, 0));
        Assert.Null(DecisionLog.RunKey(null, "CHARACTER.IRONCLAD", 5));
        Assert.Null(DecisionLog.RunKey("  ", "CHARACTER.IRONCLAD", 5));
    }

    [Fact]
    public void Card_text_round_trips_upgrades()
    {
        Assert.Equal("CARD.BASH+1", DecisionLog.CardText(new CardRef("CARD.BASH", 1)));
        Assert.Equal(new CardRef("CARD.BASH", 1), DecisionLog.ParseCard("CARD.BASH+1"));
        Assert.Equal(new CardRef("CARD.BASH"), DecisionLog.ParseCard("CARD.BASH"));
    }

    [Fact]
    public void Offer_record_holds_the_verdict_scores_and_why_there_is_none()
    {
        var state = new RunState { Character = "CHARACTER.IRONCLAD", Floor = 3, Hp = 70, MaxHp = 80 };
        CardRef[] offered = [new("CARD.BASH"), new("CARD.ANGER", 1), new("CARD.CLASH")];
        var eval = new RecommendationEngine(Pack.Value).EvaluateOffer(state, offered, OfferContext.EliteReward);

        var r = DecisionLog.ForOffer(Header(), "reward", OfferContext.EliteReward, offered, eval, partial: false);
        Assert.Equal("NTNV7N8F97_IRONCLAD_a5", r.Run);
        Assert.Equal(["CARD.BASH", "CARD.ANGER+1", "CARD.CLASH"], r.Offered);
        Assert.Equal("EliteReward", r.Context);
        Assert.Equal(3, r.Scores!.Count);
        Assert.Equal(eval.SkipRecommended ? DecisionRecord.SkipPick : eval.Best!.Value.ToString(), r.Pick);
        Assert.Null(r.Why);

        var partial = DecisionLog.ForOffer(Header(), "reward", OfferContext.CardReward, offered[..2], eval, partial: true);
        Assert.Null(partial.Pick);
        Assert.Equal("partial", partial.Why);
        Assert.False(partial.HasVerdict);
    }

    [Fact]
    public void Serialize_and_parse_round_trip_and_skip_a_broken_line()
    {
        var a = Rec(3, ["CARD.A", "CARD.B+1", "CARD.C"], "CARD.B+1");
        var b = Rec(5, ["CARD.D", "CARD.E", "CARD.F"], DecisionRecord.SkipPick) with
        {
            Kind = DecisionRecord.ShopKind,
            Buy = [new ShopBuyRecord("card", "CARD.D", 75), new ShopBuyRecord("removal", null, 100, Estimated: true)],
            Gold = 210,
        };
        var lines = new[] { DecisionLog.Serialize(a), "", DecisionLog.Serialize(b), DecisionLog.Serialize(a)[..25] };
        Assert.DoesNotContain("\n", lines[0]);
        Assert.DoesNotContain("\"why\"", lines[0]); // nulls are left out

        var back = DecisionLog.Parse(lines);
        Assert.Equal(2, back.Count);
        Assert.Equal(a.Offered, back[0].Offered);
        Assert.Equal(a.Scores, back[0].Scores);
        Assert.Equal(a.Utc, back[0].Utc);
        Assert.Equal(new CardRef("CARD.B", 1), back[0].PickCard);
        Assert.Equal(2, back[1].Buy!.Count);
        Assert.True(back[1].Buy![1].Estimated);
        Assert.Equal(210, back[1].Gold);
    }

    [Fact]
    public void Records_carry_no_personal_data()
    {
        // A co-op fixture: its players are Steam ids. None of it may reach the log.
        var file = TestPaths.AllRuns.First(p => RunHistoryReader.ReadFile(p).IsMultiplayer);
        var run = RunHistoryReader.ReadFile(file);
        var player = LocalPlayer.Resolve(run);
        var decision = DecisionExtractor.Extract(run, player.Id).First(d => d.Context != OfferContext.Shop && d.Offered.Count > 0);
        var engine = new RecommendationEngine(Pack.Value);
        var header = new DecisionHeader(run.Seed, player.Character, run.Ascension, decision.Node.Act, decision.Node.Floor, "learned", "0.1.7", "v0.107.1", DateTime.UtcNow);
        var offer = DecisionLog.ForOffer(header, "reward", decision.Context, decision.Offered, engine.EvaluateOffer(decision.StateBefore, decision.Offered, decision.Context), false);
        var shop = DecisionLog.ForShop(header, decision.Offered,
            engine.EvaluateShop(decision.StateBefore, new ShopOffer(decision.Offered.Select(c => new ShopCardOffer(c, 80)).ToArray())));

        foreach (var json in new[] { DecisionLog.Serialize(offer), DecisionLog.Serialize(shop) })
        {
            Assert.DoesNotMatch(new Regex(@"7656\d{13}"), json);
            Assert.DoesNotMatch(new Regex(@"[\\/]Users", RegexOptions.IgnoreCase), json);
            Assert.DoesNotContain(player.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), json);
        }
    }

    // ---- matching and the review ----------------------------------------------------------------------------------

    private static DecisionRecord Rec(int floor, string[] offered, string? pick, string run = "K") => new()
    {
        Run = run,
        Floor = floor,
        Act = 1,
        Screen = "reward",
        Offered = offered,
        Pick = pick,
        Scores = offered.Select((_, i) => 10.0 + i).ToArray(),
        Skip = 5,
        Model = "learned",
        Utc = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc),
    };

    private sealed record Fixture(RunHistory Run, RunPlayer Player, RunReplay Replay, IReadOnlyList<int> Eligible);

    /// <summary>The first fixture run with at least eight single-pick reward/event decisions.</summary>
    private static readonly Lazy<Fixture> Sample = new(() =>
    {
        foreach (var file in TestPaths.AllRuns)
        {
            var run = RunHistoryReader.ReadFile(file);
            var player = LocalPlayer.Resolve(run);
            var replay = new ReplayAnalyzer(Pack.Value).Replay(run, player.Id);
            var eligible = Enumerable.Range(0, replay.Decisions.Count)
                .Where(i => replay.Decisions[i] is { Decision: { Context: not OfferContext.Shop, Picked.Count: <= 1, Offered.Count: > 0 }, Offer.NoData: false })
                .ToArray();
            if (eligible.Length >= 8) return new Fixture(run, player, replay, eligible);
        }
        throw new InvalidOperationException("no fixture run with enough decisions");
    });

    /// <summary>A record of decision <paramref name="d"/> whose advice agrees (or not) with what the player did.</summary>
    private static DecisionRecord RecordOf(Fixture f, CardDecision d, bool agree)
    {
        var user = d.Picked.Count > 0 ? d.Picked[0] : (CardRef?)null;
        string pick = agree
            ? user?.ToString() ?? DecisionRecord.SkipPick
            : user is null ? d.Offered[0].ToString() : DecisionRecord.SkipPick;
        return Rec(d.Node.Floor, d.Offered.Select(c => c.ToString()).ToArray(), pick, DecisionLog.RunKey(f.Run, f.Player)!) with
        {
            Act = d.Node.Act,
            Utc = DateTimeOffset.FromUnixTimeSeconds(f.Run.StartTime + 60).UtcDateTime,
        };
    }

    [Fact]
    public void Review_judges_logged_decisions_by_the_stored_advice_and_recomputes_the_rest()
    {
        var f = Sample.Value;
        var decisions = f.Replay.Decisions;
        var unlogged = f.Eligible[0];
        var log = new List<DecisionRecord>();
        var expectedAgree = 0;
        for (var k = 1; k < f.Eligible.Count; k++)
        {
            var agree = k % 2 == 0;
            log.Add(RecordOf(f, decisions[f.Eligible[k]].Decision, agree));
            if (agree) expectedAgree++;
        }
        var recomputed = decisions[unlogged];
        if (recomputed.IsAgreement) expectedAgree++;

        var review = RunReviewBuilder.Build(f.Run, Pack.Value, decisionLog: log);

        Assert.True(review.HasDecisionLog);
        Assert.Equal(f.Eligible.Count - 1, review.StoredJudged);
        Assert.Equal(recomputed.IsComparable ? 1 : 0, review.Recomputed);
        Assert.Equal(review.StoredJudged + review.Recomputed, review.Decisions);
        Assert.Equal(expectedAgree, review.Agreements);

        // Rows from the log show the stored advice and play-time scores; a recomputed row is tagged.
        foreach (var row in review.BiggestDisagreements.Where(r => !r.Recomputed))
        {
            var stored = log.First(r => r.Floor == row.Floor && r.Offered.SequenceEqual(row.Offered.Select(c => c.ToString())));
            Assert.Equal(stored.PickCard, row.EnginePick);
            var best = Math.Max(stored.Skip!.Value, stored.Scores!.Max());
            Assert.Equal(best, row.EngineScore);
        }
        // Only the unlogged decision can be recomputed (the other unlogged ones are not comparable at all).
        Assert.All(review.BiggestDisagreements.Where(r => r.Recomputed), r => Assert.Equal(recomputed.Decision.Offered, r.Offered));
    }

    [Fact]
    public void A_stored_disagreement_uses_the_play_time_scores_for_its_gap()
    {
        var f = Sample.Value;
        var replayed = f.Replay.Decisions[f.Eligible[0]];
        var d = replayed.Decision;
        var record = RecordOf(f, d, agree: false);
        var review = RunReviewBuilder.FromReplay(f.Replay with { Decisions = [replayed] }, Pack.Value, [record]);
        var row = Assert.Single(review.BiggestDisagreements);
        Assert.False(row.Recomputed);
        Assert.Equal(0, review.Agreements);

        // Scores are 10, 11, 12 … by slot and skip 5: the best is the last slot.
        var best = 10.0 + d.Offered.Count - 1;
        var userScore = d.Picked.Count == 0 ? 5 : 10.0 + d.Offered.ToList().FindIndex(c => c.Id == d.Picked[0].Id);
        Assert.Equal(best - userScore, row.Gap, 6);
        Assert.Equal(1, review.StoredJudged);
    }

    [Fact]
    public void A_partial_or_mismatched_record_falls_back_to_recomputation()
    {
        var f = Sample.Value;
        var d = f.Replay.Decisions[f.Eligible[0]].Decision;
        var full = RecordOf(f, d, agree: true);
        var twoOfThree = full with { Offered = full.Offered.Skip(1).ToArray() };
        var partial = full with { Pick = null, Why = "partial" };
        var otherRun = full with { Run = "OTHER_IRONCLAD_a0" };
        var tooEarly = full with { Utc = DateTimeOffset.FromUnixTimeSeconds(f.Run.StartTime).UtcDateTime - TimeSpan.FromHours(1) };

        foreach (var r in new[] { twoOfThree, partial, otherRun, tooEarly })
        {
            var review = RunReviewBuilder.Build(f.Run, Pack.Value, decisionLog: [r]);
            Assert.Equal(0, review.StoredJudged);
        }
        // A neighbouring floor still matches (the tracker's floor one step off).
        var shifted = RunReviewBuilder.Build(f.Run, Pack.Value, decisionLog: [full with { Floor = full.Floor + 1 }]);
        Assert.Equal(1, shifted.StoredJudged);
        var exact = RunReviewBuilder.Build(f.Run, Pack.Value, decisionLog: [full]);
        Assert.Equal(1, exact.StoredJudged);
    }

    [Fact]
    public void No_log_means_everything_is_recomputed_as_before()
    {
        var f = Sample.Value;
        var review = RunReviewBuilder.Build(f.Run, Pack.Value);
        Assert.False(review.HasDecisionLog);
        Assert.Equal(0, review.StoredJudged);
        Assert.Equal(f.Replay.ComparableCount, review.Decisions);
        Assert.Equal(review.Decisions, review.Recomputed);
        Assert.All(review.BiggestDisagreements, r => Assert.True(r.Recomputed));
    }

    [Fact]
    public void Matcher_uses_each_record_once_and_prefers_the_exact_floor()
    {
        var f = Sample.Value;
        var d = f.Replay.Decisions[f.Eligible[0]].Decision;
        var decisions = new[] { d, d with { Node = d.Node with { Floor = d.Node.Floor + 1 } } };
        var record = RecordOf(f, d, agree: true);
        var matched = DecisionLogMatcher.Match(decisions, [record with { Floor = d.Node.Floor + 1 }]);
        Assert.Null(matched[0]); // the exact-floor decision (index 1) takes it first
        Assert.NotNull(matched[1]);

        var twice = DecisionLogMatcher.Match(decisions, [record, record with { Floor = d.Node.Floor + 1 }]);
        Assert.NotNull(twice[0]);
        Assert.NotNull(twice[1]);
        Assert.NotSame(twice[0], twice[1]);
    }
}
