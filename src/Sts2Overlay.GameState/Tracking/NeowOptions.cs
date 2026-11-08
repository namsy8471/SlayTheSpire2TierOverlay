namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// The three options Neow offers at the start of a run, replayed from the run seed (Neow.GenerateInitialOptions, v0.107.1).
/// <para>
/// Neow draws everything from the event's own stream, <c>new Rng((uint)((int)runSeed + slot) + hash("NEOW"))</c>
/// (EventModel.BeginEvent), and nothing else reads that stream before the options are made:
/// </para>
/// <code>
/// curse = NextItem(curse pool filtered by IsAllowedAtNeow)
/// positives = positive pool minus the curse's twin (Cursed Pearl→Golden Pearl, Hefty Tablet→Arcane Scroll, ...)
/// unless curse is Large Capsule: + (NextBool ? Lava Rock : Small Capsule)
/// + (NextBool ? Nutritious Oyster : Stone Humidifier)
/// + (NextBool ? Neow's Talisman : Pomander)
/// filter IsAllowedAtNeow, UnstableShuffle, take 2, then the curse  → the buttons top to bottom
/// </code>
/// <c>IsAllowedAtNeow</c> only depends on the player count (Massive Scroll: co-op only; Winged Boots, Silver Crucible:
/// single player only) and on unlocks (Kaleidoscope: every character unlocked; Scroll Boxes: ≥4 common and ≥2 uncommon
/// cards unlocked in the character's pool, true from the first run). Runs with modifiers (custom/daily) get the
/// modifiers' own Neow options instead and are not handled here.
/// </summary>
public static class NeowOptions
{
    public const string EventId = "EVENT.NEOW";
    private const string KeyPrefix = "NEOW.pages.INITIAL.options.";

    // Pool orders as in Neow.cs: the order decides which item NextItem / the shuffle land on.
    private static readonly string[] CursePool =
    [
        "CURSED_PEARL", "HEFTY_TABLET", "LARGE_CAPSULE", "LEAFY_POULTICE", "NEOWS_BONES", "PRECARIOUS_SHEARS", "SILKEN_TRESS", "SILVER_CRUCIBLE",
    ];

    private static readonly string[] PositivePool =
    [
        "ARCANE_SCROLL", "BOOMING_CONCH", "FISHING_ROD", "GOLDEN_PEARL", "KALEIDOSCOPE", "LEAD_PAPERWEIGHT", "LOST_COFFER",
        "MASSIVE_SCROLL", "NEOWS_TORMENT", "NEW_LEAF", "PHIAL_HOLSTER", "PRECISE_SCISSORS", "SCROLL_BOXES", "WINGED_BOOTS",
    ];

    /// <summary>A curse-slot relic removes its positive twin from the pool.</summary>
    private static readonly Dictionary<string, string> TwinOf = new(StringComparer.Ordinal)
    {
        ["CURSED_PEARL"] = "GOLDEN_PEARL",
        ["HEFTY_TABLET"] = "ARCANE_SCROLL",
        ["LEAFY_POULTICE"] = "NEW_LEAF",
        ["PRECARIOUS_SHEARS"] = "PRECISE_SCISSORS",
    };

    /// <summary>The event stream's seed (EventModel.BeginEvent; Neow is not a shared event, so the slot is added).</summary>
    public static uint StreamSeed(string runSeed, int slot)
    {
        uint run = unchecked((uint)GameRng.DeterministicHash(runSeed));
        return unchecked((uint)((int)run + slot) + (uint)GameRng.DeterministicHash("NEOW"));
    }

    /// <summary>
    /// The relic ids (<c>LAVA_ROCK</c>…) of the three options in button order: two positives, then the curse slot.
    /// </summary>
    /// <param name="runSeed">The run's seed string (<c>rng.seed</c> in the save, <c>Seed:</c> in the log).</param>
    /// <param name="slot">The player's slot index (0 in single player).</param>
    /// <param name="playerCount">Players in the run.</param>
    /// <param name="allCharactersUnlocked">Every character's first epoch revealed (Kaleidoscope's condition).</param>
    public static IReadOnlyList<string> Relics(string runSeed, int slot, int playerCount, bool allCharactersUnlocked)
    {
        var rng = new GameRng(StreamSeed(runSeed, slot));
        bool Allowed(string relic) => NeowOptions.Allowed(relic, playerCount, allCharactersUnlocked);

        var curses = CursePool.Where(Allowed).ToList();
        var curse = curses[rng.NextInt(curses.Count)];

        var positives = PositivePool.ToList();
        if (TwinOf.TryGetValue(curse, out var twin)) positives.Remove(twin);
        if (curse != "LARGE_CAPSULE") positives.Add(NextBool(rng) ? "LAVA_ROCK" : "SMALL_CAPSULE");
        positives.Add(NextBool(rng) ? "NUTRITIOUS_OYSTER" : "STONE_HUMIDIFIER");
        positives.Add(NextBool(rng) ? "NEOWS_TALISMAN" : "POMANDER");
        positives.RemoveAll(r => !Allowed(r));
        rng.Shuffle(positives);
        return [positives[0], positives[1], curse];
    }

    /// <summary>The three options as event text keys (<c>NEOW.pages.INITIAL.options.LAVA_ROCK</c>), in button order.</summary>
    public static IReadOnlyList<string> Keys(string runSeed, int slot, int playerCount, bool allCharactersUnlocked)
        => Relics(runSeed, slot, playerCount, allCharactersUnlocked).Select(r => KeyPrefix + r).ToArray();

    /// <summary>
    /// Every option this run could have drawn (all 28 minus the relics <c>IsAllowedAtNeow</c> rules out), as text keys:
    /// what the panel lists below the offered three.
    /// </summary>
    public static IReadOnlyList<string> PossibleKeys(int playerCount, bool allCharactersUnlocked)
        => CursePool.Concat(PositivePool)
            .Concat(["LAVA_ROCK", "SMALL_CAPSULE", "NUTRITIOUS_OYSTER", "STONE_HUMIDIFIER", "NEOWS_TALISMAN", "POMANDER"])
            .Where(r => Allowed(r, playerCount, allCharactersUnlocked))
            .Select(r => KeyPrefix + r)
            .ToArray();

    private static bool Allowed(string relic, int playerCount, bool allCharactersUnlocked) => relic switch
    {
        "MASSIVE_SCROLL" => playerCount > 1,
        "WINGED_BOOTS" or "SILVER_CRUCIBLE" => playerCount == 1,
        "KALEIDOSCOPE" => allCharactersUnlocked,
        _ => true,
    };

    /// <summary>Rng.NextBool: <c>MegaRandom.Next(2) == 0</c>, i.e. <c>(int)(NextDouble() * 2) == 0</c>.</summary>
    private static bool NextBool(GameRng rng) => rng.NextInt(2) == 0;
}
