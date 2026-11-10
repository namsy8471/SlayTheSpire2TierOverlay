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
    /// <param name="useLearnedModel">The live engine's <see cref="RecommendationEngine.UseLearnedModel"/>.</param>
    public static RunReview Build(RunHistory run, DataPack pack, long? steamId = null, bool useLearnedModel = true)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(pack);
        var player = LocalPlayer.Resolve(run, steamId);
        var replay = new ReplayAnalyzer(pack, useLearnedModel).Replay(run, player.Id);
        return FromReplay(replay, pack);
    }

    public static RunReview FromReplay(RunReplay replay, DataPack pack)
    {
        var comparable = replay.Decisions.Where(d => d.IsComparable && d.Offer is not null).ToList();
        var disagreements = comparable
            .Where(d => !d.IsAgreement)
            .Select(ToDisagreement)
            .OrderByDescending(d => d.Gap)
            .ThenBy(d => d.Floor)
            .Take(MaxDisagreements)
            .ToArray();

        return new RunReview
        {
            Run = replay.Run,
            Player = replay.Player,
            Decisions = comparable.Count,
            Agreements = comparable.Count(d => d.IsAgreement),
            ShopVisits = replay.Decisions.Count(d => d.Decision.Context == OfferContext.Shop),
            MeanGap = comparable.Count == 0 ? 0 : comparable.Average(d => d.Regret),
            BiggestDisagreements = disagreements,
            Build = BuildOf(replay, pack),
        };
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
