using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Thread-safety Harmony patches for parallel Item.Update.
    /// All patches guard shared mutable state with Monitor locks or ThreadLocal replacements.
    /// Only active when ParallelDispatch is enabled.
    /// </summary>
    static class ThreadSafetyPatches
    {
        // ── Lock objects ──
        private static readonly object DurationLock = new object();
        private static readonly object DelayLock = new object();
        private static readonly object SpawnerLock = new object();
        private static readonly object ConditionLock = new object();

        // ── ThreadLocal Rand ──
        private static readonly ThreadLocal<Random> WorkerRandom =
            new(() => new Random(Environment.CurrentManagedThreadId * 31 + Environment.TickCount));

        // ── Deferred sound queue (client only) ──
        private static readonly ConcurrentQueue<Action> DeferredSounds = new();

        // ── Registration state ──
        private static bool _registered;
        private static readonly List<(MethodBase original, MethodInfo patch)> _appliedPatches = new();

        // ────────────────────────────────────────────────────────────
        //  Registration
        // ────────────────────────────────────────────────────────────
        internal static void RegisterPatches(Harmony harmony)
        {
            if (_registered) return;

            // ── Rand.GetRNG ──
            PatchPrefix(harmony, typeof(Rand), "GetRNG",
                new[] { typeof(Rand).GetNestedType("RandSync") },
                nameof(RandGetRNGPrefix));

            // ── DurationList: StatusEffect.Apply (2 overloads) ──
            PatchLock(harmony, typeof(StatusEffect), "Apply",
                new[] { typeof(ActionType), typeof(float), typeof(Entity),
                        typeof(ISerializableEntity), typeof(Vector2?) },
                DurationLock);

            PatchLock(harmony, typeof(StatusEffect), "Apply",
                new[] { typeof(ActionType), typeof(float), typeof(Entity),
                        typeof(IReadOnlyList<ISerializableEntity>), typeof(Vector2?) },
                DurationLock);

            // ── DurationList: StatusEffect.UpdateAll ──
            PatchLock(harmony, typeof(StatusEffect), "UpdateAll",
                new[] { typeof(float) }, DurationLock);

            // ── DurationList+DelayList: StatusEffect.StopAll ──
            // Needs both locks — wrap with DurationLock in prefix, DelayLock nested
            PatchPrefix(harmony, typeof(StatusEffect), "StopAll", Type.EmptyTypes, nameof(StopAllPrefix));
            PatchPostfix(harmony, typeof(StatusEffect), "StopAll", Type.EmptyTypes, nameof(StopAllPostfix));

            // ── DurationList: PropertyConditional.Matches ──
            PatchLock(harmony, typeof(PropertyConditional), "Matches",
                new[] { typeof(ISerializableEntity) }, DurationLock);

            // ── DurationList: CharacterHealth.GetPredictedStrength ──
            // Method signature: GetPredictedStrength(AfflictionPrefab prefab, float knownStrength, IReadOnlyList<Affliction> currentAfflictions)
            var getPredicted = AccessTools.Method(typeof(CharacterHealth), "GetPredictedStrength");
            if (getPredicted != null)
                PatchLockDirect(harmony, getPredicted, DurationLock);

            // ── DurationList+DelayList: AbilityConditionHasStatusTag ──
            var absType = AccessTools.TypeByName("Barotrauma.AbilityConditionHasStatusTag");
            if (absType != null)
            {
                var matchesCond = AccessTools.Method(absType, "MatchesConditionSpecific");
                if (matchesCond != null)
                {
                    PatchPrefix(harmony, matchesCond, nameof(DualLockPrefix));
                    PatchPostfix(harmony, matchesCond, nameof(DualLockPostfix));
                }
            }

            // ── DelayList: DelayedEffect.Apply (2 overloads) ──
            PatchLock(harmony, typeof(DelayedEffect), "Apply",
                new[] { typeof(ActionType), typeof(float), typeof(Entity),
                        typeof(ISerializableEntity), typeof(Vector2?) },
                DelayLock);

            PatchLock(harmony, typeof(DelayedEffect), "Apply",
                new[] { typeof(ActionType), typeof(float), typeof(Entity),
                        typeof(IReadOnlyList<ISerializableEntity>), typeof(Vector2?) },
                DelayLock);

            // ── DelayList: DelayedEffect.Update ──
            PatchLock(harmony, typeof(DelayedEffect), "Update",
                new[] { typeof(float) }, DelayLock);

            // ── EntitySpawner methods ──
            PatchAllOverloads(harmony, typeof(EntitySpawner), "AddItemToSpawnQueue", SpawnerLock);
            PatchAllOverloads(harmony, typeof(EntitySpawner), "AddItemToRemoveQueue", SpawnerLock);
            PatchAllOverloads(harmony, typeof(EntitySpawner), "AddEntityToRemoveQueue", SpawnerLock);

            // ── Item.SetCondition (private) + related ──
            var setCondition = AccessTools.Method(typeof(Item), "SetCondition",
                new[] { typeof(float), typeof(bool), typeof(bool) });
            if (setCondition != null)
                PatchLockDirect(harmony, setCondition, ConditionLock);

            PatchLock(harmony, typeof(Item), "SendPendingNetworkUpdates", Type.EmptyTypes, ConditionLock);
            PatchLock(harmony, typeof(Item), "UpdatePendingConditionUpdates",
                new[] { typeof(float) }, ConditionLock);

            // ── Item.HasTag — thread-safe redirect ──
            // Item.HasTag() reads a private HashSet<Identifier> (non-thread-safe).
            // On worker threads, skip the HashSet and use only Prefab.Tags (ImmutableHashSet).
            // This fixes crashes from any mod (e.g. DDA SpritePatch) calling HasTag on workers.
            var hasTagSingle = AccessTools.Method(typeof(Item), "HasTag", new[] { typeof(Identifier) });
            if (hasTagSingle != null)
            {
                var prefix = new HarmonyMethod(typeof(ThreadSafetyPatches), nameof(HasTagPrefix));
                harmony.Patch(hasTagSingle, prefix: prefix);
                _appliedPatches.Add((hasTagSingle, AccessTools.Method(typeof(ThreadSafetyPatches), nameof(HasTagPrefix))));
            }

            // ── Sound.Play overloads (client only) ──
            RegisterSoundPatches(harmony);

            _registered = true;
        }

        internal static void UnregisterPatches(Harmony harmony)
        {
            if (!_registered) return;
            foreach (var (original, patch) in _appliedPatches)
            {
                harmony.Unpatch(original, patch);
            }
            _appliedPatches.Clear();
            _registered = false;
        }

        // ────────────────────────────────────────────────────────────
        //  Rand.GetRNG Prefix
        // ────────────────────────────────────────────────────────────
        public static bool RandGetRNGPrefix(ref Random __result, object randSync)
        {
            // RandSync.Unsynced = 0
            if (!ParallelDispatchPatch.IsWorkerThread) return true;
            if ((int)randSync != 0) return true; // only intercept Unsynced

            __result = WorkerRandom.Value;
            return false;
        }

        // ────────────────────────────────────────────────────────────
        //  Item.HasTag — thread-safe redirect on worker threads
        // ────────────────────────────────────────────────────────────
        public static bool HasTagPrefix(Item __instance, Identifier tag, ref bool __result)
        {
            // During active parallel dispatch, ALL threads (main + workers) use
            // Prefab.Tags (ImmutableHashSet) to completely avoid the non-thread-safe
            // instance HashSet. Once a HashSet is corrupted by concurrent access,
            // even single-threaded reads crash — so we must isolate ALL access
            // during the parallel window, not just worker-thread access.
            if (!ParallelDispatchPatch._dispatchActive) return true;

            __result = __instance.Prefab.Tags.Contains(tag);
            return false;
        }

        // ────────────────────────────────────────────────────────────
        //  DurationList + DelayList dual lock
        // ────────────────────────────────────────────────────────────
        public static void DualLockPrefix()
        {
            Monitor.Enter(DurationLock);
            Monitor.Enter(DelayLock);
        }

        public static void DualLockPostfix()
        {
            Monitor.Exit(DelayLock);
            Monitor.Exit(DurationLock);
        }

        // StatusEffect.StopAll clears both lists
        public static void StopAllPrefix()
        {
            Monitor.Enter(DurationLock);
            Monitor.Enter(DelayLock);
        }

        public static void StopAllPostfix()
        {
            Monitor.Exit(DelayLock);
            Monitor.Exit(DurationLock);
        }

        // ────────────────────────────────────────────────────────────
        //  Sound.Play — deferred queue (client only)
        // ────────────────────────────────────────────────────────────
        private static void RegisterSoundPatches(Harmony harmony)
        {
            // Sound is client-only; may not exist at compile time on server
            var soundType = AccessTools.TypeByName("Barotrauma.Sounds.Sound");
            if (soundType == null) return;

            // Patch all Play overloads
            foreach (var method in soundType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "Play") continue;
                if (method.DeclaringType != soundType) continue; // skip inherited

                var prefix = new HarmonyMethod(typeof(ThreadSafetyPatches), nameof(SoundPlayPrefix));
                harmony.Patch(method, prefix: prefix);
                _appliedPatches.Add((method, AccessTools.Method(typeof(ThreadSafetyPatches), nameof(SoundPlayPrefix))));
            }

        }

        public static bool SoundPlayPrefix(ref object __result)
        {
            if (!ParallelDispatchPatch.IsWorkerThread) return true;

            // On worker thread: skip sound play, return null SoundChannel
            __result = null;
            return false;
        }

        /// <summary>Drain the deferred sound queue on the main thread.</summary>
        internal static void FlushDeferredSounds()
        {
            while (DeferredSounds.TryDequeue(out var action))
            {
                try { action(); }
                catch { /* ignore sound errors */ }
            }
        }

        // ────────────────────────────────────────────────────────────
        //  Generic Monitor lock Prefix/Postfix generators
        // ────────────────────────────────────────────────────────────

        // We use a dictionary to map lock objects so the generic prefix/postfix can find the right lock.
        // For simplicity, we generate named methods via delegates.
        // Instead, we use a simpler approach: thread-local lock stack.
        [ThreadStatic] private static Stack<object> _lockStack;

        private static Stack<object> LockStack => _lockStack ??= new Stack<object>(4);

        // Named prefixes/postfixes for each lock type
        public static void DurationLockPrefix() { Monitor.Enter(DurationLock); }
        public static void DurationLockPostfix() { Monitor.Exit(DurationLock); }
        public static void DelayLockPrefix() { Monitor.Enter(DelayLock); }
        public static void DelayLockPostfix() { Monitor.Exit(DelayLock); }
        public static void SpawnerLockPrefix() { Monitor.Enter(SpawnerLock); }
        public static void SpawnerLockPostfix() { Monitor.Exit(SpawnerLock); }
        public static void ConditionLockPrefix() { Monitor.Enter(ConditionLock); }
        public static void ConditionLockPostfix() { Monitor.Exit(ConditionLock); }

        // ────────────────────────────────────────────────────────────
        //  Helpers
        // ────────────────────────────────────────────────────────────

        private static void PatchLock(Harmony harmony, Type type, string method, Type[] args, object lockObj)
        {
            var original = AccessTools.Method(type, method, args);
            if (original == null)
            {
                LuaCsLogger.Log($"[ItemOptimizer] ThreadSafety: Could not find {type.Name}.{method}");
                return;
            }
            PatchLockDirect(harmony, original, lockObj);
        }

        private static void PatchLockDirect(Harmony harmony, MethodBase original, object lockObj)
        {
            string prefixName, postfixName;
            if (lockObj == DurationLock) { prefixName = nameof(DurationLockPrefix); postfixName = nameof(DurationLockPostfix); }
            else if (lockObj == DelayLock) { prefixName = nameof(DelayLockPrefix); postfixName = nameof(DelayLockPostfix); }
            else if (lockObj == SpawnerLock) { prefixName = nameof(SpawnerLockPrefix); postfixName = nameof(SpawnerLockPostfix); }
            else if (lockObj == ConditionLock) { prefixName = nameof(ConditionLockPrefix); postfixName = nameof(ConditionLockPostfix); }
            else return;

            var prefix = new HarmonyMethod(AccessTools.Method(typeof(ThreadSafetyPatches), prefixName));
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(ThreadSafetyPatches), postfixName));
            harmony.Patch(original, prefix: prefix, postfix: postfix);
            _appliedPatches.Add((original, AccessTools.Method(typeof(ThreadSafetyPatches), prefixName)));
            _appliedPatches.Add((original, AccessTools.Method(typeof(ThreadSafetyPatches), postfixName)));
        }

        private static void PatchAllOverloads(Harmony harmony, Type type, string method, object lockObj)
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name == method && m.DeclaringType == type)
                    PatchLockDirect(harmony, m, lockObj);
            }
        }

        private static void PatchPrefix(Harmony harmony, Type type, string method, Type[] args, string prefixMethod)
        {
            var original = AccessTools.Method(type, method, args);
            if (original == null)
            {
                LuaCsLogger.Log($"[ItemOptimizer] ThreadSafety: Could not find {type.Name}.{method}");
                return;
            }
            PatchPrefix(harmony, original, prefixMethod);
        }

        private static void PatchPrefix(Harmony harmony, MethodBase original, string prefixMethod)
        {
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(ThreadSafetyPatches), prefixMethod));
            harmony.Patch(original, prefix: prefix);
            _appliedPatches.Add((original, AccessTools.Method(typeof(ThreadSafetyPatches), prefixMethod)));
        }

        private static void PatchPostfix(Harmony harmony, Type type, string method, Type[] args, string postfixMethod)
        {
            var original = AccessTools.Method(type, method, args);
            if (original == null) return;
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(ThreadSafetyPatches), postfixMethod));
            harmony.Patch(original, postfix: postfix);
            _appliedPatches.Add((original, AccessTools.Method(typeof(ThreadSafetyPatches), postfixMethod)));
        }

        private static void PatchPostfix(Harmony harmony, MethodBase original, string postfixMethod)
        {
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(ThreadSafetyPatches), postfixMethod));
            harmony.Patch(original, postfix: postfix);
            _appliedPatches.Add((original, AccessTools.Method(typeof(ThreadSafetyPatches), postfixMethod)));
        }
    }
}
