using System.Globalization;
using System.Text;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay.Validation;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Tools.RunHistoryAnalyzer;

/// <summary>
/// <c>upgrade-export</c>: one CSV row per upgrade candidate at every recorded smith visit (the card the player upgraded
/// has <c>chosen=1</c>). Feeds <c>tools/UpgradeCalib</c>, which fits how much a build's cards are worth upgrading.
/// Rows hold card ids and numbers only; the source column is the run file's name (no Steam id).
/// </summary>
internal static class UpgradeExportCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Run(IReadOnlyList<(string Path, RunHistory Run)> files, DataPack pack, long? user, bool allPlayers)
    {
        var me = user ?? files.Select(f => ReplayStreams.SteamIdFromPath(f.Path)).FirstOrDefault(id => id is not null) ?? LocalPlayer.FixtureSelfId;
        var streams = ReplayStreams.Build(files, allPlayers, me);
        var advisor = new UpgradeAdvisor(pack);
        var scorer = new CardScorer(pack);
        var averages = new Dictionary<string, double>(StringComparer.Ordinal);
        double fallback(string character)
        {
            if (averages.TryGetValue(character, out var v)) return v;
            var deltas = pack.Tiers.TryGetValue(character, out var t) ? t.Cards.Values.Select(e => e.UpgradedDelta).Where(d => d > 0).ToArray() : [];
            return averages[character] = deltas.Length > 0 ? deltas.Average() : 0.0;
        }
        var sb = new StringBuilder();
        sb.AppendLine("decision,source,build,character,act,floor,hp,deck,card,copies,chosen,delta,need,base,build_part,combo,core_priority,core_missing,secured,role,commit,gated_commit,core_owned,core_target,arch");
        var decision = 0;
        foreach (var s in streams)
        {
            var run = s.Run;
            DeckTimeline? timeline = null;
            for (var i = 0; i < run.Nodes.Count; i++)
            {
                var node = run.Nodes[i];
                if (node.MapPointType != "rest_site") continue;
                var stats = node.StatsFor(s.Player.Id);
                if (stats is null || !stats.RestSiteChoices.Contains("SMITH") || stats.UpgradedCards.Count == 0) continue;
                timeline ??= DeckTimeline.Build(run, s.Player.Id);
                var state = timeline.StateBefore(i);
                var profile = DeckAnalyzer.Analyze(state, pack);
                var upgraded = pack.Canonical(stats.UpgradedCards[0]);
                var candidates = advisor.Rank(state, profile);
                if (!candidates.Any(c => c.Card.Id == upgraded)) continue;
                decision++;
                var source = Path.GetFileNameWithoutExtension(s.SourcePath);
                foreach (var c in candidates)
                {
                    // delta / need: the pack's hand-set upgradedDelta and the role-need part at its pre-calibration share, so the
                    // export does not depend on the weights it is used to fit.
                    var entry = pack.FindTier(state.Character, c.Card.Id);
                    var delta = entry is not null && entry.UpgradedDelta > 0 ? entry.UpgradedDelta : fallback(state.Character);
                    var eval = scorer.Score(state, profile, c.Card);
                    double V(string key) => eval.Score.ValueOf(key);
                    var need = (V(ComponentKeys.Need) + V(ComponentKeys.Situation)) * Math.Max(0, pack.Weights.UpgradedSupplyMultiplier - 1);

                    // The card's place in the deck's builds, exactly as the advisor reads it.
                    string role = "", arch = "";
                    double commit = 0, gated = 0;
                    int coreOwned = 0, coreTarget = 0;
                    if (UpgradeAdvisor.BuildRoleOf(profile, c.Card.Id) is { } b)
                    {
                        role = b.Role;
                        arch = b.Status.Archetype.Id;
                        commit = b.Commit;
                        gated = CardScorer.GatedCommit(b.Status, profile, pack, state.Act);
                        (coreOwned, coreTarget) = ArchetypeDetector.CoreProgress(b.Status.Archetype, pack, profile.Copies);
                    }

                    sb.Append(Inv, $"{decision},{source},{run.BuildId},{state.Character},{state.Act},{node.Floor},{state.HpRatio:0.###},{state.Deck.Count},");
                    sb.Append(Inv, $"{c.Card.Id},{c.Copies},{(c.Card.Id == upgraded ? 1 : 0)},{delta:0.###},{need:0.###},");
                    sb.Append(Inv, $"{V(ComponentKeys.Base):0.###},{V(ComponentKeys.Build):0.###},{V(ComponentKeys.Combo):0.###},{V(ComponentKeys.CorePriority):0.###},");
                    sb.Append(Inv, $"{V(ComponentKeys.CoreMissing):0.###},{V(ComponentKeys.SecuredEssential):0.###},{role},{commit:0.###},{gated:0.###},{coreOwned},{coreTarget},{arch}");
                    sb.AppendLine();
                }
            }
        }
        return sb.ToString();
    }
}
