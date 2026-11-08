using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay.Validation;

namespace Sts2Overlay.Core.Tests.Replay;

/// <summary>Holdout validation: tie-fair metrics, game-grouped folds, and a calibrator that never sees the test fold.</summary>
public class HoldoutValidationTests
{
    private const string Defect = "CHARACTER.DEFECT";

    // ---------- Prediction / metrics ----------

    [Fact]
    public void Standing_CountsTiesAsCoinFlips_NotOfferOrder()
    {
        var p = new Prediction([50, 50, 40], Recommended: 0);

        Assert.Equal((0, 2), p.Standing(1));
        Assert.Equal(1.5, p.ExpectedRank(0));
        Assert.Equal(1.5, p.ExpectedRank(1));
        Assert.Equal(3.0, p.ExpectedRank(2));
        Assert.Equal(0.5, p.TopK(0, 1));
        Assert.Equal(1.0, p.TopK(1, 2));
        Assert.Equal(0.0, p.TopK(2, 2));
    }

    [Fact]
    public void Metrics_TiedPick_GetsHalfCredit_EvenWhenListedFirst()
    {
        // .run files list the picked card first; offer-order tie breaking would always credit it.
        var d = Decision(Stream("g1"), ["CARD.A", "CARD.B", "CARD.C"], picked: 0);
        var m = AgreementMetrics.Of(d, new Prediction([50, 50, 40], Recommended: 0));

        Assert.Equal(0.5, m.Top1Hits);
        Assert.Equal(1.0, m.Top2Hits);
        Assert.Equal(1.5, m.RankSum);
        Assert.Equal(0.5, m.ExactHits);
        Assert.Equal(1.0, m.SkipHits);
    }

    [Fact]
    public void Metrics_Skip_ComparesOnlyTheSkipDecision()
    {
        var d = Decision(Stream("g1"), ["CARD.A", "CARD.B", "CARD.C"], picked: null);

        var agree = AgreementMetrics.Of(d, new Prediction([1, 2, 3], Recommended: null));
        var disagree = AgreementMetrics.Of(d, new Prediction([1, 2, 3], Recommended: 2));

        Assert.Equal((1, 0, 1, 1.0, 1.0), (agree.Decisions, agree.Picks, agree.Skips, agree.SkipHits, agree.ExactHits));
        Assert.Equal((0.0, 0.0), (disagree.SkipHits, disagree.ExactHits));
    }

    [Fact]
    public void UniformRandom_IsTheAnalyticExpectation()
    {
        var pick = AgreementMetrics.UniformRandom(Decision(Stream("g1"), ["CARD.A", "CARD.B", "CARD.C"], picked: 1), skipProbability: 0.2);
        var skip = AgreementMetrics.UniformRandom(Decision(Stream("g1"), ["CARD.A", "CARD.B", "CARD.C"], picked: null), skipProbability: 0.2);

        Assert.Equal(1 / 3.0, pick.Top1, 9);
        Assert.Equal(2 / 3.0, pick.Top2, 9);
        Assert.Equal(2.0, pick.MeanRank, 9);
        Assert.Equal(0.8, pick.SkipAgreement, 9);
        Assert.Equal(0.2, skip.SkipAgreement, 9);

        var sum = pick.Add(skip);
        Assert.Equal((2, 1, 1), (sum.Decisions, sum.Picks, sum.Skips));
        Assert.Equal(0.5, sum.SkipAgreement, 9);
    }

    [Fact]
    public void Metrics_RejectPredictionOfWrongSize()
    {
        var d = Decision(Stream("g1"), ["CARD.A", "CARD.B"], picked: 0);
        Assert.Throws<ArgumentException>(() => AgreementMetrics.Of(d, new Prediction([1, 2, 3], null)));
    }

    // ---------- Folds ----------

    [Fact]
    public void Folds_KeepBothPlayersOfACoopGameTogether()
    {
        var streams = new[]
        {
            Stream("old", start: 100), Stream("mid", start: 200, person: 1), Stream("mid", start: 200, person: 2), Stream("new", start: 300),
        };

        var chrono = Folds.Chronological(streams, testGames: 1);
        Assert.Equal(["new"], chrono.Test.Select(s => s.GameKey));
        Assert.Equal(3, chrono.Train.Count);

        var logo = Folds.LeaveOneGameOut(streams);
        Assert.Equal(3, logo.Count);
        Assert.Equal(2, logo.Single(f => f.Test[0].GameKey == "mid").Test.Count);

        foreach (var f in logo.Concat(Folds.KFold(streams, 2)).Append(chrono))
        {
            Assert.Empty(f.Train.Select(s => s.GameKey).Intersect(f.Test.Select(s => s.GameKey)));
        }
    }

    [Fact]
    public void KFold_TestsEveryGameExactlyOnce()
    {
        var streams = Enumerable.Range(0, 7).Select(i => Stream($"g{i}", start: i)).ToArray();
        var folds = Folds.KFold(streams, 3);

        Assert.Equal(3, folds.Count);
        Assert.Equal(streams.Select(s => s.GameKey).Order(), folds.SelectMany(f => f.Test).Select(s => s.GameKey).Order());
        Assert.All(folds, f => Assert.Equal(7, f.Train.Count + f.Test.Count));
    }

    [Fact]
    public void Folds_RejectImpossibleSizes()
    {
        var streams = new[] { Stream("a"), Stream("b") };
        Assert.Throws<ArgumentOutOfRangeException>(() => Folds.Chronological(streams, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Folds.KFold(streams, 3));
    }

    // ---------- Streams ----------

    [Fact]
    public void SteamIdFromPath_ReadsTheProfileFolder()
    {
        Assert.Equal(76561190000000009, ReplayStreams.SteamIdFromPath(@"C:\x\SlayTheSpire2\steam\76561190000000009\profile1\saves\history\1.run"));
        Assert.Null(ReplayStreams.SteamIdFromPath(@"C:\fixtures\runs\1.run"));
    }

    [Fact]
    public void Build_OnFixtures_AddsPartnersAndDropsDuplicateFiles()
    {
        var files = TestPaths.AllRuns.Select(p => (p, RunHistoryReader.ReadFile(p))).ToArray();

        var mine = ReplayStreams.Build(files, allPlayers: false, LocalPlayer.FixtureSelfId);
        var everyone = ReplayStreams.Build(files, allPlayers: true, LocalPlayer.FixtureSelfId);
        var twice = ReplayStreams.Build(files.Concat(files), allPlayers: true, LocalPlayer.FixtureSelfId);

        Assert.Equal(files.Length, mine.Count);
        Assert.All(mine.Where(s => s.Run.IsMultiplayer), s => Assert.Equal(LocalPlayer.FixtureSelfId, s.PersonId));
        Assert.Equal(files.Length + files.Count(f => f.Item2.IsMultiplayer), everyone.Count);
        Assert.Equal(everyone.Count, twice.Count);
        Assert.Equal(files.Length, Folds.GamesByTime(everyone).Count);
    }

    // ---------- Calibrator ----------

    [Fact]
    public void Calibrator_RaisesACardPickedOverHigherRankedCards_InSeveralGames()
    {
        var pack = Pack();
        var decisions = new[]
        {
            // UNDER is picked while the engine ranks HIGH first: three times, in two games.
            Decision(Stream("g1"), ["CARD.UNDER", "CARD.HIGH"], picked: 0),
            Decision(Stream("g1"), ["CARD.UNDER", "CARD.HIGH"], picked: 0),
            Decision(Stream("g2"), ["CARD.UNDER", "CARD.HIGH"], picked: 0),
        };
        var calibrator = new TierShiftCalibrator(pack, new TierShiftOptions { MinNet = 3, MinGames = 2 }, Fixed(([10, 20], 1)));

        var shifts = calibrator.Fit(decisions);

        Assert.Contains(shifts, s => s.CardId == "CARD.UNDER" && s.Steps == 1 && s.Up == 3 && s.Games == 2);
        Assert.Contains(shifts, s => s.CardId == "CARD.HIGH" && s.Steps == -1 && s.Down == 3);

        var tuned = calibrator.Apply(shifts);
        Assert.Equal(Tier.B, tuned.FindTier(Defect, "CARD.UNDER")!.Tier);
        Assert.Equal(Tier.B, tuned.FindTier(Defect, "CARD.HIGH")!.Tier);
        Assert.Equal(Tier.B, tuned.FindTier(Defect, "CARD.HIGH")!.ActTier![1]);

        // The base pack is untouched (copy-on-write).
        Assert.Equal(Tier.C, pack.FindTier(Defect, "CARD.UNDER")!.Tier);
        Assert.Equal(Tier.A, pack.FindTier(Defect, "CARD.HIGH")!.Tier);
    }

    [Fact]
    public void Calibrator_IgnoresEvidenceFromASingleGame()
    {
        var decisions = Enumerable.Range(0, 5).Select(_ => Decision(Stream("only"), ["CARD.UNDER", "CARD.HIGH"], picked: 0)).ToArray();
        var calibrator = new TierShiftCalibrator(Pack(), new TierShiftOptions { MinNet = 3, MinGames = 2 }, Fixed(([10, 20], 1)));

        Assert.Empty(calibrator.Fit(decisions));
    }

    [Fact]
    public void CrossValidation_CalibratorNeverSeesTheTestFold()
    {
        var streams = Enumerable.Range(0, 4).Select(i => Stream($"g{i}", start: i)).ToArray();
        var spy = new SpyRecommender();
        var calibrator = new TierShiftCalibrator(Pack(), baseRecommender: spy);

        foreach (var fold in Folds.LeaveOneGameOut(streams))
        {
            spy.Seen.Clear();
            var train = fold.Train.Select(s => Decision(s, ["CARD.UNDER", "CARD.HIGH"], picked: 0)).ToArray();
            calibrator.Fit(train);

            var testGame = fold.Test[0].GameKey;
            Assert.NotEmpty(spy.Seen);
            Assert.DoesNotContain(spy.Seen, d => d.Stream.GameKey == testGame);
        }
    }

    [Fact]
    public void CrossValidation_RejectsAFoldThatSharesAGame()
    {
        var cv = new CrossValidation(Pack());
        var a = Stream("same", person: 1);
        var b = Stream("same", person: 2);
        Assert.Throws<ArgumentException>(() => cv.RunFold(new Fold("bad", [a], [b])));
    }

    [Fact]
    public void Shift_ClampsAndNeverLowersACardAboveTheCap()
    {
        Assert.Equal(Tier.B, PackEditing.Shift(Tier.C, +1));
        Assert.Equal(Tier.A, PackEditing.Shift(Tier.A, +1));
        Assert.Equal(Tier.S, PackEditing.Shift(Tier.S, +1));
        Assert.Equal(Tier.A, PackEditing.Shift(Tier.S, -1));
        Assert.Equal(Tier.D, PackEditing.Shift(Tier.D, -1));
    }

    [Fact]
    public void TierOnly_UsesActTierAndUpgradeBonus_AndSkipsBelowT0()
    {
        var pack = Pack();
        var rec = new TierOnlyRecommender(pack);

        var p = rec.Predict(Decision(Stream("g"), ["CARD.UNDER", "CARD.HIGH"], picked: 0));
        Assert.Equal([35.0, 65.0], p.Scores);
        Assert.Equal(1, p.Recommended);

        var upgraded = rec.Predict(Decision(Stream("g"), ["CARD.UNDER"], picked: 0, upgrade: 1));
        Assert.Equal(39.0, upgraded.Scores[0]); // C 35 + upgradedDelta 4
        Assert.True(upgraded.SkipRecommended); // below act 1 skip base 45
    }

    [Fact]
    public void EndToEnd_OnFixtures_EveryDecisionIsTestedOnceInLogo()
    {
        var pack = DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "tests", "Sts2Overlay.Core.Tests", "TestData", "pack-defect"));
        var files = TestPaths.AllRuns.Take(8).Select(p => (p, RunHistoryReader.ReadFile(p))).ToArray();
        var streams = ReplayStreams.Build(files, allPlayers: true, LocalPlayer.FixtureSelfId);
        var cv = new CrossValidation(pack, new TierShiftOptions { MinNet = 2 });

        var result = cv.Run("logo", Folds.LeaveOneGameOut(streams));
        var total = cv.DecisionsOf(streams).Count;

        Assert.True(total > 50, $"expected many decisions, got {total}");
        Assert.Equal(total, result.BaseTest.Decisions);
        Assert.Equal(total, result.CalibratedTest.Decisions);
        Assert.Equal(total, result.TierOnlyTest.Decisions);
        Assert.InRange(result.BaseTest.Top1, 0, 1);
        Assert.InRange(result.RandomTest.Top1, 0.25, 0.5);
    }

    // ---------- helpers ----------

    private static DataPack Pack() => new()
    {
        Tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal)
        {
            [Defect] = new TierPack
            {
                Character = Defect,
                Cards = new Dictionary<string, CardTierEntry>(StringComparer.Ordinal)
                {
                    ["CARD.UNDER"] = new() { Tier = Tier.C, UpgradedDelta = 4, Confidence = 0.6 },
                    ["CARD.HIGH"] = new() { Tier = Tier.A, ActTier = new() { [1] = Tier.A }, Confidence = 0.6 },
                },
            },
        },
    };

    private static ReplayStream Stream(string game, long start = 1, long person = 1)
    {
        var player = new RunPlayer { Id = person, Character = Defect };
        var run = new RunHistory { Seed = game, StartTime = start, Players = [player] };
        return new ReplayStream(run, player, person, $"{game}.run");
    }

    private static LabeledDecision Decision(ReplayStream stream, string[] offered, int? picked, int upgrade = 0)
    {
        var cards = offered.Select(id => new CardRef(id, upgrade)).ToArray();
        var decision = new CardDecision
        {
            Node = new RunNode { Floor = 1 },
            Offered = cards,
            Picked = picked is int i ? [cards[i]] : [],
            StateBefore = new RunState { Character = Defect, Act = 1, Hp = 50, MaxHp = 50 },
        };
        return new LabeledDecision(stream, decision, picked);
    }

    private static IRecommender Fixed((double[] Scores, int? Recommended) p) => new FixedRecommender(new Prediction(p.Scores, p.Recommended));

    private sealed class FixedRecommender(Prediction p) : IRecommender
    {
        public string Name => "fixed";

        public Prediction Predict(LabeledDecision decision) => p;
    }

    private sealed class SpyRecommender : IRecommender
    {
        public List<LabeledDecision> Seen { get; } = [];

        public string Name => "spy";

        public Prediction Predict(LabeledDecision decision)
        {
            Seen.Add(decision);
            return new Prediction(decision.Decision.Offered.Select((_, i) => (double)i).ToArray(), 0);
        }
    }
}
