using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Door sends state_out signal every frame and never self-deactivates.
    /// When fully open or fully closed (idle), most of Update is wasted.
    /// This patch throttles idle doors: skip N-1 out of N frames, replaying state_out.
    /// Doors that are transitioning (opening/closing) always run the original.
    /// Also detects server-pushed state changes to avoid multiplayer desync.
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
            if (!isIdle) return true; // door is moving, always run original

            // Also skip if broken — broken doors have special logic
            if (__instance.IsBroken) return true;

            var state = States.GetOrCreateValue(__instance);

            // Detect state change: if isOpen flipped since last recorded (server push or
            // local interaction), force a full update to sync physics body immediately.
            if (state.HasRecordedState)
            {
                bool currentIsOpen = Ref_isOpen(__instance);
                if (currentIsOpen != state.LastIsOpen)
                    return true; // state changed externally, run original immediately
            }

            state.FrameCounter++;

            if (state.FrameCounter % OptimizerConfig.DoorSkipFrames != 0)
            {
                // Replay last state_out signal
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
