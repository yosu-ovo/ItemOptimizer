using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Caches HasStatusTag lookups per target per frame.
    /// The vanilla code scans the entire DurationList + DelayList for every HasStatusTag conditional check.
    /// With 2000+ conditionals and dozens of DurationList entries, this is O(conditionals * DurationList) per frame.
    /// This patch caches the result: first query for a target builds the tag set, subsequent queries are O(1).
    /// </summary>
    static class HasStatusTagCachePatch
    {
        // target -> set of status tags active on that target this frame
        private static readonly Dictionary<ISerializableEntity, HashSet<Identifier>> _cache = new();

        // Reflection accessor for StatusEffect.statusEffectTags (private readonly HashSet<Identifier>)
        private static readonly AccessTools.FieldRef<StatusEffect, HashSet<Identifier>> Ref_statusEffectTags =
            AccessTools.FieldRefAccess<StatusEffect, HashSet<Identifier>>("statusEffectTags");

        /// <summary>
        /// Called once per frame from Stats.EndFrame (via MapEntityUpdateAllPostfix) to invalidate cache.
        /// </summary>
        internal static void OnNewFrame()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Harmony Prefix for PropertyConditional.Matches(ISerializableEntity).
        /// Intercepts HasStatusTag checks and uses cached tag sets instead of scanning DurationList.
        /// </summary>
        public static bool Prefix(PropertyConditional __instance, ISerializableEntity target, ref bool __result)
        {
            if (!OptimizerConfig.EnableHasStatusTagCache) return true;
            if (__instance.Type != PropertyConditional.ConditionType.HasStatusTag) return true;

            // TargetContainedItem conditionals delegate to MatchesContained which iterates contained items
            // and calls MatchesDirect on each — let the original handle it
            if (__instance.TargetContainedItem) return true;

            if (target == null)
            {
                __result = __instance.ComparisonOperator == PropertyConditional.ComparisonOperatorType.NotEquals;
                Stats.HasStatusTagCacheHits++;
                return false;
            }

            var tagSet = GetOrBuildTagSet(target);

            int numTagsFound = 0;
            foreach (var tag in __instance.AttributeValueAsTags)
            {
                if (tagSet.Contains(tag))
                    numTagsFound++;
            }

            bool isNotEquals = __instance.ComparisonOperator == PropertyConditional.ComparisonOperatorType.NotEquals;
            __result = isNotEquals
                ? numTagsFound < __instance.AttributeValueAsTags.Length
                : numTagsFound >= __instance.AttributeValueAsTags.Length;

            Stats.HasStatusTagCacheHits++;
            return false;
        }

        private static HashSet<Identifier> GetOrBuildTagSet(ISerializableEntity target)
        {
            if (_cache.TryGetValue(target, out var existing))
                return existing;

            var tags = new HashSet<Identifier>();

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
            _cache.Clear();
        }
    }
}
