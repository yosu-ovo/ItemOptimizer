using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    static class CustomInterfacePatch
    {
        public static bool Prefix(CustomInterface __instance)
        {
            if (!OptimizerConfig.EnableCustomInterfaceThrottle) return true;

            Item ownerItem = __instance.item;
            if (ColdStorageDetector.IsNotInActiveUse(ownerItem))
            {
                Stats.CustomInterfaceSkips++;
                return false;
            }

            return true;
        }
    }
}
