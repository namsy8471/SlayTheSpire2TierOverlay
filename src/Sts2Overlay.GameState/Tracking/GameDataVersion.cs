namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// The embedded card, relic, reward and starting-deck tables that match the game which wrote a run.
/// </summary>
public readonly record struct GameDataVersion
{
    /// <summary>The tables ported for the old seed arithmetic.</summary>
    public const string V0107 = "v0.107.1";

    /// <summary>The tables used for v0.109 through v0.112.</summary>
    public const string V0111 = "v0.111.0";
    public const string V0113 = "v0.113.0";

    /// <summary>Every embedded version, oldest first.</summary>
    public static IReadOnlyList<string> All { get; } = [V0107, V0111, V0113];

    /// <summary>The data version of a run with this seed arithmetic.</summary>
    public static string For(RngScheme scheme) => ((GameDataVersion)scheme).Version;

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

    private readonly string? _version;

    private GameDataVersion(string version) => _version = version;

    public static GameDataVersion Latest => default;

    // Existing callers that know only the seed arithmetic use the original tables for that generation.
    public static implicit operator GameDataVersion(RngScheme scheme) =>
        new(scheme == RngScheme.Legacy32 ? V0107 : V0111);

    internal string Version => _version ?? V0113;
    internal RngScheme Scheme => Version == V0107 ? RngScheme.Legacy32 : RngScheme.Xx64;

    public static GameDataVersion ForRun(int schemaVersion, string? gameVersion) =>
        Detect(schemaVersion, gameVersion) ?? Latest;

    internal static GameDataVersion? Detect(int schemaVersion, string? gameVersion)
    {
        if (schemaVersion >= 21) return Latest;
        if (RngSeeds.ForSaveSchema(schemaVersion) is { } scheme) return scheme;
        if (RngSeeds.ParseVersion(gameVersion) is { } version)
            return version.CompareTo((0, 113, 0)) >= 0 ? Latest : RngSeeds.ForGameVersion(gameVersion);
        return null;
    }

    internal string Resource(string table) => $"{table}.{Version}.json";
}

/// <summary>Loads each embedded catalog on first use.</summary>
internal sealed class EmbeddedCatalog<T>(Func<GameDataVersion, T> load)
{
    private readonly Lazy<T> _v0107 = new(() => load(RngScheme.Legacy32));
    private readonly Lazy<T> _v0111 = new(() => load(RngScheme.Xx64));
    private readonly Lazy<T> _v0113 = new(() => load(GameDataVersion.Latest));

    public T For(GameDataVersion version) => version.Version switch
    {
        GameDataVersion.V0107 => _v0107.Value,
        GameDataVersion.V0111 => _v0111.Value,
        _ => _v0113.Value,
    };
}
