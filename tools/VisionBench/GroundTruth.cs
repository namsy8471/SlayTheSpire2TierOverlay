using System.Text.Json;

namespace VisionBench;

/// <summary>One map point of a run that offered cards (reward floors and shops).</summary>
sealed record Floor(int Act, int Index, string Type, IReadOnlyList<string> Offered, IReadOnlyList<string> Gained)
{
    public string Name => $"act{Act + 1} #{Index} {Type}";
    public bool IsShop => Type == "shop";
}

/// <summary>
/// Ground truth from the game's own run save: <c>map_point_history[act][point].player_stats[].card_choices</c>
/// lists the cards that were offered at each floor (written when the player moves to the next point).
/// Cards are written as "CARD.X" or "CARD.X+1" when upgraded (current_upgrade_level).
/// </summary>
sealed class RunTruth
{
    public IReadOnlyList<Floor> Floors { get; }
    /// <summary>When the save was last written (current run only: floors after it are not in the file yet).</summary>
    public DateTimeOffset? SaveTime { get; }
    public string? Character { get; }
    public string Path { get; }
    public DateTime Start { get; }
    /// <summary>Last moment the run can have been played: save time, or the file time of a finished run.</summary>
    public DateTime End { get; }
    public bool IsCurrent => SaveTime != null;
    public string Tag => $"run {Start:HH:mm}";

    private RunTruth(IReadOnlyList<Floor> floors, DateTimeOffset? saveTime, string? character, string path, DateTime start, DateTime end)
    {
        Floors = floors;
        SaveTime = saveTime;
        Character = character;
        Path = path;
        Start = start;
        End = end;
    }

    /// <summary>
    /// The current run plus finished runs from the sibling <c>history</c> folder (*.run, same map_point_history
    /// format) that ended after <paramref name="since"/>. Captures span several runs, so one save is not enough.
    /// </summary>
    public static List<RunTruth> LoadAll(string currentSave, DateTime since)
    {
        // No current_run.save between runs: the finished runs in history/ still count.
        var runs = File.Exists(currentSave) ? new List<RunTruth> { Load(currentSave) } : [];
        string history = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(currentSave)!, "history");
        if (Directory.Exists(history))
            foreach (string f in Directory.GetFiles(history, "*.run"))
                if (File.GetLastWriteTime(f) >= since) runs.Add(Load(f));
        return runs.OrderBy(r => r.Start).ToList();
    }

    /// <summary>The run that was being played at <paramref name="time"/> (local), if any.</summary>
    public static RunTruth? At(IReadOnlyList<RunTruth> runs, DateTime time) =>
        runs.LastOrDefault(r => time >= r.Start.AddMinutes(-1) && (r.IsCurrent || time <= r.End.AddMinutes(2)));

    public static RunTruth Load(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument doc = JsonDocument.Parse(fs);
        JsonElement root = doc.RootElement;
        var floors = new List<Floor>();
        if (root.TryGetProperty("map_point_history", out JsonElement history))
        {
            int act = 0;
            foreach (JsonElement points in history.EnumerateArray())
            {
                int idx = 0;
                foreach (JsonElement point in points.EnumerateArray())
                {
                    string type = point.TryGetProperty("map_point_type", out JsonElement t) ? t.GetString() ?? "" : "";
                    var offered = new List<string>();
                    var gained = new List<string>();
                    if (point.TryGetProperty("player_stats", out JsonElement stats))
                    {
                        // Single player: one entry. In co-op we would pick our own player_id; first is fine here.
                        JsonElement ps = stats.EnumerateArray().FirstOrDefault();
                        if (ps.ValueKind == JsonValueKind.Object)
                        {
                            if (ps.TryGetProperty("card_choices", out JsonElement cc))
                                foreach (JsonElement c in cc.EnumerateArray()) offered.Add(CardKey(c.GetProperty("card")));
                            if (ps.TryGetProperty("cards_gained", out JsonElement cg))
                                foreach (JsonElement c in cg.EnumerateArray()) gained.Add(CardKey(c));
                        }
                    }
                    if (offered.Count > 0) floors.Add(new Floor(act, idx, type, offered, gained));
                    idx++;
                }
                act++;
            }
        }
        DateTimeOffset? saveTime = root.TryGetProperty("save_time", out JsonElement st) && st.TryGetInt64(out long secs)
            ? DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime() : null;
        string? character = null;
        if (root.TryGetProperty("players", out JsonElement players))
        {
            JsonElement p = players.EnumerateArray().FirstOrDefault();
            // current_run.save says "character_id", history/*.run says "character".
            if (p.ValueKind == JsonValueKind.Object && (p.TryGetProperty("character_id", out JsonElement ch) || p.TryGetProperty("character", out ch)))
                character = ch.GetString();
        }
        DateTime start = root.TryGetProperty("start_time", out JsonElement s0) && s0.TryGetInt64(out long st0)
            ? DateTimeOffset.FromUnixTimeSeconds(st0).LocalDateTime : File.GetCreationTime(path);
        DateTime end = saveTime?.LocalDateTime ?? File.GetLastWriteTime(path);
        return new RunTruth(floors, saveTime, character, path, start, end);
    }

    public static string CardKey(JsonElement card)
    {
        string id = card.GetProperty("id").GetString()!;
        int up = card.TryGetProperty("current_upgrade_level", out JsonElement u) && u.TryGetInt32(out int v) ? v : 0;
        return up > 0 ? id + "+" : id;
    }
}

/// <summary>Manual labels (data/local/bench/labels.json), see the "comment" field in that file.</summary>
sealed class Labels
{
    public sealed record Entry(string Screen, IReadOnlyList<string>? Cards, string? Note);

    public Dictionary<string, Entry> Captures { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Captures with a timestamp up to this one are labelled (unlisted ones are "Other").</summary>
    public string? LabelledThrough { get; private set; }

    public static Labels Load(string? path)
    {
        var labels = new Labels();
        if (path is null || !File.Exists(path)) return labels;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("labelledThrough", out JsonElement lt)) labels.LabelledThrough = lt.GetString();
        foreach (JsonProperty p in doc.RootElement.GetProperty("captures").EnumerateObject())
        {
            string screen = p.Value.GetProperty("screen").GetString()!;
            List<string>? cards = p.Value.TryGetProperty("cards", out JsonElement c) ? c.EnumerateArray().Select(x => x.GetString()!).ToList() : null;
            string? note = p.Value.TryGetProperty("note", out JsonElement n) ? n.GetString() : null;
            labels.Captures[p.Name] = new Entry(screen, cards, note);
        }
        return labels;
    }

    /// <summary>Expected screen for a capture, or null when it is newer than the labelled range.</summary>
    public string? ScreenOf(string fileName, string timestamp)
    {
        if (Captures.TryGetValue(fileName, out Entry? e)) return e.Screen;
        if (LabelledThrough != null && string.CompareOrdinal(timestamp, LabelledThrough) <= 0) return "Other";
        return null;
    }
}
