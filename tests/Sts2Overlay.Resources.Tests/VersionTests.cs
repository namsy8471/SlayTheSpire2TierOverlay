using Sts2Overlay.Resources;

namespace Sts2Overlay.Resources.Tests;

public sealed class GameVersionTests
{
    [Theory]
    [InlineData("v0.107.1", "v0.108", -1)]
    [InlineData("v0.108", "v0.108.0", 0)]
    [InlineData("0.107.1", "v0.107.1", 0)]
    [InlineData("v0.99", "v0.107", -1)]       // numeric, not text, comparison
    [InlineData("v1.0", "v0.999.9", 1)]
    [InlineData("v0.108-beta", "v0.108", -1)] // pre-release sorts before the release
    [InlineData("v0.108-alpha", "v0.108-beta", -1)]
    [InlineData("v0.107.1+build7", "v0.107.1", 0)]
    public void Compare(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(GameVersion.Parse(a).CompareTo(GameVersion.Parse(b))));
    }

    [Fact]
    public void EqualVersions_HaveEqualHashes_AndKeepTheirText()
    {
        var a = GameVersion.Parse("v0.108");
        var b = GameVersion.Parse("0.108.0");
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal("v0.108", a.Text);
        Assert.Equal("v0.108.0", b.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("v0..1")]
    [InlineData("v0.1.2.3.4")]
    [InlineData("v-1.0")]
    [InlineData("v0.1-")]
    public void Garbage_DoesNotParse(string? text)
    {
        Assert.False(GameVersion.TryParse(text, out _));
    }
}

public sealed class VersionGuardTests
{
    private static readonly HashSet<string> Offerable = ["CARD_POOL.IRONCLAD_CARD_POOL", "CARD_POOL.COLORLESS_CARD_POOL"];

    private static readonly GuardCard[] Catalog =
    [
        new("CARD.BASH", "CARD_POOL.IRONCLAD_CARD_POOL", "Common"),
        new("CARD.NEW_ONE", "CARD_POOL.IRONCLAD_CARD_POOL", "Rare"),
        new("CARD.NEW_TWO", "CARD_POOL.COLORLESS_CARD_POOL", "Uncommon"),
        new("CARD.STRIKE_IRONCLAD", "CARD_POOL.IRONCLAD_CARD_POOL", "Basic"), // basics are never offered
        new("CARD.WOUND", "CARD_POOL.STATUS_CARD_POOL", "Status"),            // not an offerable pool
    ];

    private static VersionGuardResult Run(string? game, string? pack, params string[] tiered)
        => VersionGuard.Evaluate(game, pack, Catalog, Offerable, tiered.Contains, tiered);

    [Fact]
    public void NewerGame_WarnsAndListsUnratedAndRemovedCards()
    {
        var r = Run("v0.108", "v0.107.1", "CARD.BASH", "CARD.GONE");

        Assert.True(r.VersionMismatch);
        Assert.Equal(1, r.Comparison);
        Assert.Equal(["CARD.NEW_ONE", "CARD.NEW_TWO"], r.UnratedCards);
        Assert.Equal(["CARD.GONE"], r.RemovedCards);

        var lines = r.PanelLines("C");
        Assert.Equal("게임 버전 v0.108이 데이터 팩 v0.107.1보다 새롭습니다: 티어가 맞지 않을 수 있음", lines[0]);
        Assert.Equal("티어 없는 카드 2장 (기본 티어 C로 계산): NEW_ONE, NEW_TWO", lines[1]);
        Assert.Equal("게임에서 빠진 카드 1장의 티어는 무시합니다", lines[2]);
    }

    [Fact]
    public void OlderGame_SaysOlder()
    {
        var lines = Run("v0.106.9", "v0.107.1", "CARD.BASH", "CARD.NEW_ONE", "CARD.NEW_TWO").PanelLines("C");

        Assert.Single(lines);
        Assert.Equal("게임 버전 v0.106.9가 데이터 팩 v0.107.1보다 오래되었습니다: 티어가 맞지 않을 수 있음", lines[0]);
    }

    [Fact]
    public void SameVersion_OrUnknownVersion_ShowsNothing_WhenEveryOfferableCardHasATier()
    {
        string[] all = ["CARD.BASH", "CARD.NEW_ONE", "CARD.NEW_TWO", "CARD.GONE"];
        Assert.Empty(Run("v0.107.1", "v0.107.1.0", all).PanelLines("C"));
        Assert.Empty(Run(null, "v0.107.1", all).PanelLines("C")); // CARD.GONE: the "removed" line needs a version mismatch
        Assert.Null(Run("garbage", "v0.107.1").Comparison);
    }

    [Theory]
    [InlineData("v0.107.1")] // same version (a hotfix that kept the number)
    [InlineData(null)]       // release_info.json missing
    [InlineData("garbage")]  // a version we cannot parse
    public void UnratedCards_AreListedWhateverTheVersionComparisonSays(string? game)
    {
        var lines = Run(game, "v0.107.1", "CARD.BASH").PanelLines("C");

        Assert.Equal(["티어 없는 카드 2장 (기본 티어 C로 계산): NEW_ONE, NEW_TWO"], lines);
    }

    [Fact]
    public void UnratedCards_UseTheCardNameInTheOverlayLanguage_AndTheIdOnlyAsFallback()
    {
        var names = new Dictionary<string, string> { ["CARD.NEW_ONE"] = "새 카드" };
        var lines = Run("v0.108", "v0.107.1", "CARD.BASH").PanelLines("C", cardName: id => names.GetValueOrDefault(id));

        Assert.Equal("티어 없는 카드 2장 (기본 티어 C로 계산): 새 카드, NEW_TWO", lines[1]);
    }

    [Fact]
    public void LongUnratedList_IsShortened()
    {
        var many = Enumerable.Range(0, 10).Select(i => new GuardCard($"CARD.N{i}", "CARD_POOL.IRONCLAD_CARD_POOL", "Common")).ToList();
        var r = VersionGuard.Evaluate("v0.108", "v0.107.1", many, Offerable, _ => false, []);

        Assert.EndsWith("N0, N1, N2 외 7장", r.PanelLines("C", maxNames: 3)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCatalog_DoesNotReportEveryTierAsRemoved()
    {
        var r = VersionGuard.Evaluate("v0.108", "v0.107.1", [], Offerable, _ => true, ["CARD.BASH"]);

        Assert.Empty(r.RemovedCards);
    }

    [Theory]
    [InlineData("v0.108", "v0.108이")]
    [InlineData("v0.109", "v0.109가")]
    [InlineData("v0.110", "v0.110이")]
    [InlineData("v1.2", "v1.2가")]
    public void KoreanSubjectParticle_FollowsTheLastDigit(string version, string expected)
    {
        Assert.Equal(expected, VersionGuardResult.Subject(version));
    }

    [Fact]
    public void LoadCatalog_ReadsIdPoolRarity_AndToleratesBrokenFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"gameVersion":"v0.108","cards":[{"id":"CARD.A","pool":"P","rarity":"Rare","names":{"kor":"가"}},{"pool":"no id"}]}""");
            Assert.Equal([new GuardCard("CARD.A", "P", "Rare")], VersionGuard.LoadCatalog(path));

            File.WriteAllText(path, "{ broken");
            Assert.Empty(VersionGuard.LoadCatalog(path));
            Assert.Empty(VersionGuard.LoadCatalog(null));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class GameInstallTests
{
    [Fact]
    public void ReleaseInfo_AssemblyHashCheck_DetectsAMismatchedDll()
    {
        using var game = new FakeGame("v0.107.1");
        var ok = game.Detect();
        Assert.Equal(GameVersion.Parse("v0.107.1"), ok.Version);
        Assert.True(ok.AssemblyHashMatches);
        Assert.Equal("v0.107.1", ok.VersionLabel);

        game.WriteDll(seed: 99); // e.g. a modded sts2.dll; release_info.json still describes the original
        Assert.False(game.Detect().AssemblyHashMatches);
    }

    [Fact]
    public void NoReleaseInfo_FallsBackToAHashLabel()
    {
        using var game = new FakeGame();
        File.Delete(game.Install.ReleaseInfoPath);

        var g = game.Detect();

        Assert.Null(g.Version);
        Assert.Null(g.AssemblyHashMatches);
        Assert.StartsWith("unknown-", g.VersionLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void Locate_PrefersTheConfiguredFolder_AndRejectsInvalidOnes()
    {
        using var game = new FakeGame();
        Assert.Equal(game.Install, GameInstall.Locate(game.Install.Directory, steamRoots: []));
        Assert.Null(GameInstall.Locate(Path.Combine(game.Root, "nope"), steamRoots: [Path.Combine(game.Root, "no-steam")]));
    }

    [Fact]
    public void Locate_FindsTheGameInASecondSteamLibrary()
    {
        using var game = new FakeGame();
        // <root>/steam/steamapps/libraryfolders.vdf lists <root>/lib2; the game lives in lib2/steamapps/common/...
        var steam = Path.Combine(game.Root, "steam");
        var lib2 = Path.Combine(game.Root, "lib2");
        var target = Path.Combine(lib2, "steamapps", "common", GameInstall.SteamFolderName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(game.Install.Directory, target);
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{lib2.Replace(@"\", @"\\", StringComparison.Ordinal)}\"\n\t}}\n}}\n");

        Assert.Equal(target, GameInstall.Locate(null, [steam])?.Directory);
    }

    [Fact]
    public void SteamRoots_StartWithTheRegistryFolders_ThenProgramFiles_WithoutDuplicates()
    {
        var registry = new Dictionary<string, string>
        {
            [@"HKEY_CURRENT_USER\Software\Valve\Steam|SteamPath"] = "d:/games/steam", // the client writes '/' and lower case
            [@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam|InstallPath"] = @"D:\Games\Steam\",
        };
        var roots = GameInstall.DefaultSteamRoots((key, value) => registry.GetValueOrDefault(key + "|" + value)).ToList();

        Assert.Equal(@"D:\Games\Steam", roots[0], ignoreCase: true);
        Assert.Single(roots, r => r.Equals(@"D:\Games\Steam", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roots, r => r.EndsWith(@"\Steam", StringComparison.OrdinalIgnoreCase) && r.Contains("Program Files", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SteamRoots_WithoutRegistryValues_AreTheProgramFilesFolders()
    {
        var roots = GameInstall.DefaultSteamRoots((_, _) => null).ToList();

        Assert.NotEmpty(roots);
        Assert.All(roots, r => Assert.Contains("Program Files", r, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Locate_FindsTheGameUnderTheSteamFolderTheRegistryNames()
    {
        using var game = new FakeGame();
        var steam = Path.Combine(game.Root, "MySteam");
        var target = Path.Combine(steam, "steamapps", "common", GameInstall.SteamFolderName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(game.Install.Directory, target);

        var roots = GameInstall.DefaultSteamRoots((_, value) => value == "SteamPath" ? steam.Replace('\\', '/') : null);
        Assert.Equal(target, GameInstall.Locate(null, roots)?.Directory, ignoreCase: true);
    }

    [Fact]
    public void ParseLibraryFolders_UnescapesBackslashes()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "label"		""
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameInstall.ParseLibraryFolders(vdf));
    }

    [RealGameFact]
    public void RealInstall_ReleaseInfoMatchesTheInstalledDll()
    {
        var install = GameInstall.Locate()!;
        var g = InstalledGame.Detect(install);

        Assert.NotNull(g.Version);
        Assert.True(g.AssemblyHashMatches, "release_info.json main_assembly_hash should equal SHA-1(sts2.dll)[0..4]");
        Assert.Equal(64, g.Fingerprint.DllSha256.Length);
    }
}

/// <summary>Skips when Slay the Spire 2 is not installed (CI). xunit 2 has no runtime skip, so the attribute sets it.</summary>
public sealed class RealGameFactAttribute : FactAttribute
{
    public RealGameFactAttribute()
    {
        if (GameInstall.Locate() is null) Skip = "needs a local Slay the Spire 2 install";
    }
}
