using System.IO;
using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.App.Tests;

/// <summary>티어표 tab: which cards a pool lists, grouping, sorting, filters, marks and texts (no window).</summary>
public class TierListPresenterTests
{
    private const string Ironclad = "CHARACTER.IRONCLAD";
    private const string Pool = "CARD_POOL.IRONCLAD_CARD_POOL";

    private static CardTierEntry Rated(Tier tier, double adjust = 0, Dictionary<int, Tier>? acts = null, double confidence = 1, string? changedIn = null)
        => new() { Tier = tier, BaseAdjust = adjust, ActTier = acts, Confidence = confidence, ChangedIn = changedIn };

    private static CatalogCard Card(string id, string type = "Attack", string rarity = "Common", string pool = Pool, string? multiplayer = null, int? cost = 1)
        => new() { Id = id, Pool = pool, Type = type, Rarity = rarity, Multiplayer = multiplayer, Cost = cost };

    /// <summary>
    /// Ironclad: A (adjust +2), A (adjust +5), B, D, a co-op-only B; catalog adds an unrated common (shown), an unrated basic,
    /// ancient and event-pool card (not offerable) and a colourless card (another pool).
    /// </summary>
    private static DataPack Pack()
    {
        var tiers = new TierPack
        {
            Character = Ironclad,
            Cards =
            {
                ["CARD.ALPHA"] = Rated(Tier.A, adjust: 2),
                ["CARD.BRAVO"] = Rated(Tier.A, adjust: 5),
                ["CARD.CHARLIE"] = Rated(Tier.B, acts: new() { [1] = Tier.A, [3] = Tier.B }),
                ["CARD.DELTA"] = Rated(Tier.D, confidence: 0.4),
                ["CARD.COOP"] = Rated(Tier.B),
            },
        };
        var catalog = new CardCatalog
        {
            Cards =
            [
                Card("CARD.ALPHA"), Card("CARD.BRAVO", "Skill", "Rare"), Card("CARD.CHARLIE", "Power", "Uncommon"), Card("CARD.DELTA", cost: -1),
                Card("CARD.COOP", "Skill", "Uncommon", multiplayer: CatalogCard.MultiplayerOnlyMode),
                Card("CARD.NEW_ONE", "Skill", "Common"),
                Card("CARD.STRIKE_IRONCLAD", rarity: "Basic"),
                Card("CARD.OLD_GOD", rarity: "Ancient"),
                Card("CARD.EVENTY", rarity: "Event", pool: "CARD_POOL.EVENT_CARD_POOL"),
                Card("CARD.GREY", pool: "CARD_POOL.COLORLESS_CARD_POOL"),
            ],
        };
        return new DataPack { Tiers = new Dictionary<string, TierPack> { [Ironclad] = tiers }, Catalog = catalog };
    }

    private static string Name(string id) => id;

    private static TierListFilter Filter(bool coop = true, string? search = null, string[]? hiddenTypes = null, string[]? hiddenRarities = null)
        => new(new HashSet<string>(hiddenTypes ?? []), new HashSet<string>(hiddenRarities ?? []), coop, search);

    [Fact]
    public void Pool_lists_rated_cards_and_offerable_unrated_ones_only()
    {
        var ids = TierListPresenter.Entries(Pack(), Ironclad).Select(e => e.Id).Order(StringComparer.Ordinal);

        Assert.Equal(["CARD.ALPHA", "CARD.BRAVO", "CARD.CHARLIE", "CARD.COOP", "CARD.DELTA", "CARD.NEW_ONE"], ids);
    }

    [Fact]
    public void Groups_go_S_to_D_then_unrated_sorted_by_adjustment_then_name_and_skip_empty_tiers()
    {
        var groups = TierListPresenter.Group(TierListPresenter.Entries(Pack(), Ironclad), Filter(), Name);

        Assert.Equal(["A", "B", "D", "?"], groups.Select(g => g.TierText));
        Assert.Equal(["CARD.BRAVO", "CARD.ALPHA"], groups[0].Cards.Select(c => c.Id)); // +5 before +2
        Assert.Equal(["CARD.CHARLIE", "CARD.COOP"], groups[1].Cards.Select(c => c.Id)); // same adjustment: by name
        Assert.Equal(["CARD.NEW_ONE"], groups[3].Cards.Select(c => c.Id));
    }

    [Fact]
    public void Unrated_section_is_left_out_when_every_offerable_card_is_rated()
    {
        var pack = Pack();
        pack.Tiers[Ironclad].Cards["CARD.NEW_ONE"] = Rated(Tier.C);

        var groups = TierListPresenter.Group(TierListPresenter.Entries(pack, Ironclad), Filter(), Name);

        Assert.DoesNotContain(groups, g => g.TierText == "?");
    }

    [Fact]
    public void Type_rarity_and_search_filters_combine_and_empty_tiers_collapse()
    {
        var entries = TierListPresenter.Entries(Pack(), Ironclad);

        var noAttacks = TierListPresenter.Group(entries, Filter(hiddenTypes: [CardTypes.Attack]), Name);
        Assert.DoesNotContain(noAttacks.SelectMany(g => g.Cards), c => c.Type == CardTypes.Attack);
        Assert.DoesNotContain(noAttacks, g => g.TierText == "D"); // DELTA was the only D

        var rareOnly = TierListPresenter.Group(entries, Filter(hiddenRarities: ["common", "uncommon"]), Name);
        Assert.Equal(["CARD.BRAVO"], rareOnly.SelectMany(g => g.Cards).Select(c => c.Id));

        var search = TierListPresenter.Group(entries, Filter(search: "char"), Name);
        Assert.Equal(["CARD.CHARLIE"], search.SelectMany(g => g.Cards).Select(c => c.Id));
    }

    [Fact]
    public void Type_and_rarity_chips_come_in_game_order()
    {
        var entries = TierListPresenter.Entries(Pack(), Ironclad);

        Assert.Equal([CardTypes.Attack, CardTypes.Skill, CardTypes.Power], TierListPresenter.TypesOf(entries));
        Assert.Equal(["common", "uncommon", "rare"], TierListPresenter.RaritiesOf(entries));
        Assert.Equal("공격", TierListPresenter.TypeName(CardTypes.Attack));
        Assert.Equal("고급", TierListPresenter.RarityName("uncommon"));
        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("Power", TierListPresenter.TypeName(CardTypes.Power));
            Assert.Equal("Other", TierListPresenter.RarityName(TierListPresenter.OtherKey));
        }
    }

    [Theory]
    [InlineData(false, null, false)] // solo run (or no run): hidden
    [InlineData(true, null, true)] // co-op run: shown
    [InlineData(false, true, true)] // chip turned on in a solo run
    [InlineData(true, false, false)] // chip turned off in a co-op run
    public void Coop_cards_follow_the_run_type_unless_the_chip_overrides(bool multiplayer, bool? choice, bool shown)
    {
        var show = TierListPresenter.ShowCoop(multiplayer, choice);
        var ids = TierListPresenter.Group(TierListPresenter.Entries(Pack(), Ironclad), Filter(coop: show), Name).SelectMany(g => g.Cards).Select(c => c.Id);

        Assert.Equal(shown, show);
        Assert.Equal(shown, ids.Contains("CARD.COOP"));
    }

    [Fact]
    public void Coop_and_estimate_flags_come_from_the_catalog_and_the_pack()
    {
        var byId = TierListPresenter.Entries(Pack(), Ironclad).ToDictionary(e => e.Id);

        Assert.True(byId["CARD.COOP"].CoopOnly);
        Assert.False(byId["CARD.ALPHA"].CoopOnly);
        Assert.True(byId["CARD.DELTA"].IsEstimate); // confidence 0.4
        Assert.False(byId["CARD.NEW_ONE"].IsEstimate); // unrated is not "estimated"
    }

    [Fact]
    public void Owned_counts_include_upgraded_copies_and_resolve_aliases()
    {
        var run = new RunState
        {
            Character = Ironclad,
            Deck = [new DeckCard(new CardRef("CARD.ALPHA")), new DeckCard(new CardRef("CARD.ALPHA", 1)), new DeckCard(new CardRef("CARD.OLD_BRAVO"))],
        };

        var owned = TierListPresenter.OwnedCounts(run, id => id == "CARD.OLD_BRAVO" ? "CARD.BRAVO" : id);

        Assert.Equal(2, owned["CARD.ALPHA"]);
        Assert.Equal(1, owned["CARD.BRAVO"]);
        Assert.Empty(TierListPresenter.OwnedCounts(null, id => id));
        Assert.True(TierListPresenter.SameCounts(owned, new Dictionary<string, int> { ["CARD.BRAVO"] = 1, ["CARD.ALPHA"] = 2 }));
        Assert.False(TierListPresenter.SameCounts(owned, new Dictionary<string, int> { ["CARD.ALPHA"] = 2 }));
    }

    [Fact]
    public void Tile_owned_mark_shows_a_count_for_extra_copies()
    {
        var tile = new TierCardVm { Id = "CARD.ALPHA", Name = "Alpha" };
        var raised = new List<string?>();
        tile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(tile.IsOwned);
        tile.Owned = 1;
        Assert.Equal("✓", tile.OwnedText);
        tile.Owned = 2;
        Assert.Equal("✓2", tile.OwnedText);
        Assert.Equal("덱에 2장", tile.OwnedLine);
        Assert.Contains(nameof(TierCardVm.IsOwned), raised);
        Assert.Contains(nameof(TierCardVm.OwnedText), raised);
    }

    [Fact]
    public void Act_marker_shows_only_a_different_tier_of_the_current_act()
    {
        var charlie = TierListPresenter.Entries(Pack(), Ironclad).Single(e => e.Id == "CARD.CHARLIE"); // B, act 1 A, act 3 B

        Assert.Equal(("1막 A", "A"), TierListPresenter.ActMarker(charlie, 1));
        Assert.Null(TierListPresenter.ActMarker(charlie, 2)); // no act-2 entry
        Assert.Null(TierListPresenter.ActMarker(charlie, 3)); // same as the base tier
        Assert.Null(TierListPresenter.ActMarker(charlie, null)); // no run
        Assert.Equal("막별 티어: 1막 A", TierListPresenter.ActTiersText(charlie));
        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal(("Act 1: A", "A"), TierListPresenter.ActMarker(charlie, 1));
            Assert.Equal("By act: Act 1: A", TierListPresenter.ActTiersText(charlie));
        }
    }

    [Fact]
    public void Missing_local_names_fall_back_to_a_readable_name_not_the_id()
    {
        var names = CardInfoProvider.Load(null, new DataPack());

        Assert.Equal("Ball Lightning", names.Name("CARD.BALL_LIGHTNING"));
        using (Loc.Use(Lang.Eng)) Assert.Equal("Ball Lightning", names.Name("CARD.BALL_LIGHTNING"));
    }

    [Fact]
    public void Headers_notes_and_details_in_both_languages()
    {
        var delta = TierListPresenter.Entries(Pack(), Ironclad).Single(e => e.Id == "CARD.DELTA");
        var coop = TierListPresenter.Entries(Pack(), Ironclad).Single(e => e.Id == "CARD.COOP");

        Assert.Equal("12장", TierListPresenter.GroupTitle("S", 12));
        Assert.Equal("미평가 · 3장", TierListPresenter.GroupTitle("?", 3));
        Assert.Equal("87장", TierListPresenter.Summary(87, 87));
        Assert.Equal("87장 중 40장 표시", TierListPresenter.Summary(40, 87));
        Assert.StartsWith("티어는 카드 자체의 기본 평가입니다.", TierListPresenter.Note);
        Assert.Equal("티어 D · 일반 · 공격 · 추정 티어", TierListPresenter.DetailText(delta)); // unplayable: no cost
        Assert.Equal("티어 B · 고급 · 스킬 · 비용 1 · 협동 전용", TierListPresenter.DetailText(coop));
        Assert.Equal("무색", TierListPresenter.PoolName(DataPack.Colorless));
        Assert.Equal("티어표", UiText.Instance.TabTiers);
        using (Loc.Use(Lang.Eng))
        {
            Assert.Equal("12 cards", TierListPresenter.GroupTitle("S", 12));
            Assert.Equal("Unrated · 3 cards", TierListPresenter.GroupTitle("?", 3));
            Assert.Equal("Showing 40 of 87", TierListPresenter.Summary(40, 87));
            Assert.Equal("Tiers rate the card on its own. Reward rankings also use your deck and top players' picks, so they can differ.", TierListPresenter.Note);
            Assert.Equal("Tier B · Uncommon · Skill · Cost 1 · Co-op only", TierListPresenter.DetailText(coop));
            Assert.Equal("Colorless", TierListPresenter.PoolName(DataPack.Colorless));
            Assert.Equal("Tiers", UiText.Instance.TabTiers);
            Assert.Equal("Co-op cards", TierListPresenter.CoopChip);
        }
    }

    [Theory]
    [InlineData(null, "CHARACTER.IRONCLAD")]
    [InlineData("COLORLESS", "COLORLESS")]
    [InlineData("CHARACTER.REGENT", "CHARACTER.REGENT")]
    [InlineData("CHARACTER.UNKNOWN", "CHARACTER.IRONCLAD")]
    public void Without_a_run_the_last_picked_pool_or_Ironclad_is_shown(string? saved, string expected)
        => Assert.Equal(expected, TierListPresenter.DefaultPool(saved));

    [Fact]
    public void Picked_pool_is_a_session_setting_not_a_user_choice()
    {
        // After a broken settings.json load, clicking a pool chip must not count as "the user changed a setting".
        Assert.Equal(SettingsStore.UserChoices(new AppSettings()), SettingsStore.UserChoices(new AppSettings { TierCharacter = "COLORLESS" }));
    }

    [Fact]
    public void Real_pack_lists_every_pool_and_flags_its_coop_cards()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SlayTheSpire2TierOverlay.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var pack = DataPackLoader.LoadDirectory(Path.Combine(root.FullName, "data", "packs", "v0.107.1"));

        Assert.DoesNotContain(TierListPresenter.Fool, TierListPresenter.AvailablePools(pack));
        var beta = DataPackLoader.LoadDirectory(Path.Combine(root.FullName, "data", "packs", "v0.111.0"),
            catalogPath: Path.Combine(root.FullName, "data", "catalog", "cards.v0.113.0.json"));
        Assert.Contains(TierListPresenter.Fool, TierListPresenter.AvailablePools(beta));
        Assert.Equal(90, beta.Catalog!.CardsOf(TierListPresenter.Fool).Count());
        Assert.NotEmpty(TierListPresenter.Entries(beta, TierListPresenter.Fool));
        foreach (var pool in TierListPresenter.AvailablePools(pack))
        {
            var entries = TierListPresenter.Entries(pack, pool);
            Assert.True(entries.Count >= 50, $"{pool}: {entries.Count} cards");
            Assert.All(entries, e => Assert.StartsWith("CARD.", e.Id));
            Assert.Contains(entries, e => e.CoopOnly);
            Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
        }
    }
}

/// <summary>Name search of the 티어표 tab, with Korean initial consonants (초성).</summary>
public class HangulSearchTests
{
    [Theory]
    [InlineData('방', 'ㅂ')]
    [InlineData('가', 'ㄱ')]
    [InlineData('힣', 'ㅎ')]
    [InlineData('뼈', 'ㅃ')]
    [InlineData('ㅂ', 'ㅂ')]
    [InlineData('ᄇ', 'ㅂ')] // Hangul Jamo initial B
    public void Choseong_of_a_syllable_or_jamo(char c, char expected) => Assert.Equal(expected, HangulSearch.Choseong(c));

    [Theory]
    [InlineData('a')]
    [InlineData('1')]
    [InlineData('ㅏ')] // a vowel is not an initial
    public void Choseong_of_anything_else_is_null(char c) => Assert.Null(HangulSearch.Choseong(c));

    [Theory]
    [InlineData("방어 본능", "ㅂㅂ")] // word initials
    [InlineData("방어 본능", "ㅂㅇ")] // initials inside a word
    [InlineData("방어 본능", "ㅂㅇㅂㄴ")]
    [InlineData("방어 본능", "방어본")] // spaces ignored
    [InlineData("방어 본능", "본능")]
    [InlineData("방어 본능", "방ㅇ")] // syllable + initial
    [InlineData("방어 본능", "")]
    [InlineData("방어 본능", "  ")]
    [InlineData("Shrug It Off", "shrug")]
    [InlineData("Shrug It Off", "IT OFF")]
    [InlineData("Shrug It Off", "sio")]
    public void Matches(string name, string query) => Assert.True(HangulSearch.Matches(name, query));

    [Theory]
    [InlineData("방어 본능", "ㄱ")]
    [InlineData("방어 본능", "ㅂㄱ")]
    [InlineData("방어 본능", "방어 본능 강화")]
    [InlineData("Shrug It Off", "bash")]
    [InlineData("방어", "바")] // a full syllable is not an initial
    public void Does_not_match(string name, string query) => Assert.False(HangulSearch.Matches(name, query));
}
