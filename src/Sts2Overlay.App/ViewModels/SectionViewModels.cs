using System.Windows.Media;
using Sts2Overlay.App.Services;

namespace Sts2Overlay.App.ViewModels;

// Section view models are immutable records: the controller builds a new one per change and swaps it in,
// which keeps the code simple (no ObservableCollection bookkeeping) and is cheap at these sizes.

/// <summary>A signed reason chip, e.g. "광역 부족 +24".</summary>
public sealed record ChipVm(string Text, bool Positive);

/// <summary>One row of the 추천 tab: an offered card, Skip, or the shop's card removal.</summary>
public sealed record OfferRowVm
{
    public required string Name { get; init; }

    /// <summary>"S".."D", or "—" for Skip/removal.</summary>
    public string TierText { get; init; } = "—";

    public string ScoreText { get; init; } = "";

    /// <summary>Score bar width in DIPs (0..110).</summary>
    public double BarWidth { get; init; }

    public bool IsBest { get; init; }
    public bool IsEstimate { get; init; }
    public bool IsUpgraded { get; init; }
    public bool IsSkip { get; init; }
    public bool Unaffordable { get; init; }

    /// <summary>"1위 · 신뢰도 50%" style sub line.</summary>
    public string SubText { get; init; } = "";

    public string? PriceText { get; init; }
    public string? Tip { get; init; }

    /// <summary>Card portrait (thumbnail + hover image); null for Skip/removal or when the art isn't extracted.</summary>
    public CardArtVm? Art { get; init; }
    public IReadOnlyList<ChipVm> Chips { get; init; } = [];
}

public sealed record OfferSectionVm
{
    public static OfferSectionVm Empty { get; } = new() { HasContent = false };

    public bool HasContent { get; init; } = true;

    /// <summary>The screen closed; the panel still shows the last offer, dimmed.</summary>
    public bool IsStale { get; init; }

    public string Title { get; init; } = "";

    /// <summary>확신 / 보통 / 박빙 / 추정.</summary>
    public string ConfidenceText { get; init; } = "";

    /// <summary>Sure | Normal | Close | Estimate (styles the confidence pill).</summary>
    public string ConfidenceKind { get; init; } = "Normal";

    public string? FlipHint { get; init; }
    public IReadOnlyList<OfferRowVm> Rows { get; init; } = [];
    public string? Footer { get; init; }
}

/// <param name="Doubt">Why the log-only rebuild is unsure of this card (shown as a "?" with this tooltip); null = certain.</param>
/// <param name="Art">Card portrait (thumbnail + hover image); null when the art isn't extracted.</param>
public sealed record DeckCardVm(string TierText, string Name, string CountText, string? UpgradeText, string? Doubt = null, CardArtVm? Art = null);

public sealed record DeckGroupVm(string Title, IReadOnlyList<DeckCardVm> Cards);

public sealed record RoleBarVm(string Label, string ValueText, double BarWidth, bool IsWeak);

public sealed record DeckSectionVm
{
    public static DeckSectionVm Empty { get; } = new() { HasContent = false };

    public bool HasContent { get; init; } = true;
    public string Summary { get; init; } = "";
    public IReadOnlyList<DeckGroupVm> Groups { get; init; } = [];
    public IReadOnlyList<RoleBarVm> Roles { get; init; } = [];
}

public sealed record ArchetypeVm
{
    public required string Name { get; init; }
    public Brush Color { get; init; } = Brushes.SteelBlue;
    public bool IsPrimary { get; init; }
    public string CommitText { get; init; } = "";
    public double CompletionBarWidth { get; init; }
    public string StageText { get; init; } = "";
    public string? ProbabilityText { get; init; }
    public string? MissingText { get; init; }
    public string? NeedsText { get; init; }
}

public sealed record BuildSectionVm
{
    public static BuildSectionVm Empty { get; } = new() { HasContent = false };

    public bool HasContent { get; init; } = true;
    public string Summary { get; init; } = "";
    public IReadOnlyList<ArchetypeVm> Archetypes { get; init; } = [];
}

public sealed record PathOptionVm(string Label, string Summary, IReadOnlyList<string> Chips, string? ScoreText, bool IsRecommended);

public sealed record PathSectionVm
{
    public static PathSectionVm Empty { get; } = new() { HasContent = false };

    public bool HasContent { get; init; } = true;
    public string? NextStep { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<PathOptionVm> Options { get; init; } = [];
}
