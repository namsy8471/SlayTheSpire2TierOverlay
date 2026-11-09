using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Replay;

/// <summary>
/// One player's numbers at one visited map point of a <c>.run</c> file: the raw material for measuring the path
/// advisor's weights (<see cref="PathStats"/>). HP and gold "before" come from the previous map point.
/// </summary>
public sealed record PathFloor
{
    /// <summary>Identifies the run (seed); both co-op players' copies of a run share it.</summary>
    public required string RunKey { get; init; }

    public long PlayerId { get; init; }
    public string Character { get; init; } = "";

    /// <summary>Players in the run (1 = single player).</summary>
    public int Players { get; init; } = 1;

    public bool Multiplayer => Players > 1;
    public int Ascension { get; init; }
    public string BuildId { get; init; } = "";

    /// <summary>1-based act.</summary>
    public int Act { get; init; }

    /// <summary>0-based index inside the act (0 = the act's ancient).</summary>
    public int IndexInAct { get; init; }

    /// <summary>0-based index into <see cref="RunHistory.Nodes"/>.</summary>
    public int NodeIndex { get; init; }

    public MapNodeType Type { get; init; }

    /// <summary>What the point turned into, e.g. <c>event</c>, <c>monster</c>, <c>event+monster</c> (room types joined by +).</summary>
    public string Resolved { get; init; } = "";

    /// <summary>Model id of the event room (e.g. <c>EVENT.X</c>), if any.</summary>
    public string? EventId { get; init; }

    /// <summary>Encounter id of the hallway fight here (e.g. <c>ENCOUNTER.NIBBITS_WEAK</c>), if any.</summary>
    public string? MonsterEncounterId { get; init; }

    /// <summary>
    /// True when the hallway fight came from the act's easy pool (the game's weak encounters all end in
    /// <c>_WEAK</c>), false for the hard pool, null when there was no hallway fight or its id is unknown.
    /// </summary>
    public bool? EasyPool => MonsterEncounterId is { Length: > 0 } id ? id.EndsWith("_WEAK", StringComparison.Ordinal) : null;

    public int HpBefore { get; init; }
    public int MaxHpBefore { get; init; }
    public int HpAfter { get; init; }
    public int MaxHpAfter { get; init; }
    public int DamageTaken { get; init; }
    public int HpHealed { get; init; }
    public int GoldBefore { get; init; }
    public int GoldAfter { get; init; }
    public int GoldGained { get; init; }
    public int GoldSpent { get; init; }

    /// <summary>Gold lost to events plus gold stolen by enemies.</summary>
    public int GoldLost { get; init; }

    public int PotionsGained { get; init; }
    public int RelicsGained { get; init; }
    public int CardsGained { get; init; }

    /// <summary>A card reward was shown here.</summary>
    public bool CardReward { get; init; }

    /// <summary>A card was taken from the card reward.</summary>
    public bool CardPicked { get; init; }

    public IReadOnlyList<string> RestChoices { get; init; } = [];

    /// <summary>The run was lost (not abandoned) here: last map point and HP 0.</summary>
    public bool Died { get; init; }

    /// <summary>Deck strength 0..1 right before this point (<see cref="PathScorer.ComputeDeckStrength"/>); NaN when no pack was given.</summary>
    public double DeckStrength { get; init; } = double.NaN;

    /// <summary>HP lost in this room as a fraction of max HP on entry. On a death this is censored at the HP on entry.</summary>
    public double DamageFraction => DamageTaken / (double)Math.Max(1, MaxHpBefore);

    public double HealFraction => HpHealed / (double)Math.Max(1, MaxHpBefore);

    public double HpRatioBefore => MaxHpBefore <= 0 ? 1 : Math.Clamp(HpBefore / (double)MaxHpBefore, 0, 1);

    public double HpRatioAfter => MaxHpAfter <= 0 ? 1 : Math.Clamp(HpAfter / (double)MaxHpAfter, 0, 1);
}

/// <summary>Turns <c>.run</c> files into <see cref="PathFloor"/> rows.</summary>
public static class PathFloorExtractor
{
    /// <summary>
    /// Drops duplicate copies of the same run. In co-op every player's history folder gets its own copy of the run
    /// (start times differ by a few seconds), so the key is the seed plus the sorted player ids. The first copy wins.
    /// </summary>
    public static IReadOnlyList<RunHistory> Distinct(IEnumerable<RunHistory> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RunHistory>();
        foreach (var run in runs)
        {
            var key = KeyOf(run);
            if (seen.Add(key)) result.Add(run);
        }
        return result;
    }

    /// <summary>Seed + sorted player ids.</summary>
    public static string KeyOf(RunHistory run)
        => run.Seed + "|" + string.Join(",", run.Players.Select(p => p.Id).Order());

    /// <summary>
    /// Every player's row at every map point. With a <paramref name="pack"/> the deck strength before each point is
    /// filled in (deck rebuilt by <see cref="DeckTimeline"/>), otherwise it is NaN.
    /// </summary>
    public static IReadOnlyList<PathFloor> Extract(RunHistory run, DataPack? pack = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        var rows = new List<PathFloor>();
        var key = KeyOf(run);
        var lastIndex = run.Nodes.Count - 1;
        var lost = !run.Win && !run.WasAbandoned;

        foreach (var player in run.Players)
        {
            var timeline = pack is null ? null : DeckTimeline.Build(run, player.Id);
            PlayerNodeStats? previous = null;
            for (var i = 0; i < run.Nodes.Count; i++)
            {
                var node = run.Nodes[i];
                var s = node.StatsFor(player.Id);
                if (s is null) continue;

                var (hpBefore, maxBefore, goldBefore) = previous is { } p
                    ? (p.CurrentHp, p.MaxHp, p.CurrentGold)
                    : (s.CurrentHp + s.DamageTaken - s.HpHealed,
                       s.MaxHp - s.MaxHpGained + s.MaxHpLost,
                       s.CurrentGold - s.GoldGained + s.GoldSpent + s.GoldLost + s.GoldStolen);

                var strength = double.NaN;
                if (timeline is not null && pack is not null)
                {
                    var state = timeline.StateBefore(i);
                    strength = PathScorer.ComputeDeckStrength(DeckAnalyzer.Analyze(state, pack), pack.Weights.Path);
                }

                rows.Add(new PathFloor
                {
                    RunKey = key,
                    PlayerId = player.Id,
                    Character = player.Character,
                    Players = run.Players.Count,
                    Ascension = run.Ascension,
                    BuildId = run.BuildId,
                    Act = node.Act,
                    IndexInAct = node.IndexInAct,
                    NodeIndex = i,
                    Type = MapGraph.ParseType(node.MapPointType),
                    Resolved = string.Join("+", node.Rooms.Select(r => r.RoomType).Where(t => t.Length > 0).Distinct()),
                    EventId = node.Rooms.FirstOrDefault(r => r.RoomType == "event")?.ModelId,
                    MonsterEncounterId = node.Rooms.FirstOrDefault(r => r.RoomType == "monster")?.ModelId,
                    HpBefore = hpBefore,
                    MaxHpBefore = maxBefore,
                    HpAfter = s.CurrentHp,
                    MaxHpAfter = s.MaxHp,
                    DamageTaken = s.DamageTaken,
                    HpHealed = s.HpHealed,
                    GoldBefore = goldBefore,
                    GoldAfter = s.CurrentGold,
                    GoldGained = s.GoldGained,
                    GoldSpent = s.GoldSpent,
                    GoldLost = s.GoldLost + s.GoldStolen,
                    PotionsGained = s.PotionChoices.Count(c => c.WasPicked) + s.BoughtPotions.Count,
                    RelicsGained = s.RelicChoices.Count(c => c.WasPicked) + s.BoughtRelics.Count,
                    CardsGained = s.CardsGained.Count,
                    CardReward = s.CardChoices.Count > 0,
                    CardPicked = s.CardChoices.Any(c => c.WasPicked),
                    RestChoices = s.RestSiteChoices,
                    Died = lost && i == lastIndex && s.CurrentHp <= 0,
                    DeckStrength = strength,
                });
                previous = s;
            }
        }
        return rows;
    }
}
