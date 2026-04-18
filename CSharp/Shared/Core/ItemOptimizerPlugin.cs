using System;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using ItemOptimizerMod.Patches;
using ItemOptimizerMod.SignalGraph;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    public sealed partial class ItemOptimizerPlugin : IAssemblyPlugin
    {
        private const string HarmonyId = "ItemOptimizerMod";
        internal static ItemOptimizerPlugin Instance;

        // Dedup guard: multiplayer may fire our Harmony postfix from both CL+SV threads.
        private static bool _roundInitialized;

        internal static Harmony harmony;

        // Cached method references for manual patching
        private static MethodInfo ciUpdateOriginal;
        private static MethodInfo msUpdateOriginal;
        private static MethodInfo wearableUpdateOriginal;
        private static MethodInfo wdUpdateOriginal;
        private static MethodInfo doorUpdateOriginal;
        private static MethodInfo hasStatusTagOriginal;
        private static MethodInfo afflictionApplyOriginal;
        private static MethodInfo btUpdateOriginal;
        private static MethodInfo pumpUpdateOriginal;

        private static HarmonyMethod ciUpdatePrefix;
        private static HarmonyMethod msUpdatePrefix;
        private static HarmonyMethod wearableUpdatePrefix;
        private static HarmonyMethod wdUpdatePrefix;
        private static HarmonyMethod wdUpdatePostfix;
        private static HarmonyMethod doorUpdatePrefix;
        private static HarmonyMethod doorUpdatePostfix;
        private static HarmonyMethod hasStatusTagTranspiler;
        private static HarmonyMethod afflictionApplyPrefix;
        private static HarmonyMethod btUpdatePrefix;
        private static HarmonyMethod pumpUpdateTranspiler;
        private static MethodInfo itemUpdateOriginal;
        private static HarmonyMethod itemUpdateTranspiler;

        // Partial methods for platform-specific initialization
        partial void InitializeClient();
        partial void DisposeClient();
        partial void InitializeServer();
        partial void DisposeServer();
        partial void RegisterProxyHandlers();

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

            // Gap thread-safety patches (for MiscParallel: checkedHulls + outsideCollisionBlocker)
            if (OptimizerConfig.EnableMiscParallel)
                GapSafetyPatch.RegisterPatches(harmony);

            // UpdateAll takeover — replaces ItemUpdatePatch + ParallelDispatchPatch
            // Single prefix on MapEntity.UpdateAll, zero per-item Harmony overhead
            UpdateAllTakeover.Register(harmony);

            // Signal graph accelerator — compile signal circuit to register-based eval
            if (OptimizerConfig.SignalGraphMode > 0)
                SignalGraphPatches.Register(harmony);

            // MotionSensor/WaterDetector complete rewrites
            if (OptimizerConfig.EnableMotionSensorRewrite)
                MotionSensorRewrite.Register(harmony);
            if (OptimizerConfig.EnableWaterDetectorRewrite)
                WaterDetectorRewrite.Register(harmony);

            // Power system rewrites
            if (OptimizerConfig.EnableRelayRewrite)
                RelayRewrite.Register(harmony);
            if (OptimizerConfig.EnablePowerTransferRewrite)
                PowerTransferRewrite.Register(harmony);
            if (OptimizerConfig.EnablePowerContainerRewrite)
                PowerContainerRewrite.Register(harmony);

            // Character stagger — enemy AI load distribution (shared: both server and client)
            if (OptimizerConfig.EnableCharacterStagger)
                CharacterStaggerPatch.Register(harmony);

            // Character zone skip — freeze NPCs in Dormant/Unloaded zones (always registered, gated by runtime flag)
            CharacterZoneSkipPatch.Register(harmony);

            // Round lifecycle — own Harmony patches instead of LuaCs IEventRoundStarted/Ended
            // (LuaCs event dispatch is unreliable in some versions)
            RegisterRoundLifecyclePatches();

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
                $"MiscParallel={OptimizerConfig.EnableMiscParallel}, " +
                $"ItemRules={OptimizerConfig.ItemRules.Count}, " +
                $"ModOpt={OptimizerConfig.ModOptLookup.Count}, " +
                $"ServerDedup={OptimizerConfig.EnableServerHashSetDedup}, " +
                $"MotionRewrite={OptimizerConfig.EnableMotionSensorRewrite}, " +
                $"WaterDetRewrite={OptimizerConfig.EnableWaterDetectorRewrite}, " +
                $"RelayRewrite={OptimizerConfig.EnableRelayRewrite}, " +
                $"PowerTransferRewrite={OptimizerConfig.EnablePowerTransferRewrite}, " +
                $"PowerContainerRewrite={OptimizerConfig.EnablePowerContainerRewrite}, " +
                $"NativeRuntime={OptimizerConfig.EnableNativeRuntime}");
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
            RegisterProxyHandlers();

            // Signal graph: set mode early, but defer Compile() to OnRoundStart()
            // when submarine items actually exist in Item.ItemList
            if (OptimizerConfig.SignalGraphMode > 0)
            {
                SignalGraphEvaluator.SetMode(OptimizerConfig.SignalGraphMode);
            }

            LuaCsLogger.Log("[ItemOptimizer] UpdateAllTakeover enabled (OnLoadCompleted)");
        }

        // ═══ Round Lifecycle — own Harmony patches ═══
        // LuaCs IEventRoundStarted/IEventRoundEnded dispatch is unreliable
        // (depends on LuaCs version / event service init). Direct Harmony is bulletproof.

        private static void RegisterRoundLifecyclePatches()
        {
            // PostFix on GameSession.StartRound(LevelData, bool, SubmarineInfo, SubmarineInfo)
            var startRound = AccessTools.Method(typeof(GameSession), nameof(GameSession.StartRound),
                new[] { typeof(LevelData), typeof(bool), typeof(SubmarineInfo), typeof(SubmarineInfo) });
            if (startRound != null)
            {
                harmony.Patch(startRound,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(ItemOptimizerPlugin), nameof(OnRoundStartPostfix))));
                LuaCsLogger.Log("[ItemOptimizer] Round lifecycle: StartRound postfix registered");
            }
            else
            {
                LuaCsLogger.LogError("[ItemOptimizer] Round lifecycle: GameSession.StartRound not found!");
            }

            // Prefix on GameSession.EndRound (entities still alive)
            var endRound = AccessTools.Method(typeof(GameSession), nameof(GameSession.EndRound));
            if (endRound != null)
            {
                harmony.Patch(endRound,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(ItemOptimizerPlugin), nameof(OnRoundEndPrefix))));
                LuaCsLogger.Log("[ItemOptimizer] Round lifecycle: EndRound prefix registered");
            }
            else
            {
                LuaCsLogger.LogError("[ItemOptimizer] Round lifecycle: GameSession.EndRound not found!");
            }
        }

        private static void OnRoundStartPostfix()
        {
            DebugConsole.NewMessage($"[ItemOptimizer] OnRoundStart (initialized={_roundInitialized})", Color.Cyan);
            if (_roundInitialized) return;
            _roundInitialized = true;

            DebugConsole.NewMessage("[ItemOptimizer] Clearing caches + initializing for new round", Color.LimeGreen);

            // ── Clear all entity-ID-indexed caches (stale from previous round) ──
            UpdateAllTakeover.ClearItemCaches();
            MotionSensorRewrite.Reset();
            HullCharacterTracker.Reset();
            WaterDetectorRewrite.Reset();
            RelayRewrite.Reset();
            PowerTransferRewrite.Reset();
            PowerContainerRewrite.Reset();
            Proxy.ProxyRegistry.ClearStaleAttachments();

            // ── Signal graph: recompile for new round's items ──
            if (OptimizerConfig.SignalGraphMode > 0)
            {
                SignalGraphEvaluator.Compile();
            }

            // ── NativeRuntime lifecycle ──
            World.NativeRuntimeBridge.OnRoundStart();
        }

        private static void OnRoundEndPrefix()
        {
            DebugConsole.NewMessage($"[ItemOptimizer] OnRoundEnd (initialized={_roundInitialized})", Color.Cyan);
            if (!_roundInitialized) return;
            _roundInitialized = false;

            DebugConsole.NewMessage("[ItemOptimizer] Cleaning up NativeRuntime", Color.Yellow);

            if (World.NativeRuntimeBridge.IsEnabled)
                World.NativeRuntimeBridge.OnRoundEnd();
        }

        public void Dispose()
        {
            _roundInitialized = false;
            DisposeClient();
            DisposeServer();
            World.NativeRuntimeBridge.OnRoundEnd();
            PerfCommands.Unregister();
            PerfProfiler.Reset();
            SpikeDetector.Reset();
            CharacterStaggerPatch.Unregister(harmony);
            CharacterZoneSkipPatch.Unregister(harmony);
            MotionSensorRewrite.Reset();
            MotionSensorRewrite.Unregister(harmony);
            HullCharacterTracker.Reset();
            WaterDetectorRewrite.Reset();
            WaterDetectorRewrite.Unregister(harmony);
            RelayRewrite.Reset();
            RelayRewrite.Unregister(harmony);
            PowerTransferRewrite.Reset();
            PowerTransferRewrite.Unregister(harmony);
            PowerContainerRewrite.Reset();
            PowerContainerRewrite.Unregister(harmony);
            SignalGraphEvaluator.Reset();
            SignalGraphPatches.Unregister(harmony);
            UpdateAllTakeover.Unregister(harmony);
            Proxy.ProxyRegistry.DetachAll();
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

            btUpdateOriginal = AccessTools.Method(typeof(ButtonTerminal), nameof(ButtonTerminal.Update));
            btUpdatePrefix = new HarmonyMethod(AccessTools.Method(typeof(ButtonTerminalPatch), nameof(ButtonTerminalPatch.Prefix)));
            pumpUpdateOriginal = AccessTools.Method(typeof(Pump), nameof(Pump.Update));
            pumpUpdateTranspiler = new HarmonyMethod(AccessTools.Method(typeof(PumpPatch), nameof(PumpPatch.Transpiler)));

            itemUpdateOriginal = AccessTools.Method(typeof(Item), nameof(Item.Update),
                new[] { typeof(float), typeof(Camera) });
            if (ItemUpdateTranspiler.CanPatch)
                itemUpdateTranspiler = new HarmonyMethod(AccessTools.Method(typeof(ItemUpdateTranspiler), nameof(ItemUpdateTranspiler.Transpiler)));
        }

        private static void ApplyPatches()
        {
            // Item-level freeze/throttle is now handled by UpdateAllTakeover directly.
            // Only component-level patches remain as Harmony hooks.
            if (OptimizerConfig.EnableCustomInterfaceThrottle)
                harmony.Patch(ciUpdateOriginal, prefix: ciUpdatePrefix);
            // Old MotionSensor/WaterDetector throttle patches are superseded by rewrites.
            // Only register old patches if rewrites are disabled — avoids stacking
            // multiple Harmony prefixes on the same method (dispatch overhead).
            if (OptimizerConfig.EnableMotionSensorThrottle && !OptimizerConfig.EnableMotionSensorRewrite)
                harmony.Patch(msUpdateOriginal, prefix: msUpdatePrefix);
            if (OptimizerConfig.EnableWearableThrottle)
                harmony.Patch(wearableUpdateOriginal, prefix: wearableUpdatePrefix);
            if (OptimizerConfig.EnableWaterDetectorThrottle && !OptimizerConfig.EnableWaterDetectorRewrite)
                harmony.Patch(wdUpdateOriginal, prefix: wdUpdatePrefix, postfix: wdUpdatePostfix);
            if (OptimizerConfig.EnableDoorThrottle)
                harmony.Patch(doorUpdateOriginal, prefix: doorUpdatePrefix, postfix: doorUpdatePostfix);
            // HasStatusTagCache uses a transpiler (always applied — checks config flag at runtime).
            // This avoids per-call Harmony dispatch overhead for non-HasStatusTag Matches calls.
            if (hasStatusTagOriginal != null)
                harmony.Patch(hasStatusTagOriginal, transpiler: hasStatusTagTranspiler);
            if (OptimizerConfig.EnableAfflictionDedup)
                harmony.Patch(afflictionApplyOriginal, prefix: afflictionApplyPrefix);
            if (OptimizerConfig.EnableButtonTerminalOpt && btUpdateOriginal != null)
                harmony.Patch(btUpdateOriginal, prefix: btUpdatePrefix);
            if (OptimizerConfig.EnablePumpOpt && pumpUpdateOriginal != null)
                harmony.Patch(pumpUpdateOriginal, transpiler: pumpUpdateTranspiler);
            // Item.Update transpiler: wraps ApplyStatusEffects with hasStatusEffectsOfType[] check.
            // Always applied (like HasStatusTagCache) — the wrapper is a no-op when effects exist.
            if (itemUpdateOriginal != null && itemUpdateTranspiler != null)
                harmony.Patch(itemUpdateOriginal, transpiler: itemUpdateTranspiler);
        }

        // ── Toggle support (called from GUI) ──

        internal static void SetStrategyEnabled(string name, bool enabled)
        {
            SetStrategyValue(name, enabled ? 1 : 0);
        }

        internal static void SetStrategyValue(string name, int value)
        {
            switch (name)
            {
                case "cold_storage":
                    OptimizerConfig.EnableColdStorageSkip = value > 0;
                    break;
                case "ground_item":
                    OptimizerConfig.EnableGroundItemThrottle = value > 0;
                    break;
                case "ci_throttle":
                    OptimizerConfig.EnableCustomInterfaceThrottle = value > 0;
                    TogglePatch(ciUpdateOriginal, ciUpdatePrefix, value > 0);
                    break;
                case "motion":
                    OptimizerConfig.EnableMotionSensorThrottle = value > 0;
                    TogglePatch(msUpdateOriginal, msUpdatePrefix, value > 0);
                    break;
                case "wearable":
                    OptimizerConfig.EnableWearableThrottle = value > 0;
                    TogglePatch(wearableUpdateOriginal, wearableUpdatePrefix, value > 0);
                    break;
                case "water_detector":
                    OptimizerConfig.EnableWaterDetectorThrottle = value > 0;
                    TogglePatchWithPostfix(wdUpdateOriginal, wdUpdatePrefix, wdUpdatePostfix, value > 0);
                    break;
                case "door":
                    OptimizerConfig.EnableDoorThrottle = value > 0;
                    TogglePatchWithPostfix(doorUpdateOriginal, doorUpdatePrefix, doorUpdatePostfix, value > 0);
                    break;
                case "has_status_tag_cache":
                    OptimizerConfig.EnableHasStatusTagCache = value > 0;
                    if (value == 0) HasStatusTagCachePatch.ClearCache();
                    break;
                case "affliction_dedup":
                    OptimizerConfig.EnableAfflictionDedup = value > 0;
                    TogglePatch(afflictionApplyOriginal, afflictionApplyPrefix, value > 0);
                    break;
                case "wire_skip":
                    OptimizerConfig.EnableWireSkip = value > 0;
                    break;
                case "motion_rewrite":
                {
                    bool enable = value > 0;
                    // Validate: turning OFF may cascade-disable NativeRuntime
                    var cascades = ToggleValidator.ValidateChange("motion_rewrite", enable);
                    if (cascades == null) break; // blocked
                    // Apply cascades before the actual change
                    foreach (var (t, v) in cascades)
                        SetStrategyValue(t, v ? 1 : 0);

                    OptimizerConfig.EnableMotionSensorRewrite = enable;
                    if (enable)
                    {
                        // Unregister old throttle patch to eliminate stacked Harmony overhead
                        harmony.Unpatch(msUpdateOriginal, msUpdatePrefix.method);
                        MotionSensorRewrite.Register(harmony);
                    }
                    else
                    {
                        MotionSensorRewrite.Unregister(harmony);
                        // Re-register old throttle patch as fallback
                        if (OptimizerConfig.EnableMotionSensorThrottle)
                            harmony.Patch(msUpdateOriginal, prefix: msUpdatePrefix);
                    }
                    break;
                }
                case "water_det_rewrite":
                    OptimizerConfig.EnableWaterDetectorRewrite = value > 0;
                    if (value > 0)
                    {
                        // Unregister old throttle patch + SignalOpt to eliminate stacked Harmony overhead
                        harmony.Unpatch(wdUpdateOriginal, wdUpdatePrefix.method);
                        harmony.Unpatch(wdUpdateOriginal, wdUpdatePostfix.method);
                        WaterDetectorRewrite.Register(harmony);
                    }
                    else
                    {
                        WaterDetectorRewrite.Unregister(harmony);
                        // Re-register old patches as fallback
                        if (OptimizerConfig.EnableWaterDetectorThrottle)
                            harmony.Patch(wdUpdateOriginal, prefix: wdUpdatePrefix, postfix: wdUpdatePostfix);
                    }
                    break;
                case "anim_lod":
                    OptimizerConfig.EnableAnimLOD = value > 0;
                    break;
                case "char_stagger":
                    OptimizerConfig.EnableCharacterStagger = value > 0;
                    break;
                case "ladder_fix":
                    OptimizerConfig.EnableLadderFix = value > 0;
                    break;
                case "platform_fix":
                    OptimizerConfig.EnablePlatformFix = value > 0;
                    break;
                case "server_hashset_dedup":
                    OptimizerConfig.EnableServerHashSetDedup = value > 0;
                    ToggleServerOptimizer(value > 0);
                    break;
                case "proxy_system":
                    OptimizerConfig.EnableProxySystem = value > 0;
                    if (value == 0) Proxy.ProxyRegistry.DetachAllItems();
                    break;
                case "signal_graph_accel":
                    OptimizerConfig.SignalGraphMode = Math.Clamp(value, 0, 2);
                    if (value > 0)
                    {
                        SignalGraphPatches.Register(harmony);
                        SignalGraphEvaluator.SetMode(value);
                        SignalGraphEvaluator.Compile();
                    }
                    else
                    {
                        SignalGraphEvaluator.SetMode(0);
                        SignalGraphPatches.Unregister(harmony);
                    }
                    break;
                case "relay_rewrite":
                    OptimizerConfig.EnableRelayRewrite = value > 0;
                    if (value > 0)
                    {
                        // Unregister old RelayOpt (Client-only) to eliminate stacked Harmony overhead
                        UnregisterRelayOpt();
                        RelayRewrite.Register(harmony);
                    }
                    else
                    {
                        RelayRewrite.Unregister(harmony);
                        // Re-register old RelayOpt as fallback (Client-only)
                        if (OptimizerConfig.EnableRelayOpt)
                            RegisterRelayOpt();
                    }
                    break;
                case "power_transfer_rewrite":
                    OptimizerConfig.EnablePowerTransferRewrite = value > 0;
                    if (value > 0)
                    {
                        PowerTransferRewrite.Register(harmony);
                    }
                    else
                    {
                        PowerTransferRewrite.Unregister(harmony);
                    }
                    break;
                case "power_container_rewrite":
                    OptimizerConfig.EnablePowerContainerRewrite = value > 0;
                    if (value > 0)
                    {
                        PowerContainerRewrite.Register(harmony);
                    }
                    else
                    {
                        PowerContainerRewrite.Unregister(harmony);
                    }
                    break;
                case "native_runtime":
                {
                    bool enable = value > 0;
                    // Validate: turning ON requires MotionSensorRewrite
                    var cascades = ToggleValidator.ValidateChange("native_runtime", enable);
                    if (cascades == null) break; // blocked

                    OptimizerConfig.EnableNativeRuntime = enable;
                    if (enable)
                    {
                        if (!World.NativeRuntimeBridge.IsEnabled)
                            World.NativeRuntimeBridge.OnRoundStart();
                    }
                    else
                    {
                        if (World.NativeRuntimeBridge.IsEnabled)
                            World.NativeRuntimeBridge.OnRoundEnd();
                    }
                    break;
                }
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

        /// <summary>
        /// Unregister old RelayOpt (Client-only) via reflection — avoids referencing Client-only type from Shared code.
        /// </summary>
        private static void UnregisterRelayOpt()
        {
            var relayOriginal = AccessTools.Method(typeof(RelayComponent), "Update",
                new[] { typeof(float), typeof(Camera) });
            if (relayOriginal == null) return;
            var relayOptType = Type.GetType("ItemOptimizerMod.Patches.SignalOptPatches+RelayOpt");
            if (relayOptType == null) return; // Not on client build
            var prefixMethod = AccessTools.Method(relayOptType, "Prefix");
            if (prefixMethod != null)
                harmony.Unpatch(relayOriginal, prefixMethod);
        }

        /// <summary>
        /// Re-register old RelayOpt (Client-only) via reflection — fallback when relay rewrite is disabled.
        /// </summary>
        private static void RegisterRelayOpt()
        {
            var relayOptType = Type.GetType("ItemOptimizerMod.Patches.SignalOptPatches+RelayOpt");
            if (relayOptType == null) return; // Not on client build
            var registerMethod = AccessTools.Method(relayOptType, "Register");
            registerMethod?.Invoke(null, new object[] { harmony });
        }
    }
}
