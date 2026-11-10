using System.Windows.Input;
using System.Windows.Media;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Text;

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

    /// <summary>Relics this row is about (a shop relic, an ancient's relic, an event option's relic): icon + hover effect text.</summary>
    public IReadOnlyList<RelicTipVm> Relics { get; init; } = [];

    public bool HasRelics => Relics.Count > 0;

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

    /// <summary>Heading of a second, dimmed block under the footer (an ancient's options not offered this time); null = no block.</summary>
    public string? ExtraTitle { get; init; }

    /// <summary>Rows of that block, ranked among themselves.</summary>
    public IReadOnlyList<OfferRowVm> ExtraRows { get; init; } = [];
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

/// <summary>One card tile of an archetype in the 빌드 tab (portrait + name; dimmed while not owned).</summary>
/// <param name="RoleText">핵심 / 완성 / 조력 / 보조.</param>
/// <param name="RarityText">일반 / 고급 / 희귀 …, null when unknown.</param>
/// <param name="TierText">"S".."D", or "?" when unrated.</param>
/// <param name="Art">Card portrait; null when the art isn't extracted (the tile shows a placeholder).</param>
/// <param name="Text">Effect text (cost, description, upgraded description) from the game's localization; null when unknown.</param>
public sealed record BuildCardVm(string Name, string RoleText, string? RarityText, string TierText, bool Owned, CardArtVm? Art, CardText? Text = null)
{
    /// <summary>"핵심 · 희귀 · 티어 A" line of the hover tooltip.</summary>
    public string DetailText => string.Join(" · ", new[] { RoleText, RarityText, Loc.T($"티어 {TierText}", $"Tier {TierText}") }.Where(s => !string.IsNullOrEmpty(s)));

    public string OwnedText => Owned ? Loc.T("보유 중", "Owned") : Loc.T("아직 없음", "Not yet");
}

/// <summary>A relic that suits an archetype in the 빌드 tab (icon tile; dimmed while not owned).</summary>
/// <param name="ReasonText">Why it is listed: the build's key relic, or how much more often top players take it in this build.</param>
public sealed record BuildRelicVm(RelicTipVm Relic, bool Owned, string ReasonText)
{
    public string OwnedText => Owned ? Loc.T("보유 중", "Owned") : Loc.T("아직 없음", "Not yet");
}

/// <summary>Cards of one archetype role, e.g. "핵심 2/3".</summary>
public sealed record BuildCardGroupVm(string Title, IReadOnlyList<BuildCardVm> Cards);

public sealed record ArchetypeVm
{
    /// <summary>Archetype id from the pack; keys the expanded/collapsed state across rebuilds.</summary>
    public string Id { get; init; } = "";

    public required string Name { get; init; }
    public Brush Color { get; init; } = Brushes.SteelBlue;
    public bool IsPrimary { get; init; }
    public string CommitText { get; init; } = "";
    public double CompletionBarWidth { get; init; }
    public string StageText { get; init; } = "";
    public string? ProbabilityText { get; init; }
    public string? MissingText { get; init; }
    public string? NeedsText { get; init; }

    /// <summary>The archetype's cards by role (핵심 → 완성 → 조력 → 보조), empty roles left out.</summary>
    public IReadOnlyList<BuildCardGroupVm> CardGroups { get; init; } = [];

    /// <summary>Relics that suit the build (key relics, then the ones top players favour in it); shown with the card tiles.</summary>
    public IReadOnlyList<BuildRelicVm> Relics { get; init; } = [];

    public bool HasRelics => Relics.Count > 0;

    /// <summary>Card tiles shown; the controller remembers the user's choice per <see cref="Id"/>.</summary>
    public bool IsExpanded { get; init; }

    /// <summary>"카드 9/15 보유".</summary>
    public string CardsSummaryText { get; init; } = "";

    /// <summary>Header click: show/hide the card tiles.</summary>
    public ICommand? ToggleCardsCommand { get; init; }
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

    /// <summary>Empty, because the run is a co-op log-only run: a guest has no save file, so never a map.</summary>
    public static PathSectionVm CoopGuestEmpty { get; } = new() { HasContent = false, NoMapAsGuest = true };

    public bool HasContent { get; init; } = true;

    /// <summary>No path advice because a co-op guest never gets the map (the empty page says so instead of "not ready").</summary>
    public bool NoMapAsGuest { get; init; }

    /// <summary>Empty page "not ready yet" (<see cref="UiText.PathEmpty"/>).</summary>
    public bool ShowNotReady => !HasContent && !NoMapAsGuest;

    /// <summary>Empty page "co-op guests get no map" (<see cref="UiText.PathEmptyCoopGuest"/>).</summary>
    public bool ShowGuestNoMap => !HasContent && NoMapAsGuest;
    public string? NextStep { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<PathOptionVm> Options { get; init; } = [];
}
