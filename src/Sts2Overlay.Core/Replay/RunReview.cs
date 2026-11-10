using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Replay;

/// <summary>One card decision where the player did not do what the engine recommends.</summary>
/// <param name="Floor">Global floor of the decision.</param>
/// <param name="Act">1-based act.</param>
/// <param name="Context">Normal / elite / boss reward or event.</param>
/// <param name="Offered">The cards on the screen, in order.</param>
/// <param name="EnginePick">Engine's top-1, null = Skip.</param>
/// <param name="UserPick">What the player took, null = Skip.</param>
/// <param name="EngineScore">Score of the engine's top candidate (card or Skip).</param>
/// <param name="UserScore">Score of what the player chose (card or Skip).</param>
/// <param name="Verdict">How the choice compares.</param>
public sealed record ReviewDisagreement(
    int Floor,
    int Act,
    OfferContext Context,
    IReadOnlyList<CardRef> Offered,
    CardRef? EnginePick,
    CardRef? UserPick,
    double EngineScore,
    double UserScore,
    ReplayVerdict Verdict)
{
    /// <summary>Score gap = engine's best − the player's choice (≥ 0).</summary>
    public double Gap => Math.Max(0, EngineScore - UserScore);

    /// <summary>
    /// True: judged by today's engine (no play-time record of this decision). False: <see cref="EnginePick"/> and the
    /// scores are the advice the overlay showed during play (<see cref="DecisionRecord"/>).
    /// </summary>
    public bool Recomputed { get; init; } = true;

    /// <summary>For a stored decision: what the current version recommends (null = Skip).</summary>
    public CardRef? CurrentPick { get; init; }

    /// <summary>For a stored decision: the current version gives a verdict that differs from the stored advice.</summary>
    public bool CurrentDiffers { get; init; }
}

/// <summary>The build the final deck formed.</summary>
public sealed record ReviewBuild
{
    /// <summary>Highest-commit archetype, null when the deck has no archetype cards.</summary>
    public ArchetypeStatus? Primary { get; init; }

    /// <summary>Second archetype when the top two are within the hybrid margin.</summary>
    public ArchetypeStatus? HybridWith { get; init; }

    /// <summary>First floor at which the primary archetype reached its commit threshold, null if never.</summary>
    public int? CommittedAtFloor { get; init; }

    public int DeckSize { get; init; }
}

/// <summary>Short end-of-run review: agreement with the engine, biggest disagreements, the build that formed.</summary>
public sealed record RunReview
{
    public required RunHistory Run { get; init; }
    public required RunPlayer Player { get; init; }

    /// <summary>Reward/event card decisions that can be compared 1:1 (shops and multi-pick groups excluded).</summary>
    public int Decisions { get; init; }

    /// <summary>Decisions where the player's choice (card or Skip) equals the engine's top-1.</summary>
    public int Agreements { get; init; }

    public double? AgreementRate => Decisions == 0 ? null : (double)Agreements / Decisions;
    public int ShopVisits { get; init; }
    public double MeanGap { get; init; }

    /// <summary>Up to three disagreements with the largest score gap, largest first.</summary>
    public IReadOnlyList<ReviewDisagreement> BiggestDisagreements { get; init; } = [];

    public required ReviewBuild Build { get; init; }

    /// <summary>A play-time decision log exists for this run (<see cref="DecisionLog"/>), matched or not.</summary>
    public bool HasDecisionLog { get; init; }

    /// <summary>Of <see cref="Decisions"/>: judged against the advice the overlay showed during play.</summary>
    public int StoredJudged { get; init; }

    /// <summary>Of <see cref="Decisions"/>: no play-time record, judged by recomputing with the current engine.</summary>
    public int Recomputed { get; init; }

    /// <summary>Last floor reached (= number of map points visited).</summary>
    public int FloorReached => Run.Nodes.Count;
}

/// <summary>Builds a <see cref="RunReview"/> from a finished run with the same engine the overlay uses live.</summary>
public static class RunReviewBuilder
{
    public const int MaxDisagreements = 3;

    /// <param name="run">The finished run.</param>
    /// <param name="pack">Data pack to replay with.</param>
    /// <param name="steamId">The local player's Steam id in co-op (see <see cref="LocalPlayer.Resolve"/>).</param>
    /// <param name="decisionLog">
    /// Play-time records (<see cref="DecisionLog"/>), of any run: the ones of this run are picked here. A decision with a
    /// matching record is judged against the advice the overlay showed then; the others are recomputed.
    /// </param>
    /// <param name="useLearnedModel">The user's <c>cardModel</c> setting (false = <c>"engine"</c>) for the recomputed decisions.</param>
    public static RunReview Build(RunHistory run, DataPack pack, long? steamId = null, IEnumerable<DecisionRecord>? decisionLog = null, bool useLearnedModel = true)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(pack);
        var player = LocalPlayer.Resolve(run, steamId);
        var replay = new ReplayAnalyzer(pack, useLearnedModel).Replay(run, player.Id);
        var records = decisionLog is null ? [] : DecisionLogMatcher.ForRun(run, player, decisionLog);
        var stored = records.Count == 0 ? null : DecisionLogMatcher.Match(replay.Decisions.Select(d => d.Decision).ToArray(), records, pack.Canonical);
        return FromReplay(replay, pack, stored);
    }

    /// <param name="replay">The run replayed with the current engine.</param>
    /// <param name="pack">Data pack (build section).</param>
    /// <param name="stored">
    /// Per replayed decision (same order) its play-time record or null; a null list means this run has no decision log. A
    /// record with a verdict decides the agreement and the gap (play-time scores); a record without one (the overlay had no
    /// rating data then) leaves the decision out, as the replay does for "no data"; no record = recomputed as before.
    /// </param>
    public static RunReview FromReplay(RunReplay replay, DataPack pack, IReadOnlyList<DecisionRecord?>? stored = null)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var judged = new List<(bool Agree, double Gap, ReviewDisagreement? Row, bool Recomputed)>();
        for (var i = 0; i < replay.Decisions.Count; i++)
        {
            var d = replay.Decisions[i];
            if (d.Offer is null) continue; // shops
            var record = stored is not null && i < stored.Count ? stored[i] : null;
            if (record is not null)
            {
                if (!record.HasVerdict || d.Decision.Picked.Count > 1) continue; // no advice then / multi-pick: nothing to grade
                var row = FromRecord(d, record, pack.Canonical);
                judged.Add((row.Verdict is ReplayVerdict.Agreed or ReplayVerdict.AgreedSkip, row.Gap, row, false));
            }
            else if (d.IsComparable)
            {
                judged.Add((d.IsAgreement, d.Regret, d.IsAgreement ? null : ToDisagreement(d), true));
            }
        }

        var disagreements = judged
            .Where(j => !j.Agree && j.Row is not null)
            .Select(j => j.Row!)
            .OrderByDescending(d => d.Gap)
            .ThenBy(d => d.Floor)
            .Take(MaxDisagreements)
            .ToArray();

        return new RunReview
        {
            Run = replay.Run,
            Player = replay.Player,
            Decisions = judged.Count,
            Agreements = judged.Count(j => j.Agree),
            ShopVisits = replay.Decisions.Count(d => d.Decision.Context == OfferContext.Shop),
            MeanGap = judged.Count == 0 ? 0 : judged.Average(j => j.Gap),
            BiggestDisagreements = disagreements,
            Build = BuildOf(replay, pack),
            HasDecisionLog = stored is not null,
            StoredJudged = judged.Count(j => !j.Recomputed),
            Recomputed = judged.Count(j => j.Recomputed),
        };
    }

    /// <summary>
    /// A decision judged against its play-time record. Agreement compares card ids (the overlay reads upgrades from the
    /// title colour, and the record matched the run's offer by id). The gap uses the play-time scores: the best of Skip and
    /// the offered cards minus the score of what the player chose.
    /// </summary>
    private static ReviewDisagreement FromRecord(DecisionReplay d, DecisionRecord record, Func<string, string> canonical)
    {
        var advice = record.PickCard;
        CardRef? user = d.Decision.Picked.Count > 0 ? d.Decision.Picked[0] : null;
        var scores = record.Scores ?? [];
        var best = Math.Max(record.Skip ?? double.MinValue, scores.DefaultIfEmpty(double.MinValue).Max());
        var engineScore = best == double.MinValue ? 0 : best;
        var userScore = user is { } u ? SlotScore(record, u, canonical) : record.Skip;

        var verdict = advice is not { } a
            ? user is null ? ReplayVerdict.AgreedSkip : ReplayVerdict.PickedButSkipRecommended
            : user is not { } p ? ReplayVerdict.SkippedButCardRecommended
            : canonical(p.Id) == canonical(a.Id) ? ReplayVerdict.Agreed : ReplayVerdict.PickedOther;

        var offer = d.Offer!;
        CardRef? current = offer.SkipRecommended ? null : offer.Best;
        var currentDiffers = !offer.NoData && (current is { } c
            ? advice is not { } adv || canonical(c.Id) != canonical(adv.Id)
            : advice is not null);
        return new ReviewDisagreement(
            d.Decision.Node.Floor,
            d.Decision.Node.Act,
            d.Decision.Context,
            d.Decision.Offered,
            advice,
            user,
            engineScore,
            userScore ?? engineScore,
            verdict)
        {
            Recomputed = false,
            CurrentPick = current,
            CurrentDiffers = currentDiffers,
        };
    }

    /// <summary>The play-time score of the offered slot holding <paramref name="card"/> (by id), or null.</summary>
    private static double? SlotScore(DecisionRecord record, CardRef card, Func<string, string> canonical)
    {
        if (record.Scores is not { } scores) return null;
        var id = canonical(card.Id);
        for (var i = 0; i < record.Offered.Count && i < scores.Count; i++)
        {
            if (canonical(DecisionLog.ParseCard(record.Offered[i]).Id) == id) return scores[i];
        }
        return null;
    }

    private static ReviewDisagreement ToDisagreement(DecisionReplay d)
    {
        var offer = d.Offer!;
        var bestCard = offer.Cards.Select(c => c.Total).DefaultIfEmpty(double.MinValue).Max();
        var engineScore = Math.Max(offer.Skip.Total, bestCard);
        CardRef? userPick = d.Decision.Picked.Count > 0 ? d.Decision.Picked[0] : null;
        var userScore = userPick is { } p
            ? (offer.Cards.FirstOrDefault(c => c.Card == p) ?? offer.Cards.FirstOrDefault(c => c.Card.Id == p.Id))?.Total ?? engineScore - d.Regret
            : offer.Skip.Total;
        return new ReviewDisagreement(
            d.Decision.Node.Floor,
            d.Decision.Node.Act,
            d.Decision.Context,
            d.Decision.Offered,
            offer.SkipRecommended ? null : offer.Best,
            userPick,
            engineScore,
            userScore,
            d.Verdict);
    }

    private static ReviewBuild BuildOf(RunReplay replay, DataPack pack)
    {
        var run = replay.Run;
        var player = replay.Player;
        var lastAct = run.Nodes.Count > 0 ? run.Nodes[^1].Act : 1;
        var final = new RunState
        {
            Character = player.Character,
            Ascension = run.Ascension,
            Act = lastAct,
            Floor = run.Nodes.Count,
            Deck = player.Deck,
            Relics = player.Relics.Select(r => r.Id).ToArray(),
            IsMultiplayer = run.IsMultiplayer,
        };
        var report = new RecommendationEngine(pack).Archetypes(final, 0);
        var primary = report.Primary;

        int? committedAt = null;
        if (primary is not null && primary.IsCommitted && run.Nodes.Count > 0)
        {
            var timeline = DeckTimeline.Build(run, player.Id);
            for (var i = 0; i < run.Nodes.Count && committedAt is null; i++)
            {
                var state = timeline.StateBefore(i) with { Deck = timeline.DeckAfter(i) };
                var status = ArchetypeDetector.Detect(state, pack).FirstOrDefault(s => s.Archetype.Id == primary.Archetype.Id);
                if (status is { IsCommitted: true }) committedAt = run.Nodes[i].Floor;
            }
        }

        return new ReviewBuild
        {
            Primary = primary,
            HybridWith = report.IsHybrid && report.Statuses.Count > 1 ? report.Statuses[1] : null,
            CommittedAtFloor = committedAt,
            DeckSize = player.Deck.Count,
        };
    }
}
