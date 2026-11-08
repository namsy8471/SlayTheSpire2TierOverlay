using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using Sts2Overlay.Bridge.Badges;

namespace Sts2Overlay.Bridge.Patches;

/// <summary>
/// F8 toggles badges. We piggyback on NGame._Input instead of adding our own Node, because a custom
/// Node subclass from a mod DLL may not receive Godot callbacks (no script registration).
/// F8 is not used by the game (F1, F3-F7, F9, F10 are debug keys; F2 feedback; F11 console).
/// We only read the event; we never mark it handled, so the game sees every key as before.
/// </summary>
[HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
internal static class GameInputPatch
{
    private static void Postfix(InputEvent inputEvent)
    {
        try
        {
            if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F8 })
            {
                TierBadge.Enabled = !TierBadge.Enabled;
                TierBadge.SetAllVisible(TierBadge.Enabled);
                BridgeLog.Info($"F8: badges {(TierBadge.Enabled ? "on" : "off")}");
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Error("GameInputPatch", ex);
        }
    }
}
