using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using Sts2Overlay.Bridge.Badges;

namespace Sts2Overlay.Bridge.Patches;

/// <summary>
/// Card reward screen (the "pick 1 of 3" screen).
///
/// RefreshOptions runs from _Ready (first open) and again on a re-roll. When it returns, the new
/// NGridCardHolder children already sit in the private _cardRow, so a Postfix sees exactly the
/// holders being shown. Postfix = runs after the original method and never changes game state.
/// </summary>
[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.RefreshOptions))]
internal static class CardRewardRefreshPatch
{
    private static readonly AccessTools.FieldRef<NCardRewardSelectionScreen, Control> CardRow =
        AccessTools.FieldRefAccess<NCardRewardSelectionScreen, Control>("_cardRow");

    private static void Postfix(NCardRewardSelectionScreen __instance,
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> extraOptions)
    {
        try
        {
            BridgeLog.Info("CardReward opened: " + string.Join(", ",
                options.Select(o => $"{o.Card.Id}+{o.Card.CurrentUpgradeLevel}")) +
                $" | alternatives={extraOptions.Count}");

            Control row = CardRow(__instance);
            foreach (NGridCardHolder holder in row.GetChildren().OfType<NGridCardHolder>())
            {
                // NGridCardHolder.CardModel returns the base card even during an upgrade preview.
                CardModel? card = holder.CardModel;
                if (card is null) continue;

                Vector2 topRight = holder.Hitbox is { } hb
                    ? hb.Position + new Vector2(hb.Size.X, 0)
                    : new Vector2(150, -211); // NCard.defaultSize = 300x422, origin at centre
                TierBadge.Attach(holder, TierBadge.DemoTier(card), topRight);
                BridgeLog.Info($"  badge {card.Id} hitbox={holder.Hitbox?.Position}/{holder.Hitbox?.Size}");
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Error("CardRewardRefreshPatch", ex);
        }
    }
}

/// <summary>
/// NGridCardHolder is pooled and reused by other screens (deck view, card library...).
/// Strip our badge whenever a holder goes back to / comes out of the pool so it never leaks.
/// </summary>
[HarmonyPatch(typeof(NGridCardHolder))]
internal static class GridCardHolderPoolPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(NGridCardHolder.OnFreedToPool))]
    private static void OnFreed(NGridCardHolder __instance) => Strip(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(NGridCardHolder.OnReturnedFromPool))]
    private static void OnReturned(NGridCardHolder __instance) => Strip(__instance);

    private static void Strip(NGridCardHolder holder)
    {
        try { TierBadge.RemoveFrom(holder); }
        catch (Exception ex) { BridgeLog.Error("GridCardHolderPoolPatch", ex); }
    }
}
