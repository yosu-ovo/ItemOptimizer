using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Door throttle patch — DISABLED BY DEFAULT.
    ///
    /// Analysis showed door updates are already very cheap (~0.07ms for 19 doors),
    /// and throttling idle doors causes rubber-banding at motion-sensor-controlled
    /// doors due to a race condition: the Prefix skips the Update, but ReceiveSignal
    /// can change isOpen in the same frame. Since Harmony Postfix does NOT run when
    /// Prefix returns false, there's no reliable way to detect and recover.
    ///
    /// The performance gain (~0.1ms) is not worth the gameplay impact.
    /// Keeping the infrastructure in case a future need arises, but defaulting to off.
    /// </summary>
    static class DoorPatch
    {
        private static readonly ConditionalWeakTable<Door, ThrottleState> States = new();

        private static readonly AccessTools.FieldRef<Door, bool> Ref_isOpen =
            AccessTools.FieldRefAccess<Door, bool>("isOpen");
        private static readonly AccessTools.FieldRef<Door, float> Ref_openState =
            AccessTools.FieldRefAccess<Door, float>("openState");

        private sealed class ThrottleState
        {
            public int FrameCounter;
            public string LastSignal;
            public bool LastIsOpen;
            public bool HasRecordedState;
        }

        public static bool Prefix(Door __instance)
        {
            if (!OptimizerConfig.EnableDoorThrottle) return true;

            // Only throttle idle doors — NOT transitioning ones
            float openState = Ref_openState(__instance);
            bool isIdle = openState <= 0f || openState >= 1f;
            if (!isIdle) return true;

            if (__instance.IsBroken) return true;

            var state = States.GetOrCreateValue(__instance);

            // Detect state change since last frame
            if (state.HasRecordedState)
            {
                bool currentIsOpen = Ref_isOpen(__instance);
                if (currentIsOpen != state.LastIsOpen)
                    return true; // state changed externally, run original immediately
            }

            state.FrameCounter++;

            if (state.FrameCounter % OptimizerConfig.DoorSkipFrames != 0)
            {
                if (state.LastSignal != null)
                    __instance.item.SendSignal(state.LastSignal, "state_out");

                Stats.DoorSkips++;
                return false;
            }

            return true;
        }

        public static void Postfix(Door __instance)
        {
            if (!OptimizerConfig.EnableDoorThrottle) return;

            var state = States.GetOrCreateValue(__instance);
            bool isOpen = Ref_isOpen(__instance);
            state.LastSignal = isOpen ? "1" : "0";
            state.LastIsOpen = isOpen;
            state.HasRecordedState = true;
        }
    }
}
