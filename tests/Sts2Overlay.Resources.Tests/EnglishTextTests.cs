using System.Text.RegularExpressions;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Resources;

namespace Sts2Overlay.Resources.Tests;

/// <summary>User-facing Resources text (version-guard panel lines, preparation progress) in English.</summary>
public sealed class EnglishTextTests : IDisposable
{
    private static readonly Regex Hangul = new("[가-힣]", RegexOptions.Compiled);
    private static readonly HashSet<string> Offerable = ["CARD_POOL.IRONCLAD_CARD_POOL"];

    private readonly FakeGame _game = new();

    public void Dispose() => _game.Dispose();

    [Fact]
    public void Version_guard_lines_in_English()
    {
        GuardCard[] catalog =
        [
            new("CARD.BASH", "CARD_POOL.IRONCLAD_CARD_POOL", "Common"),
            new("CARD.NEW_ONE", "CARD_POOL.IRONCLAD_CARD_POOL", "Common"),
            new("CARD.NEW_TWO", "CARD_POOL.IRONCLAD_CARD_POOL", "Rare"),
        ];
        string[] tiered = ["CARD.BASH", "CARD.GONE"];
        using var _ = Loc.Use(Lang.Eng);

        var newer = VersionGuard.Evaluate("v0.108", "v0.107.1", catalog, Offerable, tiered.Contains, tiered).PanelLines("C", maxNames: 1);
        Assert.Equal(
        [
            "Game version v0.108 is newer than data pack v0.107.1: tiers may be off",
            "2 cards without a tier (scored as C): NEW_ONE and 1 more",
            "Ignoring tiers of 1 card no longer in the game",
        ], newer);

        var older = VersionGuard.Evaluate("v0.106.9", "v0.107.1", catalog, Offerable, tiered.Contains, tiered).PanelLines("C");
        Assert.Equal("Game version v0.106.9 is older than data pack v0.107.1: tiers may be off", older[0]);
        Assert.DoesNotContain(newer.Concat(older), l => Hangul.IsMatch(l));
    }

    [Fact]
    public void Preparation_progress_in_English()
    {
        var reports = new List<PrepareProgress>();
        using (Loc.Use(Lang.Eng))
        {
            new ResourcePreparer(_game.Layout, FakeGame.FakeCatalog).Prepare(_game.Detect(), new Collect(reports.Add));
        }

        Assert.NotEmpty(reports);
        Assert.DoesNotContain(reports, r => Hangul.IsMatch(r.Text));
        Assert.StartsWith("Resources ready (", reports[^1].Text, StringComparison.Ordinal);
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class Collect(Action<PrepareProgress> report) : IProgress<PrepareProgress>
    {
        public void Report(PrepareProgress value) => report(value);
    }
}
