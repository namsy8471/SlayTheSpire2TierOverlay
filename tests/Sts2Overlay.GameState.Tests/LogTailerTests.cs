using System.Text;
using Sts2Overlay.GameState.Log;

namespace Sts2Overlay.GameState.Tests;

public sealed class LogTailerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sts2-tailer-").FullName;
    private string LogPath => Path.Combine(_dir, "godot.log");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Append(byte[] bytes)
    {
        using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        fs.Write(bytes);
    }

    private void Append(string text) => Append(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Returns_only_complete_lines_and_keeps_the_rest()
    {
        Append("[INFO] one\r\n[INFO] tw");
        var tailer = new LogTailer(LogPath);
        Assert.Equal(["[INFO] one"], tailer.Poll());
        Assert.Empty(tailer.Poll());
        Append("o\n[INFO] three\n");
        Assert.Equal(["[INFO] two", "[INFO] three"], tailer.Poll());
    }

    [Fact]
    public void Multibyte_character_split_across_polls_is_decoded_once_complete()
    {
        byte[] korean = Encoding.UTF8.GetBytes("가지 슬라임\n"); // 3 bytes per Hangul syllable
        Append(korean[..2]); // cut inside the first character
        var tailer = new LogTailer(LogPath);
        Assert.Empty(tailer.Poll());
        Append(korean[2..]);
        Assert.Equal(["가지 슬라임"], tailer.Poll());
    }

    [Fact]
    public void Start_at_end_skips_existing_content()
    {
        Append("old line\n");
        var tailer = new LogTailer(LogPath, startAtEnd: true);
        Assert.Empty(tailer.Poll());
        Append("new line\n");
        Assert.Equal(["new line"], tailer.Poll());
    }

    [Fact]
    public void Rotation_rename_and_recreate_restarts_from_zero()
    {
        Append("[INFO] a fairly long line from the previous game session\n");
        var tailer = new LogTailer(LogPath);
        int rotations = 0;
        tailer.Rotated += (_, _) => rotations++;
        Assert.Single(tailer.Poll());

        // What the game does on launch: godot.log → godotYYYY-MM-DDTHH.MM.SS.log, then a fresh godot.log.
        File.Move(LogPath, Path.Combine(_dir, "godot2026-11-08T05.10.23.log"));
        Append("[INFO] new\n");
        Assert.Equal(["[INFO] new"], tailer.Poll());
        Assert.Equal(1, rotations);
    }

    [Fact]
    public void Missing_file_is_not_an_error_and_is_picked_up_later()
    {
        var tailer = new LogTailer(LogPath);
        Assert.Empty(tailer.Poll());
        Append("hello\n");
        Assert.Equal(["hello"], tailer.Poll());
    }

    [Fact]
    public void Reading_does_not_block_the_writer_from_renaming()
    {
        Append("x\n");
        var tailer = new LogTailer(LogPath);
        tailer.Poll();
        // The tailer closes its handle after every poll, so the game's rename always succeeds.
        File.Move(LogPath, LogPath + ".old");
        Assert.True(File.Exists(LogPath + ".old"));
    }
}
