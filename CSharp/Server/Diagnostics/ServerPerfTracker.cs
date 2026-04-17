using System;
using System.Diagnostics;
using System.Reflection;
using Barotrauma;
using HarmonyLib;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Server-side: Harmony prefix/postfix pairs on key systems to measure per-tick timing.
    /// Mirrors the client's PerformanceCounter identifiers but runs on the dedicated server.
    /// </summary>
    static class ServerPerfTracker
    {
        private static bool _registered;

        // Per-system stopwatches (reused, no alloc)
        private static readonly Stopwatch SwGameSession = new();
        private static readonly Stopwatch SwCharacter = new();
        private static readonly Stopwatch SwStatusEffect = new();
        private static readonly Stopwatch SwMapEntity = new();
        private static readonly Stopwatch SwRagdoll = new();
        private static readonly Stopwatch SwPhysics = new();
        private static readonly Stopwatch SwNetworking = new();

        // EWMA-smoothed results (ms) — sent to client
        internal static float AvgGameSessionMs;
        internal static float AvgCharacterMs;
        internal static float AvgStatusEffectMs;
        internal static float AvgMapEntityMs;
        internal static float AvgRagdollMs;
        internal static float AvgPhysicsMs;
        internal static float AvgNetworkingMs;

        private const float Smoothing = 0.05f;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        internal static void RegisterPatches(Harmony harmony)
        {
            if (_registered) return;

            // GameSession.Update(float)
            PatchPair(harmony, typeof(GameSession), "Update", new[] { typeof(float) },
                nameof(GameSessionPre), nameof(GameSessionPost));

            // Character.UpdateAll(float, Camera)
            PatchPair(harmony, typeof(Character), "UpdateAll",
                new[] { typeof(float), typeof(Camera) },
                nameof(CharacterPre), nameof(CharacterPost));

            // StatusEffect.UpdateAll(float)
            PatchPair(harmony, typeof(StatusEffect), "UpdateAll", new[] { typeof(float) },
                nameof(StatusEffectPre), nameof(StatusEffectPost));

            // MapEntity.UpdateAll(float, Camera) — already has our prefix from UpdateAllTakeover.
            // Instead, we time it via OnPreUpdate/OnPostUpdate callbacks. Skip patching here.

            // Character.UpdateAnimAll(float)
            PatchPair(harmony, typeof(Character), "UpdateAnimAll", new[] { typeof(float) },
                nameof(RagdollPre), null); // We'll pair anim+ragdoll together

            // Ragdoll.UpdateAll(float, Camera)
            PatchPair(harmony, typeof(Ragdoll), "UpdateAll",
                new[] { typeof(float), typeof(Camera) },
                null, nameof(RagdollPost));

            // Farseer World.Step(float)
            var worldType = typeof(FarseerPhysics.Dynamics.World);
            PatchPair(harmony, worldType, "Step", new[] { typeof(float) },
                nameof(PhysicsPre), nameof(PhysicsPost));

            // GameServer.Update(float)
            var serverType = AccessTools.TypeByName("Barotrauma.Networking.GameServer");
            if (serverType != null)
            {
                PatchPair(harmony, serverType, "Update", new[] { typeof(float) },
                    nameof(NetworkingPre), nameof(NetworkingPost));
            }

            _registered = true;
            LuaCsLogger.Log("[ItemOptimizer] ServerPerfTracker: registered timing patches");
        }

        internal static void UnregisterPatches(Harmony harmony)
        {
            if (!_registered) return;
            // Harmony.UnpatchSelf will remove these with the rest
            _registered = false;
        }

        /// <summary>Called each tick after all timings recorded, to apply EWMA.</summary>
        internal static void EndTick(float mapEntityMs)
        {
            AvgGameSessionMs = AvgGameSessionMs * (1f - Smoothing) + (float)(SwGameSession.ElapsedTicks * TicksToMs) * Smoothing;
            AvgCharacterMs = AvgCharacterMs * (1f - Smoothing) + (float)(SwCharacter.ElapsedTicks * TicksToMs) * Smoothing;
            AvgStatusEffectMs = AvgStatusEffectMs * (1f - Smoothing) + (float)(SwStatusEffect.ElapsedTicks * TicksToMs) * Smoothing;
            AvgMapEntityMs = AvgMapEntityMs * (1f - Smoothing) + mapEntityMs * Smoothing;
            AvgRagdollMs = AvgRagdollMs * (1f - Smoothing) + (float)(SwRagdoll.ElapsedTicks * TicksToMs) * Smoothing;
            AvgPhysicsMs = AvgPhysicsMs * (1f - Smoothing) + (float)(SwPhysics.ElapsedTicks * TicksToMs) * Smoothing;
            AvgNetworkingMs = AvgNetworkingMs * (1f - Smoothing) + (float)(SwNetworking.ElapsedTicks * TicksToMs) * Smoothing;
        }

        internal static void Reset()
        {
            AvgGameSessionMs = 0;
            AvgCharacterMs = 0;
            AvgStatusEffectMs = 0;
            AvgMapEntityMs = 0;
            AvgRagdollMs = 0;
            AvgPhysicsMs = 0;
            AvgNetworkingMs = 0;
        }

        // ── Harmony prefix/postfix methods ──

        public static void GameSessionPre() => SwGameSession.Restart();
        public static void GameSessionPost() => SwGameSession.Stop();

        public static void CharacterPre() => SwCharacter.Restart();
        public static void CharacterPost() => SwCharacter.Stop();

        public static void StatusEffectPre() => SwStatusEffect.Restart();
        public static void StatusEffectPost() => SwStatusEffect.Stop();

        // Ragdoll = UpdateAnimAll + Ragdoll.UpdateAll combined
        public static void RagdollPre() => SwRagdoll.Restart();
        public static void RagdollPost() => SwRagdoll.Stop();

        public static void PhysicsPre() => SwPhysics.Restart();
        public static void PhysicsPost() => SwPhysics.Stop();

        public static void NetworkingPre() => SwNetworking.Restart();
        public static void NetworkingPost() => SwNetworking.Stop();

        // ── Helper ──

        private static void PatchPair(Harmony harmony, Type type, string methodName, Type[] paramTypes,
            string prefixName, string postfixName)
        {
            var original = AccessTools.Method(type, methodName, paramTypes);
            if (original == null)
            {
                LuaCsLogger.Log($"[ItemOptimizer] ServerPerfTracker: {type.Name}.{methodName} not found, skipping");
                return;
            }

            HarmonyMethod prefix = prefixName != null
                ? new HarmonyMethod(AccessTools.Method(typeof(ServerPerfTracker), prefixName))
                : null;
            HarmonyMethod postfix = postfixName != null
                ? new HarmonyMethod(AccessTools.Method(typeof(ServerPerfTracker), postfixName))
                : null;

            harmony.Patch(original, prefix: prefix, postfix: postfix);
        }
    }
}
