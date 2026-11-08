using System.Globalization;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// What the ON-GAME marks say (the badge layer), decided from an evaluation. Kept apart from AppController so the
/// wording rules are pure and unit-tested, and so the controller only wires them in.
///
/// <para>The player looks at the game, not at the side panel, so the marks repeat just the essentials: the pick
/// ("추천!" / "구매!"), the one or two strongest reasons per card, why skipping wins, and which card to remove.</para>
/// </summary>
public static class OfferMarkers
{
    public const string RecommendRibbon = "추천!";
    public const string BuyRibbon = "구매!";

    /// <summary>At most this many chips under a card: more would be a wall of text over the game.</summary>
    public const int MaxChips = 2;

    /// <summary>"광역 부족 +24" (sign always shown, rounded to whole points like the badge score).</summary>
    public static string ChipText(string label, double value)
        => $"{label} {value.ToString("+0;-0;0", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// A card's chips for the game layer: the evaluation already lists its reasons strongest first, keep the top
    /// <see cref="MaxChips"/>. Reasons that round to 0 are dropped (a chip saying "+0" explains nothing).
    /// </summary>
    public static IReadOnlyList<ChipVm> CardChips(IEnumerable<ReasonChip> chips)
        => chips.Where(c => Math.Round(c.Value) != 0)
            .Take(MaxChips)
            .Select(c => new ChipVm(ChipText(c.Label, c.Value), c.Value >= 0))
            .ToArray();

    /// <summary>
    /// The skip pill when skipping is recommended, else null. Its reason is the strongest component other than the
    /// act's base skip value: "덱 크기 +12" says WHY this skip is good, the base is there for every skip.
    /// </summary>
    public static SkipSpec? Skip(OfferEvaluation eval, Func<double, string> formatScore)
    {
        if (!eval.SkipRecommended) return null;
        var reasons = eval.Skip.TopReasons(3);
        var top = reasons.FirstOrDefault(r => r.Key != ComponentKeys.SkipBase) ?? reasons.FirstOrDefault();
        return new SkipSpec(formatScore(eval.Skip.Total), top is null ? null : ChipText(top.Label, top.Value));
    }

    /// <summary>
    /// "제거 추천: 타격" when the recommended purchase includes the card-removal service, else null.
    /// <paramref name="cardName"/> turns the target's card id into its Korean name.
    /// </summary>
    public static string? RemovalText(ShopEvaluation eval, Func<string, string> cardName)
    {
        var removal = eval.RecommendedPurchase.FirstOrDefault(i => i.Kind == ShopItemKind.CardRemoval);
        if (removal is null) return null;
        return removal.RemovalTarget is { } id ? $"제거 추천: {cardName(id)}" : "제거 추천";
    }
}
