using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The overlay's language: reading the game's settings.save, the "언어 / Language" setting, and that the fixed UI texts
/// (UiText, XAML) have an English version.
/// </summary>
public class LanguageTests
{
    private static readonly Regex Hangul = new("[가-힣]");

    // ---- settings.save parsing ----

    [Theory]
    [InlineData("""{ "aspect_ratio": "sixteen_by_nine", "language": "kor", "fps_limit": 60 }""", "kor")]
    [InlineData("""{ "language": "eng" }""", "eng")]
    [InlineData("""{ "language": "jpn" }""", "jpn")]
    [InlineData("""{ "language": "  zhs " }""", "zhs")]
    [InlineData("""{ "nested": { "language": "kor" }, "language": "eng", }""", "eng")]
    public void Reads_the_language_code(string json, string expected)
        => Assert.Equal(expected, GameLanguage.ReadLanguageCode(json));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{ "fps_limit": 60 }""")]
    [InlineData("""{ "language": "" }""")]
    [InlineData("""{ "language": 3 }""")]
    [InlineData("""{ "language": null }""")]
    [InlineData("""["language", "kor"]""")]
    [InlineData("""{ "language": "kor" """)]
    [InlineData("not json at all")]
    public void Missing_or_garbage_settings_have_no_language(string? json)
        => Assert.Null(GameLanguage.ReadLanguageCode(json));

    [Fact]
    public void Picks_the_newest_settings_file_of_the_steam_account_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "sts2-lang-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(GameLanguage.FindSettingsFile(root)); // no game data at all

            var older = WriteSettings(root, "11111111111111111", "kor", DateTime.UtcNow.AddDays(-3));
            var newer = WriteSettings(root, "22222222222222222", "eng", DateTime.UtcNow.AddMinutes(-1));
            // Not a Steam id folder: ignored even though it is the newest.
            WriteSettings(root, "modded", "jpn", DateTime.UtcNow);

            Assert.Equal(newer, GameLanguage.FindSettingsFile(root));

            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddSeconds(5));
            Assert.Equal(older, GameLanguage.FindSettingsFile(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Watcher_reports_a_change_of_the_games_language()
    {
        var root = Path.Combine(Path.GetTempPath(), "sts2-lang-" + Guid.NewGuid().ToString("N"));
        try
        {
            var watcher = new GameLanguageWatcher(root);
            Assert.False(watcher.Poll()); // nothing yet
            Assert.Null(watcher.Code);

            var file = WriteSettings(root, "11111111111111111", "kor", DateTime.UtcNow.AddMinutes(-2));
            Assert.True(watcher.Poll());
            Assert.Equal("kor", watcher.Code);
            Assert.False(watcher.Poll()); // unchanged

            WriteSettings(root, "11111111111111111", "eng", DateTime.UtcNow.AddMinutes(-1));
            Assert.True(watcher.Poll());
            Assert.Equal("eng", watcher.Code);

            // Half-written file: keep the last known language.
            File.WriteAllText(file, """{ "language": """);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            Assert.False(watcher.Poll());
            Assert.Equal("eng", watcher.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteSettings(string root, string account, string language, DateTime writtenUtc)
    {
        var dir = Path.Combine(root, "steam", account);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, GameLanguage.SettingsFileName);
        File.WriteAllText(path, $$"""{ "aspect_ratio": "sixteen_by_nine", "language": "{{language}}" }""");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    // ---- setting → effective language ----

    [Theory]
    [InlineData("auto", "kor", Lang.Kor)]
    [InlineData("auto", "eng", Lang.Eng)]
    [InlineData("auto", "jpn", Lang.Eng)] // every other game language shows English
    [InlineData(null, "eng", Lang.Eng)]   // missing setting = auto
    [InlineData("AUTO", "kor", Lang.Kor)]
    [InlineData("kor", "eng", Lang.Kor)]  // a fixed choice wins over the game
    [InlineData("kor", null, Lang.Kor)]
    [InlineData("eng", "kor", Lang.Eng)]
    [InlineData(" Eng ", null, Lang.Eng)]
    [InlineData("klingon", "eng", Lang.Eng)] // unknown setting = auto
    public void Setting_and_game_code_give_the_language(string? setting, string? gameCode, Lang expected)
        => Assert.Equal(expected, GameLanguage.Resolve(setting, gameCode));

    [Theory]
    [InlineData("auto", null)]
    [InlineData("auto", "")]
    [InlineData(null, null)]
    public void Auto_without_the_games_language_keeps_the_current_one(string? setting, string? gameCode)
        => Assert.Null(GameLanguage.Resolve(setting, gameCode));

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("auto", "auto")]
    [InlineData("KOR", "kor")]
    [InlineData("eng", "eng")]
    [InlineData("jpn", "auto")]
    public void Settings_value_is_normalized(string? setting, string expected)
        => Assert.Equal(expected, GameLanguage.NormalizeSetting(setting));

    // ---- fixed UI texts ----

    public static TheoryData<string> UiTextProperties()
    {
        var data = new TheoryData<string>();
        foreach (var p in typeof(UiText).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(string)))
        {
            data.Add(p.Name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(UiTextProperties))]
    public void Every_ui_text_has_an_english_version(string property)
    {
        var info = typeof(UiText).GetProperty(property)!;
        string english, korean;
        using (Loc.Use(Lang.Eng)) english = (string)info.GetValue(UiText.Instance)!;
        using (Loc.Use(Lang.Kor)) korean = (string)info.GetValue(UiText.Instance)!;

        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.False(Hangul.IsMatch(english), $"{property} = \"{english}\" has Hangul in English");
        Assert.False(string.IsNullOrWhiteSpace(korean));
    }

    [Fact]
    public void Ui_text_keeps_the_korean_texts()
    {
        using var _ = Loc.Use(Lang.Kor);
        Assert.Equal("추천", UiText.Instance.TabOffer);
        Assert.Equal("STS2 티어 패널", UiText.Instance.PanelTitle);
        Assert.Equal("2위", UiText.Rank(2));
    }

    [Fact]
    public void Ui_text_raises_a_change_for_every_property()
    {
        // RaiseAll raises on the UI thread: at once when there is none, but posted to it when another test class has
        // already started the shared WPF thread (WpfThread creates the App). So wait for the event instead of reading a
        // list right after the call; test classes run in parallel and the order differs from run to run.
        using var raised = new ManualResetEventSlim();
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == string.Empty) raised.Set(); // "" = all properties (WPF re-reads every binding)
        };
        UiText.Instance.PropertyChanged += handler;
        try
        {
            UiText.Instance.RaiseAll();
            Assert.True(raised.Wait(TimeSpan.FromSeconds(30)), "no change event for all properties within 30 s");
        }
        finally
        {
            UiText.Instance.PropertyChanged -= handler;
        }
    }

    [Fact]
    public void View_model_texts_have_english_versions()
    {
        using var _ = Loc.Use(Lang.Eng);
        Assert.Equal("#3", UiText.Rank(3));
        var card = new BuildCardVm("Bash", "Core", "Rare", "A", Owned: false, Art: null);
        Assert.Equal("Core · Rare · Tier A", card.DetailText);
        Assert.Equal("Not yet", card.OwnedText);
        Assert.DoesNotMatch(Hangul, ForecastSectionVm.Empty.Title);
        Assert.DoesNotMatch(Hangul, new PanelViewModel().StatusText);
        Assert.DoesNotMatch(Hangul, new PanelViewModel().RunText);
    }

    // ---- XAML guard: no Korean literal left in the views ----

    private static readonly Regex XamlTextAttribute = new(
        """\b(?:Text|Content|ToolTip|Header|Title|Value)="(?<v>[^"]*)""",
        RegexOptions.Compiled);

    [Fact]
    public void Xaml_views_have_no_korean_literals()
    {
        var viewsDir = Path.Combine(FindRepoRoot(), "src", "Sts2Overlay.App", "Views");
        var files = Directory.GetFiles(viewsDir, "*.xaml");
        Assert.NotEmpty(files);

        var problems = new List<string>();
        foreach (var file in files)
        {
            var text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline); // comments are fine
            foreach (Match m in XamlTextAttribute.Matches(text))
            {
                if (Hangul.IsMatch(m.Groups["v"].Value)) problems.Add($"{Path.GetFileName(file)}: {m.Value}");
            }
        }
        Assert.True(problems.Count == 0, "Korean text literals in XAML (bind to ViewModels.UiText instead):\n" + string.Join("\n", problems));
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SlayTheSpire2TierOverlay.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("repo root (SlayTheSpire2TierOverlay.sln) not found above " + AppContext.BaseDirectory);
    }
}
