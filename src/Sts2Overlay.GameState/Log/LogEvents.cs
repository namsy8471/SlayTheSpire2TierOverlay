using Sts2Overlay.Core.Map;

namespace Sts2Overlay.GameState.Log;

// Typed events parsed from godot.log lines. Each record's comment shows the source line and where the
// game writes it (decompiled/sts2/<file>:<line>), so a game update that changes a format is easy to fix.
//
// Player ids: in single player "Player 1"; in co-op the Steam ID (net_id). A null PlayerId means the
// line had no player (older builds wrote "Obtained CARD.X ..." without one) → treat as the local player.

/// <summary>Base type for everything parsed out of godot.log.</summary>
public abstract record LogEvent;

/// <summary>Combat category from the encounter id suffix (<c>_WEAK/_NORMAL/_ELITE/_BOSS</c>).</summary>
public enum CombatKind { Unknown, Normal, Elite, Boss }

/// <summary>Room type from <c>Preloading '&lt;X&gt; Room' assets...</c>.</summary>
public enum RoomKind { Other, Combat, Merchant, RestSite, Event, Treasure }

/// <summary>Which odds table a <c>Card rarity:</c> roll used, recovered from <c>need - offset</c>.</summary>
public enum RaritySource { Unknown, Regular, Elite, Shop, Boss }

public sealed record LobbyPlayer(ulong NetId, string Character);

/// <summary>
/// <c>Embarking on a singleplayer IRONCLAD run. Ascension: 1 Seed: 77V3X5AF52</c> /
/// <c>Embarking on a multiplayer run. Players: Player 7656..., DEFECT,Player 7656..., SILENT. Ascension: 4 Seed: ...</c>
/// (NCharacterSelectScreen.cs:701/726, also CUSTOM/DAILY variants). Both host and guest log it.
/// </summary>
/// <param name="Character">Single player only: <c>CHARACTER.X</c>. In co-op look it up in <see cref="Players"/>.</param>
/// <param name="Mode">standard, custom or daily.</param>
public sealed record RunStartedEvent(bool IsMultiplayer, string? Character, int Ascension, string Seed,
                                     IReadOnlyList<LobbyPlayer> Players, string Mode) : LogEvent;

/// <summary>
/// The game's version at startup: <c>[Sentry.NET] Initialized: env=..., release=v0.111.0</c> or
/// <c>Release Version: v0.111.0</c> (both lines seen in v0.107.1 and v0.111.0 logs). Decides the seed arithmetic when no save says it (<see cref="Tracking.RngSeeds"/>).
/// </summary>
public sealed record GameVersionEvent(string Version) : LogEvent;

/// <summary><c>Continuing run with character: CHARACTER.DEFECT</c> (NMainMenu.cs:708; players[0]'s character).</summary>
public sealed record RunContinuedEvent(string Character) : LogEvent;

/// <summary>
/// Our own net id: <c>[SteamHost] Initializing Steam host. Our player id: X</c>, <c>[SteamClient] ... Our player id: X.</c>,
/// <c>Local player X is ready</c>, or the Steam ID in <c>Profile-scoped data path initialized: user://steam/X/profile1</c>.
/// </summary>
/// <param name="FromProfilePath">True for the profile-path line: that is the Steam ID, which equals our net id only in co-op.</param>
/// <param name="Profile">The profile-path line's profile folder (<c>profile1</c> ...), so the save watcher can follow the
/// profile the game really uses; null on the other lines.</param>
public sealed record LocalPlayerIdEvent(ulong NetId, bool FromProfilePath, string? Profile = null) : LogEvent;

/// <summary><c>Player vote changed for 1: -&gt;MapVote (gen: 2 coord: (4, 9))</c> (NMapScreen.cs:938). gen = map generation = act number.</summary>
public sealed record MapVoteEvent(ulong PlayerId, int Generation, MapCoord Coord) : LogEvent;

/// <summary><c>Preloading 'Merchant Room' assets... count=1 vfx=0</c>. One per room entered.</summary>
public sealed record RoomEnteredEvent(RoomKind Room, string RawName) : LogEvent;

/// <summary><c>Preloading 'Act=HIVE' assets...</c>. Logged twice at run start; dedupe by name.</summary>
public sealed record ActEnteredEvent(string ActId) : LogEvent;

/// <summary><c>Preloading 'MainMenuEssentials' assets...</c>: back at the main menu.</summary>
public sealed record MainMenuEvent : LogEvent;

/// <summary><c>Creating NCombatRoom with mode=ActiveCombat encounter=KNIGHTS_ELITE.</c> (NCombatRoom.cs:362). VisualOnly = event backdrop, not a fight.</summary>
public sealed record CombatStartedEvent(string EncounterId, CombatKind Kind, bool VisualOnly) : LogEvent;

/// <summary>
/// <c>CHARACTER.X has won against encounter ENCOUNTER.Y.</c> (EncounterStats.cs:60) or, the first time,
/// <c>CHARACTER.X fought ENCOUNTER.Y for the first time and WON &gt;:)</c> (ProgressSaveManager.cs:780).
/// Logged right after the combat-win save and right before rewards are rolled → reward screen opens.
/// </summary>
public sealed record CombatWonEvent(string Character, string EncounterId, CombatKind Kind) : LogEvent;

/// <summary><c>has lost to encounter</c> / <c>fought ENCOUNTER.Y for the first time and LOST :(</c>.</summary>
public sealed record CombatLostEvent(string Character, string EncounterId, CombatKind Kind) : LogEvent;

/// <summary><c>CHARACTER.X has won a run with ancient EVENT.NEOW.</c> (AncientStats.cs:86; once per ancient met).</summary>
public sealed record RunWonEvent(string Character) : LogEvent;

/// <summary><c>Abandoning an in-progress run (player-initiated)</c>.</summary>
public sealed record RunAbandonedEvent : LogEvent;

/// <summary><c>Saved run history: 1794131872.run</c> (RunHistorySaveManager.cs:58): the run is over.</summary>
public sealed record RunHistorySavedEvent(string FileName) : LogEvent;

/// <summary>
/// <c>Wrote 87444 bytes to path=user://steam/.../saves\current_run.save save_dir=...</c>: the game just committed a
/// run save. Its position in the log tells us exactly which log events the save already contains, and
/// <paramref name="Bytes"/> (= file size) lets us match it to the file the watcher read.
/// </summary>
public sealed record RunSaveWrittenEvent(string FileName, long Bytes) : LogEvent;

/// <summary>
/// <c>Card rarity: Rolled 0.455, need &lt; -0.02 for rare (offset = -0.05)</c> (CardRarityOdds.cs:100), one per card slot.
/// <c>need = baseRareOdds + offset</c>, so <c>need - offset</c> tells which table was used:
/// regular 3%, elite 10%, shop 9% (A7 "Scarcity": 1.49% / 5% / 4.5%), boss 100% (boss passes offset 0).
/// </summary>
public sealed record CardRarityRolledEvent(double Roll, double Need, double Offset) : LogEvent
{
    public bool IsRare => Roll < Need;
    public double BaseRareOdds => Need - Offset;

    /// <summary>The roll as the float the game printed (parsed as float, not via double, so it compares bit-exactly
    /// with a replayed <c>Rng.NextFloat()</c>).</summary>
    public float RollFloat { get; init; }

    public RaritySource Source => BaseRareOdds switch
    {
        > 0.5 => RaritySource.Boss,
        var b when Near(b, 0.03) || Near(b, 0.0149) => RaritySource.Regular,
        var b when Near(b, 0.10) || Near(b, 0.05) => RaritySource.Elite,
        var b when Near(b, 0.09) || Near(b, 0.045) => RaritySource.Shop,
        _ => RaritySource.Unknown,
    };

    /// <summary>True when the base odds are the A7 "Scarcity" values (pity grows 0.005 instead of 0.01).</summary>
    public bool? IsScarcity => BaseRareOdds switch
    {
        var b when Near(b, 0.0149) || Near(b, 0.05) || Near(b, 0.045) => true,
        var b when Near(b, 0.03) || Near(b, 0.10) || Near(b, 0.09) => false,
        _ => null,
    };

    // Values are float32 printed in decimal (e.g. 0.07000001), so compare with a tolerance.
    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.002;
}

/// <summary><c>Player 1 obtained CARD.ANGER from card reward</c> (CardReward.cs:285) or <c>... from special card reward</c>.</summary>
public sealed record CardObtainedEvent(ulong? PlayerId, string CardId, bool Special) : LogEvent;

/// <summary><c>Player 1 obtained 17 gold from reward</c> (GoldReward.cs:78).</summary>
public sealed record GoldObtainedEvent(ulong? PlayerId, int Amount) : LogEvent;

/// <summary><c>Player 1 obtained POTION.X from potion reward</c> (PotionReward.cs:81).</summary>
public sealed record PotionObtainedEvent(ulong? PlayerId, string PotionId) : LogEvent;

/// <summary>
/// <c>Player 1 obtained RELIC.X from relic reward</c> (RelicReward.cs:111), or in co-op (Debug)
/// <c>[TreasureRoomRelicSynchronizer] Relic index 0 (RELIC.X (123)) is being picked by local player N</c>.
/// </summary>
public sealed record RelicObtainedEvent(ulong? PlayerId, string RelicId) : LogEvent;

/// <summary><c>Player 1 obtained card removal from reward</c> (CardRemovalReward.cs:38); a "chose cards" removal follows.</summary>
public sealed record CardRemovalRewardEvent(ulong PlayerId) : LogEvent;

/// <summary>
/// <c>Player 1 chose cards [HELLRAISER]</c> (CardSelectCmd.cs:862). Logged for EVERY card-select screen:
/// shop removal, rest-site smith, events, and in-combat picks. Meaning depends on the room → tracker decides.
/// </summary>
public sealed record CardsChosenEvent(ulong PlayerId, IReadOnlyList<string> CardIds) : LogEvent;

/// <summary><c>Player 1 using potion EXPLOSIVE_AMPOULE (no target)</c> / <c>discarding potion X</c>.</summary>
public sealed record PotionUsedEvent(ulong PlayerId, string PotionId, bool Discarded) : LogEvent;

/// <summary>Co-op Debug only: <c>[RestSiteSynchronizer] Rest site option index 1 chosen for player X with success True. Option: SMITH</c>.</summary>
public sealed record RestSiteOptionEvent(ulong PlayerId, string Option, bool Success) : LogEvent;

/// <summary>
/// Co-op VeryDebug: <c>[EventSynchronizer] Event EVENT.NEOW began for player X with options: EventOption title: … textKey: NEOW.pages.INITIAL.options.NEW_LEAF,EventOption …</c>.
/// The exact options offered to that player, in button order (the titles are localized and contain commas, so only the keys are kept).
/// </summary>
public sealed record EventStartedEvent(ulong PlayerId, string EventId, IReadOnlyList<string> OptionKeys) : LogEvent;


/// <summary>
/// Co-op Debug: <c>[EventSynchronizer] Option index 2 chosen for player X in event EVENT.NEOW. Choice key: NEOW.pages.INITIAL.options.PRECARIOUS_SHEARS</c>.
/// Logged for every player (local and remote). Tells what a following <c>chose cards</c> line means (remove /
/// transform / upgrade ...) and which fixed cards the option adds (see <c>Tracking/DeckEffects</c>).
/// </summary>
/// <param name="ChoiceKey">Text key of the option. For an ancient (Neow etc.) the last segment is the relic id.</param>
public sealed record EventOptionChosenEvent(ulong PlayerId, string EventId, string ChoiceKey) : LogEvent;

/// <summary>
/// Co-op Debug: <c>[ActionQueueSet] Enqueueing action PlayCardAction card: CARD.X (hash) index: 13 targetid: 2 to player queue owned by P</c>
/// (ActionQueueSet.cs:142). <paramref name="CombatIndex"/> is the card's combat id: at combat start the game numbers
/// every player's deck in player order (NetCombatCardDb.StartCombat), so player 0's deck is 0..n0-1, player 1's deck
/// n0..n0+n1-1, and cards created during the fight come after all decks.
/// </summary>
public sealed record CardPlayedEvent(ulong OwnerId, string CardId, int CombatIndex) : LogEvent;

/// <summary>
/// Co-op Debug, REMOTE players only: <c>[RunLocationTargetedMessageBuffer] Handling map-targeted message RewardObtainedMessage type: Card ...
/// skipped: False Card: CARD.X (hash) from P ...</c>. Seen for another player's shop purchase (their own client logs nothing).
/// </summary>
public sealed record RemoteShopPurchaseEvent(ulong PlayerId, string ItemId) : LogEvent;

/// <summary>
/// <c>Monster THIEVING_HOPPER performing move THIEVERY_MOVE</c>. Only the moves that change a deck are reported
/// (Thieving Hopper's steal: one card from every living player, SwipePower.Steal; nothing names the card).
/// </summary>
public sealed record MonsterMoveEvent(string MonsterId, string Move) : LogEvent;
