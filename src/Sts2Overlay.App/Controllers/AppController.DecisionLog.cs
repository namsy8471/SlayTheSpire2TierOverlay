using System.IO;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// Play-time decision log: what the overlay recommended on each card / shop screen, written when the screen is finished
/// (<see cref="DecisionRecorder"/> picks the evaluation, <see cref="DecisionLogStore"/> writes it). The run review judges
/// the player against these records instead of recomputing with the current version. It records the overlay's advice
/// only; nothing here feeds the player's picks into any scoring. Hooks: one line in EvaluateReward and EvaluateShop
/// (AppController.cs), and init / snapshot / dispose from AppController.ForecastReview.cs.
/// </summary>
public sealed partial class AppController
{
    private static readonly string DecisionAppVersion = AppVersion.FromAssembly();
    private readonly DecisionRecorder _decisionRecorder = new();
    private DecisionLogStore? _decisionStore;
    private bool _decisionOfferHint;
    private bool _decisionLogFailed;

    private void InitDecisionLog()
    {
        _decisionStore = new DecisionLogStore(AppPaths.DecisionsDir);
        _decisionStore.PruneInBackground();
    }

    /// <summary>EvaluateReward hook: a reward / choose-a-card / event card screen was evaluated.</summary>
    private void NoteOffer(RecognitionResult r, OfferContext context, OfferEvaluation eval)
        => NoteDecision(r, (h, verdict, cards) => DecisionLog.ForOffer(h, ScreenName(r.ScreenKind), context, cards, eval, verdict == PartialVerdict.ShowIncomplete));

    /// <summary>EvaluateShop hook: the merchant screen was evaluated.</summary>
    private void NoteShop(RecognitionResult r, ShopEvaluation eval)
        => NoteDecision(r, (h, _, cards) => DecisionLog.ForShop(h, cards, eval));

    private void NoteDecision(RecognitionResult r, Func<DecisionHeader, PartialVerdict, IReadOnlyList<CardRef>, DecisionRecord> make)
    {
        try
        {
            if (Demo is not null || _decisionStore is null || _snapshot.Run is not { } run || _snapshot.Seed is not { Length: > 0 } seed) return;
            // The answer ApplyRecognition just got from _lifecycle.Verdict, read without touching the lifecycle's state (a
            // language change re-scores the last closed offer through here too). A half-read row it is not timing is held.
            var verdict = r.ScreenKind is not (ScreenKind.Reward or ScreenKind.Choice) || r.Slots <= r.Cards.Count ? PartialVerdict.Complete
                : _lifecycle.VerdictDue(r) is { } due && DateTime.UtcNow >= due ? PartialVerdict.ShowIncomplete
                : PartialVerdict.Hold;
            var cards = r.Cards.Select(c => c.Card).ToArray();
            var header = new DecisionHeader(seed, run.Character, run.Ascension, run.Act, run.Floor,
                _engine.LearnedModelActive ? "learned" : "engine", DecisionAppVersion, PackName(), DateTime.UtcNow);
            var ids = cards.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var inDeck = run.Deck.Count(d => ids.Contains(d.Id));
            WriteDecision(_decisionRecorder.Evaluated(r.ScreenKind, cards, cards.Length + r.Relics.Count + r.Potions.Count, verdict, inDeck, make(header, verdict, cards)));
        }
        catch (Exception ex)
        {
            // Never let the log break a recommendation; say it once.
            if (!_decisionLogFailed) AppLog.Error("decision log failed", ex);
            _decisionLogFailed = true;
        }
    }

    /// <summary>Every snapshot (from UpdateForecastReview): finish the open screen when the floor or run moved on or the log closed it.</summary>
    private void TickDecisionLog(GameStateSnapshot snap)
    {
        var offerHint = IsOfferHint(snap.Hint);
        var gone = _decisionOfferHint && !offerHint;
        _decisionOfferHint = offerHint;
        var key = snap.Run is { } run ? DecisionLog.RunKey(snap.Seed, run.Character, run.Ascension) : null;
        WriteDecision(_decisionRecorder.Tick(key, snap.Run?.Floor ?? 0, gone));
    }

    private void WriteDecision(DecisionRecord? record)
    {
        if (record is null || _decisionStore is null) return;
        _decisionStore.Append(record);
        AppLog.Info("decision logged: " + DecisionLog.Describe(record));
    }

    private void DisposeDecisionLog()
    {
        WriteDecision(_decisionRecorder.Flush());
        _decisionStore?.Dispose();
    }

    private string PackName() => Path.GetFileName(_packDir.TrimEnd('\\', '/'));

    private static string ScreenName(ScreenKind kind) => kind switch
    {
        ScreenKind.Reward => "reward",
        ScreenKind.Choice => "choice",
        ScreenKind.Grid => "grid",
        ScreenKind.Shop => "shop",
        _ => "other",
    };
}
