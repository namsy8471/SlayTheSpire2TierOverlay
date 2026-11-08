using System.Text.Json.Nodes;

namespace Sts2Overlay.Contracts.Tests;

public sealed class BridgeConnectionFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sts2overlay-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static BridgeConnectionInfo Sample => new()
    {
        Port = 51234,
        Token = "tok-123",
        Pid = 4242,
        Protocol = ProtocolVersion.Current,
        StartedAt = new DateTimeOffset(2026, 11, 8, 12, 30, 0, TimeSpan.FromHours(9)),
    };

    [Fact]
    public void WriteThenRead_RoundTrips_AndCreatesDirectory()
    {
        var path = Path.Combine(_dir, "nested", BridgeConnectionFile.FileName);

        BridgeConnectionFile.Write(path, Sample);
        var back = BridgeConnectionFile.TryRead(path);

        Assert.Equal(Sample, back);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Write_UsesCamelCaseFields()
    {
        var path = Path.Combine(_dir, BridgeConnectionFile.FileName);

        BridgeConnectionFile.Write(path, Sample);
        var obj = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Assert.Equal(["port", "token", "pid", "protocol", "startedAt"], obj.Select(p => p.Key));
        Assert.Equal(51234, obj["port"]!.GetValue<int>());
    }

    [Fact]
    public void Write_OverwritesExistingFile()
    {
        var path = Path.Combine(_dir, BridgeConnectionFile.FileName);

        BridgeConnectionFile.Write(path, Sample);
        BridgeConnectionFile.Write(path, Sample with { Port = 60000, Token = "new" });

        var back = BridgeConnectionFile.TryRead(path);
        Assert.Equal(60000, back!.Port);
        Assert.Equal("new", back.Token);
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        Assert.Null(BridgeConnectionFile.TryRead(Path.Combine(_dir, "nope.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ broken")]
    [InlineData("""{"port":0,"token":"t"}""")]
    [InlineData("""{"port":70000,"token":"t"}""")]
    [InlineData("""{"port":5000}""")]
    public void TryRead_InvalidContent_ReturnsNull(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, BridgeConnectionFile.FileName);
        File.WriteAllText(path, content);

        Assert.Null(BridgeConnectionFile.TryRead(path));
    }

    [Fact]
    public void TryRead_IgnoresUnknownFields()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, BridgeConnectionFile.FileName);
        File.WriteAllText(path, """{"port":5000,"token":"t","pid":1,"protocol":1,"startedAt":"2026-11-08T00:00:00Z","extra":true}""");

        var info = BridgeConnectionFile.TryRead(path);

        Assert.Equal(5000, info!.Port);
        Assert.Equal(DateTimeOffset.Parse("2026-11-08T00:00:00Z"), info.StartedAt);
    }

    [Fact]
    public void TryDelete_RemovesFile_AndToleratesMissing()
    {
        var path = Path.Combine(_dir, BridgeConnectionFile.FileName);
        BridgeConnectionFile.Write(path, Sample);

        BridgeConnectionFile.TryDelete(path);
        BridgeConnectionFile.TryDelete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void GenerateToken_IsRandomAndUrlSafe()
    {
        var a = BridgeConnectionFile.GenerateToken();
        var b = BridgeConnectionFile.GenerateToken();

        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
    }

    [Fact]
    public void DefaultPath_PointsToSts2OverlayFolder()
    {
        var path = BridgeConnectionFile.DefaultPath;

        Assert.EndsWith(Path.Combine("Sts2Overlay", "bridge.json"), path);
    }
}
