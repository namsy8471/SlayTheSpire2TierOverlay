using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Log;
using Sts2Overlay.GameState.Save;

namespace Sts2Overlay.GameState.Tracking;

public sealed record TrackerOptions
{
    /// <summary>Where to read from. Null = no file IO (tests feed <see cref="GameStateTracker.ProcessLines"/> / <see cref="GameStateTracker.OnSave"/>).</summary>
    public GamePaths? Paths { get; init; }

    /// <summary>Force our co-op net id (otherwise: log "Our player id", then the Steam ID folder name).</summary>
    public ulong? LocalNetIdOverride { get; init; }

    public TimeSpan LogPollInterval { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan SaveDebounce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Replay the existing godot.log on start (needed to rebuild a run that is already in progress).</summary>
    public bool ReplayExistingLog { get; init; } = true;

    public StartingLoadouts? Loadouts { get; init; }
    public Func<DateTime>? Clock { get; init; }
}

/// <summary>
/// Combines the save file (authoritative but only written at a few moments) and godot.log (instant but
/// partial) into one live <see cref="TrackedRun"/>.
///
/// The model is "base state + patches":
///   snapshot = base (latest matching save, or a log-only rebuild for a co-op guest)
///            + every deck/relic/potion/gold log event that happened AFTER that save was written.
/// Which log events are "after the save" is decided by the log itself: the game logs
/// <c>Wrote N bytes to path=...current_run.save</c> right after each save, and N equals the file size the
/// watcher read. Log events behind that line are already inside the save; events after it are re-applied.
/// Everything is recomputed from scratch on each change, so the order in which the watcher and the
/// tailer deliver (they are on different threads/timers) never leaves a permanent error.
/// </summary>
public sealed class GameStateTracker : IDisposable
{
    private const double PityReset = -0.05, PityMax = 0.4;
    private const int ScarcityAscension = 7; // AscensionLevel.Scarcity

    private readonly TrackerOptions _options;
    private readonly StartingLoadouts _loadouts;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();

    private SaveWatcher? _watcher;
    private LogTailer? _tailer;

    // --- log-derived state (guarded by _gate) -----------------------------------------------------
    private long _seq;                                   // log line counter: total order of log events
    private readonly Dictionary<string, SaveSnapshot> _saves = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Patch> _patches = [];          // deck/relic/potion/gold changes since run start
    private readonly List<SaveMark> _saveMarks = [];     // "Wrote N bytes ... current_run*.save" lines
    private readonly List<CardRarityRolledEvent> _rollBatch = [];
    private LogRun? _logRun;
    private ulong? _logLocalId, _profileSteamId;
    private int _logAct, _logFloor;
    private string? _logActId;
    private bool _moveSinceRoom;
    private RoomKind? _room;
    private ScreenHint _screen;
    private CombatKind? _lastKind;
    private string? _lastEncounter;
    private string? _lastRestOption;
    private bool _pendingRemovalReward;
    private double? _pityLog;
    private long _pityLogSeq = -1;
    private IReadOnlyList<RarityRoll> _lastRolls = [];
    private MapCoord? _voteCoord;
    private long _voteSeq = -1;
    private RunOutcome _outcome;
    private TrackedRun? _current;

    public GameStateTracker(TrackerOptions? options = null)
    {
        _options = options ?? new TrackerOptions();
        _loadouts = _options.Loadouts ?? StartingLoadouts.Embedded;
        _clock = _options.Clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Latest snapshot, or null when no run is known (no save, no run in the log).</summary>
    public TrackedRun? Current { get { lock (_gate) return _current; } }

    /// <summary>Raised (on a background thread) after any change that can affect the snapshot.</summary>
    public event EventHandler<TrackedRun?>? Changed;

    /// <summary>Every parsed log event, for diagnostics (StateProbe prints these).</summary>
    public event EventHandler<LogEvent>? LogEventParsed;

    /// <summary>Human-readable notes (save read errors, log rotation ...).</summary>
    public event EventHandler<string>? Diagnostic;

    /// <summary>Starts the save watcher and the log tailer using <see cref="TrackerOptions.Paths"/>.</summary>
    public void Start()
    {
        GamePaths? paths = _options.Paths;
        if (paths?.SavesDirectory is { } saves && Directory.Exists(saves))
        {
            _watcher = new SaveWatcher(saves, () => _options.LocalNetIdOverride ?? paths.SteamId ?? _logLocalId, _options.SaveDebounce);
            _watcher.SaveChanged += (_, s) => OnSave(s);
            _watcher.SaveRemoved += (_, p) => OnSaveRemoved(p);
            _watcher.SaveError += (_, e) => Diagnostic?.Invoke(this, $"save read failed: {e.Path}: {e.Error.Message}");
            _watcher.Start();
        }
        else Diagnostic?.Invoke(this, "saves folder not found; save watching disabled");

        if (paths?.LogFile is { } log)
        {
            _tailer = new LogTailer(log, startAtEnd: !_options.ReplayExistingLog);
            _tailer.Rotated += (_, _) => OnLogRotated();
            _tailer.LinesRead += (_, lines) => ProcessLines(lines);
            _tailer.Start(_options.LogPollInterval);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _tailer?.Dispose();
    }

    // =============================================================================================
    // Inputs
    // =============================================================================================

    /// <summary>A save file was (re)read.</summary>
    public void OnSave(SaveSnapshot snapshot)
    {
        TrackedRun? run;
        lock (_gate)
        {
            _saves[snapshot.Path] = snapshot;
            run = Recompute();
        }
        Changed?.Invoke(this, run);
    }

    /// <summary>A save file was deleted (the game removes it when a run ends).</summary>
    public void OnSaveRemoved(string path)
    {
        TrackedRun? run;
        lock (_gate)
        {
            if (!_saves.Remove(path)) return;
            run = Recompute();
        }
        Changed?.Invoke(this, run);
    }

    /// <summary>godot.log was replaced (game relaunched): forget everything learned from the old log.</summary>
    public void OnLogRotated()
    {
        TrackedRun? run;
        lock (_gate)
        {
            ResetRun(null);
            _saveMarks.Clear();
            _logLocalId = _profileSteamId = null;
            _screen = ScreenHint.Unknown;
            run = Recompute();
        }
        Diagnostic?.Invoke(this, "godot.log rotated (game restarted)");
        Changed?.Invoke(this, run);
    }

    /// <summary>Feeds raw log lines in order (the tailer calls this; tests call it directly).</summary>
    public void ProcessLines(IEnumerable<string> lines)
    {
        var parsed = new List<LogEvent>();
        TrackedRun? run;
        bool dirty = false;
        lock (_gate)
        {
            foreach (string line in lines)
            {
                _seq++;
                if (LogLineParser.Parse(line) is not { } e) continue;
                parsed.Add(e);
                // Rarity lines come in a burst (one per card slot). Batch them, and interpret the batch as soon
                // as any other event arrives (co-op logs every player's rolls back to back).
                if (e is CardRarityRolledEvent r) { _rollBatch.Add(r); dirty = true; continue; }
                FlushRolls();
                dirty |= Apply(e);
            }
            FlushRolls(); // end of this read: the burst is complete (the game flushes the log once per ~1 s)
            run = dirty ? Recompute() : _current;
        }
        foreach (LogEvent e in parsed) LogEventParsed?.Invoke(this, e);
        if (dirty) Changed?.Invoke(this, run);
    }

    public void ProcessLine(string line) => ProcessLines([line]);

    // =============================================================================================
    // Log event handling (all under _gate)
    // =============================================================================================

    /// <returns>True if the event can change the snapshot.</returns>
    private bool Apply(LogEvent e)
    {
        switch (e)
        {
            case RunStartedEvent s:
                ResetRun(new LogRun(s.Seed, s.IsMultiplayer, s.Ascension, s.Players, s.Character, Continued: false));
                _moveSinceRoom = true; // the first room (Neow) counts as floor 1
                return true;

            case RunContinuedEvent c:
                // Act/floor are unknown from the log here; the save (loaded at continue) provides them.
                ResetRun(new LogRun(null, null, null, [], c.Character, Continued: true));
                return true;

            case LocalPlayerIdEvent id:
                if (id.FromProfilePath) _profileSteamId = id.NetId;
                else if (id.NetId > 1) _logLocalId = id.NetId; // "Local player 1 is ready" = single player, not an id
                return false;

            case ActEnteredEvent a:
                if (a.ActId != _logActId) { _logAct++; _logActId = a.ActId; _moveSinceRoom = true; }
                return true;

            case MapVoteEvent v:
                _moveSinceRoom = true;
                if (v.Generation > 0) _logAct = v.Generation; // map generation count = act number
                if (IsMe(v.PlayerId))
                {
                    _voteCoord = v.Coord;
                    _voteSeq = _seq;
                    _screen = ScreenHint.Map;
                }
                return true;

            case RoomEnteredEvent room:
                // An event that starts a fight preloads 'Event Room' then 'Combat Room' on the same floor;
                // only count a floor when a map move (vote / act start) happened since the last room.
                if (_moveSinceRoom) { _logFloor++; _moveSinceRoom = false; }
                _room = room.Room;
                _lastRestOption = null;
                _pendingRemovalReward = false;
                _screen = room.Room switch
                {
                    RoomKind.Combat => ScreenHint.Combat,
                    RoomKind.Merchant => ScreenHint.ShopLikely,
                    RoomKind.RestSite => ScreenHint.RestSite,
                    RoomKind.Event => ScreenHint.Event,
                    RoomKind.Treasure => ScreenHint.Treasure,
                    _ => ScreenHint.Unknown,
                };
                return true;

            case MainMenuEvent:
                _screen = ScreenHint.MainMenu;
                return true;

            case CombatStartedEvent cs when !cs.VisualOnly:
                _lastEncounter = cs.EncounterId;
                _lastKind = cs.Kind != CombatKind.Unknown ? cs.Kind : KindFromMap();
                _screen = ScreenHint.Combat;
                return true;

            case CombatWonEvent w:
                _lastEncounter = w.EncounterId;
                if (w.Kind != CombatKind.Unknown) _lastKind = w.Kind;
                _screen = ScreenHint.RewardScreenLikely; // rewards are rolled right after this line
                return true;

            case CombatLostEvent:
                End(RunOutcome.Lost);
                return true;
            case RunWonEvent:
                End(RunOutcome.Won);
                return true;
            case RunAbandonedEvent:
                End(RunOutcome.Abandoned);
                return true;
            case RunHistorySavedEvent:
                End(_outcome == RunOutcome.InProgress ? RunOutcome.Ended : _outcome);
                return true;

            case RunSaveWrittenEvent w:
                _saveMarks.Add(new SaveMark(_seq, w.FileName, w.Bytes));
                if (_saveMarks.Count > 64) _saveMarks.RemoveAt(0);
                return true;

            // ---- patches: only for the local player ----------------------------------------------
            case CardObtainedEvent c when IsMe(c.PlayerId):
                AddPatch(PatchKind.AddCard, c.CardId, 0);
                if (_screen == ScreenHint.RewardScreenLikely && !c.Special) _screen = ScreenHint.Map; // card picked
                return true;
            case GoldObtainedEvent g when IsMe(g.PlayerId):
                AddPatch(PatchKind.AddGold, null, g.Amount);
                return true;
            case PotionObtainedEvent p when IsMe(p.PlayerId):
                AddPatch(PatchKind.AddPotion, p.PotionId, 0);
                return true;
            case RelicObtainedEvent r when IsMe(r.PlayerId):
                AddPatch(PatchKind.AddRelic, r.RelicId, 0);
                return true;
            case PotionUsedEvent u when IsMe(u.PlayerId):
                AddPatch(PatchKind.RemovePotion, u.PotionId, 0);
                return true;
            case CardRemovalRewardEvent rr when IsMe(rr.PlayerId):
                _pendingRemovalReward = true;
                return false;
            case RestSiteOptionEvent ro when IsMe(ro.PlayerId) && ro.Success:
                _lastRestOption = ro.Option;
                return false;
            case CardsChosenEvent ch when IsMe(ch.PlayerId) && ch.CardIds.Count > 0:
                return ApplyChosen(ch);

            default:
                return false;
        }
    }

    /// <summary>
    /// "Player N chose cards [X]" is logged by every card-select screen, so its meaning comes from context:
    /// shop → removal (and the removal price goes up), rest site → smith upgrade, a "card removal" reward → removal.
    /// In combat (hand picks) and events (could be remove/transform/enchant...) we don't guess: the next save
    /// will carry the truth.
    /// </summary>
    private bool ApplyChosen(CardsChosenEvent ch)
    {
        PatchKind? kind = null;
        if (_pendingRemovalReward) { kind = PatchKind.RemoveCard; _pendingRemovalReward = false; }
        else if (_room == RoomKind.Merchant) kind = PatchKind.ShopRemoveCard;
        else if (_room == RoomKind.RestSite && (_lastRestOption is null || _lastRestOption == "SMITH")) kind = PatchKind.UpgradeCard;
        if (kind is not { } k) return false;
        foreach (string card in ch.CardIds) AddPatch(k, card, 0);
        return true;
    }

    private void AddPatch(PatchKind kind, string? id, int amount) =>
        _patches.Add(new Patch(kind, id, amount, _seq));

    private void End(RunOutcome outcome)
    {
        _outcome = outcome;
        _screen = ScreenHint.RunEnded;
    }

    private void ResetRun(LogRun? run)
    {
        _logRun = run;
        _patches.Clear();
        _rollBatch.Clear();
        _logAct = 0;
        _logFloor = 0;
        _logActId = null;
        _moveSinceRoom = false;
        _room = null;
        _lastKind = null;
        _lastEncounter = null;
        _lastRestOption = null;
        _pendingRemovalReward = false;
        _pityLog = null;
        _pityLogSeq = -1;
        _lastRolls = [];
        _voteCoord = null;
        _voteSeq = -1;
        _outcome = RunOutcome.InProgress;
        _screen = ScreenHint.Unknown;
    }

    /// <summary>Encounter ids without a known suffix (event fights): use the node type of where we are.</summary>
    private CombatKind KindFromMap()
    {
        MapGraph? map = ChooseSave()?.Save.Map;
        if (map?.Current is { } c && map.Nodes.TryGetValue(c, out MapNode? node))
            return node.Type switch { MapNodeType.Elite => CombatKind.Elite, MapNodeType.Boss => CombatKind.Boss, _ => CombatKind.Normal };
        return CombatKind.Normal;
    }

    // =============================================================================================
    // Who am I?
    // =============================================================================================

    private bool IsMultiplayer() =>
        _logRun?.IsMultiplayer ?? (ChooseSave()?.Save.PlayerNetIds.Count > 1);

    /// <summary>1 in single player (the game's fixed local net id); the Steam ID in co-op.</summary>
    private ulong MyId()
    {
        if (!IsMultiplayer()) return 1;
        return _options.LocalNetIdOverride ?? _logLocalId ?? _profileSteamId ?? _options.Paths?.SteamId
               ?? ChooseSave()?.Save.MyNetId ?? 0;
    }

    private bool IsMe(ulong? playerId) => playerId is null || playerId == MyId();

    private IReadOnlyList<ulong> PlayerOrder() =>
        _logRun is { Players.Count: > 0 } r ? r.Players.Select(p => p.NetId).ToList()
        : ChooseSave()?.Save.PlayerNetIds ?? [];

    // =============================================================================================
    // Rarity rolls → my rolls + pity
    // =============================================================================================

    private void FlushRolls()
    {
        if (_rollBatch.Count == 0) return;
        // Shop rolls use RollWithoutChangingFutureOdds: they don't move the pity counter. Ignore them.
        var rolls = _rollBatch.Where(r => r.Source != RaritySource.Shop).ToList();
        _rollBatch.Clear();
        if (rolls.Count == 0) return;

        List<CardRarityRolledEvent> mine = SelectMine(rolls);
        _lastRolls = mine.Select(r => new RarityRoll(r.Roll, r.Need, r.Offset, r.IsRare, r.Source)).ToList();
        _pityLog = After(mine[^1]);
        _pityLogSeq = _seq;
    }

    /// <summary>Pity offset after this roll (CardRarityOdds.Roll): reset on a rare, else grow, capped at 0.4.</summary>
    private double After(CardRarityRolledEvent r)
    {
        if (r.IsRare || r.Source == RaritySource.Boss) return PityReset;
        bool scarcity = r.IsScarcity ?? (Ascension() >= ScarcityAscension);
        return Math.Min(r.Offset + (scarcity ? 0.005 : 0.01), PityMax);
    }

    private int Ascension() => ChooseSave()?.Save.State.Ascension ?? _logRun?.Ascension ?? 0;

    /// <summary>
    /// Single player: all rolls are mine. Co-op: every client logs every player's reward rolls back to back,
    /// so we cut the batch into per-player runs. Each player's offsets form a chain (next = after(prev)), so a
    /// break in the chain starts a new player. We then pick the run that starts at MY known pity value, or,
    /// failing that, the run at my slot index.
    /// </summary>
    private List<CardRarityRolledEvent> SelectMine(List<CardRarityRolledEvent> rolls)
    {
        IReadOnlyList<ulong> order = PlayerOrder();
        int n = order.Count;
        if (n <= 1) return rolls;
        int myIndex = order.ToList().IndexOf(MyId());

        List<List<CardRarityRolledEvent>> segments;
        if (rolls.All(r => r.Source == RaritySource.Boss))
            segments = rolls.Count % n == 0 ? rolls.Chunk(rolls.Count / n).Select(c => c.ToList()).ToList() : [rolls];
        else
        {
            segments = [[rolls[0]]];
            for (int i = 1; i < rolls.Count; i++)
            {
                if (Math.Abs(rolls[i].Offset - After(rolls[i - 1])) > 0.001) segments.Add([]);
                segments[^1].Add(rolls[i]);
            }
        }

        if (KnownPity() is { } known)
        {
            var match = segments.Where(s => Math.Abs(s[0].Offset - known) < 0.001).ToList();
            if (match.Count == 1) return match[0];
        }
        if (myIndex >= 0 && segments.Count == n) return segments[myIndex];
        if (myIndex >= 0 && rolls.Count % n == 0) return rolls.Chunk(rolls.Count / n).ElementAt(myIndex).ToList();
        return rolls;
    }

    /// <summary>Pity before the current batch: the latest log value if newer than the save, else the save's.</summary>
    private double? KnownPity()
    {
        SaveSnapshot? save = ChooseSave();
        if (save == null) return _pityLog ?? PityReset;
        return _pityLogSeq > CutoffSeq(save) ? _pityLog : save.Save.CardRarityOffset;
    }

    // =============================================================================================
    // Snapshot composition
    // =============================================================================================

    /// <summary>
    /// The save that belongs to the run the log is showing. If the log knows the run's seed, only a save with
    /// that seed counts (an old <c>current_run_mp.save</c> from another run must not leak into a single-player
    /// run, and a co-op guest has no save at all). Otherwise (continued run / no log) take the newest save.
    /// </summary>
    private SaveSnapshot? ChooseSave()
    {
        if (_saves.Count == 0) return null;
        if (_logRun is { Seed: { } seed }) return _saves.Values.FirstOrDefault(s => s.Save.Seed == seed);
        return _saves.Values.MaxBy(s => s.FileWriteUtc);
    }

    /// <summary>
    /// Log sequence number up to which events are already inside <paramref name="save"/>:
    /// the latest "Wrote N bytes" line whose N equals the file size. If that line hasn't been tailed yet,
    /// fall back to the latest save line of any size (corrected as soon as the matching line arrives).
    /// -1 = no save line seen since the run (re)started in this log → every patch is newer than the save.
    /// </summary>
    private long CutoffSeq(SaveSnapshot save)
    {
        string name = Path.GetFileName(save.Path);
        for (int i = _saveMarks.Count - 1; i >= 0; i--)
            if (_saveMarks[i].Bytes == save.FileBytes && _saveMarks[i].FileName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return _saveMarks[i].Seq;
        return _saveMarks.Count > 0 ? _saveMarks[^1].Seq : -1;
    }

    private TrackedRun? Recompute()
    {
        _current = Compose();
        return _current;
    }

    private TrackedRun? Compose()
    {
        ulong me = MyId();
        SaveSnapshot? save = ChooseSave();
        RunState state;
        MapGraph? map = null;
        List<PotionSlot> potions;
        int maxPotions = 3;
        long cutoff;
        double? pity;
        bool fromSave = save != null;

        if (save != null)
        {
            state = save.Save.State;
            map = save.Save.Map;
            potions = save.Save.Potions.ToList();
            maxPotions = save.Save.MaxPotionSlots;
            cutoff = CutoffSeq(save);
            pity = _pityLogSeq > cutoff ? _pityLog : save.Save.CardRarityOffset;
            // Keep memory bounded: patches the save already contains are never needed again.
            if (_saveMarks.Any(m => m.Seq == cutoff && m.Bytes == save.FileBytes))
                _patches.RemoveAll(p => p.Seq <= cutoff);
        }
        else if (_logRun is { Continued: false } run && BuildFromLog(run, me) is { } rebuilt)
        {
            state = rebuilt;
            potions = [];
            cutoff = -1; // no save: every logged change since the run started applies
            pity = _pityLog ?? PityReset;
        }
        else
        {
            return null;
        }

        // ---- apply log patches newer than the save -------------------------------------------------
        var deck = state.Deck.ToList();
        var relics = state.Relics.ToList();
        int gold = state.Gold, removals = state.CardRemovalsBought, applied = 0;
        foreach (Patch p in _patches)
        {
            if (p.Seq <= cutoff) continue;
            applied++;
            switch (p.Kind)
            {
                case PatchKind.AddCard:
                    deck.Add(new DeckCard(new CardRef(p.Id!), FloorAdded: state.Floor));
                    break;
                case PatchKind.ShopRemoveCard:
                    removals++;
                    RemoveCard(deck, p.Id!);
                    break;
                case PatchKind.RemoveCard:
                    RemoveCard(deck, p.Id!);
                    break;
                case PatchKind.UpgradeCard:
                    UpgradeCard(deck, p.Id!);
                    break;
                case PatchKind.AddRelic:
                    relics.Add(p.Id!);
                    break;
                case PatchKind.AddGold:
                    gold += p.Amount;
                    break;
                case PatchKind.AddPotion:
                    int slot = Enumerable.Range(0, Math.Max(maxPotions, potions.Count + 1)).First(s => potions.All(x => x.Slot != s));
                    potions.Add(new PotionSlot(p.Id!, slot));
                    break;
                case PatchKind.RemovePotion:
                    int idx = potions.FindIndex(x => x.Id == p.Id);
                    if (idx >= 0) potions.RemoveAt(idx);
                    break;
            }
        }

        state = state with { Deck = deck, Relics = relics, Gold = gold, CardRemovalsBought = removals };

        // Our map vote moves "Current" before the room-entry save lands (useful for the path advisor).
        if (map != null && _voteCoord is { } vote && _voteSeq > cutoff && map.Nodes.ContainsKey(vote))
            map = map with { Current = vote };

        return new TrackedRun
        {
            State = state,
            Map = map,
            Screen = _screen,
            LastCombatKind = _lastKind,
            LastEncounterId = _lastEncounter,
            RarityPityOffset = pity,
            LastRewardRolls = _lastRolls,
            Potions = potions.OrderBy(x => x.Slot).ToList(),
            FromSave = fromSave,
            HpGoldReliable = fromSave,
            DeckApproximate = !fromSave || applied > 0,
            AppliedLogPatches = applied,
            LocalPlayerId = me,
            Seed = save?.Save.Seed ?? _logRun?.Seed,
            Outcome = _outcome,
            Save = save?.Save,
            UpdatedAt = _clock(),
        };
    }

    /// <summary>
    /// Co-op guest (no save file): character + ascension from the "Embarking" line, starting deck/relics/HP/gold
    /// from the embedded starting-decks table, act/floor from counted log events. Log patches are applied on top.
    /// </summary>
    private RunState? BuildFromLog(LogRun run, ulong me)
    {
        string? character = run.Character
                            ?? run.Players.FirstOrDefault(p => p.NetId == me)?.Character
                            ?? run.Players.FirstOrDefault()?.Character;
        if (character == null) return null;
        StartingLoadout? loadout = _loadouts.For(character);
        int ascension = run.Ascension ?? 0;

        var deck = (loadout?.Deck ?? []).Select(id => new DeckCard(new CardRef(id), FloorAdded: 1)).ToList();
        if (ascension >= StartingLoadouts.AscendersBaneLevel)
            deck.Add(new DeckCard(new CardRef(StartingLoadouts.AscendersBane), FloorAdded: 1));

        return new RunState
        {
            Character = character,
            Ascension = ascension,
            Act = Math.Max(_logAct, 1),
            Floor = Math.Max(_logFloor, 1),
            Hp = loadout?.StartingHp ?? 0,
            MaxHp = loadout?.StartingHp ?? 0,
            Gold = loadout?.StartingGold ?? 0,
            Deck = deck,
            Relics = loadout?.Relics.ToList() ?? [],
            IsMultiplayer = run.IsMultiplayer ?? false,
        };
    }

    /// <summary>The log only names the card, not its upgrade/enchantment: remove the plainest copy.</summary>
    private static void RemoveCard(List<DeckCard> deck, string id)
    {
        int best = -1;
        for (int i = 0; i < deck.Count; i++)
        {
            if (deck[i].Id != id) continue;
            if (best < 0 || Plainness(deck[i]) < Plainness(deck[best])) best = i;
        }
        if (best >= 0) deck.RemoveAt(best);
    }

    /// <summary>Smith upgrades one copy: pick the least-upgraded one.</summary>
    private static void UpgradeCard(List<DeckCard> deck, string id)
    {
        int best = -1;
        for (int i = 0; i < deck.Count; i++)
            if (deck[i].Id == id && (best < 0 || deck[i].Upgrade < deck[best].Upgrade)) best = i;
        if (best >= 0) deck[best] = deck[best] with { Card = deck[best].Card with { Upgrade = deck[best].Upgrade + 1 } };
    }

    private static int Plainness(DeckCard c) => c.Upgrade * 2 + (c.EnchantmentId != null ? 1 : 0);

    // =============================================================================================
    // Private types
    // =============================================================================================

    private sealed record LogRun(string? Seed, bool? IsMultiplayer, int? Ascension, IReadOnlyList<LobbyPlayer> Players,
                                 string? Character, bool Continued);

    private sealed record SaveMark(long Seq, string FileName, long Bytes);

    private enum PatchKind { AddCard, RemoveCard, ShopRemoveCard, UpgradeCard, AddRelic, AddGold, AddPotion, RemovePotion }

    /// <param name="Seq">Log line number: patches are compared to save marks by this, not by wall-clock time.</param>
    private sealed record Patch(PatchKind Kind, string? Id, int Amount, long Seq);
}
