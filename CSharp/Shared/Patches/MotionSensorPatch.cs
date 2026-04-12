using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    static class MotionSensorPatch
    {
        private static readonly ConditionalWeakTable<MotionSensor, StrongBox<int>> Counters = new();

        public static bool Prefix(MotionSensor __instance)
        {
            if (!OptimizerConfig.EnableMotionSensorThrottle) return true;

            var counter = Counters.GetOrCreateValue(__instance);
            counter.Value++;

            if (counter.Value % OptimizerConfig.MotionSensorSkipFrames != 0)
            {
                // Replay last known result to maintain signal continuity
                string signalOut = __instance.MotionDetected ? __instance.Output : __instance.FalseOutput;
                if (!string.IsNullOrEmpty(signalOut))
                {
                    __instance.item.SendSignal(new Signal(signalOut, 1), "state_out");
                }

                Stats.MotionSensorSkips++;
                return false;
            }

            return true;
        }
    }
}
