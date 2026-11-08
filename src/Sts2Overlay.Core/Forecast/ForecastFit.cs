using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Forecast;

/// <summary>Rates measured from run history that the forecaster needs but the game code doesn't give us.</summary>
/// <param name="EventCardChance">Character cards gained outside reward/shop choices, per "?" event visit.</param>
/// <param name="ActDeathChance">Per act: share of runs that reached the act and ended in it (death or abandon).</param>
/// <param name="EventVisits">"?" event visits counted (per player).</param>
/// <param name="EventCards">Cards counted toward <paramref name="EventCardChance"/>.</param>
/// <param name="Reached">Runs that reached act 1, 2, 3.</param>
public sealed record FittedRates(double EventCardChance, IReadOnlyList<double> ActDeathChance, int EventVisits, int EventCards, IReadOnlyList<int> Reached);

/// <summary>Measures <see cref="FittedRates"/> from the local player's runs.</summary>
public static class ForecastFit
{
    /// <summary>From the user's 50 runs: 114 cards over 224 "?" event visits (<c>RunHistoryAnalyzer calibrate --emit</c>). [추정, 개인 기록 기반]</summary>
    public const double DefaultEventCardChance = 0.509;

    /// <summary>From the user's 50 runs (reached act 1/2/3: 50/31/17), act 1, 2, 3. [추정, 개인 기록 기반]</summary>
    public static readonly IReadOnlyList<double> DefaultActDeathChance = [0.38, 0.452, 0.353];

    /// <summary>
    /// Event cards: at "?" event rooms and ancients, every gained card of the player's own character pool (picked from a
    /// choice or given), plus every transform anywhere that produced one. Divided by "?" event visits, because the
    /// simulator only has "?" rooms to hang them on.
    /// Death: act k "ended" when the run has a node in act k but none in act k+1 (act 3: not a win).
    /// </summary>
    public static FittedRates Measure(IEnumerable<(RunHistory Run, long? Player)> runs, ForecastCardPool pool)
    {
        var visits = 0;
        var cards = 0;
        var reached = new int[3];
        var ended = new int[3];
        var poolIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (run, player) in runs)
        {
            if (run.Players.Count == 0 || run.Nodes.Count == 0) continue;
            var me = LocalPlayer.Resolve(run, player);
            if (!poolIds.TryGetValue(me.Character, out var own))
            {
                own = pool.CharacterCards(me.Character, multiplayer: true).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                poolIds[me.Character] = own;
            }

            foreach (var node in run.Nodes)
            {
                var stats = node.StatsFor(me.Id);
                if (stats is null) continue;
                var isEvent = node.Rooms.Count > 0 && node.Rooms.All(r => r.RoomType == "event");
                var isAncient = node.MapPointType == "ancient";
                if (isEvent && node.MapPointType == "unknown") visits++;
                if (isEvent || isAncient) cards += stats.CardsGained.Count(c => own.Contains(c.Id));
                cards += stats.CardsTransformed.Count(t => own.Contains(t.Final.Id));
            }

            var lastAct = Math.Clamp(run.Nodes.Max(n => n.Act), 1, 3);
            for (var act = 1; act <= lastAct; act++) reached[act - 1]++;
            if (!run.Win) ended[lastAct - 1]++;
        }

        var hazard = Enumerable.Range(0, 3).Select(k => reached[k] == 0 ? 0.0 : (double)ended[k] / reached[k]).ToArray();
        return new FittedRates(visits == 0 ? 0 : (double)cards / visits, hazard, visits, cards, reached);
    }

    /// <summary>Options with the measured rates plugged in.</summary>
    public static ForecastOptions Apply(this ForecastOptions options, FittedRates rates)
        => options with { EventCardChance = Math.Clamp(rates.EventCardChance, 0, 1), ActDeathChance = rates.ActDeathChance };
}
