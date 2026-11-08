using Sts2Overlay.Core.History;
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
    private const string BingBong = "RELIC.BING_BONG";
    private const string UpgradeRollArg = "upgrade-roll";

    private static bool MayRollUpgrade(string cardId) =>
        CardRarities.Of(cardId) is not ("Rare" or null) && CardRarities.MaxUpgrade(cardId) > 0;
    /// <summary>Card type each egg relic upgrades when a card of that type joins the deck.</summary>
    private static readonly (string Relic, string Type)[] s_eggs =
        [("RELIC.FROZEN_EGG", "Power"), ("RELIC.MOLTEN_EGG", "Attack"), ("RELIC.TOXIC_EGG", "Skill")];

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
    private bool _cardStolen;                            // Thieving Hopper took a card this room (not yet returned)
    private int _stealAt;                                // index in _combatPlays where the steal happened
    private (List<CombatPlay> Before, List<CombatPlay> After)? _stealPlays;
    private DeckEffect? _pendingSelect;                  // meaning of my next "chose cards" (event option / relic)
    private bool _shopRemovalSeen;                       // a merchant sells one removal per visit
    private readonly List<int> _restSitePatches = [];    // provisional smith upgrades, fixed by the option line
    private readonly HashSet<string> _relicsThisRoom = new(StringComparer.Ordinal);
    private List<CombatPlay>? _combatPlays;              // plays of the fight in progress
    private IReadOnlyList<DeckCard>? _historyDeck;       // my final deck from the .run file (run over)
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
                FlushCombat();
                SettleStolenCard();
                _room = room.Room;
                _lastRestOption = null;
                _pendingRemovalReward = false;
                _pendingSelect = null;
                _shopRemovalSeen = false;
                _restSitePatches.Clear();
                _relicsThisRoom.Clear();
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
                FlushCombat();
                _combatPlays = [];
                _lastEncounter = cs.EncounterId;
                _lastKind = cs.Kind != CombatKind.Unknown ? cs.Kind : KindFromMap();
                _screen = ScreenHint.Combat;
                return true;

            case CombatWonEvent w:
                FlushCombat();
                _lastEncounter = w.EncounterId;
                if (w.Kind != CombatKind.Unknown) _lastKind = w.Kind;
                _screen = ScreenHint.RewardScreenLikely; // rewards are rolled right after this line
                return true;

            case CombatLostEvent:
                FlushCombat();
                End(RunOutcome.Lost);
                return true;
            case RunWonEvent:
                End(RunOutcome.Won);
                return true;
            case RunAbandonedEvent:
                End(RunOutcome.Abandoned);
                return true;
            case RunHistorySavedEvent h:
                FlushCombat();
                SettleStolenCard();
                End(_outcome == RunOutcome.InProgress ? RunOutcome.Ended : _outcome);
                _historyDeck = ReadHistoryDeck(h.FileName);
                return true;

            case RunSaveWrittenEvent w:
                _saveMarks.Add(new SaveMark(_seq, w.FileName, w.Bytes));
                if (_saveMarks.Count > 64) _saveMarks.RemoveAt(0);
                return true;

            // ---- patches: only for the local player ----------------------------------------------
            case MonsterMoveEvent { Move: "THIEVERY_MOVE" }:
                _cardStolen = true; // every living player loses a card (which one is not logged)
                _stealAt = _combatPlays?.Count ?? 0;
                _stealPlays = null;
                return false;
            case CardObtainedEvent { Special: true } c when IsMe(c.PlayerId) && _cardStolen:
                // Killing the hopper gives the stolen card back as a "special card reward" (SwipePower.BeforeDeath):
                // the unlogged steal and this return cancel out, so the deck is unchanged.
                _cardStolen = false;
                return false;
            case CardObtainedEvent c when IsMe(c.PlayerId):
                // From act 2 on, a reward card may come pre-upgraded (CardFactory.RollForUpgrade: act index x 25%,
                // never for rares). The roll isn't logged, so such a card is flagged.
                AddPatch(PatchKind.AddCard, c.CardId, 0, !c.Special && _logAct >= 2 && MayRollUpgrade(c.CardId) ? UpgradeRollArg : null);
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
                ApplyRelicEffect(r.RelicId);
                return true;
            case RemoteShopPurchaseEvent sp when IsMe(sp.PlayerId):
                // Only ANOTHER player's purchases are logged; seen when we follow a remote player (evaluation, host view).
                if (sp.ItemId.StartsWith("CARD.", StringComparison.Ordinal)) AddPatch(PatchKind.AddCard, sp.ItemId, 0);
                else { AddPatch(PatchKind.AddRelic, sp.ItemId, 0); ApplyRelicEffect(sp.ItemId); }
                return true;
            case EventOptionChosenEvent eo when IsMe(eo.PlayerId):
                ApplyChoice(eo);
                return true;
            case CardPlayedEvent play:
                _combatPlays?.Add(new CombatPlay(play.OwnerId, play.CardId, play.CombatIndex));
                return false;
            case PotionUsedEvent u when IsMe(u.PlayerId):
                AddPatch(PatchKind.RemovePotion, u.PotionId, 0);
                return true;
            case CardRemovalRewardEvent rr when IsMe(rr.PlayerId):
                _pendingRemovalReward = true;
                return false;
            case RestSiteOptionEvent ro when IsMe(ro.PlayerId) && ro.Success:
                _lastRestOption = ro.Option;
                return FixRestSitePatches(ro.Option);
            case CardsChosenEvent ch when IsMe(ch.PlayerId) && ch.CardIds.Count > 0:
                return ApplyChosen(ch);

            default:
                return false;
        }
    }

    /// <summary>
    /// "Player N chose cards [X]" is logged by every card-select screen, so its meaning comes from context:
    /// a "card removal" reward → removal; an event option / relic pickup just before → what that option does
    /// (<see cref="DeckEffects"/>); the first pick in a shop → the removal service (one per visit); a rest site →
    /// smith upgrade (co-op logs the chosen option right AFTER the pick, see <see cref="FixRestSitePatches"/>).
    /// Any other pick outside combat names deck cards we can't interpret: they are kept and flagged uncertain.
    /// Picks in a combat room (hand picks during the fight) are not recorded.
    /// </summary>
    private bool ApplyChosen(CardsChosenEvent ch)
    {
        if (_pendingRemovalReward)
        {
            _pendingRemovalReward = false;
            foreach (string card in ch.CardIds) AddPatch(PatchKind.RemoveCard, card, 0);
            return true;
        }
        if (_pendingSelect is { Select: not SelectMeaning.None } effect)
        {
            _pendingSelect = null;
            PatchKind kind = effect.Select switch
            {
                SelectMeaning.Remove => PatchKind.RemoveCard,
                SelectMeaning.Transform => PatchKind.TransformCard,
                SelectMeaning.Upgrade => PatchKind.UpgradeCard,
                SelectMeaning.Enchant => PatchKind.EnchantCard,
                SelectMeaning.AddChosen => PatchKind.AddCard,
                _ => PatchKind.DuplicateCard,
            };
            string? arg = effect.Select == SelectMeaning.Transform ? effect.TransformTo : effect.Enchantment;
            foreach (string card in ch.CardIds) AddPatch(kind, card, 0, arg);
            return true;
        }
        switch (_room)
        {
            case RoomKind.Merchant when !_shopRemovalSeen:
                _shopRemovalSeen = true;
                foreach (string card in ch.CardIds) AddPatch(PatchKind.ShopRemoveCard, card, 0);
                return true;
            case RoomKind.RestSite when _lastRestOption is null || _lastRestOption == "SMITH":
                foreach (string card in ch.CardIds)
                {
                    if (_lastRestOption is null) _restSitePatches.Add(_patches.Count);
                    AddPatch(PatchKind.UpgradeCard, card, 0);
                }
                return true;
            case RoomKind.Combat:
                return false;
            default:
                foreach (string card in ch.CardIds) AddPatch(PatchKind.UnknownSelect, card, 0);
                return true;
        }
    }

    /// <summary>
    /// Co-op logs "Rest site option ... Option: X" right after the pick. Single player never logs it, so a rest-site
    /// pick is first recorded as a smith upgrade and corrected here: COOK removes the picked cards (CookRestSiteOption),
    /// any other option makes them uncertain. CLONE (no pick) copies every Clone-enchanted card.
    /// </summary>
    private bool FixRestSitePatches(string option)
    {
        bool changed = false;
        if (option != "SMITH")
        {
            foreach (int i in _restSitePatches)
            {
                if (i >= _patches.Count || _patches[i].Kind != PatchKind.UpgradeCard) continue;
                _patches[i] = _patches[i] with { Kind = option == "COOK" ? PatchKind.RemoveCard : PatchKind.UnknownSelect };
                changed = true;
            }
        }
        _restSitePatches.Clear();
        if (option == "CLONE") { AddPatch(PatchKind.CloneEnchanted, null, 0); changed = true; }
        return changed;
    }

    /// <summary>
    /// An event option. An ancient's option grants the relic whose id ends the key ("NEOW.pages.INITIAL.options.FISHING_ROD");
    /// the game logs no "obtained" line for it (AncientEventModel → RelicCmd.Obtain), so the relic is added here.
    /// </summary>
    private void ApplyChoice(EventOptionChosenEvent eo)
    {
        int dot = eo.ChoiceKey.LastIndexOf('.');
        string? relic = dot >= 0 ? "RELIC." + eo.ChoiceKey[(dot + 1)..] : null;
        if (relic != null && DeckEffects.IsAncient(eo.EventId))
        {
            if (_relicsThisRoom.Add(relic)) AddPatch(PatchKind.AddRelic, relic, 0);
        }
        else if (relic != null && DeckEffects.ForRelic(relic) != null)
            _relicsThisRoom.Add(relic);
        ApplyEffect(DeckEffects.ForChoice(eo.EventId, eo.ChoiceKey));
    }

    /// <summary>A relic was obtained: apply its pickup effect once per room (an ancient option may have applied it already).</summary>
    private void ApplyRelicEffect(string relicId)
    {
        if (!_relicsThisRoom.Add(relicId)) return;
        ApplyEffect(DeckEffects.ForRelic(relicId));
    }

    /// <summary>Fixed and random deck effects of an event option / relic; its card-select meaning waits for "chose cards".</summary>
    private void ApplyEffect(DeckEffect? effect)
    {
        _pendingSelect = effect;
        if (effect is null) return;
        foreach (string card in effect.Adds) AddPatch(PatchKind.AddCard, card, 0);
        foreach (string card in effect.Removes) AddPatch(PatchKind.RemoveCard, card, 0);
        if (effect.RandomUpgrades > 0) AddPatch(PatchKind.RandomUpgrades, null, effect.RandomUpgrades);
        if (effect.UnknownAdds > 0) AddPatch(PatchKind.UnknownAdds, null, effect.UnknownAdds);
        if (effect.RandomRemoves > 0) AddPatch(PatchKind.RandomRemoves, null, effect.RandomRemoves);
        if (effect.UpgradeBasicStrikeAndDefend) AddPatch(PatchKind.UpgradeBasics, null, 0);
        if (effect.TransformAllBasics) AddPatch(PatchKind.TransformAllBasics, null, 0);
        if (effect.TransformOneStrikeAndDefend) AddPatch(PatchKind.TransformStrikeAndDefend, null, 0);
        if (effect.Transcend) AddPatch(PatchKind.Transcend, null, 0);
        if (effect.UpgradeAll) AddPatch(PatchKind.UpgradeAll, null, 0);
    }

    /// <summary>Leaving the room with the stolen card not returned (hopper escaped / reward skipped): an unknown card is gone.</summary>
    private void SettleStolenCard()
    {
        if (!_cardStolen) return;
        _cardStolen = false;
        _patches.Add(new Patch(PatchKind.CardStolen, null, 0, _seq)
        {
            Before = _stealPlays?.Before ?? [],
            Combat = new CombatObservation(_stealPlays?.After ?? []),
        });
        _stealPlays = null;
    }

    /// <summary>The fight is over: keep its plays as one observation (only used for a log-only rebuild).</summary>
    private void FlushCombat()
    {
        if (_cardStolen && _stealPlays is null && _combatPlays is { } fight)
            _stealPlays = (fight.Take(_stealAt).ToList(), fight.Skip(_stealAt).ToList());
        if (_combatPlays is { } plays)
            _patches.Add(new Patch(PatchKind.CombatSeen, null, 0, _seq)
            {
                Combat = new CombatObservation(plays),
                Arg = (_lastKind ?? CombatKind.Unknown).ToString(),
            });
        _combatPlays = null;
    }

    /// <summary>
    /// Run-end reconciliation: the game writes <c>history/&lt;n&gt;.run</c> with every player's final deck. A co-op guest
    /// has no save, so this is the only exact source; read it (read-only) when the saves folder is known.
    /// </summary>
    private IReadOnlyList<DeckCard>? ReadHistoryDeck(string fileName)
    {
        if (_options.Paths?.SavesDirectory is not { } saves) return null;
        string path = Path.Combine(saves, "history", fileName);
        try
        {
            if (IO.SharedFile.ReadAllText(path) is not { } json) return null;
            RunHistory history = RunHistoryReader.Parse(json);
            ulong me = MyId();
            RunPlayer? player = history.Players.FirstOrDefault(p => (ulong)p.Id == me)
                                ?? (history.Players.Count == 1 ? history.Players[0] : null);
            return player?.Deck;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Diagnostic?.Invoke(this, $"run history read failed: {path}: {ex.Message}");
            return null;
        }
    }

    private void AddPatch(PatchKind kind, string? id, int amount, string? arg = null) =>
        _patches.Add(new Patch(kind, id, amount, _seq) { Arg = arg });

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
        _cardStolen = false;
        _stealPlays = null;
        _pendingSelect = null;
        _shopRemovalSeen = false;
        _restSitePatches.Clear();
        _relicsThisRoom.Clear();
        _combatPlays = null;
        _historyDeck = null;
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
        var deck = new DeckReplay(state.Deck);
        var relics = state.Relics.ToList();
        int gold = state.Gold, removals = state.CardRemovalsBought, applied = 0;
        int rodFights = 0;
        IReadOnlyList<ulong> order = PlayerOrder();
        foreach (Patch p in _patches)
        {
            if (p.Seq <= cutoff) continue;
            // Uncertainty flags and fight observations only matter without a save (co-op guest): a save is exact.
            if (fromSave && p.Kind is PatchKind.UnknownSelect or PatchKind.RandomUpgrades
                    or PatchKind.UnknownAdds or PatchKind.RandomRemoves) continue;
            applied++;
            switch (p.Kind)
            {
                case PatchKind.AddCard:
                    deck.DuplicateAdds = relics.Contains(BingBong);
                    deck.UpgradeAddedTypes = s_eggs.Where(e => relics.Contains(e.Relic)).Select(e => e.Type).ToHashSet();
                    deck.Add(p.Id!, state.Floor, p.Arg == UpgradeRollArg && !fromSave ? UncertainReason.UpgradeRoll : null);
                    break;
                case PatchKind.UpgradeAll: deck.UpgradeAll(); break;
                case PatchKind.ShopRemoveCard: removals++; deck.Remove(p.Id!); break;
                case PatchKind.RemoveCard: deck.Remove(p.Id!); break;
                case PatchKind.UpgradeCard: deck.Upgrade(p.Id!, state.Floor); break;
                case PatchKind.TransformCard: deck.Transform(p.Id!, p.Arg, state.Floor); break;
                case PatchKind.EnchantCard: deck.Enchant(p.Id!, p.Arg, state.Floor); break;
                case PatchKind.DuplicateCard: deck.Duplicate(p.Id!, state.Floor); break;
                case PatchKind.UnknownSelect: deck.MarkUnknown(p.Id!, state.Floor); break;
                case PatchKind.RandomUpgrades: deck.RandomUpgrades(p.Amount); break;
                case PatchKind.UnknownAdds: deck.UnknownAdds(p.Amount); break;
                case PatchKind.RandomRemoves: deck.RandomRemoves(p.Amount); break;
                case PatchKind.CardStolen:
                    deck.Stolen(p.Before!.Where(x => x.Owner == me).ToList(), p.Combat!.Plays.Where(x => x.Owner == me).ToList());
                    break;
                case PatchKind.UpgradeBasics: deck.UpgradeBasics(); break;
                case PatchKind.TransformAllBasics: deck.TransformAllBasics(); break;
                case PatchKind.TransformStrikeAndDefend: deck.TransformStrikeAndDefend(); break;
                case PatchKind.Transcend: deck.Transcend(); break;
                case PatchKind.CloneEnchanted: deck.CloneEnchanted(); break;
                case PatchKind.CombatSeen:
                    if (!fromSave && p.Combat!.Plays.Count > 0) deck.Observe(p.Combat!, order, me, state.Floor);
                    deck.CombatEnded();
                    // Relics that upgrade random deck cards after fights (FishingRod: every 3rd normal fight;
                    // WarHammer: 4 cards per elite won). Which cards is never logged: counted as unplaced upgrades.
                    if (p.Arg == nameof(CombatKind.Normal) && relics.Contains("RELIC.FISHING_ROD") && ++rodFights % 3 == 0) deck.RandomUpgrades(1);
                    if (p.Arg == nameof(CombatKind.Elite) && relics.Contains("RELIC.WAR_HAMMER")) deck.RandomUpgrades(4);
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

        // Run over and the game wrote the history file: that deck is exact (the only exact source for a guest).
        bool fromHistory = !fromSave && _historyDeck != null;
        IReadOnlyList<DeckCard> finalDeck = fromHistory ? _historyDeck! : deck.Cards;

        state = state with { Deck = finalDeck, Relics = relics, Gold = gold, CardRemovalsBought = removals };

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
            DeckApproximate = !fromHistory && (!fromSave || applied > 0),
            UncertainCards = fromHistory ? [] : deck.Uncertain,
            UnidentifiedCards = fromHistory ? 0 : deck.Unidentified,
            UnknownUpgrades = fromHistory ? 0 : deck.UnknownUpgrades,
            UnknownRemovals = fromHistory ? 0 : deck.UnknownRemovals,
            DeckFromRunHistory = fromHistory,
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

    // =============================================================================================
    // Private types
    // =============================================================================================

    private sealed record LogRun(string? Seed, bool? IsMultiplayer, int? Ascension, IReadOnlyList<LobbyPlayer> Players,
                                 string? Character, bool Continued);

    private sealed record SaveMark(long Seq, string FileName, long Bytes);

    private enum PatchKind
    {
        AddCard, RemoveCard, ShopRemoveCard, UpgradeCard, TransformCard, EnchantCard, DuplicateCard, UnknownSelect,
        RandomUpgrades, UnknownAdds, RandomRemoves, UpgradeBasics, TransformAllBasics, TransformStrikeAndDefend, Transcend,
        CloneEnchanted, CombatSeen, CardStolen, UpgradeAll, AddRelic, AddGold, AddPotion, RemovePotion,
    }

    /// <param name="Seq">Log line number: patches are compared to save marks by this, not by wall-clock time.</param>
    private sealed record Patch(PatchKind Kind, string? Id, int Amount, long Seq)
    {
        /// <summary>Transform target / enchantment id.</summary>
        public string? Arg { get; init; }
        public CombatObservation? Combat { get; init; }
        /// <summary>CardStolen: the fight's plays before the steal (<see cref="Combat"/> holds the ones after it).</summary>
        public IReadOnlyList<CombatPlay>? Before { get; init; }
    }
}
