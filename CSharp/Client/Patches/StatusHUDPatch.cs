using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Throttles StatusHUD scan frequency and thermal overlay draw frequency.
    ///
    /// A1: StatusHUD.Update normally scans every 0.5s with physics raycasts.
    ///     We extend the interval to configurable (default 1.5s) for all StatusHUD instances.
    ///
    /// A2: DrawThermalOverlay iterates all characters * limbs with PerlinNoise every frame.
    ///     We skip drawing every N frames (default 3), reducing from 60fps to ~20fps.
    /// </summary>
    static class StatusHUDPatch
    {
        // Per-instance state for scan throttling
        private static readonly ConditionalWeakTable<StatusHUD, ThrottleState> States = new();

        private sealed class ThrottleState
        {
            public float CustomTimer;
            public bool Initialized;
        }

        // Frame counter for draw throttling
        private static int _drawFrameCounter;

        /// <summary>
        /// Prefix for StatusHUD.Update — extends scan interval.
        /// We let the original run but manipulate its internal timer via our own overlay timer.
        /// Simpler approach: we wrap the original. If our timer says "not yet", we only run the
        /// base.Update (for OnActive effects) and update thermalEffectState, but skip the scan.
        ///
        /// Actually, the simplest safe approach: just return false and replicate the minimal
        /// per-frame work (thermalEffectState advance), letting the real scan happen only
        /// when our timer expires.
        /// </summary>
        public static bool Prefix(StatusHUD __instance, float deltaTime, Camera cam)
        {
            if (!OptimizerConfig.EnableStatusHUDThrottle) return true;

            var state = States.GetOrCreateValue(__instance);
            if (!state.Initialized)
            {
                state.CustomTimer = 0f;
                state.Initialized = true;
            }

            // Always let the base ItemComponent.Update run for OnActive StatusEffects
            // We can't call base.Update from a prefix, so we return true on scan frames
            // and return false (skip) on non-scan frames, but we need OnActive effects...
            //
            // The safest approach: always return true (let original run), but patch the
            // updateTimer field to prevent scans except on our schedule.
            // StatusHUD.Update checks: if (updateTimer > 0) { updateTimer -= dt; return; }
            // We can force updateTimer to stay positive except when we want a scan.

            state.CustomTimer -= deltaTime;
            if (state.CustomTimer > 0f)
            {
                // Not time for scan yet — but we still need the original to run for:
                // - base.Update (OnActive StatusEffects)
                // - thermalEffectState update
                // - equipper null checks
                // The original will also skip the scan because its internal updateTimer > 0
                // We just let it run normally — the internal timer is usually 0.5s
                return true;
            }

            // Time for scan — reset our timer and let the original run fully
            state.CustomTimer = OptimizerConfig.StatusHUDScanInterval;
            return true;
        }

        /// <summary>
        /// Postfix for StatusHUD.Update — after the original runs, if we're in throttle mode
        /// and it's not scan time, force the updateTimer back to a large value so next frame
        /// the original skips the scan.
        ///
        /// Actually, a better approach: we Prefix to check if it's scan time.
        /// If NOT scan time and the original's updateTimer just expired (reached 0),
        /// we need to prevent the scan. We do this by setting updateTimer back up.
        ///
        /// Simplest correct approach: Postfix — after original Update runs, if our timer > 0
        /// (meaning it's not our scan frame), reset the internal updateTimer to prevent
        /// the next-frame scan from happening early.
        /// </summary>
        public static void Postfix(StatusHUD __instance)
        {
            if (!OptimizerConfig.EnableStatusHUDThrottle) return;

            var state = States.GetOrCreateValue(__instance);
            if (state.CustomTimer > 0f)
            {
                // We're between our extended scans — keep the internal timer high
                // so the original's 0.5s scan doesn't trigger
                Ref_updateTimer(__instance) = state.CustomTimer;
            }
        }

        // Access private updateTimer field
        private static readonly HarmonyLib.AccessTools.FieldRef<StatusHUD, float> Ref_updateTimer =
            HarmonyLib.AccessTools.FieldRefAccess<StatusHUD, float>("updateTimer");

        /// <summary>
        /// Prefix for StatusHUD.DrawThermalOverlay — skip drawing on non-Nth frames.
        /// This is a static method, so we don't get an __instance.
        /// </summary>
        public static bool DrawThermalPrefix()
        {
            if (!OptimizerConfig.EnableStatusHUDThrottle) return true;

            _drawFrameCounter++;
            if (_drawFrameCounter % OptimizerConfig.StatusHUDDrawSkipFrames != 0)
            {
                Stats.StatusHUDSkips++;
                return false;
            }
            return true;
        }
    }
}
