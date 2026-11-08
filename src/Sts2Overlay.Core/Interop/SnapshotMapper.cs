using Sts2Overlay.Contracts;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Interop;

/// <summary>
/// Converts bridge protocol DTOs (<see cref="Sts2Overlay.Contracts"/>) into Core domain models and
/// builds <c>badges.set</c> replies. Defensive: tolerates missing optional fields and nulls
/// from a newer or partially working mod.
/// </summary>
public static class SnapshotMapper
{
    /// <summary>
    /// Maps a <c>run.snapshot</c> to a <see cref="RunState"/>. <paramref name="hello"/> supplies
    /// <see cref="RunState.IsMultiplayer"/>; without it the run is assumed single player.
    /// Cards with an empty id are dropped; act and floor are clamped to at least 1.
    /// </summary>
    public static RunState ToRunState(RunSnapshotMessage snapshot, HelloMessage? hello = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var deck = (snapshot.Deck ?? [])
            .Where(c => c is not null && !string.IsNullOrEmpty(c.Id))
            .Select(ToDeckCard)
            .ToArray();

        var relics = (snapshot.Relics ?? [])
            .Where(r => !string.IsNullOrEmpty(r))
            .ToArray();

        return new RunState
        {
            Character = snapshot.Character ?? "",
            Ascension = Math.Max(0, snapshot.Ascension),
            Act = Math.Max(1, snapshot.Act),
            Floor = Math.Max(1, snapshot.Floor),
            Hp = Math.Max(0, snapshot.Hp),
            MaxHp = Math.Max(0, snapshot.MaxHp),
            Gold = Math.Max(0, snapshot.Gold),
            Deck = deck,
            Relics = relics,
            IsMultiplayer = hello?.IsMultiplayer ?? false,
            CardRemovalsBought = Math.Max(0, snapshot.CardRemovalsBought ?? 0),
        };
    }

    /// <summary>Maps one deck entry; negative upgrades are treated as 0.</summary>
    public static DeckCard ToDeckCard(SnapshotCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var enchantmentId = string.IsNullOrEmpty(card.Enchantment?.Id) ? null : card.Enchantment.Id;
        return new DeckCard(
            new CardRef(card.Id ?? "", Math.Max(0, card.Upgrade)),
            FloorAdded: Math.Max(0, card.FloorAdded ?? 0),
            EnchantmentId: enchantmentId,
            EnchantmentAmount: enchantmentId is null ? 0 : card.Enchantment!.Amount);
    }

    /// <summary>
    /// The offer context of a screen: merchant → <see cref="OfferContext.Shop"/>; card rewards by
    /// <see cref="ScreenOpenedMessage.Context"/> (see <see cref="ToOfferContext(string?)"/>);
    /// null for screens that are not card offers (map, deck_select, other).
    /// </summary>
    public static OfferContext? ToOfferContext(ScreenOpenedMessage screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return screen.Screen switch
        {
            ScreenKinds.Merchant => OfferContext.Shop,
            ScreenKinds.CardReward => ToOfferContext(screen.Context),
            _ => null,
        };
    }

    /// <summary>
    /// Maps a reward context string: normal→CardReward, elite→EliteReward, boss→BossReward,
    /// event→Event, merchant→Shop. Null or unknown values fall back to <see cref="OfferContext.CardReward"/>.
    /// </summary>
    public static OfferContext ToOfferContext(string? context) => context?.Trim().ToLowerInvariant() switch
    {
        RewardContexts.Elite => OfferContext.EliteReward,
        RewardContexts.Boss => OfferContext.BossReward,
        RewardContexts.Event => OfferContext.Event,
        RewardContexts.Merchant => OfferContext.Shop,
        _ => OfferContext.CardReward,
    };

    /// <summary>
    /// The offered cards in the same order as <see cref="ScreenOpenedMessage.Items"/>
    /// (index <c>i</c> of the result is item <c>i</c>). Items with an empty card id map to an
    /// empty-id <see cref="CardRef"/> rather than being dropped, to keep indices aligned.
    /// </summary>
    public static IReadOnlyList<CardRef> ToCardRefs(ScreenOpenedMessage screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return (screen.Items ?? [])
            .Select(i => new CardRef(i?.CardId ?? "", Math.Max(0, i?.Upgrade ?? 0)))
            .ToArray();
    }

    /// <summary>
    /// Merchant cards and removal price. Items without a price (the mod could not read it) or with
    /// an empty card id are left out, since a priceless shop card would look free to the scorer.
    /// </summary>
    public static ShopOffer ToShopOffer(ScreenOpenedMessage screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var cards = (screen.Items ?? [])
            .Where(i => i is not null && i.Price is >= 0 && !string.IsNullOrEmpty(i.CardId))
            .Select(i => new ShopCardOffer(new CardRef(i.CardId, Math.Max(0, i.Upgrade)), i.Price!.Value))
            .ToArray();
        return new ShopOffer(cards, screen.RemovalPrice is >= 0 ? screen.RemovalPrice : null);
    }

    /// <summary>
    /// Builds a <c>badges.set</c> message. Colors come from <see cref="TierColor(string)"/>;
    /// entries with an empty instance id are skipped.
    /// </summary>
    public static BadgesSetMessage BuildBadges(
        IEnumerable<(string instanceId, string tierLabel, double score, bool isBest, IReadOnlyList<string> chips)> items,
        bool skipRecommended,
        long seq)
    {
        ArgumentNullException.ThrowIfNull(items);
        var badges = items
            .Where(i => !string.IsNullOrEmpty(i.instanceId))
            .Select(i => new CardBadge
            {
                InstanceId = i.instanceId,
                Label = i.tierLabel ?? "",
                Color = TierColor(i.tierLabel),
                Score = double.IsFinite(i.score) ? i.score : 0,
                IsBest = i.isBest,
                Chips = i.chips?.ToArray() ?? [],
            })
            .ToArray();

        return new BadgesSetMessage
        {
            Seq = seq,
            Badges = badges,
            SkipRecommended = skipRecommended,
        };
    }

    /// <summary>Badge color for a tier label (S gold, A purple, B blue, C gray, D dark red; unknown → gray).</summary>
    public static string TierColor(string? tierLabel) => TierColors.ForLabel(tierLabel);

    /// <summary>Badge color for a <see cref="Tier"/>.</summary>
    public static string TierColor(Tier tier) => TierColors.ForLabel(tier.ToString());
}
