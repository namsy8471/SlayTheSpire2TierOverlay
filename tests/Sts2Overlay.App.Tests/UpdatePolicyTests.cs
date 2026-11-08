using Sts2Overlay.App.Services;

namespace Sts2Overlay.App.Tests;

/// <summary>Update source selection from the updateUrl setting, the check throttle and version display (no Velopack needed).</summary>
public class UpdatePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_setting_uses_the_official_github_releases(string? setting)
    {
        var spec = UpdateSourceSpec.TryParse(setting);

        Assert.Equal(new UpdateSourceSpec(UpdateSourceKind.GitHub, "https://github.com/namsy8471/SlayTheSpire2TierOverlay"), spec);
    }

    [Theory]
    [InlineData("https://github.com/someone/fork")]
    [InlineData("https://github.com/someone/fork/")]
    [InlineData("https://github.com/someone/fork.git")]
    [InlineData("https://www.github.com/someone/fork/releases/tag/v1.0.0")]
    [InlineData("http://GitHub.com/someone/fork")]
    public void Github_repo_urls_become_a_github_source_for_the_repo(string setting)
    {
        var spec = UpdateSourceSpec.TryParse(setting);

        Assert.Equal(new UpdateSourceSpec(UpdateSourceKind.GitHub, "https://github.com/someone/fork"), spec);
    }

    [Theory]
    [InlineData("https://example.com/sts2/releases")]
    [InlineData("http://192.168.0.10:8080/feed/")]
    [InlineData("https://objects.githubusercontent.com/some/path")]
    public void Other_http_urls_become_a_web_source(string setting)
    {
        var spec = UpdateSourceSpec.TryParse(setting);

        Assert.Equal(UpdateSourceKind.Web, spec?.Kind);
        Assert.Equal(setting, spec?.Location);
    }

    [Theory]
    [InlineData(@"C:\temp\releases", @"C:\temp\releases")]
    [InlineData(@"  C:\temp\releases\  ", @"C:\temp\releases\")]
    [InlineData(@"\\server\share\releases", @"\\server\share\releases")]
    [InlineData("file:///C:/temp/releases", @"C:\temp\releases")]
    public void Absolute_folders_become_a_folder_source(string setting, string expected)
    {
        var spec = UpdateSourceSpec.TryParse(setting);

        Assert.Equal(UpdateSourceKind.Folder, spec?.Kind);
        Assert.Equal(expected, spec?.Location);
    }

    [Theory]
    [InlineData("releases")]
    [InlineData(@"..\releases")]
    [InlineData("ftp://example.com/releases")]
    [InlineData("https://github.com/")]
    [InlineData("https://github.com/onlyowner")]
    public void Unusable_settings_are_rejected(string setting)
        => Assert.Null(UpdateSourceSpec.TryParse(setting));

    [Fact]
    public void First_check_always_runs_then_automatic_checks_wait_six_hours()
    {
        var schedule = new UpdateSchedule();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(schedule.ShouldCheck(t0, manual: false));
        schedule.MarkChecked(t0);

        Assert.False(schedule.ShouldCheck(t0.AddMinutes(30), manual: false));
        Assert.False(schedule.ShouldCheck(t0.AddHours(6).AddSeconds(-1), manual: false));
        Assert.True(schedule.ShouldCheck(t0.AddHours(6), manual: false));
    }

    [Fact]
    public void Manual_checks_may_run_early_but_not_more_than_once_a_minute()
    {
        var schedule = new UpdateSchedule();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        schedule.MarkChecked(t0);

        Assert.False(schedule.ShouldCheck(t0.AddSeconds(30), manual: true));
        Assert.True(schedule.ShouldCheck(t0.AddMinutes(1), manual: true));

        // A manual check also restarts the 6 h wait of the automatic ones.
        schedule.MarkChecked(t0.AddHours(1));
        Assert.False(schedule.ShouldCheck(t0.AddHours(6), manual: false));
        Assert.True(schedule.ShouldCheck(t0.AddHours(7), manual: false));
    }

    [Fact]
    public void Default_timing_matches_the_github_rate_limit_plan()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), UpdateSchedule.FirstCheckDelay);
        Assert.Equal(TimeSpan.FromHours(6), UpdateSchedule.Interval);
    }

    [Theory]
    [InlineData("0.1.0+3f2a9c1d", "0.1.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("0.2.0-beta.1+abc", "0.2.0-beta.1")]
    [InlineData(null, "?")]
    [InlineData("", "?")]
    public void Version_text_drops_the_commit_suffix(string? raw, string expected)
        => Assert.Equal(expected, AppVersion.Clean(raw));
}
