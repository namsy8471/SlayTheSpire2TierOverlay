using System.IO;
using Sts2Overlay.App.Infrastructure;

namespace Sts2Overlay.App.Tests;

/// <summary>Where data packs and generated local data are looked up for a given exe location (AppLayout.Resolve).</summary>
public class AppLayoutTests
{
    private const string LocalAppData = @"C:\Users\me\AppData\Local";
    private const string UserLocal = @"C:\Users\me\AppData\Local\Sts2Overlay\local";

    /// <summary>An existence check over a fixed set of paths (files and folders alike), case-insensitive like NTFS.</summary>
    private static Func<string, bool> Fs(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return p => set.Contains(Path.TrimEndingDirectorySeparator(p));
    }

    [Fact]
    public void Checkout_bin_folder_uses_the_repo_data_and_repo_local()
    {
        var fs = Fs(@"C:\repo\data\packs", @"C:\repo\SlayTheSpire2TierOverlay.sln");
        var layout = AppLayout.Resolve(@"C:\repo\src\Sts2Overlay.App\bin\Release\net10.0-windows10.0.22621.0\", LocalAppData, fs);

        Assert.Equal(AppLayoutKind.Checkout, layout.Kind);
        Assert.Equal(@"C:\repo", layout.ContentRoot);
        Assert.Equal(@"C:\repo", layout.RepoRoot);
        Assert.Equal(@"C:\repo\data\local", layout.LocalDataDir);
    }

    [Fact]
    public void Git_worktree_marker_file_counts_as_a_checkout()
    {
        // A worktree's .git is a file, and a source zip may lack the .sln: either marker is enough.
        var fs = Fs(@"C:\wt\data\packs", @"C:\wt\.git");
        var layout = AppLayout.Resolve(@"C:\wt\dist", LocalAppData, fs);

        Assert.Equal(AppLayoutKind.Checkout, layout.Kind);
        Assert.Equal(@"C:\wt\data\local", layout.LocalDataDir);
    }

    [Fact]
    public void Dist_exe_keeps_the_repo_data_even_with_published_data_next_to_it()
    {
        // build-exe puts dist\ inside the repo; if publish output data ever lands in dist\data, the repo still wins.
        var fs = Fs(@"C:\repo\data\packs", @"C:\repo\SlayTheSpire2TierOverlay.sln", @"C:\repo\dist\data\packs");
        var layout = AppLayout.Resolve(@"C:\repo\dist", LocalAppData, fs);

        Assert.Equal(AppLayoutKind.Checkout, layout.Kind);
        Assert.Equal(@"C:\repo", layout.ContentRoot);
        Assert.Equal(@"C:\repo\data\local", layout.LocalDataDir);
    }

    [Fact]
    public void Installed_build_reads_packs_next_to_the_exe_and_writes_local_data_to_localappdata()
    {
        const string current = @"C:\Users\me\AppData\Local\namsy8471.Sts2Overlay\current";
        var fs = Fs(current + @"\data\packs");
        var layout = AppLayout.Resolve(current + @"\", LocalAppData, fs);

        Assert.Equal(AppLayoutKind.Installed, layout.Kind);
        Assert.Equal(current, layout.ContentRoot);
        Assert.Null(layout.RepoRoot);
        // Not inside "current": Velopack replaces that folder on every update.
        Assert.Equal(UserLocal, layout.LocalDataDir);
    }

    [Fact]
    public void Unmarked_ancestor_with_packs_is_still_used_like_before()
    {
        // A copied repo without .sln/.git: the old "walk up until data/packs" rule.
        var fs = Fs(@"D:\copy\data\packs");
        var layout = AppLayout.Resolve(@"D:\copy\tools\bin", LocalAppData, fs);

        Assert.Equal(AppLayoutKind.Checkout, layout.Kind);
        Assert.Equal(@"D:\copy\data\local", layout.LocalDataDir);
    }

    [Fact]
    public void Nothing_found_falls_back_to_the_exe_folder_and_localappdata()
    {
        var layout = AppLayout.Resolve(@"E:\portable\overlay", LocalAppData, Fs());

        Assert.Equal(AppLayoutKind.Fallback, layout.Kind);
        Assert.Equal(@"E:\portable\overlay", layout.ContentRoot);
        Assert.Null(layout.RepoRoot);
        Assert.Equal(UserLocal, layout.LocalDataDir);
    }

    [Fact]
    public void Real_test_run_resolves_to_this_checkout()
    {
        // The test binaries live under tests/.../bin in the checkout, so the real file system must say Checkout.
        var layout = AppLayout.Resolve(AppContext.BaseDirectory, LocalAppData, p => Directory.Exists(p) || File.Exists(p));

        Assert.Equal(AppLayoutKind.Checkout, layout.Kind);
        Assert.True(File.Exists(Path.Combine(layout.ContentRoot, "SlayTheSpire2TierOverlay.sln")));
    }
}
