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
        private static MethodInfo hasStatusTagOriginal;
        private static MethodInfo btUpdateOriginal;
        private static MethodInfo pumpUpdateOriginal;

        private static HarmonyMethod hasStatusTagTranspiler;
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

            Localization.Init();

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
                $"HasStatusTagCache={OptimizerConfig.EnableHasStatusTagCache}, " +
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
            hasStatusTagOriginal = AccessTools.Method(typeof(PropertyConditional), nameof(PropertyConditional.Matches));

            hasStatusTagTranspiler = new HarmonyMethod(AccessTools.Method(typeof(HasStatusTagCachePatch), nameof(HasStatusTagCachePatch.Transpiler)));

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
            // HasStatusTagCache uses a transpiler (always applied — checks config flag at runtime).
            // This avoids per-call Harmony dispatch overhead for non-HasStatusTag Matches calls.
            if (hasStatusTagOriginal != null)
                harmony.Patch(hasStatusTagOriginal, transpiler: hasStatusTagTranspiler);
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
                case "has_status_tag_cache":
                    OptimizerConfig.EnableHasStatusTagCache = value > 0;
                    if (value == 0) HasStatusTagCachePatch.ClearCache();
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
                        MotionSensorRewrite.Register(harmony);
                    else
                        MotionSensorRewrite.Unregister(harmony);
                    break;
                }
                case "water_det_rewrite":
                    OptimizerConfig.EnableWaterDetectorRewrite = value > 0;
                    if (value > 0)
                        WaterDetectorRewrite.Register(harmony);
                    else
                        WaterDetectorRewrite.Unregister(harmony);
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
                        RelayRewrite.Register(harmony);
                    else
                        RelayRewrite.Unregister(harmony);
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
