namespace Sts2Overlay.Core.Data;

/// <summary>
/// <c>relics.json</c> (optional): how often top players took a relic when it was offered, in shops (bought / on display)
/// and at ancients (picked / offered). Relics are otherwise valued by rarity only (<see cref="Events.EventWeights.RelicTier"/>);
/// <see cref="ValueMultiplier"/> scales that value for relics whose rate is stable (<see cref="RelicStat.Use"/>).
/// Reward relics (elite, treasure, boss) are taken 99.9% of the time, so they carry no choice signal and have no entry.
/// </summary>
public sealed class RelicStats
{
    public const string ShopSignal = "shop";
    public const string AncientSignalPrefix = "ancient:";

    /// <summary>The multiplier never leaves this range, so one measured rate cannot dominate the score.</summary>
    public const double MinMultiplier = 0.5, MaxMultiplier = 1.5;

    public int Version { get; set; } = 1;

    public string? Source { get; set; }

    /// <summary>Median shop buy rate: shop purchases are gold-limited, so a shop rate is read relative to this, never as is.</summary>
    public double ShopMedianPickRate { get; set; }

    /// <summary>Pick rate of an ancient option chosen at random (1/3).</summary>
    public double AncientNeutralPickRate { get; set; }

    /// <summary><c>RELIC.X</c> → its measured rate.</summary>
    public Dictionary<string, RelicStat> Relics { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>clamp(pickRate / neutral, 0.5, 1.5)</c> for a relic with <c>use: true</c>, where neutral is
    /// <see cref="ShopMedianPickRate"/> for <c>signal: "shop"</c> and <see cref="AncientNeutralPickRate"/> otherwise.
    /// 1 when the relic has no entry, <c>use</c> is not true, or the neutral rate is not positive.
    /// </summary>
    public double ValueMultiplier(string? relicId)
    {
        if (relicId is null || !Relics.TryGetValue(relicId, out var r) || r.Use != true) return 1;
        var neutral = string.Equals(r.Signal, ShopSignal, StringComparison.Ordinal) ? ShopMedianPickRate : AncientNeutralPickRate;
        if (!(neutral > 0) || !double.IsFinite(r.PickRate)) return 1;
        return Math.Clamp(r.PickRate / neutral, MinMultiplier, MaxMultiplier);
    }
}

/// <summary>One relic's measured rate. Fields other than these (n, eff_n, bootstrap shares, presence lift) are evidence only.</summary>
public sealed class RelicStat
{
    /// <summary><c>shop</c> or <c>ancient:&lt;ANCIENT&gt;</c>.</summary>
    public string? Signal { get; set; }

    /// <summary>The runtime gate: true = the rate is stable enough to use; false = keep the rarity value. Required.</summary>
    public bool? Use { get; set; }

    public double PickRate { get; set; }

    /// <summary>Display-only tier band (S..D), present only when the band itself is stable.</summary>
    public string? Tier { get; set; }

    public string? Rarity { get; set; }
}
