using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    static class ItemUpdatePatch
    {
        private static readonly ConditionalWeakTable<Item, StrongBox<int>> ThrottleCounters = new();
        // Cache whether an item is holdable (truly portable): 0=unknown, 1=yes, -1=no
        private static readonly ConditionalWeakTable<Item, StrongBox<int>> HoldableCache = new();
        // Cache IsNotInActiveUse result per-item, invalidated each frame via generation counter
        private static readonly ConditionalWeakTable<Item, ActiveUseCache> ActiveUseCacheTable = new();
        private static int _frameGeneration;

        private class ActiveUseCache
        {
            public int Generation;
            public bool NotInActiveUse;
        }

        /// <summary>Call once per frame to invalidate active-use caches.</summary>
        internal static void NewFrame()
        {
            _frameGeneration++;
        }

        /// <summary>
        /// This prefix is always attached when ANY of cold_storage, ground_item, or per-item rules is active.
        /// Each strategy checks its own config flag at runtime.
        /// </summary>
        public static bool Prefix(Item __instance)
        {
            // Strategy 1: Cold Storage Skip — items in non-character containers
            if (OptimizerConfig.EnableColdStorageSkip && ColdStorageDetector.IsInColdStorage(__instance))
            {
                Stats.ColdStorageSkips++;
                return false;
            }

            // Strategy 2: Ground Item Throttle — loose holdable items on the ground
            // Only items with Holdable component (truly portable items), NOT furniture/fixtures (chairs, beds, etc.)
            if (OptimizerConfig.EnableGroundItemThrottle
                && __instance.ParentInventory == null
                && IsHoldable(__instance))
            {
                var counter = ThrottleCounters.GetOrCreateValue(__instance);
                counter.Value++;
                if (counter.Value % OptimizerConfig.GroundItemSkipFrames != 0)
                {
                    Stats.GroundItemSkips++;
                    return false;
                }
            }

            // Shared identifier lookup for Strategy 5 + 6
            string identifier = null;
            bool identifierFetched = false;

            // Strategy 5: Per-Item Rules (manual, user-defined — takes priority over ModOpt)
            if (OptimizerConfig.RuleLookup.Count > 0)
            {
                identifier = __instance.Prefab?.Identifier.Value;
                identifierFetched = true;
                if (identifier != null && OptimizerConfig.RuleLookup.TryGetValue(identifier, out var rule))
                {
                    if (CheckRuleCondition(__instance, rule.Condition))
                    {
                        if (rule.Action == ItemRuleAction.Skip)
                        {
                            Stats.ItemRuleSkips++;
                            return false;
                        }

                        if (rule.Action == ItemRuleAction.Throttle)
                        {
                            var counter = ThrottleCounters.GetOrCreateValue(__instance);
                            counter.Value++;
                            if (counter.Value % rule.SkipFrames != 0)
                            {
                                Stats.ItemRuleSkips++;
                                return false;
                            }
                        }
                    }
                    return true; // manual rule matched — skip ModOpt for this item
                }
            }

            // Strategy 6: Mod Optimization (tier-based bulk throttle, notInActiveUse)
            if (OptimizerConfig.ModOptLookup.Count > 0)
            {
                if (!identifierFetched)
                    identifier = __instance.Prefab?.Identifier.Value;

                if (identifier != null && OptimizerConfig.ModOptLookup.TryGetValue(identifier, out var skipFrames))
                {
                    if (IsModOptEligibleCached(__instance))
                    {
                        var counter = ThrottleCounters.GetOrCreateValue(__instance);
                        counter.Value++;
                        if (counter.Value % skipFrames != 0)
                        {
                            Stats.ModOptSkips++;
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>Cached per-frame version of IsModOptEligible.</summary>
        private static bool IsModOptEligibleCached(Item item)
        {
            var cache = ActiveUseCacheTable.GetOrCreateValue(item);
            if (cache.Generation == _frameGeneration)
                return cache.NotInActiveUse;
            cache.Generation = _frameGeneration;
            cache.NotInActiveUse = ColdStorageDetector.IsModOptEligible(item);
            return cache.NotInActiveUse;
        }

        private static bool CheckRuleCondition(Item item, string condition)
        {
            switch (condition)
            {
                case "coldStorage":
                    return ColdStorageDetector.IsInColdStorage(item);
                case "notInActiveUse":
                    return ColdStorageDetector.IsNotInActiveUse(item);
                case "always":
                default:
                    return true;
            }
        }

        private static bool IsHoldable(Item item)
        {
            var cached = HoldableCache.GetOrCreateValue(item);
            if (cached.Value != 0) return cached.Value > 0;
            cached.Value = item.GetComponent<Holdable>() != null ? 1 : -1;
            return cached.Value > 0;
        }
    }
}
