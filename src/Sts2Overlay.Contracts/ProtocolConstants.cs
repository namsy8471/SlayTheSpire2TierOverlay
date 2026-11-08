namespace Sts2Overlay.Contracts;

/// <summary>
/// Wire protocol version. Bump only for breaking changes; additive changes (new optional
/// fields, new message types) keep the same version (see <c>docs/PROTOCOL.md</c>).
/// </summary>
public static class ProtocolVersion
{
    /// <summary>The protocol version this build speaks.</summary>
    public const int Current = 1;

    /// <summary>True when a peer announcing <paramref name="protocol"/> can be talked to by this build.</summary>
    public static bool IsSupported(int protocol) => protocol == Current;
}

/// <summary>Values of the <c>type</c> discriminator on every <see cref="BridgeMessage"/>.</summary>
public static class MessageTypes
{
    // mod -> app
    public const string Hello = "hello";
    public const string RunSnapshot = "run.snapshot";
    public const string ScreenOpened = "screen.opened";
    public const string ScreenClosed = "screen.closed";
    public const string RunEvent = "run.event";

    // app -> mod
    public const string BadgesSet = "badges.set";
    public const string BadgesClear = "badges.clear";
}

/// <summary>Values of <see cref="ScreenOpenedMessage.Screen"/>. Receivers must tolerate other values.</summary>
public static class ScreenKinds
{
    public const string CardReward = "card_reward";
    public const string Merchant = "merchant";
    public const string DeckSelect = "deck_select";
    public const string Map = "map";
    public const string Other = "other";
}

/// <summary>Values of <see cref="ScreenOpenedMessage.Context"/>: where a card reward comes from.</summary>
public static class RewardContexts
{
    public const string Normal = "normal";
    public const string Elite = "elite";
    public const string Boss = "boss";
    public const string Event = "event";
    public const string Merchant = "merchant";
}

/// <summary>Values of <see cref="ScreenAlternative.Kind"/>. Receivers must tolerate other values.</summary>
public static class AlternativeKinds
{
    public const string Skip = "skip";
}

/// <summary>Values of <see cref="RunEventMessage.Event"/>. Receivers must tolerate other values.</summary>
public static class RunEventKinds
{
    public const string CardObtained = "card_obtained";
    public const string CardRemoved = "card_removed";
    public const string CardUpgraded = "card_upgraded";
    public const string RoomEntered = "room_entered";
    public const string RunEnded = "run_ended";
}

/// <summary>Values of <see cref="MapNode.Type"/> and <see cref="RunEventMessage.Room"/>. Receivers must tolerate other values.</summary>
public static class MapNodeTypes
{
    public const string Monster = "monster";
    public const string Elite = "elite";
    public const string Rest = "rest";
    public const string Shop = "shop";
    public const string Treasure = "treasure";
    public const string Event = "event";
    public const string Boss = "boss";
    public const string Unknown = "unknown";
}

/// <summary>Transport-level constants shared by the mod's WebSocket server and the app's client.</summary>
public static class BridgeTransport
{
    /// <summary>The server only binds to loopback.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>WebSocket request path.</summary>
    public const string Path = "/bridge";

    /// <summary>Request header carrying <see cref="BridgeConnectionInfo.Token"/> during the upgrade handshake.</summary>
    public const string TokenHeader = "X-Sts2Overlay-Token";

    /// <summary>Builds the WebSocket URI for a port read from <c>bridge.json</c>.</summary>
    public static Uri BuildUri(int port) => new($"ws://{Host}:{port}{Path}");
}

/// <summary>Badge colors per tier label (PLAN §8). Shared so the mod can draw default badges without the app.</summary>
public static class TierColors
{
    public const string S = "#F1C40F";
    public const string A = "#9B59B6";
    public const string B = "#3498DB";
    public const string C = "#95A5A6";
    public const string D = "#8B1E1E";

    /// <summary>Color for a tier label ("S".."D", case-insensitive); unknown labels get the C gray.</summary>
    public static string ForLabel(string? label) => label?.Trim().ToUpperInvariant() switch
    {
        "S" => S,
        "A" => A,
        "B" => B,
        "C" => C,
        "D" => D,
        _ => C,
    };
}
