using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// My own map vote. Single player: the vote is the move, so <c>Map.Current</c> jumps to it before the room-entry save.
/// Co-op: it is only a vote (the game draws between differing votes), so <c>Current</c> stays where the party is and the
/// vote is reported as <see cref="TrackedRun.MyVote"/>, next to the partners' votes.
/// </summary>
public class MyMapVoteTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly MapCoord Start = new(3, 0), Left = new(2, 1), Mid = new(3, 1), Right = new(4, 1);

    /// <summary>The act's ancient with three first-row children; we stand on the ancient.</summary>
    private static readonly MapGraph Map = new()
    {
        Nodes = new Dictionary<MapCoord, MapNode>
        {
            [Start] = new(Start, MapNodeType.Ancient, [Left, Mid, Right]),
            [Left] = new(Left, MapNodeType.Monster, []),
            [Mid] = new(Mid, MapNodeType.Elite, []),
            [Right] = new(Right, MapNodeType.Unknown, []),
        },
        Start = Start, Boss = Mid, Current = Start,
    };

    private static GameStateTracker NewTracker(ulong me) => new(new TrackerOptions { LocalNetIdOverride = me, Clock = () => T0 });

    private static SaveSnapshot SoloSave(long bytes) => new(
        @"C:\saves\current_run.save",
        new ParsedSave
        {
            State = new RunState { Character = "CHARACTER.IRONCLAD", Floor = 1, Hp = 80, MaxHp = 80 },
            Seed = "S1", PlayerNetIds = [1], MyNetId = 1, Map = Map,
        },
        T0, bytes);

    private static SaveSnapshot CoopSave(long bytes) => new(
        @"C:\saves\current_run_mp.save",
        new ParsedSave
        {
            State = new RunState { Character = "CHARACTER.DEFECT", Floor = 1, Hp = 75, MaxHp = 75, IsMultiplayer = true },
            Seed = "M1", PlayerNetIds = [TestPaths.Me, TestPaths.Friend], MyNetId = TestPaths.Me, Map = Map,
        },
        T0, bytes);

    private static readonly string CoopEmbark =
        $"[INFO] Embarking on a multiplayer run. Players: Player {TestPaths.Me}, DEFECT,Player {TestPaths.Friend}, SILENT. Ascension: 0 Seed: M1";

    private static string Vote(ulong player, MapCoord to) =>
        $"[INFO] Player vote changed for {player}: ->MapVote (gen: 1 coord: ({to.Col}, {to.Row}))";

    private static string Revote(ulong player, MapCoord from, MapCoord to) =>
        $"[INFO] Player vote changed for {player}: MapVote (gen: 1 coord: ({from.Col}, {from.Row}))->MapVote (gen: 1 coord: ({to.Col}, {to.Row}))";

    private static GameStateTracker CoopOnTheAncient()
    {
        var t = NewTracker(TestPaths.Me);
        t.ProcessLine(CoopEmbark);
        t.OnSave(CoopSave(100));
        return t;
    }

    [Fact]
    public void Solo_vote_still_moves_current_and_is_not_reported_as_a_vote()
    {
        var t = NewTracker(1);
        t.ProcessLine("[INFO] Embarking on a singleplayer IRONCLAD run. Ascension: 0 Seed: S1");
        t.OnSave(SoloSave(100));

        t.ProcessLine(Vote(1, Mid));

        TrackedRun run = t.Current!;
        Assert.Equal(Mid, run.Map!.Current);
        Assert.Null(run.MyVote);
        Assert.Empty(run.PartnerVotes);
    }

    [Fact]
    public void Coop_own_vote_does_not_move_current_and_is_reported_as_my_vote()
    {
        var t = CoopOnTheAncient();

        t.ProcessLine(Vote(TestPaths.Me, Left));

        TrackedRun run = t.Current!;
        Assert.Equal(Start, run.Map!.Current); // the party is still on the ancient
        Assert.Equal(Left, run.MyVote);
        Assert.Empty(run.PartnerVotes);
    }

    [Fact]
    public void Coop_partner_vote_is_kept_apart_from_mine()
    {
        var t = CoopOnTheAncient();

        t.ProcessLines([Vote(TestPaths.Me, Left), Vote(TestPaths.Friend, Right)]);

        TrackedRun run = t.Current!;
        Assert.Equal(Start, run.Map!.Current);
        Assert.Equal(Left, run.MyVote);
        PartnerVote partner = Assert.Single(run.PartnerVotes);
        Assert.Equal((TestPaths.Friend, Right), (partner.PlayerId, partner.Coord));
    }

    [Fact]
    public void Coop_changed_vote_replaces_the_earlier_one()
    {
        var t = CoopOnTheAncient();

        t.ProcessLines([Vote(TestPaths.Me, Left), Revote(TestPaths.Me, Left, Mid)]);

        Assert.Equal(Mid, t.Current!.MyVote);
        Assert.Equal(Start, t.Current.Map!.Current);
    }

    [Fact]
    public void Coop_votes_are_cleared_on_room_entry_and_the_room_save_moves_current()
    {
        var t = CoopOnTheAncient();
        t.ProcessLines([Vote(TestPaths.Me, Left), Vote(TestPaths.Friend, Right)]);

        // The game drew the partner's node: the room loads, then the room-entry save says where we really are.
        t.ProcessLine("[INFO] Preloading 'Event Room' assets... count=11 vfx=0");
        Assert.Null(t.Current!.MyVote);
        Assert.Empty(t.Current.PartnerVotes);
        Assert.Equal(Start, t.Current.Map!.Current); // not my vote's node

        SaveSnapshot entered = CoopSave(200) with { FileWriteUtc = T0.AddSeconds(5) };
        t.OnSave(entered with { Save = entered.Save with { Map = Map with { Current = Right } } });
        Assert.Equal(Right, t.Current!.Map!.Current);
        Assert.Null(t.Current.MyVote);
    }

    [Fact]
    public void Coop_vote_cast_before_the_run_is_known_as_coop_never_moves_current_and_becomes_my_vote()
    {
        // Continued run: the log names no players, and no save is in hand yet, so my id isn't known when I vote.
        var t = NewTracker(TestPaths.Me);
        t.ProcessLine("[INFO] Continuing run with character: CHARACTER.DEFECT");
        t.ProcessLine(Vote(TestPaths.Me, Left));

        t.OnSave(CoopSave(100));

        TrackedRun run = t.Current!;
        Assert.Equal(Start, run.Map!.Current);
        Assert.Equal(Left, run.MyVote);
        Assert.Empty(run.PartnerVotes); // my own vote is not shown as a partner's
    }
}
