using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// WaterDetector sends 3 signals every frame unconditionally and never self-deactivates.
    /// This patch throttles: skip N-1 out of N frames, replaying the last signals.
    /// </summary>
    static class WaterDetectorPatch
    {
        private static readonly ConditionalWeakTable<WaterDetector, ThrottleState> States = new();

        private sealed class ThrottleState
        {
            public int FrameCounter;
            public string LastSignalOut;
            public string LastWaterPct;
            public string LastHighPressure;
        }

        public static bool Prefix(WaterDetector __instance)
        {
            // Rewrite supersedes this patch — let vanilla run (rewrite prefix handles it)
            if (OptimizerConfig.EnableWaterDetectorRewrite) return true;
            if (!OptimizerConfig.EnableWaterDetectorThrottle) return true;

            var state = States.GetOrCreateValue(__instance);
            state.FrameCounter++;

            if (state.FrameCounter % OptimizerConfig.WaterDetectorSkipFrames != 0)
            {
                // Replay last known signals to maintain wiring continuity
                if (state.LastSignalOut != null)
                    __instance.item.SendSignal(state.LastSignalOut, "signal_out");
                if (state.LastWaterPct != null)
                    __instance.item.SendSignal(state.LastWaterPct, "water_%");
                if (state.LastHighPressure != null)
                    __instance.item.SendSignal(state.LastHighPressure, "high_pressure");

                Stats.WaterDetectorSkips++;
                return false;
            }

            // Let original run, then capture signals in postfix
            return true;
        }

        public static void Postfix(WaterDetector __instance)
        {
            if (OptimizerConfig.EnableWaterDetectorRewrite) return;
            if (!OptimizerConfig.EnableWaterDetectorThrottle) return;

            var state = States.GetOrCreateValue(__instance);

            // Cache outputs for replay on skipped frames
            state.LastSignalOut = __instance.WaterDetected ? __instance.Output : __instance.FalseOutput;

            if (__instance.item.CurrentHull != null)
            {
                int waterPct = WaterDetector.GetWaterPercentage(__instance.item.CurrentHull);
                state.LastWaterPct = waterPct.ToString();
            }

            state.LastHighPressure =
                (__instance.item.CurrentHull == null || __instance.item.CurrentHull.LethalPressure > 5.0f)
                ? "1" : "0";
        }
    }
}
