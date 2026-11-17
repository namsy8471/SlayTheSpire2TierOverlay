using System.IO;
using Sts2Overlay.App.Services;

namespace Sts2Overlay.App.Tests;

/// <summary>"플레이 기록 보관": save copies per change, one log mirror per game session, pause beside another writer, size cap.</summary>
public sealed class PlayRecorderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sts2-playrec-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly string _out;
    private DateTime _now = new(2026, 11, 17, 6, 0, 0, DateTimeKind.Utc);

    public PlayRecorderTests()
    {
        _game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        _out = Path.Combine(_root, "records");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Save => Path.Combine(_game, "current_run.save");
    private string Log => Path.Combine(_game, "godot.log");
    private PlayRecorder New(long maxBytes = 0) => new(_out, maxBytes, () => _now);

    private void WriteGame(string path, string text, int secondsAgo = 5)
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, _now.AddSeconds(-secondsAgo));
    }

    private string[] Saves() => Directory.GetFiles(_out, "save_*.json").Select(Path.GetFileName).Order().ToArray()!;
    private string[] Sessions() => Directory.GetFiles(Path.Combine(_out, PlayRecorder.SessionsFolder)).Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public void Copies_a_save_once_per_change_named_by_its_write_time()
    {
        var r = New();
        WriteGame(Save, "{\"a\":1}");
        Assert.Equal(1, r.Step(Save, null, null).Saves);
        Assert.Equal(0, r.Step(Save, null, null).Saves);
        Assert.Equal(["save_20261117_055955_000.json"], Saves());

        _now = _now.AddMinutes(1);
        WriteGame(Save, "{\"a\":2}");
        r.Step(Save, null, null);
        Assert.Equal(2, Saves().Length);
        Assert.Equal("{\"a\":2}", File.ReadAllText(Path.Combine(_out, Saves()[^1])));
    }

    [Fact]
    public void A_save_still_being_written_waits_for_the_next_step()
    {
        var r = New();
        WriteGame(Save, "{}", secondsAgo: 0);
        Assert.Equal(0, r.Step(Save, null, null).Saves);
        _now = _now.AddSeconds(1);
        Assert.Equal(1, r.Step(Save, null, null).Saves);
    }

    [Fact]
    public void Coop_saves_get_their_own_prefix_and_missing_files_are_skipped()
    {
        var r = New();
        var coop = Path.Combine(_game, "current_run_mp.save");
        WriteGame(coop, "{}");
        var step = r.Step(Save, coop, Log);
        Assert.Equal(1, step.Saves);
        Assert.False(step.LogCopied);
        Assert.Single(Directory.GetFiles(_out, "mp_save_*.json"));
        Assert.Empty(Saves());
    }

    [Fact]
    public void The_log_mirror_grows_in_one_file_and_a_new_game_session_starts_another()
    {
        var r = New();
        WriteGame(Log, "header A\nline 1\n");
        Assert.True(r.Step(null, null, Log).LogCopied);
        Assert.False(r.Step(null, null, Log).LogCopied); // unchanged
        WriteGame(Log, "header A\nline 1\nline 2\n", secondsAgo: 2);
        r.Step(null, null, Log);
        Assert.Single(Sessions());
        Assert.Equal("header A\nline 1\nline 2\n", File.ReadAllText(Path.Combine(_out, "sessions", Sessions()[0])));

        _now = _now.AddMinutes(10);
        WriteGame(Log, "header B\n"); // the game restarted: shorter file, new first bytes
        r.Step(null, null, Log);
        Assert.Equal(2, Sessions().Length);
    }

    [Fact]
    public void Pauses_while_another_program_writes_a_session_mirror_in_the_same_folder()
    {
        var sessions = Directory.CreateDirectory(Path.Combine(_out, PlayRecorder.SessionsFolder)).FullName;
        var other = Path.Combine(sessions, "godot_session_20261117_050000.log");
        File.WriteAllText(other, "datawatch");
        File.SetLastWriteTimeUtc(other, _now.AddSeconds(-3));
        WriteGame(Save, "{}");
        WriteGame(Log, "header\n");

        var r = New();
        var step = r.Step(Save, null, Log);
        Assert.Equal("godot_session_20261117_050000.log", step.PausedFor);
        Assert.Empty(Saves());

        _now = _now.AddMinutes(1); // the other writer stopped
        step = r.Step(Save, null, Log);
        Assert.Null(step.PausedFor);
        Assert.Single(Saves());
        Assert.Equal(2, Sessions().Length);
        Assert.Null(r.Step(Save, null, Log).PausedFor); // our own fresh mirror does not pause us
    }

    [Fact]
    public void The_size_cap_removes_the_oldest_records_but_keeps_the_current_session()
    {
        var r = New(maxBytes: 250);
        for (int i = 0; i < 4; i++)
        {
            _now = _now.AddMinutes(1);
            WriteGame(Save, new string('x', 100) + i);
            r.Step(Save, null, null);
        }
        Assert.Equal(2, Saves().Length); // 4 × 101 bytes, cap 250
        Assert.EndsWith("3", File.ReadAllText(Path.Combine(_out, Saves()[^1])));

        WriteGame(Log, new string('L', 400), secondsAgo: 1);
        r.Step(null, null, Log);
        Assert.Single(Sessions()); // larger than the cap alone, still kept
    }

    [Fact]
    public void No_cap_keeps_everything()
    {
        var r = New(maxBytes: 0);
        for (int i = 0; i < 3; i++)
        {
            _now = _now.AddMinutes(1);
            WriteGame(Save, new string('x', 1000) + i);
            r.Step(Save, null, null);
        }
        Assert.Equal(3, Saves().Length);
    }
}
