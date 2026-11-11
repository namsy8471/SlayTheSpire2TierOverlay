namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// The three options Neow offers at the start of a run, replayed from the run seed (Neow.GenerateInitialOptions;
/// v0.107.1 and v0.111.0 decompiled).
/// <para>
/// Neow draws everything from the event's own stream, <see cref="RngSeeds.EventStream"/>
/// (<c>runSeed + slot + hash("NEOW")</c>, EventModel.BeginEvent), and nothing else reads that stream before the options
/// are made:
/// </para>
/// <code>
/// curse = NextItem(curse pool filtered by IsAllowedAtNeow)
/// positives = positive pool minus the curse's twins (Cursed Pearl→Golden Pearl, Hefty Tablet→Arcane Scroll, ...)
/// unless curse is Large Capsule: + (NextBool ? Lava Rock : Small Capsule)
/// + (NextBool ? Nutritious Oyster : Stone Humidifier)
/// + (NextBool ? Neow's Talisman : Pomander)
/// filter IsAllowedAtNeow, UnstableShuffle, take 2, then the curse  → the buttons top to bottom
/// </code>
/// <c>IsAllowedAtNeow</c> only depends on the player count (Massive Scroll: co-op only; Winged Boots, Silver Crucible:
/// single player only) and on unlocks (Kaleidoscope: every character unlocked; Scroll Boxes: ≥4 common and ≥2 uncommon
/// cards unlocked in the character's pool, true from the first run). Runs with modifiers (custom/daily) get the
/// modifiers' own Neow options instead and are not handled here.
/// <para>
/// The curse pool grew from 8 to 10 (Dowsing Rod, Neow's Sacrifice; Neow's Sacrifice also removes Phial Holster and Lost
/// Coffer) in the same release that brought 64-bit seeds: run histories show v0.108.0 with the old pool and seeds and
/// v0.109.0 on with the new ones, so the pools are chosen by <see cref="RngScheme"/>.
/// </para>
/// </summary>
public static class NeowOptions
{
    public const string EventId = "EVENT.NEOW";
    private const string KeyPrefix = "NEOW.pages.INITIAL.options.";

    /// <summary>One release's pools, in Neow.cs order (the order decides which item NextItem / the shuffle land on).</summary>
    private sealed record Pools(string[] Curses, string[] Positives, Dictionary<string, string[]> TwinsOf);

    private static readonly string[] PositivePool =
    [
        "ARCANE_SCROLL", "BOOMING_CONCH", "FISHING_ROD", "GOLDEN_PEARL", "KALEIDOSCOPE", "LEAD_PAPERWEIGHT", "LOST_COFFER",
        "MASSIVE_SCROLL", "NEOWS_TORMENT", "NEW_LEAF", "PHIAL_HOLSTER", "PRECISE_SCISSORS", "SCROLL_BOXES", "WINGED_BOOTS",
    ];

    /// <summary>v0.107.1 (up to v0.108.0).</summary>
    private static readonly Pools V107 = new(
        ["CURSED_PEARL", "HEFTY_TABLET", "LARGE_CAPSULE", "LEAFY_POULTICE", "NEOWS_BONES", "PRECARIOUS_SHEARS", "SILKEN_TRESS", "SILVER_CRUCIBLE"],
        PositivePool,
        new(StringComparer.Ordinal)
        {
            // A curse-slot relic removes its positive twin(s) from the pool.
            ["CURSED_PEARL"] = ["GOLDEN_PEARL"],
            ["HEFTY_TABLET"] = ["ARCANE_SCROLL"],
            ["LEAFY_POULTICE"] = ["NEW_LEAF"],
            ["PRECARIOUS_SHEARS"] = ["PRECISE_SCISSORS"],
        });

    /// <summary>v0.109.0 on (checked against v0.111.0's Neow.cs).</summary>
    private static readonly Pools V109 = new(
        ["CURSED_PEARL", "DOWSING_ROD", "HEFTY_TABLET", "LARGE_CAPSULE", "LEAFY_POULTICE", "NEOWS_BONES", "NEOWS_SACRIFICE",
         "PRECARIOUS_SHEARS", "SILKEN_TRESS", "SILVER_CRUCIBLE"],
        PositivePool,
        new(StringComparer.Ordinal)
        {
            ["CURSED_PEARL"] = ["GOLDEN_PEARL"],
            ["HEFTY_TABLET"] = ["ARCANE_SCROLL"],
            ["LEAFY_POULTICE"] = ["NEW_LEAF"],
            ["PRECARIOUS_SHEARS"] = ["PRECISE_SCISSORS"],
            ["NEOWS_SACRIFICE"] = ["PHIAL_HOLSTER", "LOST_COFFER"],
        });

    private static readonly string[] Pairs = ["LAVA_ROCK", "SMALL_CAPSULE", "NUTRITIOUS_OYSTER", "STONE_HUMIDIFIER", "NEOWS_TALISMAN", "POMANDER"];

    private static Pools PoolsFor(RngScheme scheme) => scheme == RngScheme.Legacy32 ? V107 : V109;

    /// <summary>
    /// The scheme Neow's draw really used. A migrated save's seed (<c>old...</c>) belongs to a run started before v0.109:
    /// its Neow was drawn by the old game, with the old hash on the seed without the prefix and the old pools.
    /// </summary>
    public static RngScheme EffectiveScheme(string runSeed, RngScheme scheme) =>
        RngSeeds.IsMigrated(runSeed) ? RngScheme.Legacy32 : scheme;

    /// <summary>The event stream's seed (EventModel.BeginEvent; Neow is not a shared event, so the slot is added).</summary>
    public static ulong StreamSeed(string runSeed, int slot, RngScheme scheme = RngSeeds.Default) =>
        RngSeeds.EventStream(scheme, runSeed, slot, "NEOW");

    /// <summary>
    /// The relic ids (<c>LAVA_ROCK</c>…) of the three options in button order: two positives, then the curse slot.
    /// </summary>
    /// <param name="runSeed">The run's seed string (<c>rng.seed</c> in the save, <c>Seed:</c> in the log).</param>
    /// <param name="slot">The player's slot index (0 in single player).</param>
    /// <param name="playerCount">Players in the run.</param>
    /// <param name="allCharactersUnlocked">Every character's first epoch revealed (Kaleidoscope's condition).</param>
    /// <param name="scheme">The run's seed arithmetic (save schema / game version).</param>
    public static IReadOnlyList<string> Relics(string runSeed, int slot, int playerCount, bool allCharactersUnlocked,
                                               RngScheme scheme = RngSeeds.Default)
    {
        RngScheme effective = EffectiveScheme(runSeed, scheme);
        // Game seeds are upper-case, so the lower-case prefix only ever comes from the migration (whatever scheme was passed).
        if (RngSeeds.IsMigrated(runSeed)) runSeed = runSeed[RngSeeds.MigratedPrefix.Length..];
        Pools pools = PoolsFor(effective);
        var rng = new GameRng(StreamSeed(runSeed, slot, effective));
        bool Allowed(string relic) => NeowOptions.Allowed(relic, playerCount, allCharactersUnlocked);

        var curses = pools.Curses.Where(Allowed).ToList();
        var curse = curses[rng.NextInt(curses.Count)];

        var positives = pools.Positives.ToList();
        if (pools.TwinsOf.TryGetValue(curse, out var twins)) positives.RemoveAll(twins.Contains);
        if (curse != "LARGE_CAPSULE") positives.Add(NextBool(rng) ? "LAVA_ROCK" : "SMALL_CAPSULE");
        positives.Add(NextBool(rng) ? "NUTRITIOUS_OYSTER" : "STONE_HUMIDIFIER");
        positives.Add(NextBool(rng) ? "NEOWS_TALISMAN" : "POMANDER");
        positives.RemoveAll(r => !Allowed(r));
        rng.Shuffle(positives);
        return [positives[0], positives[1], curse];
    }

    /// <summary>The three options as event text keys (<c>NEOW.pages.INITIAL.options.LAVA_ROCK</c>), in button order.</summary>
    public static IReadOnlyList<string> Keys(string runSeed, int slot, int playerCount, bool allCharactersUnlocked,
                                             RngScheme scheme = RngSeeds.Default)
        => Relics(runSeed, slot, playerCount, allCharactersUnlocked, scheme).Select(r => KeyPrefix + r).ToArray();

    /// <summary>
    /// Every option this run could have drawn (the scheme's pools minus the relics <c>IsAllowedAtNeow</c> rules out), as
    /// text keys: what the panel lists below the offered three. Pass <see cref="EffectiveScheme"/> for a migrated seed.
    /// </summary>
    public static IReadOnlyList<string> PossibleKeys(int playerCount, bool allCharactersUnlocked, RngScheme scheme = RngSeeds.Default)
        => PossibleRelics(playerCount, allCharactersUnlocked, scheme).Select(r => KeyPrefix + r).ToArray();

    /// <summary><see cref="PossibleKeys"/> as relic ids (<c>LAVA_ROCK</c>).</summary>
    public static IReadOnlyList<string> PossibleRelics(int playerCount, bool allCharactersUnlocked, RngScheme scheme = RngSeeds.Default)
    {
        Pools pools = PoolsFor(scheme);
        return pools.Curses.Concat(pools.Positives).Concat(Pairs)
            .Where(r => Allowed(r, playerCount, allCharactersUnlocked))
            .ToArray();
    }

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
