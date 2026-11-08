using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sts2Overlay.Contracts.Tests;

public class BridgeJsonTests
{
    [Theory]
    [MemberData(nameof(SampleMessages.Names), MemberType = typeof(SampleMessages))]
    public void RoundTrip_PreservesTypeAndContent(string name)
    {
        var (message, _) = SampleMessages.Get(name);

        var json = BridgeJson.Serialize(message);
        var back = BridgeJson.Deserialize(json);

        Assert.NotNull(back);
        Assert.IsType(message.GetType(), back);
        Assert.Equal(message.Seq, back.Seq);
        Assert.Equal(ProtocolVersion.Current, back.Protocol);
        // Lists compare by reference in records, so compare the canonical JSON instead.
        Assert.Equal(json, BridgeJson.Serialize(back));
    }

    [Theory]
    [MemberData(nameof(SampleMessages.Names), MemberType = typeof(SampleMessages))]
    public void Serialize_WritesDiscriminatorFirstAndEnvelope(string name)
    {
        var (message, type) = SampleMessages.Get(name);

        var obj = JsonNode.Parse(BridgeJson.Serialize(message))!.AsObject();

        var first = obj.First();
        Assert.Equal("type", first.Key);
        Assert.Equal(type, first.Value!.GetValue<string>());
        Assert.Equal(ProtocolVersion.Current, obj["protocol"]!.GetValue<int>());
        Assert.Equal(message.Seq, obj["seq"]!.GetValue<long>());
    }

    [Theory]
    [MemberData(nameof(SampleMessages.Names), MemberType = typeof(SampleMessages))]
    public void Utf8_RoundTrip(string name)
    {
        var (message, _) = SampleMessages.Get(name);

        var back = BridgeJson.Deserialize(BridgeJson.SerializeToUtf8Bytes(message));

        Assert.NotNull(back);
        Assert.Equal(BridgeJson.Serialize(message), BridgeJson.Serialize(back));
    }

    [Fact]
    public void Serialize_UsesCamelCase()
    {
        var json = BridgeJson.Serialize(SampleMessages.Snapshot);

        Assert.Contains("\"maxHp\":75", json);
        Assert.Contains("\"cardRemovalsBought\":1", json);
        Assert.Contains("\"instanceId\":\"c2\"", json);
        Assert.DoesNotContain("MaxHp", json);
    }

    [Fact]
    public void Serialize_OmitsNulls()
    {
        var json = BridgeJson.Serialize(new RunEventMessage { Event = RunEventKinds.RunEnded });
        var obj = JsonNode.Parse(json)!.AsObject();

        Assert.DoesNotContain("card", obj.Select(p => p.Key));
        Assert.DoesNotContain("victory", obj.Select(p => p.Key));
        Assert.DoesNotContain("null", json);

        var hello = BridgeJson.Serialize(new HelloMessage { GameVersion = "1" });
        Assert.DoesNotContain("localPlayerId", hello);
        Assert.DoesNotContain("gameCommit", hello);
    }

    [Fact]
    public void MapEdges_AreSerializedAsCoordPairs()
    {
        var obj = JsonNode.Parse(BridgeJson.Serialize(SampleMessages.Snapshot))!;

        var edge = obj["map"]!["edges"]![0]!.AsArray();
        Assert.Equal(2, edge.Count);
        Assert.Equal(0, edge[0]!["col"]!.GetValue<int>());
        Assert.Equal(1, edge[1]!["row"]!.GetValue<int>());
    }

    [Fact]
    public void Deserialize_ParsesHandWrittenSnapshot()
    {
        const string json = """
            {
              "protocol": 1, "seq": 42, "type": "run.snapshot",
              "character": "CHARACTER.IRONCLAD", "act": 1, "floor": 3, "hp": 70, "maxHp": 80, "gold": 99,
              "deck": [ { "id": "CARD.BASH", "upgrade": 1, "instanceId": "x" } ],
              "relics": ["RELIC.BURNING_BLOOD"],
              "map": { "nodes": [ { "coord": { "col": 2, "row": 0 }, "type": "monster" } ],
                       "edges": [ [ { "col": 2, "row": 0 }, { "col": 3, "row": 1 } ] ],
                       "current": { "col": 2, "row": 0 } }
            }
            """;

        var msg = Assert.IsType<RunSnapshotMessage>(BridgeJson.Deserialize(json));

        Assert.Equal(42, msg.Seq);
        Assert.Equal("CHARACTER.IRONCLAD", msg.Character);
        Assert.Equal(1, msg.Deck[0].Upgrade);
        Assert.Null(msg.CardRemovalsBought);
        Assert.Empty(msg.Potions);
        Assert.Equal(new MapEdge(new MapCoord(2, 0), new MapCoord(3, 1)), msg.Map!.Edges[0]);
        Assert.Equal(new MapCoord(2, 0), msg.Map.Current);
    }

    [Fact]
    public void Deserialize_TypeNotFirst_IsAccepted()
    {
        var msg = BridgeJson.Deserialize("""{"seq":5,"protocol":1,"badges":[],"type":"badges.set"}""");

        var set = Assert.IsType<BadgesSetMessage>(msg);
        Assert.Equal(5, set.Seq);
    }

    [Fact]
    public void Deserialize_UnknownExtraProperties_AreIgnored()
    {
        const string json = """
            {"type":"screen.opened","protocol":1,"seq":3,"screen":"card_reward","futureField":{"a":[1,2]},
             "items":[{"instanceId":"r1","cardId":"CARD.X","upgrade":0,"glow":true,"rect":{"x":1,"y":2,"w":3,"h":4,"z":9}}],
             "alternatives":[{"kind":"skip","extra":1}]}
            """;

        var msg = Assert.IsType<ScreenOpenedMessage>(BridgeJson.Deserialize(json));

        Assert.Equal(ScreenKinds.CardReward, msg.Screen);
        Assert.Equal(new ScreenRect(1, 2, 3, 4), msg.Items[0].Rect);
        Assert.Equal(AlternativeKinds.Skip, msg.Alternatives[0].Kind);
    }

    [Fact]
    public void Deserialize_UnknownScreenKind_IsKeptAsString()
    {
        var msg = BridgeJson.Deserialize("""{"type":"screen.opened","protocol":1,"seq":1,"screen":"card_library"}""");

        Assert.Equal("card_library", Assert.IsType<ScreenOpenedMessage>(msg).Screen);
    }

    [Theory]
    [InlineData("""{"type":"future.message","protocol":1,"seq":1,"x":2}""")]
    [InlineData("""{"protocol":1,"seq":1}""")]
    [InlineData("""{"type":42,"protocol":1,"seq":1}""")]
    public void Deserialize_UnknownOrMissingType_ReturnsNull(string json)
    {
        Assert.Null(BridgeJson.Deserialize(json));
        Assert.Null(BridgeJson.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"type":"hello","seq":"abc"}""")]
    [InlineData("""{"type":"run.snapshot","deck":{"oops":1}}""")]
    [InlineData("""{"type":"run.snapshot","map":{"edges":[1]}}""")]
    public void Deserialize_Malformed_ReturnsNull(string json)
    {
        Assert.Null(BridgeJson.Deserialize(json));
    }

    [Fact]
    public void Deserialize_NullString_ReturnsNull()
    {
        Assert.Null(BridgeJson.Deserialize((string?)null));
        Assert.Null(BridgeJson.Deserialize(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Deserialize_MissingFields_UseDefaults()
    {
        var msg = Assert.IsType<RunSnapshotMessage>(BridgeJson.Deserialize("""{"type":"run.snapshot"}"""));

        Assert.Equal(ProtocolVersion.Current, msg.Protocol);
        Assert.Equal(0, msg.Seq);
        Assert.Equal(1, msg.Act);
        Assert.Empty(msg.Deck);
        Assert.Null(msg.Map);
    }

    [Fact]
    public void Deserialize_KeepsSenderProtocol()
    {
        var msg = BridgeJson.Deserialize("""{"type":"badges.clear","protocol":7,"seq":1}""");

        Assert.Equal(7, msg!.Protocol);
        Assert.False(ProtocolVersion.IsSupported(msg.Protocol));
        Assert.True(ProtocolVersion.IsSupported(ProtocolVersion.Current));
    }

    [Fact]
    public void Options_AreReadOnly()
    {
        Assert.True(BridgeJson.Options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => BridgeJson.Options.WriteIndented = true);
    }

    [Fact]
    public void BridgeTransport_BuildsLoopbackUri()
    {
        Assert.Equal("ws://127.0.0.1:51234/bridge", BridgeTransport.BuildUri(51234).ToString());
    }

    [Theory]
    [InlineData("S", TierColors.S)]
    [InlineData("a", TierColors.A)]
    [InlineData("B", TierColors.B)]
    [InlineData("C", TierColors.C)]
    [InlineData("D", TierColors.D)]
    [InlineData("?", TierColors.C)]
    [InlineData(null, TierColors.C)]
    public void TierColors_ForLabel(string? label, string expected)
    {
        Assert.Equal(expected, TierColors.ForLabel(label));
    }

    [Fact]
    public void Serialize_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BridgeJson.Serialize(null!));
    }

    [Fact]
    public void Serialize_KoreanChips_RoundTrip()
    {
        var json = BridgeJson.Serialize(SampleMessages.Badges);
        var back = Assert.IsType<BadgesSetMessage>(BridgeJson.Deserialize(json));

        Assert.Equal(["광역 부족", "빌드 핵심"], back.Badges[0].Chips);
        Assert.Equal(3, back.ScreenSeq);
        Assert.False(back.SkipRecommended);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("#F1C40F", doc.RootElement.GetProperty("badges")[0].GetProperty("color").GetString());
    }
}
