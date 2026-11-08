using System.IO.Compression;
using Sts2Overlay.Core.Map;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// Co-op partner map votes (shown on the map overlay). The party only moves once every player voted for the same node,
/// so in a real log the partner's latest vote right before each move must be exactly the node the party then entered.
/// That gives a ground truth for every move in the 9 real co-op runs, from both seats.
/// </summary>
public class PartnerVoteTests
{
    private static List<string> ReadGz(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var lines = new List<string>();
        for (string? l; (l = reader.ReadLine()) != null;) lines.Add(l);
        return lines;
    }

    private static bool IsVote(string line) => line.Contains("Player vote changed for ", StringComparison.Ordinal);

    private static bool IsRoom(string line) => line.StartsWith("[INFO] Preloading '", StringComparison.Ordinal) && line.Contains(" Room'", StringComparison.Ordinal);

    [Theory]
    [InlineData(TestPaths.Me, TestPaths.Friend)]
    [InlineData(TestPaths.Friend, TestPaths.Me)]
    public void Real_coop_logs_partner_vote_before_every_move_is_the_entered_node(ulong me, ulong partner)
    {
        int moves = 0, right = 0;
        var failures = new List<string>();
        foreach (string f in Directory.GetFiles(TestPaths.Fixture("coop_runs"), "*.log.gz").Order(StringComparer.Ordinal))
        {
            var tracker = new GameStateTracker(new TrackerOptions { LocalNetIdOverride = me });
            List<string> lines = ReadGz(f);
            MapCoord? lastPartnerVoteInLog = null;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (IsRoom(line) && lastPartnerVoteInLog is { } expected)
                {
                    // Just before the room loads: the tracker must show the partner's vote, and it must be the move.
                    moves++;
                    PartnerVote? shown = tracker.Current?.PartnerVotes.SingleOrDefault(v => v.PlayerId == partner);
                    if (shown?.Coord == expected) right++;
                    else failures.Add($"{Path.GetFileName(f)} line {i}: shown {shown?.Coord.ToString() ?? "none"}, partner voted {expected}");
                    lastPartnerVoteInLog = null;
                }
                tracker.ProcessLine(line);
                if (IsVote(line) && line.Contains($"for {partner}:", StringComparison.Ordinal))
                {
                    var c = line[(line.LastIndexOf("coord: (", StringComparison.Ordinal) + 8)..].TrimEnd(')', ' ').Split(", ");
                    lastPartnerVoteInLog = new MapCoord(int.Parse(c[0]), int.Parse(c[1]));
                }
                if (IsRoom(line))
                {
                    // After entering a room the vote is settled and must be cleared.
                    Assert.Empty(tracker.Current?.PartnerVotes ?? []);
                }
            }
        }
        Assert.True(moves > 100, $"only {moves} moves found");
        Assert.True(failures.Count == 0, $"{right}/{moves}\n" + string.Join("\n", failures.Take(10)));
    }

    [Fact]
    public void Single_player_has_no_partner_votes()
    {
        var tracker = new GameStateTracker(new TrackerOptions { LocalNetIdOverride = 1 });
        tracker.ProcessLines(["[INFO] Player vote changed for 1: ->MapVote (gen: 1 coord: (3, 1))"]);
        Assert.Empty(tracker.Current?.PartnerVotes ?? []);
    }
}
