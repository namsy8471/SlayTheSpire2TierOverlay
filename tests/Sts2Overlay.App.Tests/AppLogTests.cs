using Sts2Overlay.App.Infrastructure;

namespace Sts2Overlay.App.Tests;

/// <summary>app.log goes into bug reports: it must not carry the Steam ID, the Windows user name or an e-mail address.</summary>
public class AppLogTests
{
    private const string Profile = @"C:\Users\Jane Doe";
    private static readonly DateTime T0 = new(2026, 11, 8, 12, 0, 0);

    [Theory]
    [InlineData(@"game state: saves=C:\Users\Jane Doe\AppData\Roaming\SlayTheSpire2\steam\76561190000000001\profile1\saves",
        @"game state: saves=%USERPROFILE%\AppData\Roaming\SlayTheSpire2\steam\<steamid>\profile1\saves")]
    [InlineData(@"exe c:/users/JANE DOE/AppData/Local/Sts2Overlay/current/", "exe %USERPROFILE%/AppData/Local/Sts2Overlay/current/")]
    [InlineData(@"json ""C:\\Users\\Jane Doe\\settings.json""", @"json ""%USERPROFILE%\\settings.json""")]
    [InlineData(@"at Foo() in C:\Users\Jane Doe:line 3", @"at Foo() in %USERPROFILE%:line 3")]
    [InlineData(@"short name C:\Users\JANEDO~1\AppData\Local\Temp\x", @"short name C:\Users\<user>\AppData\Local\Temp\x")]
    [InlineData(@"other user D:\Users\bob\x but C:\Users\Public\x stays", @"other user D:\Users\<user>\x but C:\Users\Public\x stays")]
    [InlineData(@"not the same user C:\Users\Jane Doette\x", @"not the same user C:\Users\<user> Doette\x")]
    [InlineData("user://steam/76561190000000002/profile1 net 76561190000000001.", "user://steam/<steamid>/profile1 net <steamid>.")]
    [InlineData("not ids: 765611900000000011 1234567656119000", "not ids: 765611900000000011 1234567656119000")]
    [InlineData("mail jane.doe+sts@example.co.kr please", "mail <email> please")]
    public void Redact_removes_user_folder_steam_ids_and_emails(string line, string expected)
        => Assert.Equal(expected, AppLog.Redact(line, Profile));

    [Fact]
    public void Redact_without_a_profile_still_removes_ids()
        => Assert.Equal(@"C:\Users\<user>\x <steamid>", AppLog.Redact(@"C:\Users\jane\x 76561190000000001", null));

    [Fact]
    public void Repeated_error_is_written_once_then_counted_once_a_minute()
    {
        var throttle = new RepeatThrottle(TimeSpan.FromMinutes(1));
        const string err = "capture pipeline step failed: boom\n   at X.Y()";

        Assert.Equal([err], throttle.Next(err, T0));
        Assert.Empty(throttle.Next(err, T0.AddSeconds(1)));
        Assert.Empty(throttle.Next(err, T0.AddSeconds(30)));
        Assert.Equal(["… repeated 3 more times: capture pipeline step failed: boom"], throttle.Next(err, T0.AddSeconds(61)));
        Assert.Empty(throttle.Next(err, T0.AddSeconds(62)));

        // A different error ends the streak: the count of the dropped repeats first, then the new error.
        Assert.Equal(["… repeated 1 more times: capture pipeline step failed: boom", "other"], throttle.Next("other", T0.AddSeconds(63)));
        Assert.Equal([err], throttle.Next(err, T0.AddSeconds(64)));
    }
}
