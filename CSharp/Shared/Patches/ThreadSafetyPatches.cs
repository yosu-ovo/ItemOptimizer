using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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

        // ── Registration state ──
        private static bool _registered;
        private static readonly List<(MethodBase original, MethodInfo patch)> _appliedPatches = new();

        // ────────────────────────────────────────────────────────────
        //  Registration
        // ────────────────────────────────────────────────────────────
        internal static void RegisterPatches(Harmony harmony)
        {
            if (_registered) return;

            // ── Rand.GetRNG — transpiler to avoid per-call Harmony overhead ──
            var getRNG = AccessTools.Method(typeof(Rand), "GetRNG",
                new[] { typeof(Rand).GetNestedType("RandSync") });
            if (getRNG != null)
            {
                var transpiler = new HarmonyMethod(typeof(ThreadSafetyPatches), nameof(RandGetRNGTranspiler));
                harmony.Patch(getRNG, transpiler: transpiler);
                _appliedPatches.Add((getRNG, AccessTools.Method(typeof(ThreadSafetyPatches), nameof(RandGetRNGTranspiler))));
            }

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
            // REMOVED: No longer needed. HasStatusTagCachePatch.PreBuildAll() pre-populates
            // the cache before parallel dispatch, so TryGetCached does lock-free dictionary reads.
            // Non-HasStatusTag Matches calls don't access DurationList at all.

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

            // ── Item.HasTag — thread-safe redirect via TRANSPILER ──
            // During parallel dispatch, redirects to Prefab.Tags (ImmutableHashSet).
            // Transpiler avoids per-call Harmony dispatch overhead (~0.5us) on this very hot path.
            var hasTagSingle = AccessTools.Method(typeof(Item), "HasTag", new[] { typeof(Identifier) });
            if (hasTagSingle != null)
            {
                var transpiler = new HarmonyMethod(typeof(ThreadSafetyPatches), nameof(HasTagTranspiler));
                harmony.Patch(hasTagSingle, transpiler: transpiler);
                _appliedPatches.Add((hasTagSingle, AccessTools.Method(typeof(ThreadSafetyPatches), nameof(HasTagTranspiler))));
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
        //  Rand.GetRNG — Transpiler + helper
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Injects at method start:
        ///   Random r = RandGetRNGTryIntercept(randSync);
        ///   if (r != null) return r;
        /// </summary>
        public static IEnumerable<CodeInstruction> RandGetRNGTranspiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var skipLabel = generator.DefineLabel();

            yield return new CodeInstruction(OpCodes.Ldarg_0);  // randSync (static method, arg0)
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(ThreadSafetyPatches), nameof(RandGetRNGTryIntercept)));
            yield return new CodeInstruction(OpCodes.Dup);
            yield return new CodeInstruction(OpCodes.Brfalse_S, skipLabel);
            yield return new CodeInstruction(OpCodes.Ret);

            var originalInstructions = instructions.ToList();
            var popInstr = new CodeInstruction(OpCodes.Pop);
            popInstr.labels.Add(skipLabel);
            yield return popInstr;

            if (originalInstructions.Count > 0 && originalInstructions[0].labels.Count > 0)
            {
                popInstr.labels.AddRange(originalInstructions[0].labels);
                originalInstructions[0].labels.Clear();
            }

            foreach (var instr in originalInstructions)
                yield return instr;
        }

        /// <summary>
        /// Returns a ThreadLocal Random for Unsynced on worker threads, null otherwise (fall through).
        /// Takes int instead of RandSync to avoid boxing in transpiled IL.
        /// </summary>
        public static Random RandGetRNGTryIntercept(int randSync)
        {
            if (!UpdateAllTakeover.IsWorkerThread) return null;
            if (randSync != 0) return null; // only intercept Unsynced (= 0)
            return WorkerRandom.Value;
        }

        // ────────────────────────────────────────────────────────────
        //  Item.HasTag — Transpiler + helper
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Injects at method start:
        ///   int r = HasTagTryIntercept(this, tag);
        ///   if (r >= 0) return r != 0;
        /// </summary>
        public static IEnumerable<CodeInstruction> HasTagTranspiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var skipLabel = generator.DefineLabel();

            yield return new CodeInstruction(OpCodes.Ldarg_0);  // this (Item)
            yield return new CodeInstruction(OpCodes.Ldarg_1);  // tag (Identifier)
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(ThreadSafetyPatches), nameof(HasTagTryIntercept)));
            // Stack: [int] — -1 = not handled, 0 = false, 1 = true
            yield return new CodeInstruction(OpCodes.Dup);
            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
            yield return new CodeInstruction(OpCodes.Blt_S, skipLabel);  // if < 0, skip to original
            // Convert int to bool and return
            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
            yield return new CodeInstruction(OpCodes.Cgt);
            yield return new CodeInstruction(OpCodes.Ret);

            var originalInstructions = instructions.ToList();
            var popInstr = new CodeInstruction(OpCodes.Pop);
            popInstr.labels.Add(skipLabel);
            yield return popInstr;

            if (originalInstructions.Count > 0 && originalInstructions[0].labels.Count > 0)
            {
                popInstr.labels.AddRange(originalInstructions[0].labels);
                originalInstructions[0].labels.Clear();
            }

            foreach (var instr in originalInstructions)
                yield return instr;
        }

        /// <summary>
        /// During parallel dispatch, uses Prefab.Tags (ImmutableHashSet) for thread safety.
        /// Returns -1 when not intercepting (DispatchActive is false).
        /// </summary>
        public static int HasTagTryIntercept(Item item, Identifier tag)
        {
            if (!UpdateAllTakeover.DispatchActive) return -1;
            return item.Prefab.Tags.Contains(tag) ? 1 : 0;
        }

        // ────────────────────────────────────────────────────────────
        //  LEGACY prefixes — kept for reference but no longer registered
        // ────────────────────────────────────────────────────────────
        public static bool RandGetRNGPrefix(ref Random __result, object randSync)
        {
            // RandSync.Unsynced = 0
            if (!UpdateAllTakeover.IsWorkerThread) return true;
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
            if (!UpdateAllTakeover.DispatchActive) return true;

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
            if (!UpdateAllTakeover.IsWorkerThread) return true;

            // On worker thread: skip sound play, return null SoundChannel
            __result = null;
            return false;
        }

        // ────────────────────────────────────────────────────────────
        //  Generic Monitor lock Prefix/Postfix generators
        // ────────────────────────────────────────────────────────────

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
