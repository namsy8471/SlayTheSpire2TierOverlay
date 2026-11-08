using System.IO;
using System.Reflection;

namespace Sts2Overlay.App.Services;

/// <summary>Kind of place updates are read from (see <see cref="UpdateSourceSpec.FromSetting"/>).</summary>
public enum UpdateSourceKind
{
    /// <summary>GitHub releases of a repo (Velopack GithubSource; anonymous API, 60 requests/hour/IP).</summary>
    GitHub,

    /// <summary>Any http(s) folder holding the vpk output (releases.win.json + .nupkg) (SimpleWebSource).</summary>
    Web,

    /// <summary>A local or network folder holding the vpk output (SimpleFileSource), mainly for testing.</summary>
    Folder,
}

/// <summary>
/// Where <see cref="UpdateService"/> looks for updates, decided from the <c>updateUrl</c> setting without touching
/// Velopack, so the rules are unit-tested (tests/Sts2Overlay.App.Tests/UpdatePolicyTests.cs).
/// </summary>
public sealed record UpdateSourceSpec(UpdateSourceKind Kind, string Location)
{
    /// <summary>The official releases.</summary>
    public const string DefaultRepoUrl = "https://github.com/namsy8471/SlayTheSpire2TierOverlay";

    public static UpdateSourceSpec Default { get; } = new(UpdateSourceKind.GitHub, DefaultRepoUrl);

    /// <summary>
    /// Empty → <see cref="Default"/>. An http(s) URL → <see cref="UpdateSourceKind.GitHub"/> when it points at a
    /// github.com repo (reduced to <c>https://github.com/owner/repo</c>; <c>.git</c> or <c>/releases...</c> are dropped),
    /// else <see cref="UpdateSourceKind.Web"/>. An absolute path or file: URL → <see cref="UpdateSourceKind.Folder"/>.
    /// Anything else (a relative path, ftp:, a bare github.com without owner/repo) is invalid → null.
    /// </summary>
    public static UpdateSourceSpec? TryParse(string? updateUrl)
    {
        var value = updateUrl?.Trim();
        if (string.IsNullOrEmpty(value)) return Default;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                var host = uri.Host.ToLowerInvariant();
                if (host is "github.com" or "www.github.com")
                {
                    var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return null;
                    var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
                    return new UpdateSourceSpec(UpdateSourceKind.GitHub, $"https://github.com/{parts[0]}/{repo}");
                }
                return new UpdateSourceSpec(UpdateSourceKind.Web, value);
            }
            if (uri.IsFile) return new UpdateSourceSpec(UpdateSourceKind.Folder, Path.GetFullPath(uri.LocalPath));
            return null;
        }
        return Path.IsPathFullyQualified(value) ? new UpdateSourceSpec(UpdateSourceKind.Folder, Path.GetFullPath(value)) : null;
    }

    public override string ToString() => $"{Kind} {Location}";
}

/// <summary>
/// When to ask the update source again. GitHub's anonymous API allows 60 requests per hour per IP (shared by everyone
/// behind the same router), so: once ~15 s after start, then at most every 6 hours; a manual "업데이트 확인" may go
/// sooner but not more than once a minute.
/// </summary>
public sealed class UpdateSchedule
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    public static readonly TimeSpan ManualMinGap = TimeSpan.FromMinutes(1);

    /// <summary>How often the timer asks <see cref="ShouldCheck"/> after the first check (cheap; it rarely says yes).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    /// <summary>UTC time of the last check that was started; null before the first.</summary>
    public DateTime? LastCheckUtc { get; private set; }

    public bool ShouldCheck(DateTime nowUtc, bool manual)
    {
        if (LastCheckUtc is not { } last) return true;
        return nowUtc - last >= (manual ? ManualMinGap : Interval);
    }

    public void MarkChecked(DateTime nowUtc) => LastCheckUtc = nowUtc;
}

/// <summary>The app's version for display ("버전 0.1.0").</summary>
public static class AppVersion
{
    /// <summary>The informational version of the exe without the "+commit" suffix the SDK appends (Source Link).</summary>
    public static string FromAssembly()
        => Clean(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                 ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3));

    /// <summary>"0.1.0+abc123" → "0.1.0"; null/blank → "?".</summary>
    public static string Clean(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "?";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return (plus >= 0 ? version[..plus] : version).Trim();
    }
}
