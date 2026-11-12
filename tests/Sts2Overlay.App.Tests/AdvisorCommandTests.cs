using System.IO;
using Sts2Overlay.App.Integration.CombatAdvice;

namespace Sts2Overlay.App.Tests;

/// <summary>Where the combat advisor helper is found (setting, then next to the overlay exe) and how its command line is split.</summary>
public class AdvisorCommandTests
{
    private const string BaseDir = @"C:\made-up\overlay";
    private static readonly string DefaultExe = Path.Combine(BaseDir, "advisor", "Sts2CombatAdvisor.exe");

    private static Func<string, bool> Existing(params string[] files) => f => files.Contains(f, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(@"C:\x\Helper.exe", new[] { @"C:\x\Helper.exe" })]
    [InlineData(@"  C:\x\dotnet.exe   C:\y\Helper.dll  ", new[] { @"C:\x\dotnet.exe", @"C:\y\Helper.dll" })]
    [InlineData(@"""C:\Program Files\dotnet\dotnet.exe"" ""C:\my tools\Helper.dll"" --flag", new[] { @"C:\Program Files\dotnet\dotnet.exe", @"C:\my tools\Helper.dll", "--flag" })]
    [InlineData(@"a""b c""d e", new[] { "ab cd", "e" })]
    [InlineData(@"x """" y", new[] { "x", "", "y" })]
    [InlineData(@"""say """"hi""""""", new[] { @"say ""hi""" })]
    [InlineData("", new string[0])]
    public void Splits_a_quoted_command_line(string line, string[] expected)
        => Assert.Equal(expected, AdvisorCommand.SplitCommandLine(line));

    [Fact]
    public void The_setting_wins_and_keeps_its_leading_arguments()
    {
        var c = AdvisorCommand.Locate(@"""C:\Program Files\dotnet\dotnet.exe"" C:\tools\Helper.dll --verbose", BaseDir, out var warning,
            Existing(@"C:\Program Files\dotnet\dotnet.exe", DefaultExe));

        Assert.Null(warning);
        Assert.NotNull(c);
        Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", c.FileName);
        Assert.Equal([@"C:\tools\Helper.dll", "--verbose"], c.LeadingArgs);
        Assert.Equal("setting", c.Source);
        Assert.Equal([@"C:\tools\Helper.dll", "--verbose", "live", "--save", @"D:\s\current_run.save", "--log", @"D:\l\godot.log"],
            c.CommandArgs(AdvisorCommand.LiveArgs(@"D:\s\current_run.save", @"D:\l\godot.log")));
    }

    [Fact]
    public void Without_a_setting_the_exe_next_to_the_overlay_is_used()
    {
        var c = AdvisorCommand.Locate(null, BaseDir, out var warning, Existing(DefaultExe));

        Assert.Null(warning);
        Assert.Equal(DefaultExe, c!.FileName);
        Assert.Empty(c.LeadingArgs);
        Assert.Equal(["live", "--save", "s", "--log", "l"], c.CommandArgs(AdvisorCommand.LiveArgs("s", "l")));
    }

    [Theory]
    [InlineData(@"C:\gone\Helper.exe", "does not exist")]
    [InlineData(@"Helper.exe --x", "full path")]
    [InlineData(@"""""", "full path")]
    public void A_setting_that_cannot_be_used_falls_back_with_a_warning(string setting, string warningPart)
    {
        var c = AdvisorCommand.Locate(setting, BaseDir, out var warning, Existing(DefaultExe));

        Assert.Contains(warningPart, warning);
        Assert.Equal(DefaultExe, c!.FileName);
        Assert.Equal("default", c.Source);
    }

    [Fact]
    public void The_save_and_log_paths_are_the_ones_the_tracker_watches()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sts2-advisor-paths-" + Guid.NewGuid().ToString("N"));
        var saves = Path.Combine(dir, "steam", "11111111111111111", "profile1", "saves");
        var log = Path.Combine(dir, "logs", "godot.log");
        Directory.CreateDirectory(saves);
        try
        {
            using var adapter = new Sts2Overlay.App.Integration.GameStateAdapter(new Sts2Overlay.GameState.Tracking.TrackerOptions
            {
                Paths = new Sts2Overlay.GameState.GamePaths(saves, log, null),
            });
            Assert.Null(adapter.SinglePlayerSavePath); // nothing watched before Start
            adapter.Start();

            Assert.Equal(Path.Combine(saves, "current_run.save"), adapter.SinglePlayerSavePath);
            Assert.Equal(log, adapter.LogFile);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Nothing_found_turns_the_feature_off_silently()
    {
        Assert.Null(AdvisorCommand.Locate(null, BaseDir, out var warning, Existing()));
        Assert.Null(warning);
        Assert.Null(AdvisorCommand.Locate("   ", BaseDir, out warning, Existing()));
        Assert.Null(warning);
    }
}
