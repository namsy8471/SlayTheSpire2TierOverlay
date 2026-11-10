using Sts2Overlay.Core.Model;

namespace Sts2Overlay.App.Integration;

/// <summary>Which screen a frame shows.</summary>
public enum ScreenKind
{
    Reward,
    Shop,
    Other,
    /// <summary>"Pick 1 of N" row opened by a relic, potion or card (same banner as the reward screen).</summary>
    Choice,
    /// <summary>A card grid: an event offer or the player's own cards (see <see cref="CardGridFilter"/>).</summary>
    Grid,
}

/// <summary>One card found on screen.</summary>
/// <param name="Card">Card id and upgrade level (upgrade from the green title colour).</param>
/// <param name="ScreenBounds">The FULL card (frame, not just the art), in physical SCREEN pixels (frame offset already added).</param>
/// <param name="Confidence">0..1 match confidence (e.g. NCC score).</param>
/// <param name="Price">Gold price under the card on the shop screen; null on the reward screen or when the label was unreadable.</param>
/// <param name="Unaffordable">Shop: true when the price is drawn red (the game says not enough gold), false for cream/green, null when unknown.</param>
/// <param name="OnSale">Shop: the green half-price label.</param>
/// <param name="PriceBounds">Shop: where the price label is, physical SCREEN pixels (debug outline view).</param>
public sealed record RecognizedCard(CardRef Card, PixelRect ScreenBounds, double Confidence, int? Price = null, bool? Unaffordable = null, bool OnSale = false,
    PixelRect? PriceBounds = null);

/// <summary>One relic for sale on the merchant's rug (identified from its icon; bought or covered slots are left out).</summary>
/// <param name="RelicId"><c>RELIC.X</c>.</param>
/// <param name="Rarity">Catalog rarity (Common / Uncommon / Rare / Shop).</param>
/// <param name="ScreenBounds">The icon square, physical SCREEN pixels.</param>
/// <param name="Confidence">0..1 icon match (masked NCC).</param>
/// <param name="Price">Gold price when the label was read clearly, else null.</param>
/// <param name="Unaffordable">True when the price is drawn red, false when cream, null when unknown.</param>
public sealed record RecognizedShopRelic(string RelicId, string? Rarity, PixelRect ScreenBounds, double Confidence, int? Price = null, bool? Unaffordable = null);

/// <param name="ScreenKind">Reward / Shop / Choice / Grid / Other. Other clears the badges.</param>
/// <param name="Cards">Cards left to right (shop: row by row). Empty for Other.</param>
public sealed record RecognitionResult(ScreenKind ScreenKind, IReadOnlyList<RecognizedCard> Cards)
{
    /// <summary>Shop: the relics on the merchant's rug still for sale, left to right. Empty elsewhere.</summary>
    public IReadOnlyList<RecognizedShopRelic> Relics { get; init; } = [];

    /// <summary>Shop: the card-removal service coin (physical SCREEN pixels) when it is visible and still for sale, else null.</summary>
    public PixelRect? RemovalService { get; init; }

    /// <summary>
    /// Rest site: the campfire option buttons (design px) and whether a deck card grid is open (the Smith screen at a
    /// rest site). Null when the recogniser doesn't look (demo, no art) or nothing rest-related is on screen.
    /// </summary>
    public Sts2Overlay.Vision.Rest.RestSiteResult? RestSite { get; init; }

    /// <summary>
    /// Card slots of the screen's layout, read or not (a 3-card reward row: 3 even while only 2 cards are read);
    /// 0 when unknown. Fewer <see cref="Cards"/> than this means some card is not read yet.
    /// </summary>
    public int Slots { get; init; }

    /// <summary>Identifies the open screen: changes when the recogniser's tracker starts a new screen.</summary>
    public int ScreenId { get; init; }

    /// <summary>
    /// The recogniser waits for the next frame to decide (the latest frame did not show this screen and it is held for
    /// a frame or two, or a shop slot looked empty once): the next frame must be recognised even if it looks unchanged.
    /// </summary>
    public bool Unsettled { get; init; }

    /// <summary>Set by the capture pipeline: how many recogniser resets happened before this result was computed.</summary>
    public int Epoch { get; init; }

    public static RecognitionResult Other { get; } = new(ScreenKind.Other, []);
}

/// <summary>
/// Screen recognition seam. Implemented by an adapter over <c>Sts2Overlay.Vision</c> (lead wires it in
/// <see cref="IntegrationFactory"/>); <see cref="Fakes.FakeCardRecognizer"/> drives demo mode.
/// </summary>
public interface ICardRecognizer
{
    /// <summary>
    /// Classifies the frame and identifies the offered cards. Called on the capture worker thread (never the UI thread),
    /// one call at a time. Return null for "can't tell" (the App keeps what it shows now).
    /// The frame's pixel buffer is reused for the next capture: copy anything you need to keep.
    /// </summary>
    /// <param name="frame">BGRA pixels of the game client area.</param>
    /// <param name="characterHint">Character id from the save (e.g. <c>CHARACTER.DEFECT</c>) to narrow the card pool, or null.</param>
    RecognitionResult? Recognize(CapturedFrame frame, string? characterHint);

    /// <summary>Forget the screen merged over recent frames (the log says it closed). Called on the capture worker thread.</summary>
    void Reset()
    {
    }
}
