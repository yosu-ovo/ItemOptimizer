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
        private static MethodInfo ciUpdateOriginal;
        private static MethodInfo msUpdateOriginal;
        private static MethodInfo wearableUpdateOriginal;
        private static MethodInfo wdUpdateOriginal;
        private static MethodInfo doorUpdateOriginal;
        private static MethodInfo hasStatusTagOriginal;
        private static MethodInfo afflictionApplyOriginal;

        private static HarmonyMethod ciUpdatePrefix;
        private static HarmonyMethod msUpdatePrefix;
        private static HarmonyMethod wearableUpdatePrefix;
        private static HarmonyMethod wdUpdatePrefix;
        private static HarmonyMethod wdUpdatePostfix;
        private static HarmonyMethod doorUpdatePrefix;
        private static HarmonyMethod doorUpdatePostfix;
        private static HarmonyMethod hasStatusTagTranspiler;
        private static HarmonyMethod afflictionApplyPrefix;

        // Partial methods for platform-specific initialization
        partial void InitializeClient();
        partial void DisposeClient();
        partial void InitializeServer();
        partial void DisposeServer();

        public void PreInitPatching() { }

        public void Initialize()
        {
            Instance = this;

            try
            {
                OptimizerConfig.Load();
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError($"[ItemOptimizer] Config load failed, using defaults: {e.Message}");
            }

            try
            {
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

            // Thread safety patches (needed for parallel dispatch, must register before takeover)
            if (OptimizerConfig.EnableParallelDispatch)
            {
                if (ThreadSafetyAnalyzer.IsScanComplete)
                {
                    ThreadSafetyPatches.RegisterPatches(harmony);
                }
                else
                {
                    LuaCsLogger.Log("[ItemOptimizer] ParallelDispatch enabled in config but no safety scan found — disabling until scan is run.");
                    OptimizerConfig.EnableParallelDispatch = false;
                }
            }

            // Gap thread-safety patches (for MiscParallel: checkedHulls + outsideCollisionBlocker)
            if (OptimizerConfig.EnableMiscParallel)
                GapSafetyPatch.RegisterPatches(harmony);

            // UpdateAll takeover — replaces ItemUpdatePatch + ParallelDispatchPatch
            // Single prefix on MapEntity.UpdateAll, zero per-item Harmony overhead
            UpdateAllTakeover.Register(harmony);

            // Character stagger — enemy AI load distribution (shared: both server and client)
            if (OptimizerConfig.EnableCharacterStagger)
                CharacterStaggerPatch.Register(harmony);

            InitializeClient();
            InitializeServer();

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
                $"AnimLOD={OptimizerConfig.EnableAnimLOD}, " +
                $"CharStagger={OptimizerConfig.EnableCharacterStagger}(groups={OptimizerConfig.CharacterStaggerGroups}), " +
                $"LadderFix={OptimizerConfig.EnableLadderFix}, " +
                $"Parallel={OptimizerConfig.EnableParallelDispatch}, " +
                $"MiscParallel={OptimizerConfig.EnableMiscParallel}, " +
                $"ItemRules={OptimizerConfig.ItemRules.Count}, " +
                $"ModOpt={OptimizerConfig.ModOptLookup.Count}, " +
                $"ThreadSafetyCache={( ThreadSafetyAnalyzer.IsScanComplete ? $"loaded(safe={ThreadSafetyAnalyzer.CountSafe},cond={ThreadSafetyAnalyzer.CountConditional},unsafe={ThreadSafetyAnalyzer.CountUnsafe})" : "none" )}, " +
                $"ServerDedup={OptimizerConfig.EnableServerHashSetDedup}");
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError($"[ItemOptimizer] Initialize failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                DebugConsole.ThrowError($"[ItemOptimizer] Initialization error (mod will use defaults): {e.Message}", e);
            }
        }

        public void OnLoadCompleted()
        {
            // Safe to enable takeover now — all systems are initialized
            UpdateAllTakeover.Enabled = true;
            LuaCsLogger.Log("[ItemOptimizer] UpdateAllTakeover enabled (OnLoadCompleted)");
        }

        public void Dispose()
        {
            DisposeClient();
            DisposeServer();
            PerfCommands.Unregister();
            PerfProfiler.Reset();
            SpikeDetector.Reset();
            CharacterStaggerPatch.Unregister(harmony);
            UpdateAllTakeover.Unregister(harmony);
            ThreadSafetyPatches.UnregisterPatches(harmony);
            harmony?.UnpatchSelf();
            harmony = null;
            Stats.Reset();
            Instance = null;
        }

        private static void CacheMethodReferences()
        {
            ciUpdateOriginal = AccessTools.Method(typeof(CustomInterface), nameof(CustomInterface.Update));
            msUpdateOriginal = AccessTools.Method(typeof(MotionSensor), nameof(MotionSensor.Update));
            wearableUpdateOriginal = AccessTools.Method(typeof(Wearable), nameof(Wearable.Update));
            wdUpdateOriginal = AccessTools.Method(typeof(WaterDetector), nameof(WaterDetector.Update));
            doorUpdateOriginal = AccessTools.Method(typeof(Door), nameof(Door.Update));
            hasStatusTagOriginal = AccessTools.Method(typeof(PropertyConditional), nameof(PropertyConditional.Matches));
            afflictionApplyOriginal = AccessTools.Method(typeof(CharacterHealth), nameof(CharacterHealth.ApplyAffliction));

            ciUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(CustomInterfacePatch), nameof(CustomInterfacePatch.Prefix)));
            msUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(MotionSensorPatch), nameof(MotionSensorPatch.Prefix)));
            wearableUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(WearablePatch), nameof(WearablePatch.Prefix)));
            wdUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(WaterDetectorPatch), nameof(WaterDetectorPatch.Prefix)));
            wdUpdatePostfix = new HarmonyMethod(AccessTools.Method(typeof(WaterDetectorPatch), nameof(WaterDetectorPatch.Postfix)));
            doorUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(DoorPatch), nameof(DoorPatch.Prefix)));
            doorUpdatePostfix = new HarmonyMethod(AccessTools.Method(typeof(DoorPatch), nameof(DoorPatch.Postfix)));
            hasStatusTagTranspiler = new HarmonyMethod(AccessTools.Method(typeof(HasStatusTagCachePatch), nameof(HasStatusTagCachePatch.Transpiler)));
            afflictionApplyPrefix = new HarmonyMethod(AccessTools.Method(typeof(AfflictionDedupPatch), nameof(AfflictionDedupPatch.Prefix)));
        }

        private static void ApplyPatches()
        {
            // Item-level freeze/throttle is now handled by UpdateAllTakeover directly.
            // Only component-level patches remain as Harmony hooks.
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
            // HasStatusTagCache uses a transpiler (always applied — checks config flag at runtime).
            // This avoids per-call Harmony dispatch overhead for non-HasStatusTag Matches calls.
            if (hasStatusTagOriginal != null)
                harmony.Patch(hasStatusTagOriginal, transpiler: hasStatusTagTranspiler);
            if (OptimizerConfig.EnableAfflictionDedup)
                harmony.Patch(afflictionApplyOriginal, prefix: afflictionApplyPrefix);
        }

        // ── Toggle support (called from GUI) ──

        internal static void SetStrategyEnabled(string name, bool enabled)
        {
            switch (name)
            {
                case "cold_storage":
                    OptimizerConfig.EnableColdStorageSkip = enabled;
                    break;
                case "ground_item":
                    OptimizerConfig.EnableGroundItemThrottle = enabled;
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
                    // Transpiler is always applied; TryGetCached checks the config flag at runtime.
                    // No need to toggle the patch itself.
                    if (!enabled) HasStatusTagCachePatch.ClearCache();
                    break;
                case "affliction_dedup":
                    OptimizerConfig.EnableAfflictionDedup = enabled;
                    TogglePatch(afflictionApplyOriginal, afflictionApplyPrefix, enabled);
                    break;
                case "anim_lod":
                    OptimizerConfig.EnableAnimLOD = enabled;
                    // Prefix checks config flag at runtime — no need for TogglePatch
                    break;
                case "char_stagger":
                    OptimizerConfig.EnableCharacterStagger = enabled;
                    // Prefix checks config flag at runtime — no need for TogglePatch
                    break;
                case "ladder_fix":
                    OptimizerConfig.EnableLadderFix = enabled;
                    // Prefix/postfix checks config flag at runtime — no need for TogglePatch
                    break;
                case "platform_fix":
                    OptimizerConfig.EnablePlatformFix = enabled;
                    // Prefix/postfix checks config flag at runtime — no need for TogglePatch
                    break;
                case "parallel_dispatch":
                    if (enabled && !ThreadSafetyAnalyzer.IsScanComplete)
                    {
                        LuaCsLogger.Log("[ItemOptimizer] Cannot enable ParallelDispatch: thread safety scan not completed. Run scan first.");
                        OptimizerConfig.EnableParallelDispatch = false;
                        break;
                    }
                    OptimizerConfig.EnableParallelDispatch = enabled;
                    if (enabled)
                    {
                        ThreadSafetyPatches.RegisterPatches(harmony);
                        WorkerCrashLog.Initialize();
                        WorkerCrashLog.WriteSessionHeader();
                    }
                    else
                    {
                        ThreadSafetyPatches.UnregisterPatches(harmony);
                    }
                    break;
                case "server_hashset_dedup":
                    OptimizerConfig.EnableServerHashSetDedup = enabled;
                    // ServerOptimizer lives in Server/ — use reflection to avoid client compile error
                    ToggleServerOptimizer(enabled);
                    break;
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

        /// <summary>
        /// Toggle ServerOptimizer via reflection — avoids referencing Server-only type from Shared code.
        /// </summary>
        private static void ToggleServerOptimizer(bool enable)
        {
            var type = Type.GetType("ItemOptimizerMod.ServerOptimizer");
            if (type == null) return; // Not on server build
            var method = type.GetMethod(enable ? "RegisterPatches" : "UnregisterPatches",
                BindingFlags.Static | BindingFlags.NonPublic);
            method?.Invoke(null, new object[] { harmony });
        }
    }
}
