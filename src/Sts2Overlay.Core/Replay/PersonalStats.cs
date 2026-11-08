using Sts2Overlay.Core.History;

namespace Sts2Overlay.Core.Replay;

/// <summary>Per-character results for the personal statistics screen (PLAN §7.7 v2, available early).</summary>
public sealed record CharacterStats(string Character, int Runs, int Wins, int Abandoned, double AverageFloorReached)
{
    public double WinRate => Runs == 0 ? 0 : (double)Wins / Runs;
}

/// <summary>How often a card was taken when offered as a reward (shops excluded).</summary>
public sealed record CardPickStats(string CardId, int Offered, int Picked)
{
    public double PickRate => Offered == 0 ? 0 : (double)Picked / Offered;
}

public sealed record PersonalStatsReport(
    IReadOnlyList<CharacterStats> Characters,
    IReadOnlyList<CardPickStats> Cards,
    int Runs,
    int MultiplayerRuns);

/// <summary>
/// Aggregates the local player's run history. Note: "win rate of decks containing card X" is deliberately
/// not computed — it is biased toward long runs (PLAN §7.7).
/// </summary>
public static class PersonalStats
{
    public static PersonalStatsReport Compute(IEnumerable<RunHistory> runs, long? steamId = null)
    {
        var list = runs.ToArray();
        var perChar = new Dictionary<string, (int runs, int wins, int abandoned, int floors)>(StringComparer.Ordinal);
        var offered = new Dictionary<string, int>(StringComparer.Ordinal);
        var picked = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var run in list)
        {
            var me = LocalPlayer.Resolve(run, steamId);
            var e = perChar.GetValueOrDefault(me.Character);
            perChar[me.Character] = (e.runs + 1, e.wins + (run.Win ? 1 : 0), e.abandoned + (run.WasAbandoned ? 1 : 0), e.floors + run.Nodes.Count);

            foreach (var decision in DecisionExtractor.Extract(run, me.Id))
            {
                if (decision.Context == Model.OfferContext.Shop) continue;
                foreach (var card in decision.Offered)
                {
                    offered[card.Id] = offered.GetValueOrDefault(card.Id) + 1;
                }
                foreach (var card in decision.Picked)
                {
                    picked[card.Id] = picked.GetValueOrDefault(card.Id) + 1;
                }
            }
        }

        var characters = perChar
            .Select(kv => new CharacterStats(kv.Key, kv.Value.runs, kv.Value.wins, kv.Value.abandoned, kv.Value.runs == 0 ? 0 : (double)kv.Value.floors / kv.Value.runs))
            .OrderByDescending(c => c.Runs).ThenBy(c => c.Character, StringComparer.Ordinal)
            .ToArray();

        var cards = offered
            .Select(kv => new CardPickStats(kv.Key, kv.Value, picked.GetValueOrDefault(kv.Key)))
            .OrderByDescending(c => c.Picked).ThenByDescending(c => c.Offered).ThenBy(c => c.CardId, StringComparer.Ordinal)
            .ToArray();

        return new PersonalStatsReport(characters, cards, list.Length, list.Count(r => r.IsMultiplayer));
    }
}
