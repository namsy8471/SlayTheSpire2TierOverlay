using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Events;

/// <summary>
/// The deck as the event scorer sees it: curses, basic Strikes, basic Defends and everything else.
/// Chosen removals/transforms consume it (best target first), so "remove 2 cards" with one curse is
/// curse + Strike, not curse twice.
/// </summary>
internal sealed class DeckPool
{
    private readonly DataPack _pack;
    private readonly RunState _state;
    private int _curses, _strikes, _defends, _others;

    public DeckPool(DataPack pack, RunState state, DeckProfile profile)
    {
        _pack = pack;
        _state = state;
        foreach (var c in state.Deck)
        {
            // Eternal cards (Ascender's Bane, Greed, …) cannot be removed or transformed: no target at all.
            if (pack.IsEternal(c.Id)) continue;
            if (DeckAnalyzer.IsCurse(pack, state.Character, c.Id)) _curses++;
            else if (CardIdHeuristics.IsBasicStrike(c.Id)) _strikes++;
            else if (CardIdHeuristics.IsBasicDefend(c.Id)) _defends++;
            else _others++;
        }
        _ = profile;
    }

    /// <summary>Short note (current language) of the targets the last <see cref="Remove"/>/<see cref="Transform"/> took.</summary>
    public string? LastDetail { get; private set; }

    private int Total => _curses + _strikes + _defends + _others;

    public double Remove(int count, bool choose, string? filter, EventWeights w)
    {
        var shop = _pack.Weights.Shop;
        return Take(count, choose, filter, shop.CurseRemovalValue, shop.BasicRemovalValue,
            choose ? w.RemoveOtherChosen : w.RemoveOtherRandom);
    }

    public double Transform(int count, bool choose, string? filter, EventWeights w)
        => Take(count, choose, filter, w.TransformCurse, w.TransformBasic, w.TransformOther);

    /// <summary>
    /// Cards the game can still upgrade (<see cref="UpgradeAdvisor.CanUpgrade"/>: no curses, statuses, quest cards or
    /// cards at their catalog maxUpgrade), narrowed by <paramref name="filter"/>.
    /// </summary>
    public int Upgradable(string? filter)
    {
        var f = Filter(filter);
        return _state.Deck.Count(c => UpgradeAdvisor.CanUpgrade(_pack, _state.Character, c) && Matches(c.Id, f));
    }

    private double Take(int count, bool choose, string? filter, double curseValue, double basicValue, double otherValue)
    {
        var f = Filter(filter);
        int curses = f.Curse ? _curses : 0, strikes = f.Strike ? _strikes : 0, defends = f.Defend ? _defends : 0, others = f.Other ? _others : 0;
        var total = 0.0;
        if (choose)
        {
            int tc = 0, tb = 0, to = 0;
            for (var k = 0; k < count; k++)
            {
                if (curses > 0) { curses--; _curses--; tc++; total += curseValue; }
                else if (strikes > 0) { strikes--; _strikes--; tb++; total += basicValue; }
                else if (defends > 0) { defends--; _defends--; tb++; total += basicValue; }
                else if (others > 0) { others--; _others--; to++; total += otherValue; }
            }
            LastDetail = string.Join(", ", new[]
            {
                tc > 0 ? Loc.T($"저주 {tc}", $"Curse {tc}") : null,
                tb > 0 ? Loc.T($"기본 카드 {tb}", $"Basic {tb}") : null,
                to > 0 ? Loc.T($"기타 {to}", $"Other {to}") : null,
            }.Where(s => s is not null));
            return total;
        }

        // Random: expected value over the matching cards; the pool is left as is (the target is unknown).
        var n = curses + strikes + defends + others;
        if (n == 0) { LastDetail = null; return 0; }
        var each = (curses * curseValue + (strikes + defends) * basicValue + others * otherValue) / n;
        var share = (curses + strikes + defends) * 100 / Math.Max(1, n);
        LastDetail = Loc.T($"기본·저주 비율 {share}%", $"Basic/Curse share {share}%");
        return each * Math.Min(count, n);
    }

    private readonly record struct Kinds(bool Curse, bool Strike, bool Defend, bool Other, string? Type);

    private static Kinds Filter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return new(true, true, true, true, null);
        var f = filter.ToLowerInvariant();
        // Checked before "basic"/"strike": these texts mention those words in a different sense.
        if (f.Contains("upgradable")) return new(false, true, true, true, null);   // PAEL_TOOTH: curses cannot be upgraded
        if (f.Contains("non-basic")) return new(true, false, false, true, null);   // SLIPPERY_BRIDGE: anything but Strike/Defend
        if (f.Contains("starter")) return new(false, false, false, true, null);    // ARCHAIC_TOOTH: Bash/Neutralize/… only
        if (f.Contains("strike") && f.Contains("defend")) return new(false, true, true, false, null);
        if (f.Contains("strike")) return new(false, true, false, false, null);
        if (f.Contains("defend")) return new(false, false, true, false, null);
        if (f.Contains("basic")) return new(false, true, true, false, null);
        if (f.Contains("curse")) return new(true, false, false, false, null);
        if (f.Contains("attack")) return new(false, true, false, true, CardTypes.Attack);
        if (f.Contains("skill")) return new(false, false, true, true, CardTypes.Skill);
        if (f.Contains("power")) return new(false, false, false, true, CardTypes.Power);
        return new(true, true, true, true, null);
    }

    private bool Matches(string id, Kinds f)
    {
        if (CardIdHeuristics.IsBasicStrike(id)) return f.Strike;
        if (CardIdHeuristics.IsBasicDefend(id)) return f.Defend;
        if (!f.Other) return false;
        return f.Type is null || _pack.CardType(_state.Character, id) == f.Type;
    }
}
