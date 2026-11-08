using Sts2Overlay.App.Integration;

namespace Sts2Overlay.App.ViewModels;

/// <summary>What one badge says (unit-free). The badge window turns it into a positioned <see cref="BadgeVm"/>.</summary>
/// <param name="CardBounds">Full card bounds in physical screen pixels (from the recogniser).</param>
/// <param name="PriceBounds">Shop: where the price label was read, physical screen pixels (debug outline only).</param>
/// <param name="Chips">Reason chips drawn under the card on the game screen (top 1-2), or null for none.</param>
/// <param name="Ribbon">Gold ribbon text on the badge ("추천!", "구매!"), or null.</param>
public sealed record BadgeSpec(PixelRect CardBounds, string TierText, string ScoreText, bool IsBest, bool IsEstimate, bool IsUpgraded,
    PixelRect? PriceBounds = null, IReadOnlyList<ChipVm>? Chips = null, string? Ribbon = null);

/// <summary>The "스킵 추천!" pill: the skip score and its top reason (e.g. "덱 크기 +12"), drawn under the card row.</summary>
public sealed record SkipSpec(string ScoreText, string? Reason);

/// <summary>The marker on the shop's card-removal service.</summary>
/// <param name="Coin">The removal coin, physical screen pixels (from the recogniser's probe).</param>
/// <param name="Text">What to say, e.g. "제거 추천: 타격".</param>
public sealed record RemovalSpec(PixelRect Coin, string Text);

/// <summary>A badge placed on the badge layer, in DIPs relative to the layer's top-left.</summary>
/// <param name="Scale">Badges are designed at <see cref="Views.BadgeLayout.BaseWidth"/> x <see cref="Views.BadgeLayout.BaseHeight"/> DIPs and scaled with the card size.</param>
/// <param name="HasRibbon">A ribbon is pinned on this badge: it replaces the small crown + "추천" at the badge's right end.</param>
public sealed record BadgeVm(double Left, double Top, double Scale, string TierText, string ScoreText, bool IsBest, bool IsEstimate, bool IsUpgraded,
    bool HasRibbon = false)
{
    /// <summary>The small crown + "추천" inside the badge: only for the best card when no ribbon says it louder.</summary>
    public bool ShowCrown => IsBest && !HasRibbon;
}

/// <summary>The gold "추천!" / "구매!" ribbon (design <see cref="Views.BadgeLayout.RibbonWidth"/> x <see cref="Views.BadgeLayout.RibbonHeight"/>).</summary>
public sealed record RibbonVm(double Left, double Top, double Scale, string Text);

/// <summary>Reason chips under one card. <paramref name="Width"/> is in layer DIPs (the chips are centred in it and
/// trimmed to it); the chip design itself is scaled by <paramref name="Scale"/>.</summary>
public sealed record ChipStackVm(double Left, double Top, double Width, double Scale, IReadOnlyList<ChipVm> Chips)
{
    /// <summary>The stack width before scaling: the panel is scaled by <see cref="Scale"/> (a LayoutTransform), so it
    /// must be this wide in design units to end up <see cref="Width"/> DIPs wide.</summary>
    public double DesignWidth => Width / Scale;
}

/// <summary>The "스킵 추천!" pill, placed.</summary>
public sealed record SkipVm(double Left, double Top, double Scale, string ScoreText, string? Reason);

/// <summary>The card-removal marker, placed.</summary>
public sealed record MarkerVm(double Left, double Top, double Scale, string Text);

/// <summary>Debug outline of a recognised card or of its price label (demo mode / debug outline mode), DIPs.</summary>
public sealed record BoundsVm(double Left, double Top, double Width, double Height, bool IsPrice);

public sealed class BadgeLayerViewModel : ObservableObject
{
    private IReadOnlyList<BadgeVm> _badges = [];
    private IReadOnlyList<BoundsVm> _bounds = [];
    private IReadOnlyList<RibbonVm> _ribbons = [];
    private IReadOnlyList<ChipStackVm> _chipStacks = [];
    private SkipVm? _skip;
    private MarkerVm? _removal;

    public IReadOnlyList<BadgeVm> Badges
    {
        get => _badges;
        set => Set(ref _badges, value);
    }

    public IReadOnlyList<BoundsVm> Bounds
    {
        get => _bounds;
        set => Set(ref _bounds, value);
    }

    /// <summary>"추천!" / "구매!" ribbons pinned on the badges of recommended cards.</summary>
    public IReadOnlyList<RibbonVm> Ribbons
    {
        get => _ribbons;
        set => Set(ref _ribbons, value);
    }

    /// <summary>Reason chips under each card (reward screen).</summary>
    public IReadOnlyList<ChipStackVm> ChipStacks
    {
        get => _chipStacks;
        set => Set(ref _chipStacks, value);
    }

    /// <summary>"스킵 추천!" pill under the cards, or null.</summary>
    public SkipVm? Skip
    {
        get => _skip;
        set => Set(ref _skip, value);
    }

    /// <summary>"제거 추천: ..." marker on the shop's card-removal service, or null.</summary>
    public MarkerVm? Removal
    {
        get => _removal;
        set => Set(ref _removal, value);
    }
}
