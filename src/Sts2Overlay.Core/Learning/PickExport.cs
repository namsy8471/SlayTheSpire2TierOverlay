using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Learning;

/// <summary>One option on a pick screen: the offered card with the engine's score split into components.</summary>
public sealed record PickOption(string Card, int Upgrade, int Copies, double Total, IReadOnlyDictionary<string, double> Components);

/// <summary>
/// One card reward screen of a recorded run, as training data for a learned pick model: the state right before the
/// screen, every offered card with the engine's score components, the engine's skip score, and what the player took.
/// </summary>
public sealed record PickScreen
{
    public required string RunId { get; init; }
    public required string Character { get; init; }
    public required string BuildId { get; init; }
    public int Ascension { get; init; }
    public bool Win { get; init; }

    /// <summary>Map points the run visited (= floor reached).</summary>
    public int FloorsReached { get; init; }

    /// <summary>Highest act index (1-based) the run entered.</summary>
    public int ActsReached { get; init; }

    public int NodeIndex { get; init; }
    public int Group { get; init; }
    public required string Context { get; init; }
    public int Act { get; init; }
    public int Floor { get; init; }
    public int Hp { get; init; }
    public int MaxHp { get; init; }
    public int Gold { get; init; }

    /// <summary>Deck right before the screen, see <see cref="PickExport.CardToken"/>.</summary>
    public required IReadOnlyList<string> Deck { get; init; }

    public required IReadOnlyList<string> Relics { get; init; }
    public required IReadOnlyList<PickOption> Options { get; init; }
    public double SkipTotal { get; init; }
    public required IReadOnlyDictionary<string, double> SkipComponents { get; init; }

    /// <summary>Index into <see cref="Options"/> of the card the player took, −1 for a skip.</summary>
    public int Pick { get; init; }

    /// <summary>Index into <see cref="Options"/> of the engine's recommendation, −1 when it recommends Skip.</summary>
    public int EngineBest { get; init; }
}

/// <summary>
/// One fight of a recorded run (monster, elite or boss room) with the deck that went in and what it cost, for checking
/// a combat simulator against real outcomes.
/// </summary>
public sealed record FightRecord
{
    public required string RunId { get; init; }
    public required string Character { get; init; }
    public required string BuildId { get; init; }
    public int Ascension { get; init; }
    public bool Win { get; init; }
    public int NodeIndex { get; init; }
    public int Act { get; init; }
    public int Floor { get; init; }

    /// <summary>monster | elite | boss</summary>
    public required string RoomType { get; init; }

    /// <summary>Encounter model id, e.g. <c>ENCOUNTER.X</c>.</summary>
    public string? Encounter { get; init; }

    public required IReadOnlyList<string> Monsters { get; init; }
    public int Turns { get; init; }
    public int HpBefore { get; init; }
    public int MaxHpBefore { get; init; }

    /// <summary>HP lost at this map point (the fight; a "?" fight node can add event damage).</summary>
    public int DamageTaken { get; init; }

    public int HpAfter { get; init; }

    /// <summary>The run ended here, killed by this encounter.</summary>
    public bool Died { get; init; }

    public required IReadOnlyList<string> Deck { get; init; }
    public required IReadOnlyList<string> Relics { get; init; }
}

/// <summary>Why a run or a screen was left out of an export, counted for the summary.</summary>
public sealed class PickExportStats
{
    public int Runs { get; set; }
    public int RunsSkippedMultiplayer { get; set; }
    public int RunsSkippedAbandoned { get; set; }
    public int Screens { get; set; }
    public int ScreensSkippedShop { get; set; }
    public int ScreensSkippedSingleCard { get; set; }
    public int ScreensSkippedMultiPick { get; set; }
    public int Fights { get; set; }

    /// <summary>
    /// Exported runs whose deck replay had nothing to explain away. The replay walks back from the recorded final deck, so a
    /// change the file does not record (some relic/event effects) shows up as a <see cref="DeckTimeline.Warnings"/> entry and
    /// the decks before it may be off by that card.
    /// </summary>
    public int RunsCleanReplay { get; set; }

    public int ReplayWarnings { get; set; }

    /// <summary>Replay warnings by kind (floor numbers and ids taken out).</summary>
    public Dictionary<string, int> ReplayWarningKinds { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Turns recorded runs into training rows for the learned card policy (docs/research/LEARNED_CARD_POLICY_PLAN.md step 1).
/// Rows hold card/relic ids and numbers only: the run id is the file name the caller passes (never a Steam id).
/// </summary>
public sealed class PickExport
{
    private readonly DataPack _pack;
    private readonly RecommendationEngine _engine;

    public PickExport(DataPack pack)
    {
        _pack = pack ?? throw new ArgumentNullException(nameof(pack));
        _engine = new RecommendationEngine(pack);
    }

    public PickExportStats Stats { get; } = new();

    /// <summary>
    /// <c>ID</c>, <c>ID+n</c> for an upgraded card, <c>…#ENCHANTMENT.X:amount</c> for an enchanted one. Ids are canonical
    /// (pack aliases applied).
    /// </summary>
    public string CardToken(DeckCard c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var token = _pack.Canonical(c.Card.Id);
        if (c.Card.Upgrade > 0)
        {
            token += "+" + c.Card.Upgrade.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (c.EnchantmentId is not null)
        {
            token += "#" + c.EnchantmentId + ":" + c.EnchantmentAmount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return token;
    }

    /// <summary>
    /// Every usable card reward screen and every fight of a single-player, finished run. Multiplayer and abandoned runs
    /// give nothing (counted in <see cref="Stats"/>). Shop screens (several buys under a gold limit), single-card offers
    /// and groups with more than one pick (two reward screens the file cannot split) are left out.
    /// </summary>
    public (IReadOnlyList<PickScreen> Screens, IReadOnlyList<FightRecord> Fights) Export(string runId, RunHistory run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Stats.Runs++;
        if (run.IsMultiplayer || run.Players.Count == 0)
        {
            Stats.RunsSkippedMultiplayer++;
            return ([], []);
        }

        if (run.WasAbandoned)
        {
            Stats.RunsSkippedAbandoned++;
            return ([], []);
        }

        var player = run.Players[0];
        var timeline = DeckTimeline.Build(run, player.Id);
        Stats.ReplayWarnings += timeline.Warnings.Count;
        foreach (var w in timeline.Warnings)
        {
            var kind = System.Text.RegularExpressions.Regex.Replace(w, @"floor \d+: |\(floor \d+\)|[A-Z_]+\.[A-Z0-9_]+(\+\d+)?", "").Trim();
            Stats.ReplayWarningKinds[kind] = Stats.ReplayWarningKinds.GetValueOrDefault(kind) + 1;
        }

        if (timeline.Warnings.Count == 0)
        {
            Stats.RunsCleanReplay++;
        }

        var floorsReached = run.Nodes.Count;
        var actsReached = run.Nodes.Count == 0 ? 0 : run.Nodes.Max(n => n.Act);

        var screens = new List<PickScreen>();
        foreach (var d in DecisionExtractor.Extract(run, player.Id, timeline))
        {
            if (d.Context == OfferContext.Shop)
            {
                Stats.ScreensSkippedShop++;
                continue;
            }

            if (d.Offered.Count < 2)
            {
                Stats.ScreensSkippedSingleCard++;
                continue;
            }

            if (d.Picked.Count > 1)
            {
                Stats.ScreensSkippedMultiPick++;
                continue;
            }

            screens.Add(Screen(runId, run, d, floorsReached, actsReached));
        }

        Stats.Screens += screens.Count;
        var fights = Fights(runId, run, timeline);
        Stats.Fights += fights.Count;
        return (screens, fights);
    }

    private PickScreen Screen(string runId, RunHistory run, CardDecision d, int floorsReached, int actsReached)
    {
        var state = d.StateBefore;
        var profile = DeckAnalyzer.Analyze(state, _pack);
        var eval = _engine.EvaluateOffer(state, profile, d.Offered, d.Context);

        // The engine returns one evaluation per offered card in offer order; match by position so duplicates stay apart.
        var options = new List<PickOption>(d.Offered.Count);
        for (var i = 0; i < d.Offered.Count; i++)
        {
            var e = eval.Cards[i];
            var id = _pack.Canonical(e.Card.Id);
            options.Add(new PickOption(id, e.Card.Upgrade, profile.Copies.GetValueOrDefault(id), e.Total, ComponentsOf(e.Score)));
        }

        var pick = d.Picked.Count == 0 ? -1 : IndexOf(d.Offered, d.Picked[0]);
        var best = eval.Best is { } b ? IndexOf(d.Offered, b) : -1;
        return new PickScreen
        {
            RunId = runId,
            Character = state.Character,
            BuildId = run.BuildId,
            Ascension = run.Ascension,
            Win = run.Win,
            FloorsReached = floorsReached,
            ActsReached = actsReached,
            NodeIndex = d.NodeIndex,
            Group = d.GroupIndex,
            Context = d.Context.ToString(),
            Act = state.Act,
            Floor = state.Floor,
            Hp = state.Hp,
            MaxHp = state.MaxHp,
            Gold = state.Gold,
            Deck = state.Deck.Select(CardToken).ToArray(),
            Relics = state.Relics.ToArray(),
            Options = options,
            SkipTotal = eval.Skip.Total,
            SkipComponents = ComponentsOf(eval.Skip),
            Pick = pick,
            EngineBest = best,
        };
    }

    private List<FightRecord> Fights(string runId, RunHistory run, DeckTimeline timeline)
    {
        var fights = new List<FightRecord>();
        var playerId = timeline.PlayerId;
        for (var i = 0; i < run.Nodes.Count; i++)
        {
            var node = run.Nodes[i];
            var room = node.Rooms.FirstOrDefault(r => r.RoomType is "monster" or "elite" or "boss");
            var stats = node.StatsFor(playerId);
            if (room is null || stats is null)
            {
                continue;
            }

            var state = timeline.StateBefore(i);
            var last = i == run.Nodes.Count - 1;
            fights.Add(new FightRecord
            {
                RunId = runId,
                Character = state.Character,
                BuildId = run.BuildId,
                Ascension = run.Ascension,
                Win = run.Win,
                NodeIndex = i,
                Act = node.Act,
                Floor = node.Floor,
                RoomType = room.RoomType,
                Encounter = room.ModelId,
                Monsters = room.MonsterIds,
                Turns = room.TurnsTaken,
                HpBefore = state.Hp,
                MaxHpBefore = state.MaxHp,
                DamageTaken = stats.DamageTaken,
                HpAfter = stats.CurrentHp,
                Died = last && !run.Win && room.ModelId is not null && room.ModelId == run.KilledByEncounter,
                Deck = state.Deck.Select(CardToken).ToArray(),
                Relics = state.Relics.ToArray(),
            });
        }

        return fights;
    }

    private static Dictionary<string, double> ComponentsOf(ScoreBreakdown score)
    {
        // Several components can share a key (e.g. one build part per archetype): sum them, as ScoreBreakdown.ValueOf does.
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var c in score.Components)
        {
            if (Math.Abs(c.Value) > 1e-9)
            {
                result[c.Key] = result.GetValueOrDefault(c.Key) + c.Value;
            }
        }

        return result;
    }

    private static int IndexOf(IReadOnlyList<CardRef> offered, CardRef card)
    {
        for (var i = 0; i < offered.Count; i++)
        {
            if (offered[i] == card)
            {
                return i;
            }
        }

        return -1;
    }
}
