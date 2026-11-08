using System.Text.Json;

namespace Sts2Overlay.GameState.Save;

/// <summary>
/// Reads unlock facts from <c>progress.save</c> (the profile's timeline). Only what the overlay needs: whether every
/// character is unlocked, which Neow's Kaleidoscope option checks (Kaleidoscope.IsAllowedAtNeow →
/// <c>UnlockState.CharacterCardPools.Count() == AllCharacters.Count()</c>).
/// </summary>
public static class ProgressSave
{
    public const string FileName = "progress.save";

    /// <summary>UnlockState.Characters: Ironclad always, the others once their first epoch is revealed.</summary>
    private static readonly string[] CharacterEpochs = ["SILENT1_EPOCH", "REGENT1_EPOCH", "NECROBINDER1_EPOCH", "DEFECT1_EPOCH"];

    /// <summary>
    /// True when every character is unlocked (UnlockState(ProgressState) keeps only epochs whose state is
    /// <c>revealed</c>); null when the file can't be read or has no epoch list.
    /// </summary>
    public static bool? AllCharactersUnlocked(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("epochs", out var epochs) || epochs.ValueKind != JsonValueKind.Array) return null;
            var revealed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in epochs.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Object
                    && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && e.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                    && state.GetString() == "revealed")
                {
                    revealed.Add(id.GetString()!);
                }
            }
            return CharacterEpochs.All(revealed.Contains);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><see cref="AllCharactersUnlocked(string)"/> for <c>&lt;savesDirectory&gt;/progress.save</c>.</summary>
    public static bool? AllCharactersUnlockedIn(string savesDirectory)
    {
        try
        {
            return IO.SharedFile.ReadAllText(Path.Combine(savesDirectory, FileName)) is { } json ? AllCharactersUnlocked(json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
