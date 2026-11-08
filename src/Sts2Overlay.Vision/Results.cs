namespace Sts2Overlay.Vision;

/// <summary>What the frame shows, as far as the overlay cares.</summary>
public enum ScreenKind
{
    Other,
    /// <summary>NCardRewardSelectionScreen ("카드를 선택하세요" / "Choose a Card").</summary>
    RewardCardSelect,
    /// <summary>The merchant's inventory (rug with 5 character + 2 colourless cards).</summary>
    Shop,
}

public enum CardSlotKind
{
    Reward,
    ShopCharacter,
    ShopColorless,
}

/// <summary>Context that improves accuracy; everything is optional.</summary>
/// <param name="CharacterId">The player's character (e.g. CHARACTER.IRONCLAD from the save). Cards from its
/// pool and the colourless pool win near-ties, and identical-art pairs are resolved by pool.</param>
public sealed record RecognitionHints(string? CharacterId = null);

/// <summary>One card slot after localisation and identification.</summary>
/// <param name="Slot">0-based slot index in the layout (left to right; shop: 0-4 character, 5-6 colourless).</param>
/// <param name="SlotKind">Reward slot, shop character-card slot or shop colourless slot.</param>
/// <param name="CardId">Best card id, or null when nothing card-like was found in the slot.</param>
/// <param name="Upgraded">Title drawn in the upgrade green (#7FFF00).</param>
/// <param name="Score">NCC of the best candidate (1 = identical).</param>
/// <param name="Margin">Score gap to the best different (non-twin) card.</param>
/// <param name="Accepted">Passed the score/margin thresholds; only accepted cards should be shown to the user.</param>
/// <param name="AmbiguousWith">Another card with the same artwork that could not be ruled out.</param>
/// <param name="UpgradeEvidence">Fraction of title pixels that are upgrade-green.</param>
/// <param name="UpgradeKnown">False when the card is partly covered (hover tooltip, merchant hand) and no green
/// was seen: the title may simply be hidden, so "not upgraded" is a guess.</param>
/// <param name="NodeScale">Card node scale found by the search (0.8 normal / 1.0 hovered on the reward screen).</param>
/// <param name="ArtBounds">Portrait rectangle in frame pixels.</param>
/// <param name="CardBounds">Whole card in frame pixels (draw tier badges relative to this).</param>
/// <param name="PriceBounds">Shop only: where the price label is.</param>
/// <param name="Price">Shop only: price in gold when it was read clearly and fits the game's price rules, else null.</param>
/// <param name="Candidates">Top candidates (debugging, bench report).</param>
public sealed record RecognizedCard(
    int Slot,
    CardSlotKind SlotKind,
    string? CardId,
    bool Upgraded,
    float Score,
    float Margin,
    bool Accepted,
    string? AmbiguousWith,
    float UpgradeEvidence,
    bool UpgradeKnown,
    float NodeScale,
    PixelRect ArtBounds,
    PixelRect CardBounds,
    PixelRect? PriceBounds,
    int? Price,
    IReadOnlyList<ArtMatch> Candidates)
{
    /// <summary>
    /// Occlusion-robust second opinion, present when the whole-art match was rejected on a confirmed screen.
    /// When it is accepted, <see cref="CardId"/>/<see cref="Margin"/>/<see cref="Accepted"/> come from it,
    /// while <see cref="Score"/> stays the whole-art NCC (low: part of the card is covered).
    /// </summary>
    public PartialMatch? Partial { get; init; }

    /// <summary>Shop only: the raw price reading (digits, label colour, scores), also when <see cref="Price"/> is null.</summary>
    public PriceReading? PriceReading { get; init; }

    /// <summary>Shop only: the game drew the price in red, i.e. the player cannot afford this card right now.</summary>
    public bool? Unaffordable => PriceReading is { Tint: not PriceTint.Unknown } r ? r.Unaffordable : null;

    public override string ToString() =>
        $"#{Slot} {CardId ?? "-"}{(Upgraded ? "+" : UpgradeKnown ? "" : "?")} s={Score:F3} m={Margin:F3}{(Accepted ? "" : " (rejected)")}{(AmbiguousWith != null ? $" ~{AmbiguousWith}" : "")}{(Partial != null ? $" [partial {Partial}]" : "")}{(PriceReading != null ? $" ${PriceReading}" : "")}";
}

/// <summary>Result of the cheap colour-probe test that runs before any card matching.</summary>
/// <param name="RewardLikely">Reward banner + dimmed backdrop found.</param>
/// <param name="ShopLikely">Merchant rug found.</param>
/// <param name="Detail">Per-probe distances, for logs and the bench.</param>
public sealed record ScreenGate(bool RewardLikely, bool ShopLikely, string Detail);

/// <param name="Kind">Screen class (confirmed by card matching, not just the probes).</param>
/// <param name="Cards">Accepted cards, left to right (what the overlay should annotate).</param>
/// <param name="Slots">Every slot examined, including rejected ones (debugging).</param>
/// <param name="Gate">The probe test that decided which layouts to try.</param>
/// <param name="ElapsedMs">Wall time of <see cref="CardRecognizer.Recognize"/>.</param>
/// <param name="Detail">Short explanation (e.g. which layout hypothesis won).</param>
public sealed record ScreenRecognition(
    ScreenKind Kind,
    IReadOnlyList<RecognizedCard> Cards,
    IReadOnlyList<RecognizedCard> Slots,
    ScreenGate Gate,
    double ElapsedMs,
    string Detail);
