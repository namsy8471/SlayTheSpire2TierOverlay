using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay;
using Sts2Overlay.Core.Replay.Validation;
using Sts2Overlay.Core.Rest;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Tests.Data;
using Sts2Overlay.Core.Tests.Scoring;

namespace Sts2Overlay.Core.Tests.Rest;

/// <summary>
/// Curses, statuses, quest cards and other cards the game cannot upgrade (catalog <c>maxUpgrade 0</c>) are never rest-site
/// upgrade candidates, with the real v0.107.1 pack loaded the way the overlay loads it (pack directory only, no explicit
/// catalog). Regression: the overlay loaded its pack without a catalog and advised upgrading the curse 탐욕 (Greed).
/// </summary>
public class NoCurseUpgradeTests
{
    private const string Silent = "CHARACTER.SILENT";

    private static string PackDir => Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1");

    /// <summary>The pack exactly as the app's AppController.LoadPack loads it.</summary>
    private static readonly Lazy<DataPack> AppPack = new(() => DataPackLoader.LoadDirectory(PackDir));

    /// <summary>The game's own rule: a card can be upgraded when its type allows it and maxUpgrade is above its level.</summary>
    private static bool GameAllowsUpgrade(CatalogCard c, int level)
        => c.Type is not ("Curse" or "Status" or "Quest") && level < c.MaxUpgrade;

    [Fact]
    public void Pack_loaded_from_its_directory_finds_the_committed_catalog()
    {
        var pack = AppPack.Value;

        Assert.NotNull(pack.Catalog);
        Assert.Equal(RealCatalog.Instance.Cards.Count, pack.Catalog!.Cards.Count);
        Assert.Equal(CardTypes.Curse, pack.CardType(Silent, "CARD.GREED"));
        Assert.True(DeckAnalyzer.IsCurse(pack, Silent, "CARD.GREED"));
        // The sample pack has no data/catalog/cards.sample.json: still no catalog, as before.
        Assert.Null(DataPackLoader.LoadDirectory(RealCatalog.SamplePackDir).Catalog);
    }

    [Fact]
    public void Sibling_catalog_path_follows_the_pack_directory_name()
    {
        var expected = Path.Combine(TestPaths.RepoRoot, "data", "catalog", "cards.v0.107.1.json");

        Assert.Equal(expected, DataPackLoader.SiblingCatalogPath(PackDir));
        Assert.Equal(expected, DataPackLoader.SiblingCatalogPath(PackDir + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void The_reported_deck_never_offers_Greed_for_an_upgrade()
    {
        // The deck from the bug report (Silent, act 1 rest site before the boss, HP 81%).
        string[] deck =
        [
            "CARD.BUBBLE_BUBBLE", "CARD.DAGGER_SPRAY", "CARD.DASH", "CARD.GREED", "CARD.LEADING_STRIKE", "CARD.MIRAGE",
            "CARD.NEUTRALIZE", "CARD.NOXIOUS_FUMES+1", "CARD.POISONED_STAB", "CARD.PREPARED+1", "CARD.SURVIVOR",
            .. Enumerable.Repeat("CARD.DEFEND_SILENT", 6), .. Enumerable.Repeat("CARD.STRIKE_SILENT", 5),
        ];
        var run = TestPacks.State(act: 1, deck: deck, hp: 57, maxHp: 70) with { Character = Silent };

        var advice = new RestSiteAdvisor(AppPack.Value).Advise(run, [MapNodeType.Boss]);

        Assert.NotEmpty(advice.Upgrades);
        Assert.DoesNotContain(advice.Upgrades, u => u.Card.Id == "CARD.GREED");
        Assert.DoesNotContain(advice.Upgrades, u => u.Card.Upgrade > 0);
    }

    [Fact]
    public void Every_catalog_card_is_upgradable_exactly_when_the_game_allows_it()
    {
        var pack = AppPack.Value;
        var wrong = new List<string>();
        foreach (var c in pack.Catalog!.Cards)
        {
            // Ask as the card's own character when it has one, else as every character (curses belong to none).
            var owners = c.PoolOwner is { } o && o != DataPack.Colorless ? [o] : pack.Tiers.Keys.ToArray();
            foreach (var character in owners)
            {
                for (var level = 0; level <= 1; level++)
                {
                    var card = new DeckCard(new CardRef(c.Id, level));
                    if (UpgradeAdvisor.CanUpgrade(pack, character, card) != GameAllowsUpgrade(c, level))
                    {
                        wrong.Add($"{c.Id}+{level} as {character} ({c.Type}, maxUpgrade {c.MaxUpgrade})");
                    }
                }
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public void No_fixture_rest_site_offers_a_curse_status_or_unupgradable_card()
    {
        var pack = AppPack.Value;
        var files = TestPaths.AllRuns.Select(p => (p, RunHistoryReader.ReadFile(p))).ToArray();
        var decisions = RestSiteBacktest.Run(ReplayStreams.Build(files, allPlayers: true, LocalPlayer.FixtureSelfId), pack);

        Assert.NotEmpty(decisions);
        var bad = decisions
            .SelectMany(d => d.Advice.Upgrades.Select(u => (d, u)))
            .Where(x => !IsRealUpgrade(pack, x.u.Card))
            .Select(x => $"{x.d.RunKey} floor {x.d.Floor}: {x.u.Card}")
            .ToArray();
        Assert.Empty(bad);
    }

    [Fact]
    public void No_fixture_deck_at_any_floor_ranks_a_curse_status_or_unupgradable_card()
    {
        // Every deck of every player before every room of the fixture runs, not only at rest sites: more decks with
        // curses (Neow, events) and statuses in them.
        var pack = AppPack.Value;
        var advisor = new UpgradeAdvisor(pack);
        var files = TestPaths.AllRuns.Select(p => (p, RunHistoryReader.ReadFile(p))).ToArray();
        int decks = 0, decksWithUnupgradable = 0;
        var bad = new List<string>();
        foreach (var s in ReplayStreams.Build(files, allPlayers: true, LocalPlayer.FixtureSelfId))
        {
            var timeline = DeckTimeline.Build(s.Run, s.Player.Id);
            for (var i = 0; i < s.Run.Nodes.Count; i++)
            {
                var state = timeline.StateBefore(i);
                decks++;
                if (state.Deck.Any(card => pack.FindCatalog(card.Id) is { } c && !GameAllowsUpgrade(c, card.Upgrade))) decksWithUnupgradable++;
                bad.AddRange(advisor.Rank(state).Where(u => !IsRealUpgrade(pack, u.Card)).Select(u => $"{s.GameKey} node {i}: {u.Card}"));
            }
        }

        Assert.Empty(bad);
        Assert.True(decks > 500, $"only {decks} decks");
        // The check means something only if many of those decks did hold curses, statuses or quest cards.
        Assert.True(decksWithUnupgradable > 50, $"only {decksWithUnupgradable} decks held an unupgradable card");
    }

    private static bool IsRealUpgrade(DataPack pack, CardRef card)
        => pack.FindCatalog(card.Id) is { } c && GameAllowsUpgrade(c, card.Upgrade);
}
