using System.Globalization;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Resources;

namespace Sts2Overlay.App.Controllers;

/// <summary>One card of a pool in the 티어표 tab (data only; names come from the caller).</summary>
/// <param name="Id">Canonical card id.</param>
/// <param name="Rating">The pack's tier entry; null = unrated (offerable, but the pack has no entry).</param>
/// <param name="Type">attack | skill | power | … (lower case), null when unknown.</param>
/// <param name="Rarity">common | uncommon | rare | … (lower case), null when unknown.</param>
/// <param name="Cost">Energy cost; null when unknown, negative for unplayable.</param>
internal sealed record TierListEntry(string Id, CardTierEntry? Rating, string? Type, string? Rarity, int? Cost, bool CostX, bool CoopOnly)
{
    public Tier? Tier => Rating?.Tier;
    public double BaseAdjust => Rating?.BaseAdjust ?? 0;
    public bool IsEstimate => Rating?.IsEstimate == true;
}

/// <summary>What the 티어표 tab shows.</summary>
/// <param name="HiddenTypes">Type keys switched off (<see cref="TierListPresenter.TypeKey"/>); everything else is shown.</param>
/// <param name="HiddenRarities">Rarity keys switched off (<see cref="TierListPresenter.RarityKey"/>).</param>
/// <param name="ShowCoop">Show multiplayer-only cards.</param>
/// <param name="Search">Name search (<see cref="HangulSearch"/>); null/empty = no search.</param>
internal sealed record TierListFilter(IReadOnlySet<string> HiddenTypes, IReadOnlySet<string> HiddenRarities, bool ShowCoop, string? Search = null)
{
    public static TierListFilter All { get; } = new(new HashSet<string>(), new HashSet<string>(), true);
}

/// <summary>A tier section before it becomes view models: "S".."D", or "?" for the unrated cards.</summary>
internal sealed record TierListGroup(string TierText, IReadOnlyList<TierListEntry> Cards);

/// <summary>
/// The 티어표 tab's logic, kept apart from <see cref="AppController"/> so it can be tested without a window: which cards a pool
/// has (the pack's tier list + offerable cards it does not rate), grouping S → D → 미평가, sorting, filters and the texts.
/// The tier shown is the pack's base tier, the card on its own: card rewards are ranked with the deck and the learned
/// model, so their order can differ, and the tab says so (<see cref="Note"/>).
/// </summary>
internal static class TierListPresenter
{
    public const string Ironclad = "CHARACTER.IRONCLAD";
    public const string Silent = "CHARACTER.SILENT";
    public const string Defect = "CHARACTER.DEFECT";
    public const string Necrobinder = "CHARACTER.NECROBINDER";
    public const string Regent = "CHARACTER.REGENT";

    /// <summary>The selector chips, in order: the five characters, then the colourless pool.</summary>
    public static readonly IReadOnlyList<string> Pools = [Ironclad, Silent, Defect, Necrobinder, Regent, DataPack.Colorless];

    private static readonly Tier[] TierOrder = [Tier.S, Tier.A, Tier.B, Tier.C, Tier.D];
    private static readonly string[] TypeOrder = [CardTypes.Attack, CardTypes.Skill, CardTypes.Power];
    private static readonly string[] RarityOrder = ["basic", "common", "uncommon", "rare", "ancient"];

    /// <summary>Key of a card whose type or rarity is unknown.</summary>
    public const string OtherKey = "other";

    public static bool IsPool(string? key) => key is not null && Pools.Contains(key, StringComparer.Ordinal);

    /// <summary>The chip to start with when there is no run: the last one picked (settings.json), else Ironclad.</summary>
    public static string DefaultPool(string? saved) => IsPool(saved) ? saved! : Ironclad;

    /// <summary>Chip label of a pool.</summary>
    public static string PoolName(string key) => key switch
    {
        Ironclad => Loc.T("아이언클래드", "Ironclad"),
        Silent => Loc.T("사일런트", "Silent"),
        Defect => Loc.T("디펙트", "Defect"),
        Necrobinder => Loc.T("네크로바인더", "Necrobinder"),
        Regent => Loc.T("리젠트", "Regent"),
        DataPack.Colorless => Loc.T("무색", "Colorless"),
        _ => key.Replace("CHARACTER.", "", StringComparison.Ordinal),
    };

    /// <summary>
    /// Every card of <paramref name="pool"/>: the pack's tier entries for it, then the catalog's cards of that pool that can be
    /// offered (same rule as <see cref="VersionGuard"/>: not basic/ancient/event/…) and have no tier entry anywhere.
    /// </summary>
    public static IReadOnlyList<TierListEntry> Entries(DataPack pack, string pool)
    {
        var list = new List<TierListEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (pack.Tiers.TryGetValue(pool, out var tiers))
        {
            foreach (var (rawId, rating) in tiers.Cards)
            {
                var id = pack.Canonical(rawId);
                if (seen.Add(id)) list.Add(Entry(pack, pool, id, rating));
            }
        }
        if (pack.Catalog is { } catalog)
        {
            foreach (var card in catalog.CardsOf(pool))
            {
                var id = pack.Canonical(card.Id);
                if (seen.Contains(id) || !VersionGuard.IsOfferedRarity(card.Rarity) || pack.FindTier(pool, id) is not null) continue;
                seen.Add(id);
                list.Add(Entry(pack, pool, id, null));
            }
        }
        return list;
    }

    private static TierListEntry Entry(DataPack pack, string pool, string id, CardTierEntry? rating)
    {
        var card = pack.FindCatalog(id);
        return new TierListEntry(id, rating, pack.CardType(pool, id), pack.CardRarity(pool, id), card?.Cost ?? rating?.Cost,
            card?.CostX == true, card?.MultiplayerOnly == true);
    }

    public static string TypeKey(TierListEntry e) => e.Type ?? OtherKey;

    public static string RarityKey(TierListEntry e) => e.Rarity ?? OtherKey;

    /// <summary>Type keys present in <paramref name="entries"/>: attack, skill, power, then any other in alphabetical order.</summary>
    public static IReadOnlyList<string> TypesOf(IEnumerable<TierListEntry> entries) => Ordered(entries.Select(TypeKey), TypeOrder);

    /// <summary>Rarity keys present: basic, common, uncommon, rare, ancient, then any other.</summary>
    public static IReadOnlyList<string> RaritiesOf(IEnumerable<TierListEntry> entries) => Ordered(entries.Select(RarityKey), RarityOrder);

    private static string[] Ordered(IEnumerable<string> keys, string[] order)
        => keys.Distinct(StringComparer.Ordinal)
            .OrderBy(k => Array.IndexOf(order, k) is var i && i >= 0 ? i : order.Length)
            .ThenBy(k => k == OtherKey ? 1 : 0)
            .ThenBy(k => k, StringComparer.Ordinal)
            .ToArray();

    public static string TypeName(string key) => key switch
    {
        CardTypes.Attack => Loc.T("공격", "Attack"),
        CardTypes.Skill => Loc.T("스킬", "Skill"),
        CardTypes.Power => Loc.T("파워", "Power"),
        CardTypes.Status => Loc.T("상태이상", "Status"),
        CardTypes.Curse => Loc.T("저주", "Curse"),
        CardTypes.Quest => Loc.T("퀘스트", "Quest"),
        _ => Loc.T("기타", "Other"),
    };

    public static string RarityName(string key) => CardInfoProvider.RarityName(key) is var n && n != "?" ? n : Loc.T("기타", "Other");

    /// <summary>Co-op cards: hidden in a solo run (and with no run), shown in a multiplayer run, unless the chip overrides it.</summary>
    public static bool ShowCoop(bool isMultiplayerRun, bool? userChoice) => userChoice ?? isMultiplayerRun;

    public static bool Passes(TierListEntry e, TierListFilter f, Func<string, string> name)
        => (f.ShowCoop || !e.CoopOnly)
           && !f.HiddenTypes.Contains(TypeKey(e))
           && !f.HiddenRarities.Contains(RarityKey(e))
           && (string.IsNullOrWhiteSpace(f.Search) || HangulSearch.Matches(name(e.Id), f.Search));

    /// <summary>
    /// Sections S, A, B, C, D, then the unrated cards ("?"); sections the filter leaves empty are left out. Inside a tier: the
    /// pack's fine adjustment (<see cref="CardTierEntry.BaseAdjust"/>) high to low, then the name; unrated cards by name.
    /// </summary>
    public static IReadOnlyList<TierListGroup> Group(IEnumerable<TierListEntry> entries, TierListFilter filter, Func<string, string> name)
    {
        var shown = entries.Where(e => Passes(e, filter, name)).ToArray();
        var groups = new List<TierListGroup>();
        foreach (var tier in TierOrder)
        {
            var cards = shown.Where(e => e.Tier == tier)
                .OrderByDescending(e => e.BaseAdjust)
                .ThenBy(e => name(e.Id), StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToArray();
            if (cards.Length > 0) groups.Add(new TierListGroup(tier.ToString(), cards));
        }
        var unrated = shown.Where(e => e.Tier is null)
            .OrderBy(e => name(e.Id), StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToArray();
        if (unrated.Length > 0) groups.Add(new TierListGroup("?", unrated));
        return groups;
    }

    /// <summary>Section header next to the tier letter: "12장", or "미평가 · 3장" for the unrated cards.</summary>
    public static string GroupTitle(string tierText, int count)
        => tierText == "?" ? Loc.T($"미평가 · {count}장", $"Unrated · {count} cards") : Loc.T($"{count}장", $"{count} cards");

    /// <summary>"1막 A" when the pack rates the card differently in the current act; null with no run or no difference.</summary>
    public static (string Text, string Tier)? ActMarker(TierListEntry e, int? act)
    {
        if (act is not { } a || e.Rating?.ActTier is not { } acts || !acts.TryGetValue(a, out var t) || t == e.Rating.Tier) return null;
        return (Loc.T($"{a}막 {t}", $"Act {a}: {t}"), t.ToString());
    }

    /// <summary>Tooltip line with every act the pack rates differently: "막별 티어: 1막 A · 3막 C"; null when none.</summary>
    public static string? ActTiersText(TierListEntry e)
    {
        if (e.Rating?.ActTier is not { Count: > 0 } acts) return null;
        var parts = acts.Where(p => p.Value != e.Rating.Tier).OrderBy(p => p.Key)
            .Select(p => Loc.T($"{p.Key}막 {p.Value}", $"Act {p.Key}: {p.Value}")).ToArray();
        return parts.Length == 0 ? null : Loc.T("막별 티어: ", "By act: ") + string.Join(" · ", parts);
    }

    /// <summary>Energy cost on the portrait: "X", the number, or null (unplayable / unknown).</summary>
    public static string? CostText(TierListEntry e)
        => e.CostX ? "X" : e.Cost is { } c && c >= 0 ? c.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>"티어 A · 희귀 · 공격 · 비용 1 · 협동 전용 · 추정".</summary>
    public static string DetailText(TierListEntry e)
    {
        var parts = new List<string>
        {
            e.Tier is { } t ? Loc.T($"티어 {t}", $"Tier {t}") : Loc.T("미평가", "Unrated"),
            RarityName(RarityKey(e)),
            TypeName(TypeKey(e)),
        };
        if (CostText(e) is { } cost) parts.Add(Loc.T($"비용 {cost}", $"Cost {cost}"));
        if (e.CoopOnly) parts.Add(Loc.T("협동 전용", "Co-op only"));
        if (e.IsEstimate) parts.Add(Loc.T("추정 티어", "Estimated tier"));
        return string.Join(" · ", parts);
    }

    /// <summary>Copies of each card in the deck, by canonical id (upgraded copies count too).</summary>
    public static IReadOnlyDictionary<string, int> OwnedCounts(RunState? run, Func<string, string> canonical)
        => run is null
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : run.Deck.GroupBy(c => canonical(c.Id), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    public static bool SameCounts(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
        => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var n) && n == p.Value);

    /// <summary>"87장 중 40장 표시" (all shown: "87장").</summary>
    public static string Summary(int shown, int total)
        => shown == total ? Loc.T($"{total}장", $"{total} cards") : Loc.T($"{total}장 중 {shown}장 표시", $"Showing {shown} of {total}");

    public static string NoMatch => Loc.T("조건에 맞는 카드가 없습니다.", "No card matches the filters.");

    public static string NoData => Loc.T("이 카드 풀의 티어 데이터가 없습니다.", "No tier data for this card pool.");

    public static string CoopChip => Loc.T("협동 카드", "Co-op cards");

    public static string CoopChipToolTip => Loc.T("협동(멀티플레이) 전용 카드 표시/숨기기", "Show/hide cards that exist only in co-op runs");

    /// <summary>The standing line under the selector.</summary>
    public static string Note => Loc.T(
        "티어는 카드 자체의 기본 평가입니다. 보상 화면의 순위는 내 덱과 랭커 선택 경향을 함께 반영해 다를 수 있습니다.",
        "Tiers rate the card on its own. Reward rankings also use your deck and top players' picks, so they can differ.");
}
