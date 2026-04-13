using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Animation LOD: skip UpdateAnimations for characters that are invisible
    /// (SimplePhysicsEnabled) or reduce frequency for distant characters.
    ///
    /// Targets Character.UpdateAnimAll — a static method that loops all characters.
    /// We replace it with a prefix that returns false (skips original) and does
    /// our own filtered loop.
    ///
    /// Uses hysteresis band to prevent flickering at the distance boundary:
    /// enter half-rate at 6000px, recover to full-rate at 5000px.
    ///
    /// Client-only: uses camera position for distance checks.
    /// </summary>
    static class AnimLODPatch
    {
        private static int _frameCounter;
        private static MethodInfo _originalMethod;

        /// <summary>Distance² threshold beyond which animation updates run at half frequency.</summary>
        private static float _halfRateDistSq = 6000f * 6000f;
        /// <summary>Distance² threshold below which half-rate characters recover to full rate.</summary>
        private static float _fullRateDistSq = 5000f * 5000f;

        /// <summary>Tracks which characters are currently in half-rate mode (by ID).</summary>
        private static readonly HashSet<ushort> _halfRateChars = new();

        public static void Register(Harmony harmony)
        {
            _originalMethod = AccessTools.Method(typeof(Character), "UpdateAnimAll");
            if (_originalMethod == null)
            {
                LuaCsLogger.LogError("[AnimLOD] Could not find Character.UpdateAnimAll");
                return;
            }

            harmony.Patch(_originalMethod,
                prefix: new HarmonyMethod(typeof(AnimLODPatch), nameof(Prefix)));

            LuaCsLogger.Log("[AnimLOD] Patch registered");
        }

        public static void Unregister(Harmony harmony)
        {
            if (_originalMethod != null)
                harmony.Unpatch(_originalMethod, AccessTools.Method(typeof(AnimLODPatch), nameof(Prefix)));
            _halfRateChars.Clear();
        }

        /// <summary>
        /// Replaces original UpdateAnimAll. Returns false to skip the original.
        /// </summary>
        static bool Prefix(float deltaTime)
        {
            if (!OptimizerConfig.EnableAnimLOD)
            {
                return true; // disabled → run original
            }

            _frameCounter++;
            var controlled = Character.Controlled;
            Vector2 camPos = controlled?.WorldPosition ?? GameMain.GameScreen?.Cam?.GetPosition() ?? Vector2.Zero;

            int skipCount = 0;
            int halfRateCount = 0;

            foreach (Character c in Character.CharacterList)
            {
                if (!c.Enabled || c.AnimController.Frozen) continue;

                // Always update the controlled character at full rate
                if (c == controlled)
                {
                    c.AnimController.UpdateAnimations(deltaTime);
                    continue;
                }

                // SimplePhysicsEnabled characters are invisible — skip animation entirely
                if (c.AnimController.SimplePhysicsEnabled)
                {
                    skipCount++;
                    continue;
                }

                // Hysteresis-based half-rate decision
                float distSq = Vector2.DistanceSquared(c.WorldPosition, camPos);
                bool wasHalfRate = _halfRateChars.Contains(c.ID);

                bool isHalfRate;
                if (wasHalfRate)
                    isHalfRate = distSq > _fullRateDistSq;  // recover only when closer than 5000px
                else
                    isHalfRate = distSq > _halfRateDistSq;  // enter half-rate only beyond 6000px

                if (isHalfRate)
                {
                    _halfRateChars.Add(c.ID);

                    // Use character ID hash to stagger which frame each character updates
                    if ((_frameCounter + c.ID) % 2 != 0)
                    {
                        halfRateCount++;
                        continue;
                    }
                    // When we do update, compensate delta for the skipped frame
                    c.AnimController.UpdateAnimations(deltaTime * 2f);
                    continue;
                }

                _halfRateChars.Remove(c.ID);

                // Close characters: full rate
                c.AnimController.UpdateAnimations(deltaTime);
            }

            Stats.AnimLODSkipped = skipCount;
            Stats.AnimLODHalfRate = halfRateCount;

            return false; // skip original
        }
    }
}
