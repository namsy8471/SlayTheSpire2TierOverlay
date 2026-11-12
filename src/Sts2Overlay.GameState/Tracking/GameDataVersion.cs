namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// Which game data tables (the names-free catalogs embedded into this assembly: cards, card pools, relics, starting decks)
/// belong to a run. It is not detected on its own: it follows the run's <see cref="RngScheme"/>, which already says which
/// generation of the game wrote the save or the log (save <c>schema_version</c>, else the log's game version, else
/// <see cref="RngSeeds.Default"/>).
/// <list type="bullet">
/// <item><see cref="RngScheme.Legacy32"/> (v0.108 and older, save schema ≤ 18) → the v0.107.1 tables.</item>
/// <item><see cref="RngScheme.Xx64"/> (v0.109 and newer, save schema ≥ 19) → the v0.111.0 tables. No catalogs exist for
/// v0.109 and v0.110, so their data is assumed to equal v0.111.0's.</item>
/// </list>
/// A save from before v0.109 that a newer game migrated (seed prefixed <c>old</c>) is still read with the new tables: the
/// running game migrated its cards too (e.g. <c>SerializableRunV19ToV20</c> renames <c>CARD.SCARE</c>).
/// </summary>
public static class GameDataVersion
{
    /// <summary>The tables ported for the old seed arithmetic.</summary>
    public const string V0107 = "v0.107.1";

    /// <summary>The tables for v0.109 and newer.</summary>
    public const string V0111 = "v0.111.0";

    /// <summary>Every embedded version, oldest first.</summary>
    public static IReadOnlyList<string> All { get; } = [V0107, V0111];

    /// <summary>The data version of a run with this seed arithmetic.</summary>
    public static string For(RngScheme scheme) => scheme == RngScheme.Legacy32 ? V0107 : V0111;

    /// <summary>The embedded resource name of a catalog file, e.g. <c>cards</c> → <c>cards.v0.111.0.json</c>.</summary>
    internal static string Resource(string table, RngScheme scheme) => $"{table}.{For(scheme)}.json";

    /// <summary>
    /// Ids the game renamed with a save migration, old id → id of this version. v0.111 (<c>SharedMigrationHelper.v110Renames</c>,
    /// used by <c>SerializableRunV19ToV20</c>, <c>RunHistoryV9ToV10</c>, <c>ProgressSaveV22ToV23</c>): Scare was reworked into
    /// Sidestep and every saved <c>CARD.SCARE</c> becomes <c>CARD.SIDESTEP</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Renames(RngScheme scheme) =>
        scheme == RngScheme.Legacy32 ? s_noRenames : s_v111Renames;

    /// <summary>
    /// Whether every player sees one copy of this event (<c>EventModel.IsShared</c>). It matters for the event's own stream:
    /// v0.111.0 <c>EventModel.BeginEvent</c> seeds it with <c>runSeed + (IsShared ? 0 : slot) + hash(id)</c>, so in co-op all
    /// players of a shared event draw from the same stream (<see cref="RngSeeds.EventStream"/>).
    /// <para>Xx64: the eight v0.111.0 events that override <c>IsShared</c> to true. Legacy32: always false, i.e. the slot is
    /// always added as before. v0.107.1 marked six of these events shared too (pack <c>events.json</c>), but its
    /// <c>BeginEvent</c> seed is not in hand (no v0.107.1 decompile), so the old behaviour is kept.</para>
    /// </summary>
    /// <param name="eventId"><c>EVENT.JUNGLE_MAZE_ADVENTURE</c> or the entry <c>JUNGLE_MAZE_ADVENTURE</c>.</param>
    public static bool IsSharedEvent(RngScheme scheme, string eventId)
    {
        if (scheme == RngScheme.Legacy32) return false;
        string entry = eventId.StartsWith("EVENT.", StringComparison.Ordinal) ? eventId["EVENT.".Length..] : eventId;
        return s_v111SharedEvents.Contains(entry);
    }

    // v0.111.0: BattlewornDummy, DenseVegetation, FakeMerchant, JungleMazeAdventure, MorphicGrove, PunchOff, TheLanternKey,
    // WarHistorianRepy (`public override bool IsShared => true`).
    private static readonly HashSet<string> s_v111SharedEvents = new(StringComparer.Ordinal)
    {
        "BATTLEWORN_DUMMY", "DENSE_VEGETATION", "FAKE_MERCHANT", "JUNGLE_MAZE_ADVENTURE",
        "MORPHIC_GROVE", "PUNCH_OFF", "THE_LANTERN_KEY", "WAR_HISTORIAN_REPY",
    };

    private static readonly Dictionary<string, string> s_noRenames = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> s_v111Renames = new(StringComparer.Ordinal)
    {
        ["CARD.SCARE"] = "CARD.SIDESTEP",
    };
}
