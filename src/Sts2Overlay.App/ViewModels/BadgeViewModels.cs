using Sts2Overlay.App.Integration;

namespace Sts2Overlay.App.ViewModels;

/// <summary>What one badge says (unit-free). The badge window turns it into a positioned <see cref="BadgeVm"/>.</summary>
/// <param name="CardBounds">Full card bounds in physical screen pixels (from the recogniser).</param>
/// <param name="PriceBounds">Shop: where the price label was read, physical screen pixels (debug outline only).</param>
public sealed record BadgeSpec(PixelRect CardBounds, string TierText, string ScoreText, bool IsBest, bool IsEstimate, bool IsUpgraded,
    PixelRect? PriceBounds = null);

/// <summary>A badge placed on the badge layer, in DIPs relative to the layer's top-left.</summary>
/// <param name="Scale">Badges are designed at <see cref="Views.BadgeLayout.BaseWidth"/> x <see cref="Views.BadgeLayout.BaseHeight"/> DIPs and scaled with the card size.</param>
public sealed record BadgeVm(double Left, double Top, double Scale, string TierText, string ScoreText, bool IsBest, bool IsEstimate, bool IsUpgraded);

/// <summary>Debug outline of a recognised card or of its price label (demo mode / debug outline mode), DIPs.</summary>
public sealed record BoundsVm(double Left, double Top, double Width, double Height, bool IsPrice);

public sealed class BadgeLayerViewModel : ObservableObject
{
    private IReadOnlyList<BadgeVm> _badges = [];
    private IReadOnlyList<BoundsVm> _bounds = [];
    private BadgeVm? _skip;

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

    /// <summary>"스킵 추천" pill under the cards, or null.</summary>
    public BadgeVm? Skip
    {
        get => _skip;
        set => Set(ref _skip, value);
    }
}
