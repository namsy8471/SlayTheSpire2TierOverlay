using System.Text.Json.Serialization;

namespace Sts2Overlay.Core.Data;

/// <summary><c>data/packs/&lt;version&gt;/tiers/&lt;character&gt;.json</c> (PLAN §6.2).</summary>
public sealed class TierPack
{
    /// <summary>Character id (<c>CHARACTER.DEFECT</c>) or <c>COLORLESS</c> for shared cards.</summary>
    public string Character { get; set; } = "";

    public string? DataVersion { get; set; }

    public Dictionary<string, CardTierEntry> Cards { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Curated knowledge about one card. Everything except <see cref="Tier"/> is optional.</summary>
public sealed class CardTierEntry
{
    public Tier Tier { get; set; } = Tier.C;

    /// <summary>Per-act override, keyed by act number ("1".."3").</summary>
    public Dictionary<int, Tier>? ActTier { get; set; }

    /// <summary>Points added to the base value when the card is upgraded.</summary>
    public double UpgradedDelta { get; set; }

    /// <summary>Role strengths 0..1, keys from <see cref="Roles"/>.</summary>
    public Dictionary<string, double> Roles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Resource axes this card produces, e.g. <c>STAR</c>, <c>ORB_LIGHTNING</c>, <c>DISCARD_ENGINE</c>.</summary>
    public List<string> Provides { get; set; } = [];

    /// <summary>Resource axes this card spends.</summary>
    public List<string> Consumes { get; set; } = [];

    /// <summary>Axes that must be provided by the deck for this card's synergy to count (any-of).</summary>
    public List<string> Requires { get; set; } = [];

    /// <summary>Multiplier on the duplicate penalty. 0 = copies are fine, 1 = default.</summary>
    public double DupFactor { get; set; } = 1.0;

    /// <summary>0..1: how much the card "replaces itself" (draw/cycle). Reduces dilution.</summary>
    public double Cycle { get; set; }

    public string? Tip { get; set; }

    /// <summary>0..1 confidence in this rating. Low confidence cards cannot beat Skip (PLAN §7.2).</summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>Game version in which the card changed since it was rated, or null.</summary>
    public string? ChangedIn { get; set; }

    public string Source { get; set; } = "curated";

    /// <summary>Optional card facts used when no catalog is loaded. Catalog values win.</summary>
    public string? Type { get; set; }

    public string? Rarity { get; set; }

    public int? Cost { get; set; }

    [JsonIgnore]
    public bool IsEstimate => ChangedIn is not null || Confidence < 0.5;
}
