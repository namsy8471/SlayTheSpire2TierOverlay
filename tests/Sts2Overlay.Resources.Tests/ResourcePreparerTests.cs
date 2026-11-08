using Sts2Overlay.Resources;

namespace Sts2Overlay.Resources.Tests;

public sealed class ResourcePreparerTests : IDisposable
{
    private readonly FakeGame _game = new();
    private readonly ResourcePreparer _preparer;

    public ResourcePreparerTests() => _preparer = new ResourcePreparer(_game.Layout, FakeGame.FakeCatalog);

    public void Dispose() => _game.Dispose();

    [Fact]
    public void FreshFolder_NeedsPrepare_ThenBuildsEverything()
    {
        var game = _game.Detect();
        Assert.Equal(PrepareNeed.Missing, _preparer.Check(game).Need);

        var reports = new List<PrepareProgress>();
        var manifest = _preparer.Prepare(game, new SyncProgress<PrepareProgress>(reports.Add));

        var layout = _game.Layout;
        Assert.True(File.Exists(Path.Combine(layout.LocalizationDir, "kor", "cards.json")));
        Assert.True(File.Exists(Path.Combine(layout.LocalizationDir, "eng", "cards.json")));
        Assert.True(File.Exists(Path.Combine(layout.CardArtDir, "ironclad", "bash.webp")));
        Assert.True(File.Exists(Path.Combine(layout.CardArtDir, "ironclad", "beta", "anger.webp")));
        Assert.False(File.Exists(Path.Combine(layout.CardArtDir, "other.webp")));
        Assert.Contains("가나다", File.ReadAllText(layout.CatalogPath("v0.107.1")), StringComparison.Ordinal);

        Assert.Equal("catalog.v0.107.1.json", manifest.CatalogFile);
        Assert.Equal(2, manifest.CardArtFiles);
        Assert.Equal(3, manifest.LocalizationFiles);
        Assert.Equal(1, manifest.CatalogCards);
        Assert.Equal("v0.107.1", manifest.GameVersion);
        Assert.Equal(game.Fingerprint, manifest.Fingerprint);
        Assert.Equal(layout.CatalogPath("v0.107.1"), layout.ManifestCatalog());

        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.True(reports.Select(r => r.Fraction).SequenceEqual(reports.Select(r => r.Fraction).Order()), "progress must not go backwards");
        Assert.False(Directory.Exists(Path.Combine(layout.LocalDir, ".prepare-staging")));
        Assert.False(File.Exists(Path.Combine(layout.LocalDir, ".prepare.lock")));
    }

    [Fact]
    public void SecondRun_IsUpToDate()
    {
        _preparer.Prepare(_game.Detect());

        // A new process: detect again from disk, read the manifest back.
        var check = new ResourcePreparer(_game.Layout).Check(_game.Detect());

        Assert.Equal(PrepareNeed.UpToDate, check.Need);
    }

    [Fact]
    public void GameUpdate_DllChanged_IsStale_AndRebuildWritesNewVersion()
    {
        _preparer.Prepare(_game.Detect());

        _game.WriteDll(seed: 2);
        _game.WriteReleaseInfo("v0.108");
        var updated = _game.Detect();
        var check = _preparer.Check(updated);
        Assert.Equal(PrepareNeed.Stale, check.Need);
        Assert.Contains("game files changed", check.Reason, StringComparison.Ordinal);

        var manifest = _preparer.Prepare(updated);
        Assert.Equal("catalog.v0.108.json", manifest.CatalogFile);
        Assert.Equal(PrepareNeed.UpToDate, _preparer.Check(_game.Detect()).Need);
        Assert.Equal(_game.Layout.CatalogPath("v0.108"), _game.Layout.ManifestCatalog());
    }

    [Fact]
    public void PckRewritten_IsStale()
    {
        _preparer.Prepare(_game.Detect());
        File.SetLastWriteTimeUtc(_game.Install.PckPath, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(PrepareNeed.Stale, _preparer.Check(_game.Detect()).Need);
    }

    [Fact]
    public void MissingOutput_OrOldFormat_OrBrokenManifest_TriggersRebuild()
    {
        var game = _game.Detect();
        _preparer.Prepare(game);

        Directory.Delete(_game.Layout.CardArtDir, recursive: true);
        Assert.Equal(PrepareNeed.Missing, _preparer.Check(game).Need);

        _preparer.Prepare(game);
        var m = ResourceManifest.TryLoad(_game.Layout.ManifestPath)!;
        m.PrepareFormat = ResourcePreparer.FormatVersion - 1;
        m.Save(_game.Layout.ManifestPath);
        Assert.Equal(PrepareNeed.Stale, _preparer.Check(game).Need);

        File.WriteAllText(_game.Layout.ManifestPath, "{ not json");
        Assert.Equal(PrepareNeed.Missing, _preparer.Check(game).Need);
    }

    [Fact]
    public void FailedRebuild_KeepsOldResources()
    {
        var game = _game.Detect();
        _preparer.Prepare(game);
        var before = File.ReadAllText(_game.Layout.ManifestPath);

        _game.WriteDll(seed: 3);
        var failing = new ResourcePreparer(_game.Layout, _ => throw new InvalidOperationException("type renamed"));
        Assert.Throws<InvalidOperationException>(() => failing.Prepare(_game.Detect()));

        Assert.Equal(before, File.ReadAllText(_game.Layout.ManifestPath));
        Assert.True(File.Exists(Path.Combine(_game.Layout.CardArtDir, "ironclad", "bash.webp")));
        Assert.False(Directory.Exists(Path.Combine(_game.Layout.LocalDir, ".prepare-staging")));
    }

    [Fact]
    public void SecondPreparerAtTheSameTime_IsRefused()
    {
        Directory.CreateDirectory(_game.Layout.LocalDir);
        using var held = new FileStream(Path.Combine(_game.Layout.LocalDir, ".prepare.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<PrepareBusyException>(() => _preparer.Prepare(_game.Detect()));
    }

    [Fact]
    public void Cancel_StopsAndKeepsNoPartialManifest()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _preparer.Prepare(_game.Detect(), ct: cts.Token));
        Assert.False(File.Exists(_game.Layout.ManifestPath));
    }

    /// <summary><see cref="Progress{T}"/> posts to a sync context; tests want the calls inline.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
