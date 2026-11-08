using Sts2Overlay.Contracts;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Interop;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.Interop;

public class SnapshotMapperTests
{
    private static RunSnapshotMessage Snapshot => new()
    {
        Seq = 2,
        Character = "CHARACTER.DEFECT",
        Ascension = 5,
        Act = 2,
        Floor = 23,
        Hp = 30,
        MaxHp = 60,
        Gold = 150,
        Deck =
        [
            new SnapshotCard { Id = "CARD.STRIKE_DEFECT", InstanceId = "c1" },
            new SnapshotCard
            {
                Id = "CARD.BALL_LIGHTNING",
                Upgrade = 1,
                InstanceId = "c2",
                Enchantment = new CardEnchantment { Id = "ENCHANTMENT.SHARP", Amount = 3 },
                FloorAdded = 7,
            },
        ],
        Relics = ["RELIC.CRACKED_CORE"],
        CardRemovalsBought = 2,
    };

    [Fact]
    public void ToRunState_MapsScalarsAndRelics()
    {
        var state = SnapshotMapper.ToRunState(Snapshot);

        Assert.Equal("CHARACTER.DEFECT", state.Character);
        Assert.Equal(5, state.Ascension);
        Assert.Equal(2, state.Act);
        Assert.Equal(23, state.Floor);
        Assert.Equal(30, state.Hp);
        Assert.Equal(60, state.MaxHp);
        Assert.Equal(0.5, state.HpRatio);
        Assert.Equal(150, state.Gold);
        Assert.Equal(["RELIC.CRACKED_CORE"], state.Relics);
        Assert.Equal(2, state.CardRemovalsBought);
        Assert.False(state.IsMultiplayer);
    }

    [Fact]
    public void ToRunState_MapsDeckUpgradesAndEnchantments()
    {
        var deck = SnapshotMapper.ToRunState(Snapshot).Deck;

        Assert.Equal(2, deck.Count);
        Assert.Equal(new CardRef("CARD.STRIKE_DEFECT", 0), deck[0].Card);
        Assert.False(deck[0].Card.IsUpgraded);
        Assert.Null(deck[0].EnchantmentId);
        Assert.Equal(0, deck[0].FloorAdded);

        Assert.Equal(new CardRef("CARD.BALL_LIGHTNING", 1), deck[1].Card);
        Assert.True(deck[1].Card.IsUpgraded);
        Assert.Equal("ENCHANTMENT.SHARP", deck[1].EnchantmentId);
        Assert.Equal(3, deck[1].EnchantmentAmount);
        Assert.Equal(7, deck[1].FloorAdded);
    }

    [Fact]
    public void ToRunState_TakesMultiplayerFromHello()
    {
        var state = SnapshotMapper.ToRunState(Snapshot, new HelloMessage { IsMultiplayer = true, LocalPlayerId = "p1" });

        Assert.True(state.IsMultiplayer);
    }

    [Fact]
    public void ToRunState_MissingOptionalFields_UseSafeDefaults()
    {
        var msg = Assert.IsType<RunSnapshotMessage>(BridgeJson.Deserialize("""{"type":"run.snapshot","protocol":1,"seq":1}"""));

        var state = SnapshotMapper.ToRunState(msg);

        Assert.Equal("", state.Character);
        Assert.Equal(1, state.Act);
        Assert.Equal(1, state.Floor);
        Assert.Empty(state.Deck);
        Assert.Empty(state.Relics);
        Assert.Equal(0, state.CardRemovalsBought);
    }

    [Fact]
    public void ToRunState_ExplicitNullsAndBadValues_AreTolerated()
    {
        const string json = """
            {"type":"run.snapshot","character":null,"act":0,"floor":-3,"hp":-1,"deck":null,"relics":[null,"RELIC.ANCHOR"],
             "cardRemovalsBought":null}
            """;
        var msg = Assert.IsType<RunSnapshotMessage>(BridgeJson.Deserialize(json));

        var state = SnapshotMapper.ToRunState(msg);

        Assert.Equal("", state.Character);
        Assert.Equal(1, state.Act);
        Assert.Equal(1, state.Floor);
        Assert.Equal(0, state.Hp);
        Assert.Empty(state.Deck);
        Assert.Equal(["RELIC.ANCHOR"], state.Relics);
    }

    [Fact]
    public void ToRunState_DropsCardsWithoutIdAndClampsNegativeUpgrade()
    {
        var snapshot = Snapshot with
        {
            Deck =
            [
                new SnapshotCard { Id = "", InstanceId = "x" },
                new SnapshotCard { Id = "CARD.ZAP", Upgrade = -1, InstanceId = "y", Enchantment = new CardEnchantment { Id = "", Amount = 5 } },
            ],
        };

        var deck = SnapshotMapper.ToRunState(snapshot).Deck;

        var card = Assert.Single(deck);
        Assert.Equal(new CardRef("CARD.ZAP", 0), card.Card);
        Assert.Null(card.EnchantmentId);
        Assert.Equal(0, card.EnchantmentAmount);
    }

    [Theory]
    [InlineData(ScreenKinds.CardReward, RewardContexts.Normal, OfferContext.CardReward)]
    [InlineData(ScreenKinds.CardReward, RewardContexts.Elite, OfferContext.EliteReward)]
    [InlineData(ScreenKinds.CardReward, RewardContexts.Boss, OfferContext.BossReward)]
    [InlineData(ScreenKinds.CardReward, RewardContexts.Event, OfferContext.Event)]
    [InlineData(ScreenKinds.CardReward, "ELITE", OfferContext.EliteReward)]
    [InlineData(ScreenKinds.CardReward, null, OfferContext.CardReward)]
    [InlineData(ScreenKinds.CardReward, "something_new", OfferContext.CardReward)]
    [InlineData(ScreenKinds.Merchant, null, OfferContext.Shop)]
    [InlineData(ScreenKinds.Merchant, RewardContexts.Elite, OfferContext.Shop)]
    public void ToOfferContext_MapsScreenAndContext(string screen, string? context, OfferContext expected)
    {
        var msg = new ScreenOpenedMessage { Screen = screen, Context = context };

        Assert.Equal(expected, SnapshotMapper.ToOfferContext(msg));
    }

    [Theory]
    [InlineData(ScreenKinds.Map)]
    [InlineData(ScreenKinds.DeckSelect)]
    [InlineData(ScreenKinds.Other)]
    [InlineData("future_screen")]
    public void ToOfferContext_NonOfferScreens_ReturnNull(string screen)
    {
        Assert.Null(SnapshotMapper.ToOfferContext(new ScreenOpenedMessage { Screen = screen, Context = RewardContexts.Boss }));
    }

    [Fact]
    public void ToOfferContext_MerchantString_IsShop()
    {
        Assert.Equal(OfferContext.Shop, SnapshotMapper.ToOfferContext(RewardContexts.Merchant));
    }

    [Fact]
    public void ToCardRefs_PreservesOrderAndUpgrades()
    {
        var msg = new ScreenOpenedMessage
        {
            Screen = ScreenKinds.CardReward,
            Items =
            [
                new ScreenItem { InstanceId = "r1", CardId = "CARD.GLACIER", Upgrade = 1 },
                new ScreenItem { InstanceId = "r2", CardId = "CARD.COOLHEADED" },
                new ScreenItem { InstanceId = "r3", CardId = "CARD.GLACIER" },
            ],
        };

        var cards = SnapshotMapper.ToCardRefs(msg);

        Assert.Equal(
            [new CardRef("CARD.GLACIER", 1), new CardRef("CARD.COOLHEADED"), new CardRef("CARD.GLACIER")],
            cards);
    }

    [Fact]
    public void ToCardRefs_EmptyScreen_ReturnsEmpty()
    {
        Assert.Empty(SnapshotMapper.ToCardRefs(new ScreenOpenedMessage()));
    }

    [Fact]
    public void ToShopOffer_MapsPricesAndRemoval()
    {
        var msg = new ScreenOpenedMessage
        {
            Screen = ScreenKinds.Merchant,
            Items =
            [
                new ScreenItem { InstanceId = "m1", CardId = "CARD.DEFRAGMENT", Price = 75 },
                new ScreenItem { InstanceId = "m2", CardId = "CARD.ECHO_FORM", Upgrade = 1, Price = 160 },
                new ScreenItem { InstanceId = "m3", CardId = "CARD.UNPRICED" },
            ],
            RemovalPrice = 100,
        };

        var shop = SnapshotMapper.ToShopOffer(msg);

        Assert.Equal(
            [new ShopCardOffer(new CardRef("CARD.DEFRAGMENT"), 75), new ShopCardOffer(new CardRef("CARD.ECHO_FORM", 1), 160)],
            shop.Cards);
        Assert.Equal(100, shop.RemovalPrice);
    }

    [Fact]
    public void ToShopOffer_RemovalSoldOut_IsNull()
    {
        var shop = SnapshotMapper.ToShopOffer(new ScreenOpenedMessage { Screen = ScreenKinds.Merchant });

        Assert.Empty(shop.Cards);
        Assert.Null(shop.RemovalPrice);
    }

    [Fact]
    public void ToShopOffer_FromWireJson()
    {
        const string json = """
            {"type":"screen.opened","protocol":1,"seq":9,"screen":"merchant",
             "items":[{"instanceId":"m1","cardId":"CARD.HOLOGRAM","upgrade":0,"price":49,"rect":{"x":1,"y":2,"w":3,"h":4}}],
             "removalPrice":75}
            """;
        var msg = Assert.IsType<ScreenOpenedMessage>(BridgeJson.Deserialize(json));

        var shop = SnapshotMapper.ToShopOffer(msg);

        Assert.Equal(OfferContext.Shop, SnapshotMapper.ToOfferContext(msg));
        Assert.Equal(49, Assert.Single(shop.Cards).Price);
        Assert.Equal(75, shop.RemovalPrice);
    }

    [Fact]
    public void BuildBadges_MapsFieldsAndColors()
    {
        var items = new (string, string, double, bool, IReadOnlyList<string>)[]
        {
            ("r1", "S", 92.4, true, ["광역 부족", "빌드 핵심"]),
            ("r2", "D", 12, false, []),
            ("", "A", 50, false, []),
        };

        var msg = SnapshotMapper.BuildBadges(items, skipRecommended: true, seq: 17);

        Assert.Equal(17, msg.Seq);
        Assert.Equal(ProtocolVersion.Current, msg.Protocol);
        Assert.True(msg.SkipRecommended);
        Assert.Equal(2, msg.Badges.Count);

        var best = msg.Badges[0];
        Assert.Equal("r1", best.InstanceId);
        Assert.Equal("S", best.Label);
        Assert.Equal("#F1C40F", best.Color);
        Assert.Equal(92.4, best.Score);
        Assert.True(best.IsBest);
        Assert.Equal(["광역 부족", "빌드 핵심"], best.Chips);

        Assert.Equal("#8B1E1E", msg.Badges[1].Color);
        Assert.False(msg.Badges[1].IsBest);
    }

    [Fact]
    public void BuildBadges_SerializesAsBadgesSet()
    {
        var msg = SnapshotMapper.BuildBadges([("r1", "B", 55.0, false, (IReadOnlyList<string>)["방어 보강"])], false, 3);

        var back = Assert.IsType<BadgesSetMessage>(BridgeJson.Deserialize(BridgeJson.Serialize(msg)));

        Assert.Equal("#3498DB", Assert.Single(back.Badges).Color);
        Assert.False(back.SkipRecommended);
        Assert.Contains("\"type\":\"badges.set\"", BridgeJson.Serialize(msg));
    }

    [Theory]
    [InlineData(Tier.S, "#F1C40F")]
    [InlineData(Tier.A, "#9B59B6")]
    [InlineData(Tier.B, "#3498DB")]
    [InlineData(Tier.C, "#95A5A6")]
    [InlineData(Tier.D, "#8B1E1E")]
    public void TierColor_ByTier(Tier tier, string expected)
    {
        Assert.Equal(expected, SnapshotMapper.TierColor(tier));
        Assert.Equal(expected, SnapshotMapper.TierColor(tier.ToString()));
    }

    [Fact]
    public void TierColor_UnknownLabel_IsGray()
    {
        Assert.Equal("#95A5A6", SnapshotMapper.TierColor("Z"));
    }
}
