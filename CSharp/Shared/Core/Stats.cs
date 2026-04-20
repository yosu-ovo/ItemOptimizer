using System.Diagnostics;

namespace ItemOptimizerMod
{
    static class Stats
    {
        // Raw per-frame counters
        internal static int ColdStorageSkips;
        internal static int GroundItemSkips;
        internal static int MotionSensorSkips;
        internal static int ItemRuleSkips;
        internal static int ModOptSkips;
        internal static int WaterDetectorSkips;
        internal static int WireSkips;
        internal static int HasStatusTagCacheHits;

        // ── Character optimization counters ──
        internal static int AnimLODSkipped;
        internal static int AnimLODHalfRate;
        internal static int CharStaggerSkipped;
        internal static int LadderFixCorrections;
        internal static int PlatformFixCorrections;

        // ── Proxy dispatch counters ──
        internal static int ProxyItems;
        internal static float ProxyPhysicsMs;

        // ── Signal graph accelerator ──
        internal static int SignalGraphAccelSkips;
        internal static float SignalGraphTickMs;

        // ── Zone-based structure skip ──
        internal static int ZoneSkips;
        internal static int ZonePassiveSkips;
        internal static int ZoneCharSkips;
        internal static int ZoneManagedItems;

        // ── Component dispatch (transpiler-based skip) ──
        internal static int ComponentSkips;
        internal static int InertComponentSkips;

        // ── Total dispatch wall time (entire UpdateAllPrefix) ──
        internal static float TotalDispatchMs;

        // ── Per-phase diagnostic timing (ms) ──
        internal static float PhaseAMs;     // Hull/Structure/Gap/Power
        internal static float PhaseBMs;     // Item dispatch (includes main thread)
        internal static float PhaseCMs;     // PriorityItems (LuaCs)
        internal static float PhaseDMs;     // Tail (ProjSpecific + Spawner)
        // Sub-phase B breakdown
        internal static float PhaseBClassifyMs;  // classification loop
        internal static float PhaseBPreBuildMs;  // HasStatusTag PreBuildAll
        internal static float PhaseBMainLoopMs;  // main thread item.Update loop (wall-clock, includes Stopwatch per-item)
        internal static float PhaseBNativeRtMs;  // NativeRuntime.Tick() wall-clock
        internal static float AvgPhaseAMs;
        internal static float AvgPhaseBMs;
        internal static float AvgPhaseCMs;
        internal static float AvgPhaseDMs;
        internal static float AvgPhaseBClassifyMs;
        internal static float AvgPhaseBPreBuildMs;
        internal static float AvgPhaseBMainLoopMs;
        internal static float AvgPhaseBNativeRtMs;

        internal static float AvgColdStorageSkips;
        internal static float AvgGroundItemSkips;
        internal static float AvgMotionSensorSkips;
        internal static float AvgItemRuleSkips;
        internal static float AvgModOptSkips;
        internal static float AvgWaterDetectorSkips;
        internal static float AvgWireSkips;
        internal static float AvgHasStatusTagCacheHits;

        // ── Character optimization averages ──
        internal static float AvgAnimLODSkipped;
        internal static float AvgAnimLODHalfRate;
        internal static float AvgCharStaggerSkipped;
        internal static float AvgLadderFixCorrections;
        internal static float AvgPlatformFixCorrections;

        // ── Proxy dispatch averages ──
        internal static float AvgProxyItems;
        internal static float AvgProxyPhysicsMs;

        // ── Signal graph accelerator averages ──
        internal static float AvgSignalGraphAccelSkips;
        internal static float AvgSignalGraphTickMs;

        // ── Zone-based structure skip averages ──
        internal static float AvgZoneSkips;
        internal static float AvgZonePassiveSkips;
        internal static float AvgZoneCharSkips;

        // ── Component dispatch averages ──
        internal static float AvgComponentSkips;
        internal static float AvgInertComponentSkips;

        // ── Total dispatch average ──
        internal static float AvgTotalDispatchMs;

        // ── Memory diagnostics (sampled per frame) ──
        internal static float TotalMemoryMB;
        internal static int Gen0Count;
        internal static int Gen1Count;
        internal static int Gen2Count;
        internal static int Gen2FlashFrames;   // >0 = Gen2 GC just happened, decay counter
        private static int _prevGen2Count;

        // ── Per-system object counts ──
        internal static int SG_Nodes;
        internal static int SG_Registers;
        internal static int Zone_Count;
        internal static int Zone_Components;
        internal static int Zone_ActiveZones;

        private const float Smoothing = 0.05f;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        internal static void EndFrame()
        {
            AvgColdStorageSkips = AvgColdStorageSkips * (1f - Smoothing) + ColdStorageSkips * Smoothing;
            AvgGroundItemSkips = AvgGroundItemSkips * (1f - Smoothing) + GroundItemSkips * Smoothing;
            AvgMotionSensorSkips = AvgMotionSensorSkips * (1f - Smoothing) + MotionSensorSkips * Smoothing;
            AvgItemRuleSkips = AvgItemRuleSkips * (1f - Smoothing) + ItemRuleSkips * Smoothing;
            AvgModOptSkips = AvgModOptSkips * (1f - Smoothing) + ModOptSkips * Smoothing;
            AvgWaterDetectorSkips = AvgWaterDetectorSkips * (1f - Smoothing) + WaterDetectorSkips * Smoothing;
            AvgWireSkips = AvgWireSkips * (1f - Smoothing) + WireSkips * Smoothing;
            AvgHasStatusTagCacheHits = AvgHasStatusTagCacheHits * (1f - Smoothing) + HasStatusTagCacheHits * Smoothing;

            AvgAnimLODSkipped = AvgAnimLODSkipped * (1f - Smoothing) + AnimLODSkipped * Smoothing;
            AvgAnimLODHalfRate = AvgAnimLODHalfRate * (1f - Smoothing) + AnimLODHalfRate * Smoothing;
            AvgCharStaggerSkipped = AvgCharStaggerSkipped * (1f - Smoothing) + CharStaggerSkipped * Smoothing;
            AvgLadderFixCorrections = AvgLadderFixCorrections * (1f - Smoothing) + LadderFixCorrections * Smoothing;
            AvgPlatformFixCorrections = AvgPlatformFixCorrections * (1f - Smoothing) + PlatformFixCorrections * Smoothing;

            // Proxy dispatch EMA
            AvgProxyItems = AvgProxyItems * (1f - Smoothing) + ProxyItems * Smoothing;
            AvgProxyPhysicsMs = AvgProxyPhysicsMs * (1f - Smoothing) + ProxyPhysicsMs * Smoothing;
            AvgTotalDispatchMs = AvgTotalDispatchMs * (1f - Smoothing) + TotalDispatchMs * Smoothing;

            // Signal graph accel EMA
            AvgSignalGraphAccelSkips = AvgSignalGraphAccelSkips * (1f - Smoothing) + SignalGraphAccelSkips * Smoothing;
            AvgSignalGraphTickMs = AvgSignalGraphTickMs * (1f - Smoothing) + SignalGraphTickMs * Smoothing;

            // Zone skip EMA
            AvgZoneSkips = AvgZoneSkips * (1f - Smoothing) + ZoneSkips * Smoothing;
            AvgZonePassiveSkips = AvgZonePassiveSkips * (1f - Smoothing) + ZonePassiveSkips * Smoothing;
            AvgZoneCharSkips = AvgZoneCharSkips * (1f - Smoothing) + ZoneCharSkips * Smoothing;

            // Component dispatch EMA
            AvgComponentSkips = AvgComponentSkips * (1f - Smoothing) + ComponentSkips * Smoothing;
            AvgInertComponentSkips = AvgInertComponentSkips * (1f - Smoothing) + InertComponentSkips * Smoothing;

            // Per-phase diagnostic EMA
            AvgPhaseAMs = AvgPhaseAMs * (1f - Smoothing) + PhaseAMs * Smoothing;
            AvgPhaseBMs = AvgPhaseBMs * (1f - Smoothing) + PhaseBMs * Smoothing;
            AvgPhaseCMs = AvgPhaseCMs * (1f - Smoothing) + PhaseCMs * Smoothing;
            AvgPhaseDMs = AvgPhaseDMs * (1f - Smoothing) + PhaseDMs * Smoothing;
            AvgPhaseBClassifyMs = AvgPhaseBClassifyMs * (1f - Smoothing) + PhaseBClassifyMs * Smoothing;
            AvgPhaseBPreBuildMs = AvgPhaseBPreBuildMs * (1f - Smoothing) + PhaseBPreBuildMs * Smoothing;
            AvgPhaseBMainLoopMs = AvgPhaseBMainLoopMs * (1f - Smoothing) + PhaseBMainLoopMs * Smoothing;
            AvgPhaseBNativeRtMs = AvgPhaseBNativeRtMs * (1f - Smoothing) + PhaseBNativeRtMs * Smoothing;

            // Memory diagnostics snapshot
            TotalMemoryMB = (float)(System.GC.GetTotalMemory(false) / (1024.0 * 1024.0));
            Gen0Count = System.GC.CollectionCount(0);
            Gen1Count = System.GC.CollectionCount(1);
            Gen2Count = System.GC.CollectionCount(2);
            if (Gen2Count > _prevGen2Count)
                Gen2FlashFrames = 30;
            else if (Gen2FlashFrames > 0)
                Gen2FlashFrames--;
            _prevGen2Count = Gen2Count;

            // Per-system object counts
            SG_Nodes = SignalGraph.SignalGraphEvaluator.AcceleratedNodeCount;
            SG_Registers = SignalGraph.SignalGraphEvaluator.RegisterCount;
            if (World.NativeRuntimeBridge.IsEnabled && World.NativeRuntimeBridge.Runtime != null)
            {
                var zones = World.NativeRuntimeBridge.Runtime.Graph.Zones;
                Zone_Count = zones.Count;
                int compSum = 0, activeZ = 0;
                for (int i = 0; i < zones.Count; i++)
                {
                    compSum += zones[i].Components.Count;
                    if (zones[i].Tier < World.ZoneTier.Dormant) activeZ++;
                }
                Zone_Components = compSum;
                Zone_ActiveZones = activeZ;
            }

            // Invalidate per-frame caches
            Patches.HasStatusTagCachePatch.OnNewFrame();
            Patches.UpdateAllTakeover.RefreshFrameFlags();
            ColdStorageDetector.NewFrame();

            ColdStorageSkips = 0;
            GroundItemSkips = 0;
            MotionSensorSkips = 0;
            ItemRuleSkips = 0;
            ModOptSkips = 0;
            WaterDetectorSkips = 0;
            WireSkips = 0;
            HasStatusTagCacheHits = 0;
            AnimLODSkipped = 0;
            AnimLODHalfRate = 0;
            CharStaggerSkipped = 0;
            LadderFixCorrections = 0;
            PlatformFixCorrections = 0;
            ProxyItems = 0;
            ProxyPhysicsMs = 0;
            SignalGraphAccelSkips = 0;
            SignalGraphTickMs = 0;
            ZoneSkips = 0;
            ZonePassiveSkips = 0;
            ZoneCharSkips = 0;
            ComponentSkips = 0;
            InertComponentSkips = 0;
            TotalDispatchMs = 0;
            PhaseAMs = 0;
            PhaseBMs = 0;
            PhaseCMs = 0;
            PhaseDMs = 0;
            PhaseBClassifyMs = 0;
            PhaseBPreBuildMs = 0;
            PhaseBMainLoopMs = 0;
            PhaseBNativeRtMs = 0;
        }

        // Per-skip cost in ms (measured from iter-21 profiling data)
        internal const float CostColdStorage    = 0.0005f;  // ~0.5μs generic item
        internal const float CostGroundItem     = 0.0005f;  // ~0.5μs generic item
        internal const float CostMotionSensor   = 0.0043f;  // 4.3μs measured
        internal const float CostWaterDetector  = 0.0046f;  // 4.6μs measured
        internal const float CostItemRule       = 0.003f;   // ~3μs avg modded item
        internal const float CostModOpt         = 0.0003f;  // ~0.3μs light item
        internal const float CostWireSkip       = 0.0001f;  // ~0.1μs wire
        internal const float CostHSTCache       = 0.0002f;  // ~0.2μs tag scan
        internal const float CostAnimLODSkip    = 0.005f;   // ~5μs full anim
        internal const float CostAnimLODHalf    = 0.002f;   // ~2μs half-rate anim
        internal const float CostCharStagger    = 0.01f;    // ~10μs AI tick
        internal const float CostZoneSkip       = 0.001f;   // ~1μs mixed item
        internal const float CostSignalGraph    = 0.003f;   // ~3μs per accel node
        internal const float CostComponentSkip  = 0.0008f;  // ~0.8μs component update

        internal static float EstimatedSavedMs()
        {
            return AvgColdStorageSkips      * CostColdStorage
                 + AvgGroundItemSkips       * CostGroundItem
                 + AvgMotionSensorSkips     * CostMotionSensor
                 + AvgItemRuleSkips         * CostItemRule
                 + AvgModOptSkips           * CostModOpt
                 + AvgWaterDetectorSkips    * CostWaterDetector
                 + AvgWireSkips             * CostWireSkip
                 + AvgHasStatusTagCacheHits * CostHSTCache
                 + AvgAnimLODSkipped        * CostAnimLODSkip
                 + AvgAnimLODHalfRate       * CostAnimLODHalf
                 + AvgCharStaggerSkipped    * CostCharStagger
                 + AvgZoneSkips             * CostZoneSkip
                 + AvgSignalGraphAccelSkips * CostSignalGraph
                 + (AvgComponentSkips + AvgInertComponentSkips) * CostComponentSkip;
        }

        internal static void Reset()
        {
            ColdStorageSkips = 0;
            GroundItemSkips = 0;
            MotionSensorSkips = 0;
            ItemRuleSkips = 0;
            ModOptSkips = 0;
            WaterDetectorSkips = 0;
            WireSkips = 0;
            HasStatusTagCacheHits = 0;
            AnimLODSkipped = 0;
            AnimLODHalfRate = 0;
            CharStaggerSkipped = 0;
            ProxyItems = 0;
            AvgColdStorageSkips = 0;
            AvgGroundItemSkips = 0;
            AvgMotionSensorSkips = 0;
            AvgItemRuleSkips = 0;
            AvgModOptSkips = 0;
            AvgWaterDetectorSkips = 0;
            AvgWireSkips = 0;
            AvgHasStatusTagCacheHits = 0;
            AvgAnimLODSkipped = 0;
            AvgAnimLODHalfRate = 0;
            AvgCharStaggerSkipped = 0;
            LadderFixCorrections = 0;
            AvgLadderFixCorrections = 0;
            PlatformFixCorrections = 0;
            AvgPlatformFixCorrections = 0;
            AvgProxyItems = 0;
            AvgProxyPhysicsMs = 0;
            AvgSignalGraphAccelSkips = 0;
            AvgSignalGraphTickMs = 0;
            AvgZoneSkips = 0;
            AvgZonePassiveSkips = 0;
            AvgZoneCharSkips = 0;
            AvgComponentSkips = 0;
            AvgInertComponentSkips = 0;
            AvgTotalDispatchMs = 0;
            AvgPhaseAMs = 0;
            AvgPhaseBMs = 0;
            AvgPhaseCMs = 0;
            AvgPhaseDMs = 0;
            AvgPhaseBClassifyMs = 0;
            AvgPhaseBPreBuildMs = 0;
            AvgPhaseBMainLoopMs = 0;
            AvgPhaseBNativeRtMs = 0;
            TotalMemoryMB = 0;
            Gen0Count = 0;
            Gen1Count = 0;
            Gen2Count = 0;
            Gen2FlashFrames = 0;
            _prevGen2Count = 0;
            SG_Nodes = 0;
            SG_Registers = 0;
            Zone_Count = 0;
            Zone_Components = 0;
            Zone_ActiveZones = 0;
        }
    }
}
