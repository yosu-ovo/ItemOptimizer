using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    static class WearablePatch
    {
        private static readonly ConditionalWeakTable<Wearable, StrongBox<int>> Counters = new();

        public static bool Prefix(Wearable __instance)
        {
            if (!OptimizerConfig.EnableWearableThrottle) return true;

            var picker = __instance.Picker;
            if (picker == null) return true;

#if CLIENT
            if (picker == Character.Controlled) return true;
#endif

            var counter = Counters.GetOrCreateValue(__instance);
            counter.Value++;

            if (counter.Value % OptimizerConfig.WearableSkipFrames != 0)
            {
                // Still update position to follow the character
                if (__instance.item.GetComponent<Holdable>() is not { IsActive: true })
                {
                    __instance.item.SetTransform(picker.SimPosition, 0.0f);
                }

                Stats.WearableSkips++;
                return false;
            }

            return true;
        }
    }
}
