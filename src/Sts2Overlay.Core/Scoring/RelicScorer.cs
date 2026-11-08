using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>
/// A relic's value for this deck, used wherever a relic is offered (shop, ancient, event):
/// <list type="number">
/// <item><b>base</b> = rarity value × the relic's measured take rate multiplier (<see cref="Events.EventWeights.RelicValue"/>,
/// <c>relics.json</c>); the same number every deck gets.</item>
/// <item><b>fit</b> = base × (m − 1), where m is how much more (or less) top players took the relic in the builds this deck
/// leans toward: for each build with commit c ≥ <see cref="RelicFitWeights.MinCommit"/>,
/// r = (measured lift: <c>relic-fit.json</c> fitRatio, core-held / no-core adjusted when measured)^<see cref="RelicFitWeights.FitExponent"/>,
/// else r = 1 + <see cref="RelicFitWeights.KeyRelicBonus"/> × w for the build's ranker-chosen key relics, else r = 1.
/// m = clamp(1 + Σ c·(r − 1) / max(1, Σ c), min, max).</item>
/// </list>
/// Without a leaning build or any data for the relic, fit is 0 and the value is exactly the old rarity value.
/// </summary>
public sealed class RelicScorer(DataPack pack, Events.EventWeights? eventWeights = null)
{
    public DataPack Pack { get; } = pack;

    /// <summary>Rarity values come from these (default: the pack's <c>weights.json</c> "events").</summary>
    public Events.EventWeights EventWeights { get; } = eventWeights ?? pack.Weights.Events;

    private RelicFitWeights W => Pack.Weights.RelicFit;

    /// <param name="profile">The deck's builds and their commits (<see cref="DeckAnalyzer"/>).</param>
    /// <param name="relicId"><c>RELIC.X</c>; null = an unknown/random relic (base only).</param>
    /// <param name="rarity">common/uncommon/rare/shop/ancient/...; null = the default relic value.</param>
    /// <param name="relicStats">Measured take rates; null when the caller already blends its own rate (an ancient option).</param>
    public RelicEvaluation Score(DeckProfile profile, string? relicId, string? rarity, RelicStats? relicStats)
    {
        var baseValue = EventWeights.RelicValue(rarity, relicId, relicStats);
        var measured = relicStats?.ValueMultiplier(relicId) ?? 1.0;
        var baseDetail = measured == 1.0 ? rarity : $"{rarity} · 랭커 선택률 ×{measured:0.00}";
        var parts = new List<ScoreComponent> { new(ComponentKeys.RelicBase, ScoringLabels.RelicBase, baseValue, baseDetail) };

        var fit = relicId is null ? null : Fit(profile, Pack.Canonical(relicId));
        if (fit is { } f && f.Multiplier != 1.0)
        {
            parts.Add(new ScoreComponent(ComponentKeys.RelicFit,
                f.Multiplier > 1 ? ScoringLabels.RelicFit : ScoringLabels.RelicMisfit,
                baseValue * (f.Multiplier - 1), f.Detail));
        }
        return new RelicEvaluation(relicId, new ScoreBreakdown(parts), fit?.Multiplier ?? 1.0, fit?.Archetype);
    }

    /// <summary>The build-fit multiplier and the build that drives it most; null when no leaning build has data for the relic.</summary>
    public FitResult? Fit(DeckProfile profile, string relicId)
    {
        double commitSum = 0, weighted = 0;
        (double Pull, Archetype Archetype, string Detail)? strongest = null;
        foreach (var s in profile.Archetypes)
        {
            if (s.Commit < W.MinCommit) continue;
            commitSum += s.Commit;
            if (Ratio(s, relicId) is not { } r) continue;
            var pull = s.Commit * (r.Ratio - 1);
            weighted += pull;
            if (strongest is null || Math.Abs(pull) > Math.Abs(strongest.Value.Pull)) strongest = (pull, s.Archetype, r.Detail);
        }
        if (strongest is null) return null;
        var m = Math.Clamp(1 + weighted / Math.Max(1.0, commitSum), W.MinMultiplier, W.MaxMultiplier);
        return new FitResult(m, strongest.Value.Archetype, $"{strongest.Value.Archetype.DisplayName}: {strongest.Value.Detail}");
    }

    /// <summary>r for one build: measured rate ratio, else the key-relic weight, else null (no evidence).</summary>
    private (double Ratio, string Detail)? Ratio(Archetypes.ArchetypeStatus s, string relicId)
    {
        if (Pack.RelicFit?.Get(s.Archetype.Id, relicId) is { Use: true } stat
            && stat.Lift(s.CoreSecured, W.MinSplitDecisions, out var split) is { } lift)
        {
            var ratio = Math.Clamp(Math.Pow(lift, W.FitExponent), W.MinMultiplier, W.MaxMultiplier);
            var situation = !split ? "" : s.CoreSecured ? " (코어 보유 시)" : " (코어 없을 때)";
            var rate = split ? (s.CoreSecured ? stat.PickRateCoreHeld : stat.PickRateNoCore) ?? stat.PickRateIn : stat.PickRateIn;
            return (ratio, $"랭커 선택률 {rate:P0}{situation} vs 다른 빌드 {stat.PickRateOut:P0}");
        }
        foreach (var k in s.Archetype.KeyRelics)
        {
            if (string.Equals(Pack.Canonical(k.Id), relicId, StringComparison.Ordinal))
                return (1 + W.KeyRelicBonus * k.W, "랭커 빌드 핵심 유물");
        }
        return null;
    }

    public readonly record struct FitResult(double Multiplier, Archetype Archetype, string Detail);
}

/// <summary>A relic valued for the current deck: base (rarity × measured rate) + build fit. Components sum to the total.</summary>
public sealed record RelicEvaluation(string? RelicId, ScoreBreakdown Value, double FitMultiplier, Archetype? FitArchetype)
{
    public double Total => Value.Total;
}
