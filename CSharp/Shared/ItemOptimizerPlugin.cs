using System;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using ItemOptimizerMod.Patches;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    public sealed partial class ItemOptimizerPlugin : IAssemblyPlugin
    {
        private const string HarmonyId = "ItemOptimizerMod";
        internal static ItemOptimizerPlugin Instance;

        internal static Harmony harmony;

        // Cached method references for manual patching
        private static MethodInfo itemUpdateOriginal;
        private static MethodInfo ciUpdateOriginal;
        private static MethodInfo msUpdateOriginal;
        private static MethodInfo wearableUpdateOriginal;
        private static MethodInfo wdUpdateOriginal;
        private static MethodInfo doorUpdateOriginal;
        private static MethodInfo hasStatusTagOriginal;
        private static MethodInfo afflictionApplyOriginal;

        private static HarmonyMethod itemUpdatePrefix;
        private static HarmonyMethod ciUpdatePrefix;
        private static HarmonyMethod msUpdatePrefix;
        private static HarmonyMethod wearableUpdatePrefix;
        private static HarmonyMethod wdUpdatePrefix;
        private static HarmonyMethod wdUpdatePostfix;
        private static HarmonyMethod doorUpdatePrefix;
        private static HarmonyMethod doorUpdatePostfix;
        private static HarmonyMethod hasStatusTagPrefix;
        private static HarmonyMethod afflictionApplyPrefix;

        // Partial methods for client-side initialization (implemented in Client/)
        partial void InitializeClient();
        partial void DisposeClient();

        public void PreInitPatching() { }

        public void Initialize()
        {
            Instance = this;
            OptimizerConfig.Load();

            harmony?.UnpatchSelf();
            harmony = new Harmony(HarmonyId);

            CacheMethodReferences();
            ApplyPatches();
            PerfProfiler.RegisterPatches(harmony);
            PerfCommands.Register();

            // Spike detector — always-on per-item timing for diagnosing frame spikes
            SpikeDetector.Initialize(harmony);
            SpikeDetector.ThresholdMs = OptimizerConfig.SpikeThresholdMs;
            if (OptimizerConfig.EnableSpikeDetector)
                SpikeDetector.SetEnabled(true);

            // Parallel dispatch (experimental)
            if (OptimizerConfig.EnableParallelDispatch)
            {
                ThreadSafetyPatches.RegisterPatches(harmony);
                ParallelDispatchPatch.Register(harmony);
            }

            InitializeClient();

            LuaCsLogger.Log($"[ItemOptimizer] Initialized. " +
                $"ColdStorage={OptimizerConfig.EnableColdStorageSkip}, " +
                $"GroundItem={OptimizerConfig.EnableGroundItemThrottle}(skip={OptimizerConfig.GroundItemSkipFrames}), " +
                $"CI={OptimizerConfig.EnableCustomInterfaceThrottle}, " +
                $"Motion={OptimizerConfig.EnableMotionSensorThrottle}(skip={OptimizerConfig.MotionSensorSkipFrames}), " +
                $"Wearable={OptimizerConfig.EnableWearableThrottle}(skip={OptimizerConfig.WearableSkipFrames}), " +
                $"WaterDet={OptimizerConfig.EnableWaterDetectorThrottle}(skip={OptimizerConfig.WaterDetectorSkipFrames}), " +
                $"Door={OptimizerConfig.EnableDoorThrottle}(skip={OptimizerConfig.DoorSkipFrames}), " +
                $"HasStatusTagCache={OptimizerConfig.EnableHasStatusTagCache}, " +
                $"StatusHUD={OptimizerConfig.EnableStatusHUDThrottle}, " +
                $"AfflictionDedup={OptimizerConfig.EnableAfflictionDedup}, " +
                $"Parallel={OptimizerConfig.EnableParallelDispatch}, " +
                $"ItemRules={OptimizerConfig.ItemRules.Count}, " +
                $"ModOpt={OptimizerConfig.ModOptLookup.Count}, " +
                $"ThreadSafetyCache={( ThreadSafetyAnalyzer.IsScanComplete ? $"loaded(safe={ThreadSafetyAnalyzer.CountSafe},cond={ThreadSafetyAnalyzer.CountConditional},unsafe={ThreadSafetyAnalyzer.CountUnsafe})" : "none" )}");
        }

        public void OnLoadCompleted()
        {
            // Safe to enable parallel dispatch now — all systems are initialized
            if (OptimizerConfig.EnableParallelDispatch)
            {
                ParallelDispatchPatch.Enabled = true;
                LuaCsLogger.Log("[ItemOptimizer] Parallel dispatch enabled (OnLoadCompleted)");
            }
        }

        public void Dispose()
        {
            DisposeClient();
            PerfCommands.Unregister();
            PerfProfiler.Reset();
            SpikeDetector.Reset();
            ParallelDispatchPatch.Unregister(harmony);
            ThreadSafetyPatches.UnregisterPatches(harmony);
            harmony?.UnpatchSelf();
            harmony = null;
            Stats.Reset();
            Instance = null;
        }

        private static void CacheMethodReferences()
        {
            itemUpdateOriginal = AccessTools.Method(typeof(Item), nameof(Item.Update));
            ciUpdateOriginal = AccessTools.Method(typeof(CustomInterface), nameof(CustomInterface.Update));
            msUpdateOriginal = AccessTools.Method(typeof(MotionSensor), nameof(MotionSensor.Update));
            wearableUpdateOriginal = AccessTools.Method(typeof(Wearable), nameof(Wearable.Update));
            wdUpdateOriginal = AccessTools.Method(typeof(WaterDetector), nameof(WaterDetector.Update));
            doorUpdateOriginal = AccessTools.Method(typeof(Door), nameof(Door.Update));
            hasStatusTagOriginal = AccessTools.Method(typeof(PropertyConditional), nameof(PropertyConditional.Matches));
            afflictionApplyOriginal = AccessTools.Method(typeof(CharacterHealth), nameof(CharacterHealth.ApplyAffliction));

            itemUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(ItemUpdatePatch), nameof(ItemUpdatePatch.Prefix)));
            ciUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(CustomInterfacePatch), nameof(CustomInterfacePatch.Prefix)));
            msUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(MotionSensorPatch), nameof(MotionSensorPatch.Prefix)));
            wearableUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(WearablePatch), nameof(WearablePatch.Prefix)));
            wdUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(WaterDetectorPatch), nameof(WaterDetectorPatch.Prefix)));
            wdUpdatePostfix = new HarmonyMethod(AccessTools.Method(typeof(WaterDetectorPatch), nameof(WaterDetectorPatch.Postfix)));
            doorUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(DoorPatch), nameof(DoorPatch.Prefix)));
            doorUpdatePostfix = new HarmonyMethod(AccessTools.Method(typeof(DoorPatch), nameof(DoorPatch.Postfix)));
            hasStatusTagPrefix = new HarmonyMethod(AccessTools.Method(typeof(HasStatusTagCachePatch), nameof(HasStatusTagCachePatch.Prefix)));
            afflictionApplyPrefix = new HarmonyMethod(AccessTools.Method(typeof(AfflictionDedupPatch), nameof(AfflictionDedupPatch.Prefix)));
        }

        private static void ApplyPatches()
        {
            // Item.Update prefix is shared by cold_storage, ground_item, per-item rules, and mod optimization
            if (OptimizerConfig.EnableColdStorageSkip
             || OptimizerConfig.EnableGroundItemThrottle
             || OptimizerConfig.RuleLookup.Count > 0
             || OptimizerConfig.ModOptLookup.Count > 0)
            {
                harmony.Patch(itemUpdateOriginal, prefix: itemUpdatePrefix);
                _itemPatchAttached = true;
            }
            if (OptimizerConfig.EnableCustomInterfaceThrottle)
                harmony.Patch(ciUpdateOriginal, prefix: ciUpdatePrefix);
            if (OptimizerConfig.EnableMotionSensorThrottle)
                harmony.Patch(msUpdateOriginal, prefix: msUpdatePrefix);
            if (OptimizerConfig.EnableWearableThrottle)
                harmony.Patch(wearableUpdateOriginal, prefix: wearableUpdatePrefix);
            if (OptimizerConfig.EnableWaterDetectorThrottle)
                harmony.Patch(wdUpdateOriginal, prefix: wdUpdatePrefix, postfix: wdUpdatePostfix);
            if (OptimizerConfig.EnableDoorThrottle)
                harmony.Patch(doorUpdateOriginal, prefix: doorUpdatePrefix, postfix: doorUpdatePostfix);
            if (OptimizerConfig.EnableHasStatusTagCache)
                harmony.Patch(hasStatusTagOriginal, prefix: hasStatusTagPrefix);
            if (OptimizerConfig.EnableAfflictionDedup)
                harmony.Patch(afflictionApplyOriginal, prefix: afflictionApplyPrefix);

            // Stats frame counter — always active (Last priority so it runs AFTER
            // PerfProfiler and ParallelDispatchPatch postfixes have read raw counters)
            harmony.Patch(
                AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ItemOptimizerPlugin), nameof(MapEntityUpdateAllPostfix)))
                    { priority = Priority.Last });
        }

        // ── Harmony callbacks ──

        private static void MapEntityUpdateAllPostfix()
        {
            Stats.EndFrame();
        }

        // ── Toggle support (called from GUI) ──

        internal static void SetStrategyEnabled(string name, bool enabled)
        {
            switch (name)
            {
                case "cold_storage":
                    OptimizerConfig.EnableColdStorageSkip = enabled;
                    SyncItemUpdatePatch();
                    break;
                case "ground_item":
                    OptimizerConfig.EnableGroundItemThrottle = enabled;
                    SyncItemUpdatePatch();
                    break;
                case "ci_throttle":
                    OptimizerConfig.EnableCustomInterfaceThrottle = enabled;
                    TogglePatch(ciUpdateOriginal, ciUpdatePrefix, enabled);
                    break;
                case "motion":
                    OptimizerConfig.EnableMotionSensorThrottle = enabled;
                    TogglePatch(msUpdateOriginal, msUpdatePrefix, enabled);
                    break;
                case "wearable":
                    OptimizerConfig.EnableWearableThrottle = enabled;
                    TogglePatch(wearableUpdateOriginal, wearableUpdatePrefix, enabled);
                    break;
                case "water_detector":
                    OptimizerConfig.EnableWaterDetectorThrottle = enabled;
                    TogglePatchWithPostfix(wdUpdateOriginal, wdUpdatePrefix, wdUpdatePostfix, enabled);
                    break;
                case "door":
                    OptimizerConfig.EnableDoorThrottle = enabled;
                    TogglePatchWithPostfix(doorUpdateOriginal, doorUpdatePrefix, doorUpdatePostfix, enabled);
                    break;
                case "has_status_tag_cache":
                    OptimizerConfig.EnableHasStatusTagCache = enabled;
                    TogglePatch(hasStatusTagOriginal, hasStatusTagPrefix, enabled);
                    if (!enabled) HasStatusTagCachePatch.ClearCache();
                    break;
                case "affliction_dedup":
                    OptimizerConfig.EnableAfflictionDedup = enabled;
                    TogglePatch(afflictionApplyOriginal, afflictionApplyPrefix, enabled);
                    break;
                case "parallel_dispatch":
                    OptimizerConfig.EnableParallelDispatch = enabled;
                    if (enabled)
                    {
                        ThreadSafetyPatches.RegisterPatches(harmony);
                        ParallelDispatchPatch.Register(harmony);
                        ParallelDispatchPatch.Enabled = true; // safe — toggled from GUI means game is loaded
                    }
                    else
                    {
                        ParallelDispatchPatch.Unregister(harmony);
                        ThreadSafetyPatches.UnregisterPatches(harmony);
                    }
                    break;
            }
        }

        /// <summary>
        /// ItemUpdatePatch.Prefix is shared by cold_storage and per-item rules.
        /// Keep the patch attached if EITHER feature needs it; remove only when neither does.
        /// </summary>
        private static bool _itemPatchAttached;

        internal static void SyncItemUpdatePatch()
        {
            bool needed = OptimizerConfig.EnableColdStorageSkip
                       || OptimizerConfig.EnableGroundItemThrottle
                       || OptimizerConfig.RuleLookup.Count > 0
                       || OptimizerConfig.ModOptLookup.Count > 0;
            if (needed && !_itemPatchAttached)
            {
                harmony.Patch(itemUpdateOriginal, prefix: itemUpdatePrefix);
                _itemPatchAttached = true;
            }
            else if (!needed && _itemPatchAttached)
            {
                harmony.Unpatch(itemUpdateOriginal, itemUpdatePrefix.method);
                _itemPatchAttached = false;
            }
        }

        private static void TogglePatch(MethodInfo original, HarmonyMethod prefix, bool enable)
        {
            if (enable)
                harmony.Patch(original, prefix: prefix);
            else
                harmony.Unpatch(original, prefix.method);
        }

        private static void TogglePatchWithPostfix(MethodInfo original, HarmonyMethod prefix, HarmonyMethod postfix, bool enable)
        {
            if (enable)
            {
                harmony.Patch(original, prefix: prefix, postfix: postfix);
            }
            else
            {
                harmony.Unpatch(original, prefix.method);
                harmony.Unpatch(original, postfix.method);
            }
        }
    }
}
