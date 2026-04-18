using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Caches HasStatusTag lookups per target per frame.
    ///
    /// Uses a TRANSPILER instead of prefix to eliminate per-call Harmony overhead:
    /// - Prefix: ~0.3-1.0us overhead on EVERY PropertyConditional.Matches call (thousands/frame)
    /// - Transpiler: inserts a branch at the start of the IL; non-HasStatusTag calls pay only
    ///   ~3ns for one field load + compare + branch-not-taken.
    ///
    /// The vanilla code scans the entire DurationList + DelayList for every HasStatusTag conditional check.
    /// With 2000+ conditionals and dozens of DurationList entries, this is O(conditionals * DurationList) per frame.
    /// This patch caches the result: first query for a target builds the tag set, subsequent queries are O(1).
    /// </summary>
    static class HasStatusTagCachePatch
    {
        // target -> set of status tags active on that target this frame
        private static readonly Dictionary<ISerializableEntity, HashSet<Identifier>> _cache = new();

        private static readonly HashSet<Identifier> EmptySet = new();

        // Pool of reusable HashSets to avoid per-frame GC pressure.
        // On frame start, all cache entries are returned to the pool instead of discarded.
        private static readonly List<HashSet<Identifier>> _pool = new();

        // When true, the cache is fully populated for this frame — any target not in _cache
        // has no active status tags. This enables lock-free reads during parallel dispatch.
        private static bool _allBuilt;

        // Reflection accessor for StatusEffect.statusEffectTags (private readonly HashSet<Identifier>)
        private static readonly AccessTools.FieldRef<StatusEffect, HashSet<Identifier>> Ref_statusEffectTags =
            AccessTools.FieldRefAccess<StatusEffect, HashSet<Identifier>>("statusEffectTags");

        /// <summary>
        /// Called once per frame from Stats.EndFrame (via MapEntityUpdateAllPostfix) to invalidate cache.
        /// </summary>
        internal static void OnNewFrame()
        {
            // Return all HashSets to the pool instead of discarding — avoids GC pressure
            foreach (var kvp in _cache)
            {
                kvp.Value.Clear();
                _pool.Add(kvp.Value);
            }
            _cache.Clear();
            _allBuilt = false;
        }

        /// <summary>
        /// Pre-build the entire cache from DurationList + DelayList.
        /// Call this BEFORE parallel dispatch starts so that during dispatch,
        /// all TryGetCached calls are pure dictionary reads (thread-safe, no locks needed).
        /// </summary>
        internal static void PreBuildAll()
        {
            if (!OptimizerConfig.EnableHasStatusTagCache) return;
            // OnNewFrame() already cleared and pooled — no need to clear again

            foreach (var durationEffect in StatusEffect.DurationList)
            {
                var effectTags = Ref_statusEffectTags(durationEffect.Parent);
                if (effectTags == null || effectTags.Count == 0) continue;
                foreach (var target in durationEffect.Targets)
                {
                    if (!_cache.TryGetValue(target, out var tags))
                    {
                        tags = RentFromPool();
                        _cache[target] = tags;
                    }
                    foreach (var tag in effectTags)
                        tags.Add(tag);
                }
            }

            foreach (var delayedEffect in DelayedEffect.DelayList)
            {
                var effectTags = Ref_statusEffectTags(delayedEffect.Parent);
                if (effectTags == null || effectTags.Count == 0) continue;
                foreach (var target in delayedEffect.Targets)
                {
                    if (!_cache.TryGetValue(target, out var tags))
                    {
                        tags = RentFromPool();
                        _cache[target] = tags;
                    }
                    foreach (var tag in effectTags)
                        tags.Add(tag);
                }
            }

            _allBuilt = true;
        }

        private static HashSet<Identifier> RentFromPool()
        {
            int count = _pool.Count;
            if (count > 0)
            {
                var set = _pool[count - 1];
                _pool.RemoveAt(count - 1);
                return set;
            }
            return new HashSet<Identifier>();
        }

        /// <summary>
        /// Harmony Transpiler for PropertyConditional.Matches(ISerializableEntity).
        /// Injects a fast-path check at the very start of the method:
        ///   int r = TryGetCached(this, target);
        ///   if (r >= 0) return r != 0;
        ///   // ... original code ...
        ///
        /// Non-HasStatusTag calls hit one static call + branch-not-taken ≈ negligible overhead.
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var skipLabel = generator.DefineLabel();

            // Call TryGetCached(this, target) -> returns int (-1 = not handled, 0 = false, 1 = true)
            yield return new CodeInstruction(OpCodes.Ldarg_0);  // this (PropertyConditional)
            yield return new CodeInstruction(OpCodes.Ldarg_1);  // target (ISerializableEntity)
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(HasStatusTagCachePatch), nameof(TryGetCached)));
            // Stack: [int result]
            yield return new CodeInstruction(OpCodes.Dup);
            // Stack: [int result, int result]
            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
            yield return new CodeInstruction(OpCodes.Blt_S, skipLabel); // if result < 0, skip to original
            // Stack: [int result]  — result is 0 or 1, convert to bool and return
            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
            yield return new CodeInstruction(OpCodes.Cgt);  // 1 > 0 = true, 0 > 0 = false
            yield return new CodeInstruction(OpCodes.Ret);

            // skipLabel: pop the duplicated -1 and continue with original code
            var originalInstructions = instructions.ToList();
            var popInstr = new CodeInstruction(OpCodes.Pop);
            popInstr.labels.Add(skipLabel);
            yield return popInstr;

            // Transfer any labels from the first original instruction to our pop
            // (exception handlers, etc. that target the method start)
            if (originalInstructions.Count > 0 && originalInstructions[0].labels.Count > 0)
            {
                popInstr.labels.AddRange(originalInstructions[0].labels);
                originalInstructions[0].labels.Clear();
            }

            foreach (var instr in originalInstructions)
                yield return instr;
        }

        /// <summary>
        /// Called from transpiled IL. Returns:
        ///   -1 = not handled (run original code)
        ///    0 = result is false
        ///    1 = result is true
        /// </summary>
        public static int TryGetCached(PropertyConditional instance, ISerializableEntity target)
        {
            if (!OptimizerConfig.EnableHasStatusTagCache) return -1;
            if (instance.Type != PropertyConditional.ConditionType.HasStatusTag) return -1;
            if (instance.TargetContainedItem) return -1;

            if (target == null)
            {
                Stats.HasStatusTagCacheHits++;
                return instance.ComparisonOperator == PropertyConditional.ComparisonOperatorType.NotEquals ? 1 : 0;
            }

            var tagSet = GetOrBuildTagSet(target);

            int numTagsFound = 0;
            foreach (var tag in instance.AttributeValueAsTags)
            {
                if (tagSet.Contains(tag))
                    numTagsFound++;
            }

            bool isNotEquals = instance.ComparisonOperator == PropertyConditional.ComparisonOperatorType.NotEquals;
            bool result = isNotEquals
                ? numTagsFound < instance.AttributeValueAsTags.Length
                : numTagsFound >= instance.AttributeValueAsTags.Length;

            Stats.HasStatusTagCacheHits++;
            return result ? 1 : 0;
        }

        private static HashSet<Identifier> GetOrBuildTagSet(ISerializableEntity target)
        {
            if (_cache.TryGetValue(target, out var existing))
                return existing;

            // If PreBuildAll was already called, the cache is complete —
            // any target not in the cache has no active status tags.
            // This avoids iterating DurationList/DelayList during parallel dispatch.
            if (_allBuilt)
                return EmptySet;

            var tags = RentFromPool();

            // Scan DurationList — same logic as vanilla PropertyConditional.cs:460-468
            foreach (var durationEffect in StatusEffect.DurationList)
            {
                if (!durationEffect.Targets.Contains(target)) continue;
                var effectTags = Ref_statusEffectTags(durationEffect.Parent);
                if (effectTags != null)
                {
                    foreach (var tag in effectTags)
                        tags.Add(tag);
                }
            }

            // Scan DelayList — same logic as vanilla PropertyConditional.cs:471-479
            foreach (var delayedEffect in DelayedEffect.DelayList)
            {
                if (!delayedEffect.Targets.Contains(target)) continue;
                var effectTags = Ref_statusEffectTags(delayedEffect.Parent);
                if (effectTags != null)
                {
                    foreach (var tag in effectTags)
                        tags.Add(tag);
                }
            }

            _cache[target] = tags;
            return tags;
        }

        internal static void ClearCache()
        {
            foreach (var kvp in _cache)
            {
                kvp.Value.Clear();
                _pool.Add(kvp.Value);
            }
            _cache.Clear();
            _allBuilt = false;
        }
    }
}
