using System.Collections.ObjectModel;
using System.Text.Json;

namespace Sts2Overlay.Core.Model;

/// <summary>Numbers saved on one copy of a card, compared by value when grouping or comparing decks.</summary>
public sealed class SavedCardProperties : ReadOnlyDictionary<string, decimal>, IEquatable<SavedCardProperties>
{
    public new static SavedCardProperties Empty { get; } = new(new Dictionary<string, decimal>());

    public SavedCardProperties(IDictionary<string, decimal> values)
        : base(new SortedDictionary<string, decimal>(values, StringComparer.Ordinal)) { }

    /// <summary>Both a run save and a finished run use the game's SerializableCard format.</summary>
    public static SavedCardProperties Read(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object || !card.TryGetProperty("props", out var props)
            || props.ValueKind != JsonValueKind.Object || !props.TryGetProperty("ints", out var ints)
            || ints.ValueKind != JsonValueKind.Array)
            return Empty;

        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var entry in ints.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } key
                && entry.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int number))
                values[key] = number;
        }
        return values.Count == 0 ? Empty : new SavedCardProperties(values);
    }

    public bool Equals(SavedCardProperties? other) =>
        ReferenceEquals(this, other) || other is not null && Count == other.Count && this.SequenceEqual(other);

    public override bool Equals(object? obj) => Equals(obj as SavedCardProperties);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in this) hash.Add(entry);
        return hash.ToHashCode();
    }
}
