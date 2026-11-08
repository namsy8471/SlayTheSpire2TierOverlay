using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2Overlay.Bridge;

/// <summary>
/// Mod entry point. The game's ModManager finds this class through [ModInitializer]
/// and calls the static Initialize method once, early in game startup.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class BridgeMod
{
    public const string HarmonyId = "namsy8471.Sts2Overlay.Bridge";

    internal static Harmony? Harmony { get; private set; }

    public static void Initialize()
    {
        BridgeLog.Info($"Initialize: v{Assembly.GetExecutingAssembly().GetName().Version} " +
                       $"(modded={ModManager.IsRunningModded()})");

        Harmony = new Harmony(HarmonyId);

        // Patch each class separately: if one target's signature changed after a game patch,
        // only that feature is lost instead of the whole mod.
        foreach (Type patchClass in typeof(BridgeMod).Assembly.GetTypes()
                     .Where(t => t.GetCustomAttribute<HarmonyPatch>() != null))
        {
            try
            {
                Harmony.CreateClassProcessor(patchClass).Patch();
                BridgeLog.Info($"Patched {patchClass.Name}");
            }
            catch (Exception ex)
            {
                BridgeLog.Error($"Patch failed for {patchClass.Name} (feature disabled)", ex);
            }
        }
    }
}
