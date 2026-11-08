namespace Sts2Overlay.Core.Data;

/// <summary>Generated locally by the bridge mod's CatalogDumper (PLAN §6.1). Never committed.</summary>
public sealed class CardCatalog
{
    public string? GameVersion { get; set; }
    public string? Commit { get; set; }
    public List<CatalogCard> Cards { get; set; } = [];
}

public sealed class CatalogCard
{
    public string Id { get; set; } = "";

    /// <summary>Card pool, e.g. <c>CHARACTER.DEFECT</c> or <c>COLORLESS</c>.</summary>
    public string? Pool { get; set; }

    public Dictionary<string, string> Names { get; set; } = new(StringComparer.Ordinal);

    /// <summary>attack | skill | power | status | curse | quest</summary>
    public string? Type { get; set; }

    /// <summary>basic | common | uncommon | rare | ancient | special | curse</summary>
    public string? Rarity { get; set; }

    public int? Cost { get; set; }
    public bool CostX { get; set; }
    public List<string> Keywords { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string? Target { get; set; }
    public bool GainsBlock { get; set; }
    public bool MultiplayerOnly { get; set; }
}
