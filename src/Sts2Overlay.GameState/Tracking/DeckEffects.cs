namespace Sts2Overlay.GameState.Tracking;

/// <summary>What the next <c>Player N chose cards [...]</c> line means after an event option / relic pickup.</summary>
public enum SelectMeaning
{
    None,
    /// <summary>The chosen deck cards leave the deck (CardSelectCmd.FromDeckForRemoval).</summary>
    Remove,
    /// <summary>The chosen deck cards become another card: <see cref="DeckEffect.TransformTo"/> if fixed, else unknown (random).</summary>
    Transform,
    /// <summary>The chosen deck cards are upgraded (FromDeckForUpgrade).</summary>
    Upgrade,
    /// <summary>The chosen deck cards get an enchantment (FromDeckForEnchantment).</summary>
    Enchant,
    /// <summary>The chosen cards come from a grid of NEW cards and join the deck (FromSimpleGrid / FromChooseACardScreen).</summary>
    AddChosen,
    /// <summary>The chosen deck card is copied (Dolly's Mirror).</summary>
    Duplicate,
}

/// <summary>How a random upgrade picks its cards (see <see cref="DeckEffect.Pick"/>).</summary>
public enum UpgradePick
{
    /// <summary>A stream we can't replay (the run's shared Niche stream: its position depends on every monster spawned).</summary>
    Unknown,
    /// <summary><c>deck.Where(IsUpgradable).ToList().StableShuffle(event.Rng).Take(n)</c> as the event's first RNG use.</summary>
    EventShuffle,
    /// <summary><c>event.Rng.NextItem(deck.Where(IsUpgradable))</c> as the event's call number <see cref="DeckEffect.EventRngCall"/>.</summary>
    EventNextItem,
}

/// <summary>
/// The deck side of one event option or relic pickup, as far as v0.107.1 does it deterministically.
/// Random parts are counted, not guessed: <see cref="RandomUpgrades"/> (which card is upgraded comes from the game RNG and
/// is never logged), <see cref="UnknownAdds"/> (a random card joins the deck; combat plays may identify it later),
/// <see cref="RandomRemoves"/> (a random deck card leaves).
/// </summary>
public sealed record DeckEffect
{
    public SelectMeaning Select { get; init; }
    /// <summary>Fixed transform target (Wood Carvings → PECK); null = random result.</summary>
    public string? TransformTo { get; init; }
    /// <summary>Enchantment applied by <see cref="SelectMeaning.Enchant"/>, when known.</summary>
    public string? Enchantment { get; init; }
    /// <summary>Fixed cards added to the deck (curses, event cards).</summary>
    public IReadOnlyList<string> Adds { get; init; } = [];
    /// <summary>Fixed cards removed from the deck (e.g. the Lantern Key).</summary>
    public IReadOnlyList<string> Removes { get; init; } = [];
    public int RandomUpgrades { get; init; }
    /// <summary>Card type the random upgrades are limited to (Whetstone: Attack, War Paint: Skill); null = any.</summary>
    public string? RandomUpgradeType { get; init; }
    /// <summary>
    /// How the random upgrades pick cards, when the stream is one we can replay from the run seed: the event's own
    /// stream (<c>EventModel.Rng</c>, seeded by run seed + player slot + event id, fresh for each event).
    /// </summary>
    public UpgradePick Pick { get; init; }
    /// <summary><see cref="UpgradePick.EventNextItem"/>: which call of the event stream picks the card (0-based).</summary>
    public int EventRngCall { get; init; }
    /// <summary>
    /// A random transform (<see cref="TransformTo"/> null) done with the event's own stream
    /// (<c>CardCmd.TransformToRandom(card, Rng)</c>): which call of that stream picks the new card (0-based). Null = a
    /// stream we can't replay.
    /// </summary>
    public int? TransformRngCall { get; init; }
    public int UnknownAdds { get; init; }
    public int RandomRemoves { get; init; }
    /// <summary>Upgrade one basic Strike and one basic Defend (Neow's Talisman).</summary>
    public bool UpgradeBasicStrikeAndDefend { get; init; }
    /// <summary>Transform every basic Strike/Defend into a random card (Pandora's Box).</summary>
    public bool TransformAllBasics { get; init; }
    /// <summary>Transform one basic Strike and one basic Defend into random cards (Leafy Poultice).</summary>
    public bool TransformOneStrikeAndDefend { get; init; }
    /// <summary>Archaic Tooth: the starter card is replaced by its upgraded form (keeps the upgrade level).</summary>
    public bool Transcend { get; init; }
    /// <summary>Tablet of Truth, 5th decipher: every upgradable card in the deck is upgraded.</summary>
    public bool UpgradeAll { get; init; }
}

/// <summary>
/// Event options and relic pickups that change the deck, keyed by the log's choice key / relic id. Built from the
/// decompiled v0.107.1 source (<c>Models.Events/*.cs</c> option handlers, <c>Models.Relics/*.cs</c> AfterObtained) and
/// checked against the per-floor <c>cards_gained/removed/transformed/upgraded</c> of 51 run-history files.
/// Options that don't touch the deck are simply absent.
/// </summary>
public static class DeckEffects
{
    private const string C = "CARD.";

    private static DeckEffect Sel(SelectMeaning m, params string[] adds) => new() { Select = m, Adds = adds.Select(a => C + a).ToList() };
    private static DeckEffect Add(params string[] cards) => new() { Adds = cards.Select(a => C + a).ToList() };
    private static DeckEffect Ench(string? enchantment = null) => new() { Select = SelectMeaning.Enchant, Enchantment = enchantment is null ? null : "ENCHANTMENT." + enchantment };
    private static DeckEffect Upgrades(int n, string? type = null) => new() { RandomUpgrades = n, RandomUpgradeType = type };
    // Tablet of Truth: decipher k (1..4) upgrades Rng.NextItem(upgradable deck cards) with the event's k-th call.
    // An event's random transform: CardCmd.TransformToRandom(card, event Rng) as the event stream's call number `call`.
    private static DeckEffect EventTransform(int call) => new() { Select = SelectMeaning.Transform, TransformRngCall = call };
    private static DeckEffect Decipher(int k) => new() { RandomUpgrades = 1, Pick = UpgradePick.EventNextItem, EventRngCall = k - 1 };

    /// <summary>Event options by full choice key (<c>EVENT.pages.PAGE.options.OPTION</c>).</summary>
    private static readonly Dictionary<string, DeckEffect> s_events = new(StringComparer.Ordinal)
    {
        ["AMALGAMATOR.pages.INITIAL.options.COMBINE_STRIKES"] = Sel(SelectMeaning.Remove, "ULTIMATE_STRIKE"),
        ["AMALGAMATOR.pages.INITIAL.options.COMBINE_DEFENDS"] = Sel(SelectMeaning.Remove, "ULTIMATE_DEFEND"),
        ["AROMA_OF_CHAOS.pages.INITIAL.options.LET_GO"] = EventTransform(0),
        ["AROMA_OF_CHAOS.pages.INITIAL.options.MAINTAIN_CONTROL"] = Sel(SelectMeaning.Upgrade),
        ["BATTLEWORN_DUMMY.pages.INITIAL.options.SETTING_2"] = Upgrades(2), // after winning the dummy fight: 2 random upgrades
        ["BRAIN_LEECH.pages.INITIAL.options.SHARE_KNOWLEDGE"] = Sel(SelectMeaning.AddChosen),
        ["BYRDONIS_NEST.pages.INITIAL.options.TAKE"] = Add("BYRDONIS_EGG"),
        ["CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN"] = Add("DEBT"),
        ["DOORS_OF_LIGHT_AND_DARK.pages.INITIAL.options.LIGHT"] = new() { RandomUpgrades = 2, Pick = UpgradePick.EventShuffle },
        ["DOORS_OF_LIGHT_AND_DARK.pages.INITIAL.options.DARK"] = Sel(SelectMeaning.Remove),
        ["ENDLESS_CONVEYOR.pages.INITIAL.options.OBSERVE_CHEF"] = Upgrades(1),
        ["ENDLESS_CONVEYOR.pages.ALL.options.JELLY_LIVER"] = Sel(SelectMeaning.Transform),
        ["ENDLESS_CONVEYOR.pages.ALL.options.SPICY_SNAPPY"] = Upgrades(1),
        ["ENDLESS_CONVEYOR.pages.ALL.options.SEAPUNK_SALAD"] = Add("FEEDING_FRENZY"),
        ["ENDLESS_CONVEYOR.pages.ALL.options.FRIED_EEL"] = new() { UnknownAdds = 1 },
        ["FIELD_OF_MAN_SIZED_HOLES.pages.INITIAL.options.RESIST"] = Sel(SelectMeaning.Remove, "NORMALITY"),
        ["FIELD_OF_MAN_SIZED_HOLES.pages.INITIAL.options.ENTER_YOUR_HOLE"] = Ench("PERFECT_FIT"),
        ["GRAVE_OF_THE_FORGOTTEN.pages.INITIAL.options.CONFRONT"] = Ench("SOULS_POWER") with { Adds = [C + "DECAY"] },
        ["INFESTED_AUTOMATON.pages.INITIAL.options.STUDY"] = new() { UnknownAdds = 1 },
        ["INFESTED_AUTOMATON.pages.INITIAL.options.TOUCH_CORE"] = new() { UnknownAdds = 1 },
        ["LOST_WISP.pages.INITIAL.options.CLAIM"] = Add("DECAY"),
        ["LUMINOUS_CHOIR.pages.INITIAL.options.REACH_INTO_THE_FLESH"] = Sel(SelectMeaning.Remove, "SPORE_MIND"),
        ["MORPHIC_GROVE.pages.INITIAL.options.GROUP"] = EventTransform(0),
        ["PUNCH_OFF.pages.INITIAL.options.NAB"] = Add("INJURY"),
        ["REFLECTIONS.pages.INITIAL.options.SHATTER"] = Add("BAD_LUCK"),
        ["ROOM_FULL_OF_CHEESE.pages.INITIAL.options.GORGE"] = Sel(SelectMeaning.AddChosen),
        ["SAPPHIRE_SEED.pages.INITIAL.options.EAT"] = Sel(SelectMeaning.Upgrade),
        ["SAPPHIRE_SEED.pages.INITIAL.options.PLANT"] = Ench("SOWN"),
        ["SELF_HELP_BOOK.pages.INITIAL.options.READ_THE_BACK"] = Ench(),
        ["SELF_HELP_BOOK.pages.INITIAL.options.READ_PASSAGE"] = Ench(),
        ["SELF_HELP_BOOK.pages.INITIAL.options.READ_ENTIRE_BOOK"] = Ench(),
        ["SLIPPERY_BRIDGE.pages.INITIAL.options.OVERCOME"] = new() { RandomRemoves = 1 },
        ["SPIRALING_WHIRLPOOL.pages.INITIAL.options.OBSERVE"] = Ench("SPIRAL"),
        ["SPIRIT_GRAFTER.pages.INITIAL.options.LET_IT_IN"] = Add("METAMORPHOSIS"),
        ["SPIRIT_GRAFTER.pages.INITIAL.options.REJECTION"] = Sel(SelectMeaning.Upgrade),
        ["STONE_OF_ALL_TIME.pages.INITIAL.options.PUSH"] = Ench(),
        ["SUNKEN_TREASURY.pages.INITIAL.options.SECOND_CHEST"] = Add("GREED"),
        ["SYMBIOTE.pages.INITIAL.options.APPROACH"] = Ench("CORRUPTED"),
        ["SYMBIOTE.pages.INITIAL.options.KILL_WITH_FIRE"] = EventTransform(0),
        // THE_LANTERN_KEY FIGHT: the key comes as a special card reward after the fight, which IS logged ("obtained ... from special card reward").
        // Tablet of Truth (TabletOfTruth.LoseMaxHpAndUpgrade): each decipher upgrades one random card, the 5th all of them.
        ["TABLET_OF_TRUTH.pages.INITIAL.options.DECIPHER_1"] = Decipher(1),
        ["TABLET_OF_TRUTH.pages.DECIPHER_1.options.DECIPHER"] = Decipher(2),
        ["TABLET_OF_TRUTH.pages.DECIPHER_2.options.DECIPHER"] = Decipher(3),
        ["TABLET_OF_TRUTH.pages.DECIPHER_3.options.DECIPHER"] = Decipher(4),
        ["TABLET_OF_TRUTH.pages.DECIPHER_4.options.DECIPHER"] = new() { UpgradeAll = true },
        ["THE_LEGENDS_WERE_TRUE.pages.INITIAL.options.NAB_THE_MAP"] = Add("SPOILS_MAP"),
        ["THIS_OR_THAT.pages.INITIAL.options.ORNATE"] = Add("CLUMSY"),
        ["TRASH_HEAP.pages.INITIAL.options.GRAB"] = new() { UnknownAdds = 1 },
        ["TRIAL.pages.MERCHANT.options.GUILTY"] = Add("REGRET"),
        ["TRIAL.pages.MERCHANT.options.INNOCENT"] = Sel(SelectMeaning.Upgrade, "SHAME"),
        ["TRIAL.pages.NOBLE.options.INNOCENT"] = Add("REGRET"),
        ["TRIAL.pages.NONDESCRIPT.options.GUILTY"] = Add("DOUBT"),
        ["TRIAL.pages.NONDESCRIPT.options.INNOCENT"] = Sel(SelectMeaning.Transform, "DOUBT"),
        ["UNREST_SITE.pages.INITIAL.options.REST"] = Add("POOR_SLEEP"),
        ["WAR_HISTORIAN_REPY.pages.INITIAL.options.UNLOCK_CAGE"] = new() { Removes = [C + "LANTERN_KEY"] },
        ["WAR_HISTORIAN_REPY.pages.INITIAL.options.UNLOCK_CHEST"] = new() { Removes = [C + "LANTERN_KEY"] },
        ["WATERLOGGED_SCRIPTORIUM.pages.INITIAL.options.TENTACLE_QUILL"] = Ench("STEADY"),
        ["WATERLOGGED_SCRIPTORIUM.pages.INITIAL.options.PRICKLY_SPONGE"] = Ench("STEADY"),
        ["WELLSPRING.pages.INITIAL.options.BATHE"] = Sel(SelectMeaning.Remove, "GUILTY"),
        // Wongo's bargain bin: an unnamed common relic (RelicFactory.PullNextRelicFromFront, no log line). Whetstone /
        // War Paint would upgrade 2 random cards: count them as possible unplaced upgrades.
        ["WELCOME_TO_WONGOS.pages.INITIAL.options.BARGAIN_BIN"] = Upgrades(2),
        ["WHISPERING_HOLLOW.pages.INITIAL.options.HUG"] = EventTransform(1), // CalculateVars draws the gold first
        ["WOOD_CARVINGS.pages.INITIAL.options.BIRD"] = new() { Select = SelectMeaning.Transform, TransformTo = C + "PECK" },
        ["WOOD_CARVINGS.pages.INITIAL.options.SNAKE"] = Ench("SLITHER"),
        ["WOOD_CARVINGS.pages.INITIAL.options.TORUS"] = new() { Select = SelectMeaning.Transform, TransformTo = C + "TORIC_TOUGHNESS" },
        ["ZEN_WEAVER.pages.INITIAL.options.BREATHING_TECHNIQUES"] = Add("ENLIGHTENMENT"),
        ["ZEN_WEAVER.pages.INITIAL.options.EMOTIONAL_AWARENESS"] = Sel(SelectMeaning.Remove),
        ["ZEN_WEAVER.pages.INITIAL.options.ARACHNID_ACUPUNCTURE"] = Sel(SelectMeaning.Remove),
    };

    /// <summary>Relic pickups (ancient options, relic rewards, treasure picks) by relic id.</summary>
    private static readonly Dictionary<string, DeckEffect> s_relics = new(StringComparer.Ordinal)
    {
        ["RELIC.ASTROLABE"] = Sel(SelectMeaning.Transform),
        ["RELIC.ARCANE_SCROLL"] = new() { UnknownAdds = 1 },
        ["RELIC.ARCHAIC_TOOTH"] = new() { Transcend = true },
        ["RELIC.BEAUTIFUL_BRACELET"] = Ench("SWIFT"),
        ["RELIC.BIIIG_HUG"] = Sel(SelectMeaning.Remove),
        ["RELIC.BLOOD_SOAKED_ROSE"] = Add("ENTHRALLED"),
        ["RELIC.CALLING_BELL"] = Add("CURSE_OF_THE_BELL"),
        ["RELIC.CURSED_PEARL"] = Add("GREED"),
        ["RELIC.DISTINGUISHED_CAPE"] = Add("APPARITION", "APPARITION", "APPARITION"),
        ["RELIC.DOLLYS_MIRROR"] = Sel(SelectMeaning.Duplicate),
        ["RELIC.ELECTRIC_SHRYMP"] = Ench(),
        ["RELIC.EMPTY_CAGE"] = Sel(SelectMeaning.Remove),
        ["RELIC.FRAGRANT_MUSHROOM"] = Upgrades(2),
        ["RELIC.GNARLED_HAMMER"] = Ench(),
        ["RELIC.HEFTY_TABLET"] = Sel(SelectMeaning.AddChosen, "INJURY"),
        ["RELIC.JEWELRY_BOX"] = Add("APOTHEOSIS"),
        ["RELIC.KIFUDA"] = Ench(),
        ["RELIC.LEAD_PAPERWEIGHT"] = Sel(SelectMeaning.AddChosen),
        ["RELIC.LEAFY_POULTICE"] = new() { TransformOneStrikeAndDefend = true },
        ["RELIC.MASSIVE_SCROLL"] = Sel(SelectMeaning.AddChosen),
        ["RELIC.NEOWS_TALISMAN"] = new() { UpgradeBasicStrikeAndDefend = true },
        ["RELIC.NEOWS_TORMENT"] = Add("NEOWS_FURY"),
        ["RELIC.NEW_LEAF"] = Sel(SelectMeaning.Transform),
        ["RELIC.PAELS_GROWTH"] = Ench("CLONE"),
        ["RELIC.PAELS_HORN"] = Add("RELAX"),
        ["RELIC.PAELS_TOOTH"] = Sel(SelectMeaning.Remove),
        ["RELIC.PANDORAS_BOX"] = new() { TransformAllBasics = true },
        ["RELIC.POMANDER"] = Sel(SelectMeaning.Upgrade),
        ["RELIC.PRECARIOUS_SHEARS"] = Sel(SelectMeaning.Remove),
        ["RELIC.PRECISE_SCISSORS"] = Sel(SelectMeaning.Remove),
        ["RELIC.PRESERVED_FOG"] = Sel(SelectMeaning.Remove, "FOLLY"),
        ["RELIC.PUNCH_DAGGER"] = Ench(),
        ["RELIC.ROYAL_STAMP"] = Ench("ROYALLY_APPROVED"),
        ["RELIC.SAND_CASTLE"] = Upgrades(6),
        ["RELIC.SCROLL_BOXES"] = Sel(SelectMeaning.AddChosen),
        ["RELIC.SEA_GLASS"] = Sel(SelectMeaning.AddChosen),
        ["RELIC.STORYBOOK"] = Add("BRIGHTEST_FLAME"),
        ["RELIC.TANXS_WHISTLE"] = Add("WHISTLE"),
        ["RELIC.TRI_BOOMERANG"] = Ench("INSTINCT"),
        ["RELIC.WAR_PAINT"] = Upgrades(2, "Skill"),
        ["RELIC.WHETSTONE"] = Upgrades(2, "Attack"),
        ["RELIC.YUMMY_COOKIE"] = Sel(SelectMeaning.Upgrade),
    };

    /// <summary>Archaic Tooth's starter → "transcended" card (ArchaicTooth.GetTranscendenceTransformedCard).</summary>
    public static readonly IReadOnlyDictionary<string, string> Transcendence = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [C + "BASH"] = C + "BREAK",
        [C + "NEUTRALIZE"] = C + "SUPPRESS",
        [C + "UNLEASH"] = C + "PROTECTOR",
        [C + "FALLING_STAR"] = C + "METEOR_SHOWER",
        [C + "DUALCAST"] = C + "QUADCAST",
    };

    /// <summary>Effect of an event option, or of the relic an ancient option grants (key ends with the relic id).</summary>
    public static DeckEffect? ForChoice(string eventId, string choiceKey)
    {
        if (s_events.TryGetValue(choiceKey, out DeckEffect? e)) return e;
        // Ancients (Neow, Pael, Vakuu, Orobas, Tanx, Darv, ...): "NEOW.pages.INITIAL.options.PRECARIOUS_SHEARS".
        int dot = choiceKey.LastIndexOf('.');
        return dot >= 0 ? ForRelic("RELIC." + choiceKey[(dot + 1)..]) : null;
    }

    public static DeckEffect? ForRelic(string relicId) => s_relics.GetValueOrDefault(relicId);

    /// <summary>Ancient events (subclasses of AncientEventModel): every option grants a relic.</summary>
    public static bool IsAncient(string eventId) => s_ancients.Contains(eventId);

    private static readonly HashSet<string> s_ancients = new(StringComparer.Ordinal)
    {
        "EVENT.NEOW", "EVENT.DARV", "EVENT.NONUPEIPE", "EVENT.OROBAS", "EVENT.PAEL", "EVENT.TANX", "EVENT.TEZCATARA", "EVENT.VAKUU",
    };

    /// <summary>Basic Strike / Defend of any character (<c>CARD.STRIKE_DEFECT</c> ...).</summary>
    public static bool IsBasicStrike(string id) => id.StartsWith(C + "STRIKE_", StringComparison.Ordinal);
    public static bool IsBasicDefend(string id) => id.StartsWith(C + "DEFEND_", StringComparison.Ordinal);
}
