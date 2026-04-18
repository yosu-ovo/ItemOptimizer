using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Throttles MotionSensor.Update: on skipped frames, performs a lightweight
    /// center-point proximity check instead of the full per-limb scan.
    /// This avoids detection blind spots that cause door rubber-banding.
    /// </summary>
    static class MotionSensorPatch
    {
        private static readonly ConditionalWeakTable<MotionSensor, StrongBox<int>> Counters = new();

        // Cached field accessors for the detection range
        private static readonly AccessTools.FieldRef<MotionSensor, float> Ref_rangeX =
            AccessTools.FieldRefAccess<MotionSensor, float>("rangeX");
        private static readonly AccessTools.FieldRef<MotionSensor, float> Ref_rangeY =
            AccessTools.FieldRefAccess<MotionSensor, float>("rangeY");

        public static bool Prefix(MotionSensor __instance, float deltaTime)
        {
            // Rewrite supersedes this patch — let vanilla run (rewrite prefix handles it)
            if (OptimizerConfig.EnableMotionSensorRewrite) return true;
            if (!OptimizerConfig.EnableMotionSensorThrottle) return true;

            var counter = Counters.GetOrCreateValue(__instance);
            counter.Value++;

            if (counter.Value % OptimizerConfig.MotionSensorSkipFrames != 0)
            {
                // Lightweight check: use character center position instead of per-limb scan.
                // This catches >95% of cases (character standing near sensor) and avoids
                // the 0-2 frame blind spot that causes door rubber-banding.
                bool detected = QuickDetect(__instance);
                string signalOut = detected ? __instance.Output : __instance.FalseOutput;
                if (!string.IsNullOrEmpty(signalOut))
                    __instance.item.SendSignal(new Signal(signalOut, 1), "state_out");

                if (detected)
                    __instance.MotionDetected = true;

                Stats.MotionSensorSkips++;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Fast detection: check if any character's center point is within the sensor's
        /// detection rectangle. Skips the expensive per-limb CircleIntersectsRectangle.
        /// Also skips wall/submarine detection (very rare use case for door sensors).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool QuickDetect(MotionSensor sensor)
        {
            float rx = Ref_rangeX(sensor);
            float ry = Ref_rangeY(sensor);
            Vector2 detectPos = sensor.item.WorldPosition + sensor.TransformedDetectOffset;

            foreach (Character character in Character.CharacterList)
            {
                if (character.IsDead && sensor.IgnoreDead) continue;
                if (!character.Enabled) continue;
                if (!sensor.TriggersOn(character)) continue;

                float dx = Math.Abs(character.WorldPosition.X - detectPos.X);
                float dy = Math.Abs(character.WorldPosition.Y - detectPos.Y);

                // Use a slightly expanded range (1.5x) to compensate for
                // not checking limb extents — catches characters approaching
                if (dx < rx * 1.5f && dy < ry * 1.5f)
                {
                    // Also check velocity: vanilla requires MinimumVelocity
                    if (character.AnimController?.Collider != null)
                    {
                        var vel = character.AnimController.Collider.LinearVelocity;
                        if (vel.LengthSquared() >= sensor.MinimumVelocity * sensor.MinimumVelocity)
                            return true;
                    }
                }
            }

            return false;
        }
    }
}
