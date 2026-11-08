using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace Sts2Overlay.Bridge.Patches;

/// <summary>
/// SPIKE: only logs shop cards and prices to prove we can read them. Badges on shop cards come in M3.
/// UpdateVisual is protected, so it is targeted by name string instead of nameof().
/// </summary>
[HarmonyPatch(typeof(NMerchantCard), "UpdateVisual")]
internal static class MerchantCardPatch
{
    private static void Postfix(NMerchantCard __instance)
    {
        try
        {
            if (__instance.Entry is MerchantCardEntry { CreationResult: { } result } entry)
            {
                BridgeLog.Info($"Shop card: {result.Card.Id}+{result.Card.CurrentUpgradeLevel} " +
                               $"cost={entry.Cost} sale={entry.IsOnSale}");
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Error("MerchantCardPatch", ex);
        }
    }
}
