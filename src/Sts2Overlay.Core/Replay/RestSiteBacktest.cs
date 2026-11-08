using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Replay.Validation;
using Sts2Overlay.Core.Rest;

namespace Sts2Overlay.Core.Replay;

/// <summary>One recorded rest-site visit next to what <see cref="RestSiteAdvisor"/> would have said.</summary>
public sealed record RestDecision
{
    public required string RunKey { get; init; }
    public long PersonId { get; init; }
    public string Character { get; init; } = "";
    public bool Multiplayer { get; init; }
    public int Act { get; init; }
    public int Floor { get; init; }

    /// <summary>HP ratio on entering the rest site.</summary>
    public double Hp { get; init; }

    /// <summary><c>rest_site_choices</c> as recorded, e.g. <c>["SMITH"]</c>, <c>["HEAL"]</c>, <c>["DIG"]</c>.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>The player's choice when it was exactly one of HEAL / SMITH; null for other options or both.</summary>
    public RestAction? PlayerAction { get; init; }

    /// <summary>The card the player upgraded (first of <c>upgraded_cards</c>) on a SMITH visit.</summary>
    public string? UpgradedCard { get; init; }

    public required RestSiteAdvice Advice { get; init; }

    /// <summary>
    /// Where <see cref="UpgradedCard"/> lands among the advisor's upgrade candidates, ties included. Null when the card
    /// is not among the candidates.
    /// </summary>
    public TiedRank? UpgradeRank { get; init; }

    /// <summary>Same, ranking by the tier pack's <c>upgradedDelta</c> alone (the "pack only" baseline, many ties).</summary>
    public TiedRank? DeltaOnlyRank { get; init; }

    /// <summary>Number of upgradable cards the advisor ranked.</summary>
    public int Candidates => Advice.Upgrades.Count;

    public bool Agrees => PlayerAction is { } a && a == Advice.Action;
}

/// <summary>
/// A rank that is honest about ties: the card shares places <see cref="Best"/> … <see cref="Best"/> + <see cref="Tied"/> − 1
/// with the other cards of the same score. Counting a tie as "rank 1" would flatter a coarse score (most cards of a
/// pack have the same <c>upgradedDelta</c>), so hits are counted as the chance that a random tie-break puts the card
/// within the top k.
/// </summary>
/// <param name="Best">1 + the number of candidates with a strictly higher score.</param>
/// <param name="Tied">Candidates with the same score, the card itself included (≥ 1).</param>
public readonly record struct TiedRank(int Best, int Tied)
{
    /// <summary>P(the card is within the top <paramref name="k"/> after a random tie-break), 0..1.</summary>
    public double HitWithin(int k) => Math.Clamp((k - Best + 1) / (double)Tied, 0.0, 1.0);

    /// <summary>The worst place the card can get (used to sort disagreements).</summary>
    public int Worst => Best + Tied - 1;
}

/// <summary>Agreement of the advisor with recorded rest-site choices (proxies, not targets).</summary>
public sealed record RestBacktestSummary
{
    /// <summary>All rest-site visits read.</summary>
    public int Visits { get; init; }

    /// <summary>Count per recorded choice set, e.g. <c>SMITH → 134</c>, <c>DIG → 5</c>, <c>SMITH+HEAL → 3</c>.</summary>
    public IReadOnlyDictionary<string, int> ChoiceKinds { get; init; } = new Dictionary<string, int>();

    /// <summary>Visits with exactly one of HEAL / SMITH.</summary>
    public int Decided { get; init; }
    public int Agreed { get; init; }
    public int PlayerHealed { get; init; }
    public int AdvisorHealed { get; init; }

    /// <summary>Same visits, "heal below <see cref="PathWeights.RestHealBelowHp"/>" (fitted on this kind of data: in-sample).</summary>
    public int ThresholdAgreed { get; init; }

    /// <summary>Agreement on the visits the advisor called 확신 / 박빙.</summary>
    public int SureDecided { get; init; }
    public int SureAgreed { get; init; }

    /// <summary>SMITH visits whose upgraded card could be located among the candidates.</summary>
    public int SmithWithCard { get; init; }
    public int SmithCardMissing { get; init; }
    /// <summary>Expected top-1 / top-3 hits (ties split by <see cref="TiedRank.HitWithin"/>).</summary>
    public double Top1 { get; init; }
    public double Top3 { get; init; }
    public double DeltaOnlyTop1 { get; init; }
    public double DeltaOnlyTop3 { get; init; }

    /// <summary>Expected top-1 / top-3 hits if a candidate were picked at random (Σ 1/n, Σ min(3, n)/n).</summary>
    public double RandomTop1 { get; init; }
    public double RandomTop3 { get; init; }

    /// <summary>Mean number of upgrade candidates on those visits.</summary>
    public double MeanCandidates { get; init; }
}

/// <summary>
/// Replays every rest-site visit of the run history through <see cref="RestSiteAdvisor"/>.
/// <para>History files do not store the act map, only the rooms walked. So the "path ahead" is the rooms the player
/// actually walked after the rest site in that act (a boss is appended when the run ended earlier), built into a
/// one-path map with <see cref="RestSiteAdvisor.ChainMap"/>. That is a little optimistic for the advisor: it sees the
/// player's real route, while the live overlay sees the recommended one.</para>
/// </summary>
public static class RestSiteBacktest
{
    public static IReadOnlyList<RestDecision> Run(IEnumerable<ReplayStream> streams, DataPack pack, PathWeights? path = null)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var advisor = new RestSiteAdvisor(pack, path);
        var result = new List<RestDecision>();
        foreach (var s in streams)
        {
            var run = s.Run;
            DeckTimeline? timeline = null;
            for (var i = 0; i < run.Nodes.Count; i++)
            {
                var node = run.Nodes[i];
                if (node.MapPointType != "rest_site") continue;
                var stats = node.StatsFor(s.Player.Id);
                if (stats is null) continue;

                timeline ??= DeckTimeline.Build(run, s.Player.Id);
                var state = timeline.StateBefore(i);
                var actNodes = run.Nodes.Where(n => n.ActIndex == node.ActIndex).ToArray();
                var visited = actNodes.Where(n => n.IndexInAct <= node.IndexInAct).Select(n => MapGraph.ParseType(n.MapPointType)).ToArray();
                var ahead = actNodes.Where(n => n.IndexInAct > node.IndexInAct).Select(n => MapGraph.ParseType(n.MapPointType)).ToArray();
                var advice = advisor.Advise(state, RestSiteAdvisor.ChainMap(visited, ahead, node.Act));

                var choices = stats.RestSiteChoices;
                var heal = choices.Contains("HEAL");
                var smith = choices.Contains("SMITH");
                RestAction? player = heal == smith ? null : heal ? RestAction.Heal : RestAction.Smith;
                var upgraded = smith && stats.UpgradedCards.Count > 0 ? pack.Canonical(stats.UpgradedCards[0]) : null;

                result.Add(new RestDecision
                {
                    RunKey = s.GameKey,
                    PersonId = s.PersonId,
                    Character = s.Character,
                    Multiplayer = run.IsMultiplayer,
                    Act = node.Act,
                    Floor = node.Floor,
                    Hp = state.HpRatio,
                    Choices = choices,
                    PlayerAction = player,
                    UpgradedCard = upgraded,
                    Advice = advice,
                    UpgradeRank = RankOf(advice, upgraded, c => c.Total),
                    DeltaOnlyRank = RankOf(advice, upgraded, c => c.Score.ValueOf(Scoring.UpgradeComponentKeys.Upgrade)),
                });
            }
        }
        return result;
    }

    public static RestBacktestSummary Summarize(IReadOnlyList<RestDecision> decisions, PathWeights? path = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var threshold = (path ?? new PathWeights()).RestHealBelowHp ?? 0.5;
        var decided = decisions.Where(d => d.PlayerAction is not null).ToArray();
        var sure = decided.Where(d => d.Advice.Confidence == RestConfidence.Sure).ToArray();
        var smith = decisions.Where(d => d.PlayerAction == RestAction.Smith && d.UpgradedCard is not null).ToArray();
        var located = smith.Where(d => d.UpgradeRank is not null).ToArray();
        return new RestBacktestSummary
        {
            Visits = decisions.Count,
            ChoiceKinds = decisions
                .GroupBy(d => d.Choices.Count == 0 ? "(없음)" : string.Join("+", d.Choices))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            Decided = decided.Length,
            Agreed = decided.Count(d => d.Agrees),
            PlayerHealed = decided.Count(d => d.PlayerAction == RestAction.Heal),
            AdvisorHealed = decided.Count(d => d.Advice.Action == RestAction.Heal),
            ThresholdAgreed = decided.Count(d => (d.Hp < threshold) == (d.PlayerAction == RestAction.Heal)),
            SureDecided = sure.Length,
            SureAgreed = sure.Count(d => d.Agrees),
            SmithWithCard = located.Length,
            SmithCardMissing = smith.Length - located.Length,
            Top1 = located.Sum(d => d.UpgradeRank!.Value.HitWithin(1)),
            Top3 = located.Sum(d => d.UpgradeRank!.Value.HitWithin(3)),
            DeltaOnlyTop1 = located.Sum(d => d.DeltaOnlyRank!.Value.HitWithin(1)),
            DeltaOnlyTop3 = located.Sum(d => d.DeltaOnlyRank!.Value.HitWithin(3)),
            RandomTop1 = located.Sum(d => 1.0 / d.Candidates),
            RandomTop3 = located.Sum(d => Math.Min(3.0, d.Candidates) / d.Candidates),
            MeanCandidates = located.Length == 0 ? 0 : located.Average(d => d.Candidates),
        };
    }

    /// <summary>
    /// Where <paramref name="cardId"/> lands among <paramref name="advice"/>'s upgrade candidates when they are ranked
    /// by <paramref name="value"/> (ties kept). Null when the card is not a candidate.
    /// </summary>
    public static TiedRank? RankOf(RestSiteAdvice advice, string? cardId, Func<Scoring.UpgradeCandidate, double> value)
    {
        if (cardId is null) return null;
        var target = advice.Upgrades.FirstOrDefault(c => c.Card.Id == cardId);
        if (target is null) return null;
        var v = value(target);
        return new TiedRank(1 + advice.Upgrades.Count(c => value(c) > v + 1e-9), advice.Upgrades.Count(c => Math.Abs(value(c) - v) <= 1e-9));
    }
}
