using System.IO;
using System.Text.RegularExpressions;
using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The play-time decision log on the App side: which evaluation of a screen becomes its record (<see cref="DecisionRecorder"/>),
/// the file store, and the review texts that say what the agreement count is based on.
/// </summary>
public class DecisionLogTests
{
    private const string Run = "SEED_IRONCLAD_a5";

    private static CardRef[] Cards(params string[] ids) => ids.Select(i => new CardRef(i)).ToArray();

    private static DecisionRecord Rec(string pick, int floor = 3, string run = Run) =>
        new() { Run = run, Floor = floor, Act = 1, Screen = "reward", Pick = pick, Model = "learned", Utc = DateTime.UtcNow };

    // ---- which evaluation becomes the record -------------------------------------------------------------------

    [Fact]
    public void Several_evaluations_of_one_screen_give_one_record_the_last_complete_one()
    {
        var rec = new DecisionRecorder();
        var abc = Cards("CARD.A", "CARD.B", "CARD.C");
        var first = Rec("CARD.A");
        var last = Rec("CARD.B");

        Assert.Null(rec.Evaluated(ScreenKind.Reward, abc, 3, PartialVerdict.Complete, 0, first));
        Assert.Null(rec.Evaluated(ScreenKind.Reward, abc, 3, PartialVerdict.Complete, 0, Rec("skip")));
        Assert.Null(rec.Evaluated(ScreenKind.Reward, abc, 3, PartialVerdict.Complete, 0, last));
        Assert.Null(rec.Tick(Run, 3, screenGone: false)); // still open
        Assert.Same(last, rec.Tick(Run, 3, screenGone: true));
        Assert.Null(rec.Tick(Run, 3, screenGone: true)); // written once
    }

    [Fact]
    public void A_held_partial_evaluation_never_becomes_the_record()
    {
        var rec = new DecisionRecorder();
        var held = Rec("CARD.A");
        Assert.Null(rec.Evaluated(ScreenKind.Reward, Cards("CARD.A", "CARD.B"), 2, PartialVerdict.Hold, 0, held));
        Assert.Null(rec.Tick(Run, 4, screenGone: false)); // floor moved on: finished, but nothing was recommended

        // Hold, then the missing card is read (complete), then a hover hides one again (held): the complete one stays.
        var complete = Rec("CARD.C");
        rec.Evaluated(ScreenKind.Reward, Cards("CARD.D", "CARD.E"), 2, PartialVerdict.Hold, 0, Rec("CARD.D"));
        rec.Evaluated(ScreenKind.Reward, Cards("CARD.D", "CARD.E", "CARD.F"), 3, PartialVerdict.Complete, 0, complete);
        rec.Evaluated(ScreenKind.Reward, Cards("CARD.D", "CARD.F"), 2, PartialVerdict.Hold, 0, Rec("CARD.F"));
        rec.Evaluated(ScreenKind.Reward, Cards("CARD.D", "CARD.F"), 2, PartialVerdict.ShowIncomplete, 0, Rec("CARD.F"));
        Assert.Same(complete, rec.Flush());
    }

    [Fact]
    public void Shown_with_a_card_unread_is_kept_only_when_no_complete_read_follows()
    {
        var rec = new DecisionRecorder();
        var incomplete = Rec("CARD.A") with { Pick = null, Why = "partial" };
        rec.Evaluated(ScreenKind.Reward, Cards("CARD.A", "CARD.B"), 2, PartialVerdict.ShowIncomplete, 0, incomplete);
        Assert.Same(incomplete, rec.Tick(Run, 3, screenGone: true));
    }

    [Fact]
    public void Two_different_screens_on_one_floor_give_two_records()
    {
        var rec = new DecisionRecorder();
        var first = Rec("CARD.A");
        var second = Rec("CARD.E");
        Assert.Null(rec.Evaluated(ScreenKind.Reward, Cards("CARD.A", "CARD.B", "CARD.C"), 3, PartialVerdict.Complete, 0, first));
        // The boss's second card reward: other cards, same floor. The first screen is finished by it.
        Assert.Same(first, rec.Evaluated(ScreenKind.Reward, Cards("CARD.D", "CARD.E", "CARD.F"), 3, PartialVerdict.Complete, 1, second));
        Assert.Same(second, rec.Tick(Run, 3, screenGone: true));
    }

    [Fact]
    public void An_evaluation_made_after_the_pick_is_ignored()
    {
        var rec = new DecisionRecorder();
        var before = Rec("CARD.B");
        var abc = Cards("CARD.A", "CARD.B", "CARD.C");
        rec.Evaluated(ScreenKind.Reward, abc, 3, PartialVerdict.Complete, offeredInDeck: 1, before);
        // The log already added CARD.B to the deck while the row was still on screen: that re-score is not the advice shown.
        rec.Evaluated(ScreenKind.Reward, abc, 3, PartialVerdict.Complete, offeredInDeck: 2, Rec("skip"));
        Assert.Same(before, rec.Flush());
    }

    [Fact]
    public void A_finished_screen_seen_again_is_not_written_twice()
    {
        var rec = new DecisionRecorder();
        var abc = Cards("CARD.A", "CARD.B", "CARD.C");
        var shown = Rec("CARD.A");
        rec.Evaluated(ScreenKind.Reward, abc, 3, PartialVerdict.Complete, 0, shown);
        Assert.Same(shown, rec.Tick(Run, 3, screenGone: true));
        // Closing animation / a stale result re-scored after the floor moved on: same cards.
        Assert.Null(rec.Evaluated(ScreenKind.Reward, abc[..2], 2, PartialVerdict.ShowIncomplete, 1, Rec("CARD.B")));
        Assert.Null(rec.Evaluated(ScreenKind.Reward, abc, 3, PartialVerdict.Complete, 1, Rec("CARD.B", floor: 4)));
        Assert.Null(rec.Tick(Run, 5, screenGone: true));
    }

    [Fact]
    public void A_shop_keeps_the_bundle_shown_before_the_first_purchase()
    {
        var rec = new DecisionRecorder();
        var entering = Rec("") with { Kind = DecisionRecord.ShopKind, Pick = null, Screen = "shop" };
        rec.Evaluated(ScreenKind.Shop, Cards("CARD.A", "CARD.B"), 4, PartialVerdict.Complete, 0, entering with { Buy = [] });
        rec.Evaluated(ScreenKind.Shop, Cards("CARD.A", "CARD.B", "CARD.C"), 5, PartialVerdict.Complete, 0, entering); // a card read late
        rec.Evaluated(ScreenKind.Shop, Cards("CARD.A", "CARD.C"), 4, PartialVerdict.Complete, 0, Rec("x")); // bought CARD.B
        Assert.Same(entering, rec.Tick(Run, 3, screenGone: true));

        // A shop on a later floor is a new screen.
        var next = entering with { Floor = 9 };
        rec.Evaluated(ScreenKind.Shop, Cards("CARD.A"), 3, PartialVerdict.Complete, 0, next);
        Assert.Same(next, rec.Tick(Run, 10, screenGone: false));
    }

    [Fact]
    public void Run_end_finishes_the_open_screen()
    {
        var rec = new DecisionRecorder();
        var boss = Rec("CARD.A", floor: 17);
        rec.Evaluated(ScreenKind.Reward, Cards("CARD.A", "CARD.B", "CARD.C"), 3, PartialVerdict.Complete, 0, boss);
        Assert.Same(boss, rec.Tick(null, 0, screenGone: false));
    }

    // ---- the file store --------------------------------------------------------------------------------------------

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sts2-decisions-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    [Fact]
    public void Store_round_trip_keeps_order_and_runs_apart()
    {
        using var tmp = new TempDir();
        using var store = new DecisionLogStore(tmp.Path);
        store.Append(Rec("CARD.A", floor: 2));
        store.Append(Rec("CARD.B", floor: 7, run: "OTHER_SILENT_a0"));
        store.Append(Rec("skip", floor: 3));
        Assert.True(store.Flush(TimeSpan.FromSeconds(5)));

        var back = store.Read(Run);
        Assert.Equal(["CARD.A", "skip"], back.Select(r => r.Pick));
        Assert.Equal([2, 3], back.Select(r => r.Floor));
        Assert.Single(store.Read("OTHER_SILENT_a0"));
        Assert.Empty(store.Read("NONE_X_a0"));
        Assert.Empty(store.Read(null));
    }

    [Fact]
    public void Store_ignores_a_broken_last_line_and_the_next_record_starts_a_new_line()
    {
        using var tmp = new TempDir();
        using var store = new DecisionLogStore(tmp.Path);
        Directory.CreateDirectory(tmp.Path);
        var good = DecisionLog.Serialize(Rec("CARD.A"));
        File.WriteAllText(store.PathFor(Run), good + "\n" + good[..30]); // crashed mid-line

        Assert.Single(store.Read(Run));
        store.Append(Rec("CARD.B"));
        store.Flush(TimeSpan.FromSeconds(5));
        Assert.Equal(["CARD.A", "CARD.B"], store.Read(Run).Select(r => r.Pick));
    }

    [Fact]
    public void Prune_keeps_the_newest_run_files()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Path);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 6; i++)
        {
            var path = Path.Combine(tmp.Path, $"R{i}_IRONCLAD_a0.jsonl");
            File.WriteAllText(path, "{}\n");
            File.SetLastWriteTimeUtc(path, start.AddHours(i));
        }
        File.WriteAllText(Path.Combine(tmp.Path, "notes.txt"), "not a run file");

        Assert.Equal(2, DecisionLogStore.Prune(tmp.Path, keep: 4));
        var left = Directory.GetFiles(tmp.Path, "*.jsonl").Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["R2_IRONCLAD_a0.jsonl", "R3_IRONCLAD_a0.jsonl", "R4_IRONCLAD_a0.jsonl", "R5_IRONCLAD_a0.jsonl"], left);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "notes.txt")));
        Assert.Equal(300, DecisionLogStore.KeepRuns);
    }

    [Fact]
    public void Stored_lines_have_no_steam_id_or_user_path()
    {
        using var tmp = new TempDir();
        using var store = new DecisionLogStore(tmp.Path);
        store.Append(Rec("CARD.A") with { Seed = "SEED", Character = "CHARACTER.IRONCLAD", App = "0.1.7", Pack = "v0.107.1", Offered = ["CARD.A"] });
        store.Flush(TimeSpan.FromSeconds(5));
        var text = File.ReadAllText(store.PathFor(Run));
        Assert.DoesNotMatch(new Regex(@"7656\d{13}"), text);
        Assert.DoesNotMatch(new Regex(@"[\\/]Users", RegexOptions.IgnoreCase), text);
    }

    // ---- review texts ------------------------------------------------------------------------------------------------

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SlayTheSpire2TierOverlay.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    private static readonly CardInfoProvider IdNames = CardInfoProvider.Load(null, new DataPack());

    private static RunReview Review(bool hasLog)
    {
        var file = Directory.GetFiles(Path.Combine(RepoRoot, "tests", "fixtures", "runs"), "*.run").Order(StringComparer.Ordinal).First();
        var run = RunHistoryReader.ReadFile(file);
        var stored = new ReviewDisagreement(12, 1, OfferContext.EliteReward, Cards("CARD.A", "CARD.B", "CARD.C"),
            new CardRef("CARD.A"), new CardRef("CARD.B"), 30, 20, ReplayVerdict.PickedOther)
        { Recomputed = false, CurrentPick = null, CurrentDiffers = true };
        var recomputed = new ReviewDisagreement(5, 1, OfferContext.CardReward, Cards("CARD.D", "CARD.E", "CARD.F"),
            new CardRef("CARD.D"), null, 25, 20, ReplayVerdict.SkippedButCardRecommended);
        return new RunReview
        {
            Run = run,
            Player = LocalPlayer.Resolve(run),
            Decisions = 22,
            Agreements = 15,
            HasDecisionLog = hasLog,
            StoredJudged = hasLog ? 18 : 0,
            Recomputed = hasLog ? 4 : 22,
            BiggestDisagreements = hasLog ? [stored, recomputed] : [recomputed],
            Build = new ReviewBuild(),
        };
    }

    [Fact]
    public void Review_says_what_the_agreement_is_based_on_in_Korean()
    {
        using var _ = Loc.Use(Lang.Kor);
        var vm = ForecastReviewPresenter.Review(Review(hasLog: true), IdNames, showBanner: false);
        Assert.StartsWith("22개 결정 중 15개 일치 (68%) · 당시 추천 기준 18개, 현재 버전 재계산 4개", vm.AgreementText);
        Assert.EndsWith(" · 재계산", vm.Disagreements[1].Header);
        Assert.DoesNotContain("재계산", vm.Disagreements[0].Header);
        Assert.StartsWith("당시 추천 ", vm.Disagreements[0].ChoiceText);
        Assert.EndsWith(" · 지금 버전은 스킵 추천", vm.Disagreements[0].ChoiceText);
        Assert.StartsWith("추천 ", vm.Disagreements[1].ChoiceText);

        var none = ForecastReviewPresenter.Review(Review(hasLog: false), IdNames, showBanner: false);
        Assert.StartsWith("22개 결정 중 15개 일치 (68%) · 현재 버전으로 다시 계산한 결과입니다 (플레이 당시 추천 기록 없음)", none.AgreementText);
    }

    [Fact]
    public void Review_says_what_the_agreement_is_based_on_in_English()
    {
        using var _ = Loc.Use(Lang.Eng);
        var vm = ForecastReviewPresenter.Review(Review(hasLog: true), IdNames, showBanner: false);
        Assert.StartsWith("15 of 22 agreed (68%) · 18 judged by the advice shown then, 4 recomputed with the current version", vm.AgreementText);
        Assert.EndsWith(" · recomputed", vm.Disagreements[1].Header);
        Assert.StartsWith("Advice then ", vm.Disagreements[0].ChoiceText);
        Assert.EndsWith(" · current version picks Skip", vm.Disagreements[0].ChoiceText);

        var none = ForecastReviewPresenter.Review(Review(hasLog: false), IdNames, showBanner: false);
        Assert.StartsWith("15 of 22 agreed (68%) · recomputed with the current version (no record of the advice shown during play)", none.AgreementText);
        foreach (var t in new[] { vm.AgreementText, vm.Note, none.AgreementText, none.Note }.Concat(vm.Disagreements.SelectMany(d => new[] { d.Header, d.ChoiceText })))
        {
            Assert.DoesNotMatch(new Regex("[가-힣]"), t ?? "");
        }
    }
}
