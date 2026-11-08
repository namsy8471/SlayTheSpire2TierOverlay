using System.Text.Json;
using Sts2Overlay.GameState.Save;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// Runs the history helpers over the 24 anonymised real <c>.run</c> files in tests/fixtures/runs
/// (same <c>map_point_history</c> shape as current_run.save).
/// </summary>
public class MapPointHistoryTests
{
    public static IEnumerable<object[]> RunFiles =>
        Directory.GetFiles(TestPaths.RunsDir, "*.run").OrderBy(p => p, StringComparer.Ordinal).Select(p => new object[] { Path.GetFileName(p) });

    [Theory]
    [MemberData(nameof(RunFiles))]
    public void Real_history_files_are_consistent(string file)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RunsDir, file)));
        JsonElement history = doc.RootElement.GetProperty("map_point_history");

        int floors = MapPointHistory.TotalFloors(history);
        Assert.Equal(MapPointHistory.Entries(history).Count(), floors);
        Assert.InRange(floors, 1, 60);

        foreach (JsonElement player in doc.RootElement.GetProperty("players").EnumerateArray())
        {
            ulong id = player.GetProperty("id").GetUInt64();
            int shops = MapPointHistory.Entries(history).Count(e =>
                e.TryGetProperty("rooms", out JsonElement rooms)
                && rooms.EnumerateArray().Any(r => r.TryGetProperty("room_type", out JsonElement t) && t.GetString() == "shop")
                && MapPointHistory.PlayerStats(e, id) != null);
            int removals = MapPointHistory.CountShopRemovals(history, id);
            // The merchant sells one removal per visit.
            Assert.InRange(removals, 0, shops);
        }
    }

    [Fact]
    public void Totals_over_all_fixture_runs()
    {
        int files = 0, floors = 0, removals = 0;
        foreach (string path in Directory.GetFiles(TestPaths.RunsDir, "*.run"))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement h = doc.RootElement.GetProperty("map_point_history");
            files++;
            floors += MapPointHistory.TotalFloors(h);
            removals += MapPointHistory.CountShopRemovals(h, doc.RootElement.GetProperty("players")[0].GetProperty("id").GetUInt64());
        }
        Assert.Equal(24, files);
        Assert.True(floors > 24 * 5);
        Assert.True(removals > 0, "the user removed cards at shops in at least one run");
    }
}
