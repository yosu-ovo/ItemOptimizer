using Barotrauma;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Skips Character.Update for characters in Dormant/Unloaded zones.
    /// Characters are fully suspended — no health, oxygen, status effects, AI, or physics.
    /// This matches the item-level zone skip in UpdateAllTakeover: the entire structure freezes.
    ///
    /// Reuses UpdateAllTakeover._dormantSubFlags[] (same flat array, same PrecomputeZoneFlags).
    /// Player characters are never skipped.
    /// </summary>
    static class CharacterZoneSkipPatch
    {
        internal static void Register(Harmony harmony)
        {
            var method = AccessTools.Method(typeof(Character), "Update",
                new[] { typeof(float), typeof(Camera) });
            if (method == null)
            {
                LuaCsLogger.LogError("[CharZoneSkip] Could not find Character.Update(float, Camera)");
                return;
            }
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(CharacterZoneSkipPatch), nameof(Prefix)));
            LuaCsLogger.Log("[CharZoneSkip] Patch registered");
        }

        internal static void Unregister(Harmony harmony)
        {
            var method = AccessTools.Method(typeof(Character), "Update",
                new[] { typeof(float), typeof(Camera) });
            if (method != null)
                harmony.Unpatch(method,
                    AccessTools.Method(typeof(CharacterZoneSkipPatch), nameof(Prefix)));
        }

        static bool Prefix(Character __instance)
        {
            if (!UpdateAllTakeover._hasZoneSkip) return true;

            // Never freeze player characters
            if (__instance.IsPlayer) return true;

            var sub = __instance.Submarine;
            if (sub == null) return true;

            if (UpdateAllTakeover._dormantSubFlags[sub.ID & 0xFFFF])
            {
                Stats.ZoneCharSkips++;
                return false;
            }

            return true;
        }
    }
}
