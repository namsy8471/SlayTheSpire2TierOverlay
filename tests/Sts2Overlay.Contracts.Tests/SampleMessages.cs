namespace Sts2Overlay.Contracts.Tests;

/// <summary>One fully populated instance of every message type.</summary>
internal static class SampleMessages
{
    public static readonly ScreenRect Rect = new(100.5, 200, 180, 250);

    public static HelloMessage Hello => new()
    {
        Seq = 1,
        GameVersion = "0.98.3",
        GameCommit = "abc1234",
        ModVersion = "0.1.0",
        IsMultiplayer = true,
        LocalPlayerId = "76561190000000001",
    };

    public static RunSnapshotMessage Snapshot => new()
    {
        Seq = 2,
        Character = "CHARACTER.DEFECT",
        Ascension = 3,
        Act = 2,
        Floor = 23,
        Hp = 41,
        MaxHp = 75,
        Gold = 187,
        Deck =
        [
            new SnapshotCard { Id = "CARD.STRIKE_DEFECT", Upgrade = 0, InstanceId = "c1" },
            new SnapshotCard
            {
                Id = "CARD.BALL_LIGHTNING",
                Upgrade = 1,
                InstanceId = "c2",
                Enchantment = new CardEnchantment { Id = "ENCHANTMENT.SHARP", Amount = 2 },
                FloorAdded = 4,
            },
        ],
        Relics = ["RELIC.CRACKED_CORE", "RELIC.ANCHOR"],
        Potions = ["POTION.FIRE_POTION"],
        CardRemovalsBought = 1,
        Map = new MapSnapshot
        {
            Nodes =
            [
                new MapNode { Coord = new MapCoord(0, 0), Type = MapNodeTypes.Monster },
                new MapNode { Coord = new MapCoord(1, 1), Type = MapNodeTypes.Elite },
            ],
            Edges = [new MapEdge(new MapCoord(0, 0), new MapCoord(1, 1))],
            Current = new MapCoord(0, 0),
        },
    };

    public static ScreenOpenedMessage CardReward => new()
    {
        Seq = 3,
        Screen = ScreenKinds.CardReward,
        Context = RewardContexts.Elite,
        Items =
        [
            new ScreenItem { InstanceId = "r1", CardId = "CARD.COOLHEADED", Upgrade = 0, Rect = Rect },
            new ScreenItem { InstanceId = "r2", CardId = "CARD.GLACIER", Upgrade = 1, Rect = Rect with { X = 300 } },
        ],
        Alternatives = [new ScreenAlternative { Kind = AlternativeKinds.Skip, Rect = Rect with { Y = 900 } }],
        Viewport = new ScreenSize(1920, 1080),
    };

    public static ScreenOpenedMessage Merchant => new()
    {
        Seq = 4,
        Screen = ScreenKinds.Merchant,
        Items =
        [
            new ScreenItem { InstanceId = "m1", CardId = "CARD.DEFRAGMENT", Price = 75 },
            new ScreenItem { InstanceId = "m2", CardId = "CARD.ECHO_FORM", Upgrade = 1, Price = 160 },
        ],
        RemovalPrice = 100,
    };

    public static ScreenClosedMessage Closed => new() { Seq = 5, Screen = ScreenKinds.CardReward };

    public static RunEventMessage CardObtained => new()
    {
        Seq = 6,
        Event = RunEventKinds.CardObtained,
        Card = new RunEventCard { Id = "CARD.GLACIER", Upgrade = 1, InstanceId = "c3" },
        Act = 2,
        Floor = 23,
    };

    public static RunEventMessage RoomEntered => new()
    {
        Seq = 7,
        Event = RunEventKinds.RoomEntered,
        Room = MapNodeTypes.Shop,
        Coord = new MapCoord(3, 7),
        Act = 2,
        Floor = 24,
    };

    public static RunEventMessage RunEnded => new() { Seq = 8, Event = RunEventKinds.RunEnded, Victory = false };

    public static BadgesSetMessage Badges => new()
    {
        Seq = 9,
        Badges =
        [
            new CardBadge { InstanceId = "r1", Label = "S", Color = TierColors.S, Score = 91.5, IsBest = true, Chips = ["광역 부족", "빌드 핵심"] },
            new CardBadge { InstanceId = "r2", Label = "C", Color = TierColors.C, Score = 40 },
        ],
        SkipRecommended = false,
        ScreenSeq = 3,
    };

    public static BadgesClearMessage Clear => new() { Seq = 10 };

    public static TheoryData<string> Names => new()
    {
        nameof(Hello), nameof(Snapshot), nameof(CardReward), nameof(Merchant), nameof(Closed),
        nameof(CardObtained), nameof(RoomEntered), nameof(RunEnded), nameof(Badges), nameof(Clear),
    };

    public static (BridgeMessage Message, string Type) Get(string name) => name switch
    {
        nameof(Hello) => (Hello, MessageTypes.Hello),
        nameof(Snapshot) => (Snapshot, MessageTypes.RunSnapshot),
        nameof(CardReward) => (CardReward, MessageTypes.ScreenOpened),
        nameof(Merchant) => (Merchant, MessageTypes.ScreenOpened),
        nameof(Closed) => (Closed, MessageTypes.ScreenClosed),
        nameof(CardObtained) => (CardObtained, MessageTypes.RunEvent),
        nameof(RoomEntered) => (RoomEntered, MessageTypes.RunEvent),
        nameof(RunEnded) => (RunEnded, MessageTypes.RunEvent),
        nameof(Badges) => (Badges, MessageTypes.BadgesSet),
        nameof(Clear) => (Clear, MessageTypes.BadgesClear),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
