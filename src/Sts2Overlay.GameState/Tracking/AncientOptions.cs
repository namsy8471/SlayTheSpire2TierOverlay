namespace Sts2Overlay.GameState.Tracking;

/// <summary>A deck card as the ancients' option rules see it: id and enchantment (if any).</summary>
public readonly record struct AncientDeckCard(string Id, string? EnchantmentId = null);

/// <summary>
/// What an ancient's option generation reads besides its random stream (v0.107.1). Everything is in the run save
/// written when the player steps onto the ancient's node: the deck, the relics, the character, the player's unlocked
/// epochs (<c>players[].unlock_state</c>) and the act.
/// </summary>
public sealed record AncientInputs
{
    /// <summary>The run's seed string (<c>rng.seed</c>).</summary>
    public required string RunSeed { get; init; }

    /// <summary>The player's slot (0 in single player; the index in the save's player list in co-op).</summary>
    public int Slot { get; init; }

    /// <summary>0-based act index (Darv's act-gated relic sets).</summary>
    public int ActIndex { get; init; }

    /// <summary>The player's character, e.g. <c>CHARACTER.IRONCLAD</c> (Orobas's Sea Glass picks another one).</summary>
    public string Character { get; init; } = "";

    public IReadOnlyList<AncientDeckCard> Deck { get; init; } = [];

    /// <summary>Relic ids (<c>RELIC.BURNING_BLOOD</c>) in the order the player holds them.</summary>
    public IReadOnlyList<string> Relics { get; init; } = [];

    /// <summary>The player's revealed epochs (<c>SILENT1_EPOCH</c> ...): they decide the unlocked characters. Null = all unlocked.</summary>
    public IReadOnlyCollection<string>? UnlockedEpochs { get; init; }

    /// <summary>A run modifier clears the starting deck (Darv then never offers Pandora's Box).</summary>
    public bool DeckClearingModifier { get; init; }

    /// <summary>The run's seed arithmetic (save schema / game version).</summary>
    public RngScheme Scheme { get; init; } = RngSeeds.Default;
}

/// <summary>
/// The options an act-start ancient offers, replayed exactly (v0.107.1, Models.Events.*.GenerateInitialOptions).
/// <para>
/// Every ancient draws from its own fresh stream, seeded like Neow's: <c>new Rng(runSeed + slot + hash(id))</c>
/// with id = <c>PAEL</c>, <c>OROBAS</c> ... (EventModel.BeginEvent; ancients are not shared events, so the slot is added).
/// Nothing else reads that stream before the options are made, so the run seed plus the player's deck and relics at the
/// node decide them completely. The rules, in the game's order:
/// </para>
/// <code>
/// PAEL       NextItem(Flesh, Horn, Tears);
///            NextItem([Wing, Claw if ≥3 cards Goopy can enchant, Tooth if ≥5 removable cards] ×2 + Growth);
///            NextItem(Eye, Blood, Legion if no event pet)
/// TEZCATARA  NextItem(Cocoa, Cookie, Soup if a Basic Strike is in the deck); NextItem(Hug, Storybook, Mittens);
///            NextItem(Compass, Candle, Toy Box, Seal of Gold)
/// OROBAS     other = NextItem(unlocked characters but mine); first = NextFloat &lt; 1/3 ? Prismatic Gem : Sea Glass(other);
///            NextItem(Shrymp, Glass Eye, Sand Castle, first); NextItem(Coffer, Driftwood, Pearl);
///            NextItem(Touch if a Starter relic is held, Tooth if a Transcendence starter card is in the deck | LOCKED)
/// NONUPEIPE  9 relics + Bracelet if ≥4 cards Swift can enchant; shuffle; take 3
/// TANX       9 relics + Tri-Boomerang if ≥3 cards Instinct can enchant; shuffle; take 3
/// VAKUU      shuffle each of 3 pools; take each pool's first
/// DARV       per relic set that passes its filter, NextItem(set); shuffle; NextBool ? take 2 + Dusty Tome : take 3
/// </code>
/// <c>Rng.NextItem</c> of an empty list draws nothing; of a one-item list it still draws. <c>Hook.ShouldAllowAncient</c>
/// has no overrides in this version.
/// <para>
/// From v0.109 (<see cref="RngScheme.Xx64"/>, checked on the v0.111.0 source and run histories) two ancients changed:
/// Orobas moved Sand Castle from the first pool to the second (<c>NextItem(Shrymp, Glass Eye, first)</c>,
/// <c>NextItem(Coffer, Driftwood, Pearl, Sand Castle)</c>), and Darv split its act-gated pairs into single-relic sets
/// (Ectoplasm, Sozu: act 2; Philosopher's Stone, Velvet Choker: act 2 and later). The other five are unchanged.
/// </para>
/// </summary>
public static class AncientOptions
{
    /// <summary>Sea Glass's option key: shared by every character (its button title is the character).</summary>
    public const string SeaGlass = "SEA_GLASS";

    /// <summary>Orobas's third slot when neither Touch of Orobas nor Archaic Tooth fits the player.</summary>
    public const string OrobasLocked = "OPTION_POOL_3_LOCKED";

    private static readonly string[] Ancients = ["PAEL", "TEZCATARA", "OROBAS", "NONUPEIPE", "TANX", "VAKUU", "DARV"];

    /// <summary>ModelDb.AllCharacters order, with the epoch that unlocks each (Ironclad: always).</summary>
    private static readonly (string Character, string? Epoch)[] Characters =
    [
        ("CHARACTER.IRONCLAD", null), ("CHARACTER.SILENT", "SILENT1_EPOCH"), ("CHARACTER.REGENT", "REGENT1_EPOCH"),
        ("CHARACTER.NECROBINDER", "NECROBINDER1_EPOCH"), ("CHARACTER.DEFECT", "DEFECT1_EPOCH"),
    ];

    /// <summary>ArchaicTooth.TranscendenceUpgrades keys: the starter cards it can transform.</summary>
    private static readonly HashSet<string> TranscendenceStarters = new(StringComparer.Ordinal)
    {
        "CARD.BASH", "CARD.NEUTRALIZE", "CARD.UNLEASH", "CARD.FALLING_STAR", "CARD.DUALCAST",
    };

    /// <summary>True for the act-start ancients this class replays (Neow is <see cref="NeowOptions"/>).</summary>
    public static bool Handles(string eventId) =>
        eventId.StartsWith("EVENT.", StringComparison.Ordinal) && Ancients.Contains(eventId["EVENT.".Length..]);

    /// <summary>The ancient's stream seed: like Neow's, with the ancient's own id hashed in (<see cref="RngSeeds.EventStream"/>).</summary>
    public static ulong StreamSeed(string runSeed, int slot, string eventId, RngScheme scheme = RngSeeds.Default) =>
        RngSeeds.EventStream(scheme, runSeed, slot, Entry(eventId));

    /// <summary>
    /// The offered options in button order, as option-key suffixes (<c>PAELS_FLESH</c>, <see cref="SeaGlass"/>,
    /// <see cref="OrobasLocked"/>); null when <paramref name="eventId"/> is not a handled ancient.
    /// </summary>
    public static IReadOnlyList<string>? Options(string eventId, AncientInputs inputs) => Draw(eventId, inputs)?.Options;

    /// <summary>The options as event text keys (<c>PAEL.pages.INITIAL.options.PAELS_FLESH</c>), in button order.</summary>
    public static IReadOnlyList<string>? Keys(string eventId, AncientInputs inputs) =>
        Options(eventId, inputs)?.Select(o => KeyPrefix(eventId) + o).ToArray();

    /// <summary>
    /// The options plus, when Sea Glass is offered, the character it shows cards of (run history lists that option by
    /// the character's id, not by <c>SEA_GLASS</c>).
    /// </summary>
    public static AncientDraw? Draw(string eventId, AncientInputs inputs)
    {
        if (!Handles(eventId)) return null;
        var rng = new GameRng(StreamSeed(inputs.RunSeed, inputs.Slot, eventId, inputs.Scheme));
        string? seaGlass = null;
        IReadOnlyList<string> options = Entry(eventId) switch
        {
            "PAEL" => Pael(rng, inputs),
            "TEZCATARA" => Tezcatara(rng, inputs),
            "OROBAS" => Orobas(rng, inputs, out seaGlass),
            "NONUPEIPE" => Nonupeipe(rng, inputs),
            "TANX" => Tanx(rng, inputs),
            "VAKUU" => Vakuu(rng),
            "DARV" => Darv(rng, inputs),
            _ => throw new InvalidOperationException(eventId),
        };
        return new AncientDraw(options, seaGlass);
    }

    /// <summary>
    /// Every option this ancient can show (Orobas's locked placeholder aside), as option-key suffixes. The same set in
    /// both schemes (the v0.109 changes moved options between pools, they added none).
    /// </summary>
    public static IReadOnlyList<string> AllOptions(string eventId, RngScheme scheme = RngSeeds.Default) => Entry(eventId) switch
    {
        "PAEL" => ["PAELS_FLESH", "PAELS_HORN", "PAELS_TEARS", "PAELS_WING", "PAELS_EYE", "PAELS_BLOOD", "PAELS_CLAW", "PAELS_TOOTH", "PAELS_LEGION", "PAELS_GROWTH"],
        "TEZCATARA" => ["VERY_HOT_COCOA", "YUMMY_COOKIE", "BIIIG_HUG", "STORYBOOK", "TOASTY_MITTENS", "GOLDEN_COMPASS", "PUMPKIN_CANDLE", "TOY_BOX", "SEAL_OF_GOLD", "NUTRITIOUS_SOUP"],
        "OROBAS" => [.. OrobasPool1(scheme), .. OrobasPool2(scheme), "TOUCH_OF_OROBAS", "ARCHAIC_TOOTH", SeaGlass, "PRISMATIC_GEM"],
        "NONUPEIPE" => [.. NonupeipePool, "BEAUTIFUL_BRACELET"],
        "TANX" => [.. TanxPool, "TRI_BOOMERANG"],
        "VAKUU" => [.. VakuuPools.SelectMany(p => p)],
        "DARV" => [.. DarvSetsFor(scheme).SelectMany(s => s.Relics), "DUSTY_TOME"],
        _ => [],
    };

    /// <summary>
    /// The options this player could have drawn at this node (the pool the offered ones came from): every option minus the
    /// conditional ones the deck, relics, act or modifiers rule out. As text keys; null when the ancient isn't handled.
    /// </summary>
    public static IReadOnlyList<string>? PossibleKeys(string eventId, AncientInputs p)
    {
        if (!Handles(eventId)) return null;
        bool touch = TouchAllowed(p), tooth = ArchaicToothAllowed(p);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        void Unless(bool allowed, string option) { if (!allowed) excluded.Add(option); }
        switch (Entry(eventId))
        {
            case "PAEL":
                Unless(ClawAllowed(p), "PAELS_CLAW");
                Unless(PaelsToothAllowed(p), "PAELS_TOOTH");
                Unless(!HasEventPet(p), "PAELS_LEGION");
                break;
            case "TEZCATARA":
                Unless(SoupAllowed(p), "NUTRITIOUS_SOUP");
                break;
            case "OROBAS":
                Unless(touch, "TOUCH_OF_OROBAS");
                Unless(tooth, "ARCHAIC_TOOTH");
                break;
            case "NONUPEIPE":
                Unless(BraceletAllowed(p), "BEAUTIFUL_BRACELET");
                break;
            case "TANX":
                Unless(BoomerangAllowed(p), "TRI_BOOMERANG");
                break;
            case "DARV":
                foreach (var set in DarvSetsFor(p.Scheme))
                    foreach (string relic in set.Relics)
                        Unless(DarvGateOpen(set.Gate, p), relic);
                break;
        }
        var options = AllOptions(eventId, p.Scheme).Where(o => !excluded.Contains(o)).ToList();
        if (Entry(eventId) == "OROBAS" && !touch && !tooth) options.Add(OrobasLocked);
        return options.Select(o => KeyPrefix(eventId) + o).ToArray();
    }

    /// <summary><c>PAEL.pages.INITIAL.options.</c> for <c>EVENT.PAEL</c>.</summary>
    public static string KeyPrefix(string eventId) => Entry(eventId) + ".pages.INITIAL.options.";

    private static string Entry(string eventId) => eventId.StartsWith("EVENT.", StringComparison.Ordinal) ? eventId["EVENT.".Length..] : eventId;

    // ---------------------------------------------------------------------------------------------------------------
    // The ancients, in the game's draw order. Pool orders are the source's: they decide which item a draw lands on.
    // ---------------------------------------------------------------------------------------------------------------

    private static string[] Pael(GameRng rng, AncientInputs p)
    {
        string first = NextItem(rng, ["PAELS_FLESH", "PAELS_HORN", "PAELS_TEARS"])!;
        var second = new List<string> { "PAELS_WING" };
        if (ClawAllowed(p)) second.Add("PAELS_CLAW");
        if (PaelsToothAllowed(p)) second.Add("PAELS_TOOTH");
        second.AddRange(second.ToArray()); // list.AddRange(list): every entry but Growth twice as likely
        second.Add("PAELS_GROWTH");
        string middle = NextItem(rng, second)!;
        var third = new List<string> { "PAELS_EYE", "PAELS_BLOOD" };
        if (!HasEventPet(p)) third.Add("PAELS_LEGION");
        return [first, middle, NextItem(rng, third)!];
    }

    private static string[] Tezcatara(GameRng rng, AncientInputs p)
    {
        var first = new List<string> { "VERY_HOT_COCOA", "YUMMY_COOKIE" };
        if (SoupAllowed(p)) first.Add("NUTRITIOUS_SOUP");
        return
        [
            NextItem(rng, first)!,
            NextItem(rng, ["BIIIG_HUG", "STORYBOOK", "TOASTY_MITTENS"])!,
            NextItem(rng, ["GOLDEN_COMPASS", "PUMPKIN_CANDLE", "TOY_BOX", "SEAL_OF_GOLD"])!,
        ];
    }

    private static string[] Orobas(GameRng rng, AncientInputs p, out string? seaGlass)
    {
        var others = Characters
            .Where(c => c.Epoch is null || p.UnlockedEpochs is null || p.UnlockedEpochs.Contains(c.Epoch))
            .Select(c => c.Character)
            .Where(c => c != p.Character)
            .ToList();
        string other = NextItem(rng, others) ?? p.Character;
        string first;
        if (rng.NextFloat() < 0.3333333f) { first = "PRISMATIC_GEM"; seaGlass = null; }
        else { first = SeaGlass; seaGlass = other; }
        string a = NextItem(rng, [.. OrobasPool1(p.Scheme), first])!;
        if (a != SeaGlass) seaGlass = null;
        string b = NextItem(rng, OrobasPool2(p.Scheme))!;
        var third = new List<string>();
        if (TouchAllowed(p)) third.Add("TOUCH_OF_OROBAS");
        if (ArchaicToothAllowed(p)) third.Add("ARCHAIC_TOOTH");
        if (third.Count == 0) third.Add(OrobasLocked);
        return [a, b, NextItem(rng, third)!];
    }

    /// <summary>Orobas.OptionPool1 (before the Prismatic Gem / Sea Glass slot is appended): v0.109 moved Sand Castle to pool 2.</summary>
    private static string[] OrobasPool1(RngScheme scheme) =>
        scheme == RngScheme.Legacy32 ? ["ELECTRIC_SHRYMP", "GLASS_EYE", "SAND_CASTLE"] : ["ELECTRIC_SHRYMP", "GLASS_EYE"];

    /// <summary>Orobas.OptionPool2.</summary>
    private static string[] OrobasPool2(RngScheme scheme) =>
        scheme == RngScheme.Legacy32 ? ["ALCHEMICAL_COFFER", "DRIFTWOOD", "RADIANT_PEARL"] : ["ALCHEMICAL_COFFER", "DRIFTWOOD", "RADIANT_PEARL", "SAND_CASTLE"];

    private static readonly string[] NonupeipePool =
    [
        "BLESSED_ANTLER", "BRILLIANT_SCARF", "DELICATE_FROND", "DIAMOND_DIADEM", "FUR_COAT", "GLITTER", "JEWELRY_BOX", "LOOMING_FRUIT", "SIGNET_RING",
    ];

    private static string[] Nonupeipe(GameRng rng, AncientInputs p)
    {
        var list = NonupeipePool.ToList();
        if (BraceletAllowed(p)) list.Add("BEAUTIFUL_BRACELET");
        rng.Shuffle(list);
        return [.. list.Take(3)];
    }

    private static readonly string[] TanxPool =
    [
        "CLAWS", "CROSSBOW", "IRON_CLUB", "MEAT_CLEAVER", "SAI", "SPIKED_GAUNTLETS", "TANXS_WHISTLE", "THROWING_AXE", "WAR_HAMMER",
    ];

    private static string[] Tanx(GameRng rng, AncientInputs p)
    {
        var list = TanxPool.ToList();
        if (BoomerangAllowed(p)) list.Add("TRI_BOOMERANG");
        rng.Shuffle(list);
        return [.. list.Take(3)];
    }

    private static readonly string[][] VakuuPools =
    [
        ["BLOOD_SOAKED_ROSE", "WHISPERING_EARRING", "FIDDLE"],
        ["PRESERVED_FOG", "SERE_TALON", "DISTINGUISHED_CAPE"],
        ["CHOICES_PARADOX", "MUSIC_BOX", "LORDS_PARASOL", "JEWELED_MASK"],
    ];

    private static string[] Vakuu(GameRng rng)
    {
        var pools = VakuuPools.Select(p => p.ToList()).ToArray();
        foreach (var pool in pools) rng.Shuffle(pool); // all three are shuffled before any is read
        return [pools[0][0], pools[1][0], pools[2][0]];
    }

    private enum DarvGate { None, NoDeckClearingModifier, Act2, Act3, Act2OrLater }

    /// <summary>Darv._validRelicSets, v0.107.1: the act-gated relics come in pairs.</summary>
    private static readonly (DarvGate Gate, string[] Relics)[] DarvSetsV107 =
    [
        (DarvGate.None, ["ASTROLABE"]), (DarvGate.None, ["BLACK_STAR"]), (DarvGate.None, ["CALLING_BELL"]), (DarvGate.None, ["EMPTY_CAGE"]),
        (DarvGate.NoDeckClearingModifier, ["PANDORAS_BOX"]), (DarvGate.None, ["RUNIC_PYRAMID"]), (DarvGate.None, ["SNECKO_EYE"]),
        (DarvGate.Act2, ["ECTOPLASM", "SOZU"]), (DarvGate.Act3, ["PHILOSOPHERS_STONE", "VELVET_CHOKER"]),
    ];

    /// <summary>Darv._validRelicSets, v0.109+: every relic is its own set; Philosopher's Stone and Velvet Choker from act 2 on.</summary>
    private static readonly (DarvGate Gate, string[] Relics)[] DarvSetsV109 =
    [
        (DarvGate.None, ["ASTROLABE"]), (DarvGate.None, ["BLACK_STAR"]), (DarvGate.None, ["CALLING_BELL"]), (DarvGate.None, ["EMPTY_CAGE"]),
        (DarvGate.NoDeckClearingModifier, ["PANDORAS_BOX"]), (DarvGate.None, ["RUNIC_PYRAMID"]), (DarvGate.None, ["SNECKO_EYE"]),
        (DarvGate.Act2, ["ECTOPLASM"]), (DarvGate.Act2, ["SOZU"]),
        (DarvGate.Act2OrLater, ["PHILOSOPHERS_STONE"]), (DarvGate.Act2OrLater, ["VELVET_CHOKER"]),
    ];

    private static (DarvGate Gate, string[] Relics)[] DarvSetsFor(RngScheme scheme) =>
        scheme == RngScheme.Legacy32 ? DarvSetsV107 : DarvSetsV109;

    private static bool DarvGateOpen(DarvGate gate, AncientInputs p) => gate switch
    {
        DarvGate.NoDeckClearingModifier => !p.DeckClearingModifier,
        DarvGate.Act2 => p.ActIndex == 1,
        DarvGate.Act3 => p.ActIndex == 2,
        DarvGate.Act2OrLater => p.ActIndex >= 1,
        _ => true,
    };

    private static string[] Darv(GameRng rng, AncientInputs p)
    {
        var picks = DarvSetsFor(p.Scheme)
            .Where(s => DarvGateOpen(s.Gate, p))
            .Select(s => NextItem(rng, s.Relics)!) // one draw per set, single-relic sets included
            .ToList();
        rng.Shuffle(picks);
        return NextBool(rng) ? [.. picks.Take(2), "DUSTY_TOME"] : [.. picks.Take(3)];
    }

    // ---------------------------------------------------------------------------------------------------------------

    // The conditional options' rules (shared by the draw and by PossibleKeys).
    private static bool ClawAllowed(AncientInputs p) => p.Deck.Count(c => CanEnchant(c, "Goopy", p.Scheme)) >= 3;
    private static bool PaelsToothAllowed(AncientInputs p) => p.Deck.Count(c => !CardRarities.HasCanonicalKeyword(c.Id, "Eternal", p.Scheme)) >= 5; // IsRemovable
    private static bool SoupAllowed(AncientInputs p) => p.Deck.Any(c => CardRarities.HasTag(c.Id, "Strike", p.Scheme) && CardRarities.Of(c.Id, p.Scheme) == "Basic");
    private static bool TouchAllowed(AncientInputs p) => p.Relics.Any(r => RelicTrading.RarityOf(r, p.Scheme) == "Starter"); // TouchOfOrobas.SetupForPlayer
    private static bool ArchaicToothAllowed(AncientInputs p) => p.Deck.Any(c => TranscendenceStarters.Contains(c.Id)); // ArchaicTooth.SetupForPlayer
    private static bool BraceletAllowed(AncientInputs p) => p.Deck.Count(c => CanEnchant(c, "Swift", p.Scheme)) >= 4;
    private static bool BoomerangAllowed(AncientInputs p) => p.Deck.Count(c => CanEnchant(c, "Instinct", p.Scheme)) >= 3;

    /// <summary>
    /// EnchantmentModel.CanEnchant for the three enchantments ancients count: never Status / Curse / Quest cards, never an
    /// Unplayable deck card, never an already enchanted card (none of them stacks); Goopy only Defend-tagged cards,
    /// Instinct only Attacks, Swift anything else.
    /// </summary>
    private static bool CanEnchant(AncientDeckCard card, string enchantment, RngScheme scheme)
    {
        string? type = CardRarities.TypeOf(card.Id, scheme);
        if (type is null or "Status" or "Curse" or "Quest") return false;
        if (CardRarities.HasCanonicalKeyword(card.Id, "Unplayable", scheme)) return false;
        if (card.EnchantmentId is not null) return false;
        return enchantment switch
        {
            "Goopy" => CardRarities.HasTag(card.Id, "Defend", scheme),
            "Instinct" => type == "Attack",
            _ => true,
        };
    }

    /// <summary>Player.HasEventPet: a relic that adds a pet (Byrdpip, Pael's Legion) or Byrdonis Egg in the deck.</summary>
    private static bool HasEventPet(AncientInputs p) =>
        p.Relics.Any(r => RelicTrading.AddsPet(r, p.Scheme)) || p.Deck.Any(c => c.Id == "CARD.BYRDONIS_EGG");

    /// <summary>Rng.NextItem: nothing drawn for an empty list.</summary>
    private static T? NextItem<T>(GameRng rng, IReadOnlyList<T> items) => items.Count == 0 ? default : items[rng.NextInt(items.Count)];

    /// <summary>Rng.NextBool: <c>MegaRandom.Next(2) == 0</c>.</summary>
    private static bool NextBool(GameRng rng) => rng.NextInt(2) == 0;
}

/// <summary>An ancient's replayed options (key suffixes, button order) and Sea Glass's character when it is offered.</summary>
public sealed record AncientDraw(IReadOnlyList<string> Options, string? SeaGlassCharacter);
