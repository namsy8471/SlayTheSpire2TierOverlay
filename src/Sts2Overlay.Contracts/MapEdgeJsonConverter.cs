using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Overlay.Contracts;

/// <summary>
/// Reads and writes <see cref="MapEdge"/> as <c>[{"col":0,"row":0},{"col":1,"row":1}]</c>.
/// Hand-written (no reflection) so it works with the source-generated context.
/// </summary>
public sealed class MapEdgeJsonConverter : JsonConverter<MapEdge>
{
    public override MapEdge Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("map edge must be an array [from, to]");
        }

        reader.Read();
        var from = ReadCoord(ref reader);
        reader.Read();
        var to = ReadCoord(ref reader);
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            // Tolerate extra trailing elements from a newer sender.
            reader.Skip();
            reader.Read();
        }

        return new MapEdge(from, to);
    }

    public override void Write(Utf8JsonWriter writer, MapEdge value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        WriteCoord(writer, value.From);
        WriteCoord(writer, value.To);
        writer.WriteEndArray();
    }

    private static MapCoord ReadCoord(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("map coord must be an object {col, row}");
        }

        int col = 0, row = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("unexpected token in map coord");
            }

            var isCol = reader.ValueTextEquals("col");
            var isRow = !isCol && reader.ValueTextEquals("row");
            reader.Read();
            if (isCol)
            {
                col = reader.GetInt32();
            }
            else if (isRow)
            {
                row = reader.GetInt32();
            }
            else
            {
                reader.Skip();
            }
        }

        return new MapCoord(col, row);
    }

    private static void WriteCoord(Utf8JsonWriter writer, MapCoord coord)
    {
        writer.WriteStartObject();
        writer.WriteNumber("col", coord.Col);
        writer.WriteNumber("row", coord.Row);
        writer.WriteEndObject();
    }
}
