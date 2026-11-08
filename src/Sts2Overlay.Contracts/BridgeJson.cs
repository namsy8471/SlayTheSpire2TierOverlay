using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Overlay.Contracts;

/// <summary>
/// Shared JSON settings and (de)serialization entry points for the bridge protocol.
/// Writing: camelCase, nulls omitted, <c>type</c> discriminator first.
/// Reading: unknown properties ignored, <c>type</c> may appear anywhere in the object,
/// and anything that cannot be understood (unknown <c>type</c>, malformed JSON) yields <c>null</c>
/// instead of throwing, so an older app survives a newer mod and vice versa.
/// </summary>
public static class BridgeJson
{
    /// <summary>Read-only options backed by the source-generated <see cref="BridgeJsonContext"/>.</summary>
    public static JsonSerializerOptions Options => BridgeJsonContext.Default.Options;

    /// <summary>Serializes a message (including its <c>type</c> discriminator) to a JSON string.</summary>
    public static string Serialize(BridgeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, BridgeJsonContext.Default.BridgeMessage);
    }

    /// <summary>Serializes a message to UTF-8 bytes, ready for a WebSocket text frame.</summary>
    public static byte[] SerializeToUtf8Bytes(BridgeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(message, BridgeJsonContext.Default.BridgeMessage);
    }

    /// <summary>Parses a message; returns null for malformed JSON, a missing or unknown <c>type</c>, or empty input.</summary>
    public static BridgeMessage? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, BridgeJsonContext.Default.BridgeMessage);
        }
        catch (Exception ex) when (IsParseFailure(ex))
        {
            return null;
        }
    }

    /// <summary>UTF-8 overload of <see cref="Deserialize(string?)"/>.</summary>
    public static BridgeMessage? Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(utf8Json, BridgeJsonContext.Default.BridgeMessage);
        }
        catch (Exception ex) when (IsParseFailure(ex))
        {
            return null;
        }
    }

    // JsonException: malformed JSON or unknown discriminator. NotSupportedException: no/unknown
    // discriminator falls back to the abstract base type. Others: bad numbers/formats.
    private static bool IsParseFailure(Exception ex) =>
        ex is JsonException or NotSupportedException or FormatException or InvalidOperationException or ArgumentException;
}

/// <summary>Source-generated serialization metadata for every protocol type (no runtime reflection).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    AllowOutOfOrderMetadataProperties = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(BridgeMessage))]
[JsonSerializable(typeof(BridgeConnectionInfo))]
public sealed partial class BridgeJsonContext : JsonSerializerContext;
