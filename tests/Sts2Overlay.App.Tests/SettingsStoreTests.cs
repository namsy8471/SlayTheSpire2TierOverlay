using System.IO;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Tests;

/// <summary>A typo in settings.json (e.g. single backslashes in gameDir) must not wipe the user's settings.</summary>
[Collection(nameof(StandingNoticesCollection))]
public sealed class SettingsStoreTests : IDisposable
{
    private const string Broken = "{ \"gameDir\": \"D:\\Games\\Slay the Spire 2\", \"recognizeHz\": 2 }"; // \G, \S: invalid JSON escapes
    private readonly string _dir = Directory.CreateTempSubdirectory("sts2-settings-").FullName;

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        StandingNotices.Set(StandingNotices.Settings, null);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Broken_file_gives_defaults_keeps_a_bad_copy_and_shows_a_notice()
    {
        File.WriteAllText(SettingsPath, Broken);
        File.WriteAllText(SettingsPath + ".bad", "older bad copy");

        var settings = SettingsStore.Load(SettingsPath);

        Assert.True(settings.LoadedFromBrokenFile);
        Assert.Null(settings.GameDir);
        Assert.Equal(3, settings.RecognizeHz); // the default
        Assert.Equal(Broken, File.ReadAllText(SettingsPath + ".bad")); // older .bad replaced
        using (Loc.Use(Lang.Kor))
            Assert.Contains("settings.json을 읽지 못해 기본값으로 실행 중입니다 (원본은 settings.json.bad로 보관)", StandingNotices.Lines());
        using (Loc.Use(Lang.Eng))
            Assert.Contains(StandingNotices.Lines(), l => l.StartsWith("settings.json could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void Broken_file_is_not_overwritten_on_exit_when_nothing_was_changed()
    {
        File.WriteAllText(SettingsPath, Broken);
        var settings = SettingsStore.Load(SettingsPath);

        // What OnExit / PanelWindow.SavePosition always write back, changed or not.
        settings.PanelX = 100;
        settings.PanelY = 200;
        settings.SelectedTab = "Offer";
        SettingsStore.Save(SettingsPath, settings);

        Assert.Equal(Broken, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Broken_file_is_replaced_once_the_user_changes_a_setting()
    {
        File.WriteAllText(SettingsPath, Broken);
        var settings = SettingsStore.Load(SettingsPath);

        settings.MapOverlay = false; // e.g. the tray toggle
        SettingsStore.Save(SettingsPath, settings);

        var reloaded = SettingsStore.Load(SettingsPath);
        Assert.False(reloaded.LoadedFromBrokenFile);
        Assert.False(reloaded.MapOverlay);
        Assert.False(settings.LoadedFromBrokenFile); // later saves (exit) go through as usual
        Assert.Equal(Broken, File.ReadAllText(SettingsPath + ".bad"));
    }

    [Fact]
    public void Valid_or_missing_file_loads_and_saves_as_before()
    {
        Assert.False(SettingsStore.Load(SettingsPath).LoadedFromBrokenFile); // missing: defaults, nothing kept
        Assert.False(File.Exists(SettingsPath + ".bad"));

        File.WriteAllText(SettingsPath, "{ \"gameDir\": \"D:\\\\Games\\\\Slay the Spire 2\", // a comment\n \"recognizeHz\": 2, }");
        var settings = SettingsStore.Load(SettingsPath);
        Assert.False(settings.LoadedFromBrokenFile);
        Assert.Equal((@"D:\Games\Slay the Spire 2", 2.0), (settings.GameDir, settings.RecognizeHz));

        settings.PanelX = 5;
        SettingsStore.Save(SettingsPath, settings);
        Assert.Equal(5, SettingsStore.Load(SettingsPath).PanelX);
        Assert.DoesNotContain("loadedFromBrokenFile", File.ReadAllText(SettingsPath), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(SettingsPath + ".bad"));
    }
}

/// <summary><see cref="StandingNotices"/> is process-wide: tests that set notices do not run in parallel with each other.</summary>
[CollectionDefinition(nameof(StandingNoticesCollection), DisableParallelization = true)]
public sealed class StandingNoticesCollection;
