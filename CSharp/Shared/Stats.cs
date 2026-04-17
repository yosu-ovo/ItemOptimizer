using System.Diagnostics;

namespace ItemOptimizerMod
{
    static class Stats
    {
        // Raw per-frame counters
        internal static int ColdStorageSkips;
        internal static int GroundItemSkips;
        internal static int CustomInterfaceSkips;
        internal static int MotionSensorSkips;
        internal static int WearableSkips;
        internal static int ItemRuleSkips;
        internal static int ModOptSkips;
        internal static int WaterDetectorSkips;
        internal static int DoorSkips;
        internal static int WireSkips;
        internal static int HasStatusTagCacheHits;
        internal static int StatusHUDSkips;
        internal static int AfflictionDedupSkips;

        // ── Character optimization counters ──
        internal static int AnimLODSkipped;
        internal static int AnimLODHalfRate;
        internal static int CharStaggerSkipped;
        internal static int LadderFixCorrections;
        internal static int PlatformFixCorrections;

        // ── Proxy dispatch counters ──
        internal static int ProxyItems;
        internal static float ProxyBatchComputeMs;
        internal static float ProxySyncBackMs;
        internal static float ProxyPhysicsMs;

        // ── Signal graph accelerator ──
        internal static int SignalGraphAccelSkips;
        internal static float SignalGraphTickMs;

        // ── Total dispatch wall time (entire UpdateAllPrefix) ──
        internal static float TotalDispatchMs;

        // ── Per-phase diagnostic timing (ms) ──
        internal static float PhaseAMs;     // Hull/Structure/Gap/Power
        internal static float PhaseProxyMs; // Proxy Tick
        internal static float PhaseBMs;     // Item dispatch (includes main thread)
        internal static float PhaseCMs;     // PriorityItems (LuaCs)
        internal static float PhaseDMs;     // Tail (ProjSpecific + Spawner)
        // Sub-phase B breakdown
        internal static float PhaseBClassifyMs;  // classification loop
        internal static float PhaseBPreBuildMs;  // HasStatusTag PreBuildAll
        internal static float PhaseBMainLoopMs;  // main thread item.Update loop (wall-clock, includes Stopwatch per-item)
        internal static float AvgPhaseAMs;
        internal static float AvgPhaseProxyMs;
        internal static float AvgPhaseBMs;
        internal static float AvgPhaseCMs;
        internal static float AvgPhaseDMs;
        internal static float AvgPhaseBClassifyMs;
        internal static float AvgPhaseBPreBuildMs;
        internal static float AvgPhaseBMainLoopMs;

        internal static float AvgColdStorageSkips;
        internal static float AvgGroundItemSkips;
        internal static float AvgCustomInterfaceSkips;
        internal static float AvgMotionSensorSkips;
        internal static float AvgWearableSkips;
        internal static float AvgItemRuleSkips;
        internal static float AvgModOptSkips;
        internal static float AvgWaterDetectorSkips;
        internal static float AvgDoorSkips;
        internal static float AvgWireSkips;
        internal static float AvgHasStatusTagCacheHits;
        internal static float AvgStatusHUDSkips;
        internal static float AvgAfflictionDedupSkips;

        // ── Character optimization averages ──
        internal static float AvgAnimLODSkipped;
        internal static float AvgAnimLODHalfRate;
        internal static float AvgCharStaggerSkipped;
        internal static float AvgLadderFixCorrections;
        internal static float AvgPlatformFixCorrections;

        // ── Proxy dispatch averages ──
        internal static float AvgProxyItems;
        internal static float AvgProxyBatchComputeMs;
        internal static float AvgProxySyncBackMs;
        internal static float AvgProxyPhysicsMs;

        // ── Signal graph accelerator averages ──
        internal static float AvgSignalGraphAccelSkips;
        internal static float AvgSignalGraphTickMs;

        // ── Total dispatch average ──
        internal static float AvgTotalDispatchMs;

        private const float Smoothing = 0.05f;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        internal static void EndFrame()
        {
            AvgColdStorageSkips = AvgColdStorageSkips * (1f - Smoothing) + ColdStorageSkips * Smoothing;
            AvgGroundItemSkips = AvgGroundItemSkips * (1f - Smoothing) + GroundItemSkips * Smoothing;
            AvgCustomInterfaceSkips = AvgCustomInterfaceSkips * (1f - Smoothing) + CustomInterfaceSkips * Smoothing;
            AvgMotionSensorSkips = AvgMotionSensorSkips * (1f - Smoothing) + MotionSensorSkips * Smoothing;
            AvgWearableSkips = AvgWearableSkips * (1f - Smoothing) + WearableSkips * Smoothing;
            AvgItemRuleSkips = AvgItemRuleSkips * (1f - Smoothing) + ItemRuleSkips * Smoothing;
            AvgModOptSkips = AvgModOptSkips * (1f - Smoothing) + ModOptSkips * Smoothing;
            AvgWaterDetectorSkips = AvgWaterDetectorSkips * (1f - Smoothing) + WaterDetectorSkips * Smoothing;
            AvgDoorSkips = AvgDoorSkips * (1f - Smoothing) + DoorSkips * Smoothing;
            AvgWireSkips = AvgWireSkips * (1f - Smoothing) + WireSkips * Smoothing;
            AvgHasStatusTagCacheHits = AvgHasStatusTagCacheHits * (1f - Smoothing) + HasStatusTagCacheHits * Smoothing;
            AvgStatusHUDSkips = AvgStatusHUDSkips * (1f - Smoothing) + StatusHUDSkips * Smoothing;
            AvgAfflictionDedupSkips = AvgAfflictionDedupSkips * (1f - Smoothing) + AfflictionDedupSkips * Smoothing;

            AvgAnimLODSkipped = AvgAnimLODSkipped * (1f - Smoothing) + AnimLODSkipped * Smoothing;
            AvgAnimLODHalfRate = AvgAnimLODHalfRate * (1f - Smoothing) + AnimLODHalfRate * Smoothing;
            AvgCharStaggerSkipped = AvgCharStaggerSkipped * (1f - Smoothing) + CharStaggerSkipped * Smoothing;
            AvgLadderFixCorrections = AvgLadderFixCorrections * (1f - Smoothing) + LadderFixCorrections * Smoothing;
            AvgPlatformFixCorrections = AvgPlatformFixCorrections * (1f - Smoothing) + PlatformFixCorrections * Smoothing;

            // Proxy dispatch EMA
            AvgProxyItems = AvgProxyItems * (1f - Smoothing) + ProxyItems * Smoothing;
            AvgProxyBatchComputeMs = AvgProxyBatchComputeMs * (1f - Smoothing) + ProxyBatchComputeMs * Smoothing;
            AvgProxySyncBackMs = AvgProxySyncBackMs * (1f - Smoothing) + ProxySyncBackMs * Smoothing;
            AvgProxyPhysicsMs = AvgProxyPhysicsMs * (1f - Smoothing) + ProxyPhysicsMs * Smoothing;
            AvgTotalDispatchMs = AvgTotalDispatchMs * (1f - Smoothing) + TotalDispatchMs * Smoothing;

            // Signal graph accel EMA
            AvgSignalGraphAccelSkips = AvgSignalGraphAccelSkips * (1f - Smoothing) + SignalGraphAccelSkips * Smoothing;
            AvgSignalGraphTickMs = AvgSignalGraphTickMs * (1f - Smoothing) + SignalGraphTickMs * Smoothing;

            // Per-phase diagnostic EMA
            AvgPhaseAMs = AvgPhaseAMs * (1f - Smoothing) + PhaseAMs * Smoothing;
            AvgPhaseProxyMs = AvgPhaseProxyMs * (1f - Smoothing) + PhaseProxyMs * Smoothing;
            AvgPhaseBMs = AvgPhaseBMs * (1f - Smoothing) + PhaseBMs * Smoothing;
            AvgPhaseCMs = AvgPhaseCMs * (1f - Smoothing) + PhaseCMs * Smoothing;
            AvgPhaseDMs = AvgPhaseDMs * (1f - Smoothing) + PhaseDMs * Smoothing;
            AvgPhaseBClassifyMs = AvgPhaseBClassifyMs * (1f - Smoothing) + PhaseBClassifyMs * Smoothing;
            AvgPhaseBPreBuildMs = AvgPhaseBPreBuildMs * (1f - Smoothing) + PhaseBPreBuildMs * Smoothing;
            AvgPhaseBMainLoopMs = AvgPhaseBMainLoopMs * (1f - Smoothing) + PhaseBMainLoopMs * Smoothing;

            // Invalidate per-frame caches
            Patches.HasStatusTagCachePatch.OnNewFrame();
            Patches.UpdateAllTakeover.RefreshFrameFlags();
            ColdStorageDetector.NewFrame();

            ColdStorageSkips = 0;
            GroundItemSkips = 0;
            CustomInterfaceSkips = 0;
            MotionSensorSkips = 0;
            WearableSkips = 0;
            ItemRuleSkips = 0;
            ModOptSkips = 0;
            WaterDetectorSkips = 0;
            DoorSkips = 0;
            WireSkips = 0;
            HasStatusTagCacheHits = 0;
            StatusHUDSkips = 0;
            AfflictionDedupSkips = 0;
            AnimLODSkipped = 0;
            AnimLODHalfRate = 0;
            CharStaggerSkipped = 0;
            LadderFixCorrections = 0;
            PlatformFixCorrections = 0;
            ProxyItems = 0;
            ProxyBatchComputeMs = 0;
            ProxySyncBackMs = 0;
            ProxyPhysicsMs = 0;
            SignalGraphAccelSkips = 0;
            SignalGraphTickMs = 0;
            TotalDispatchMs = 0;
            PhaseAMs = 0;
            PhaseProxyMs = 0;
            PhaseBMs = 0;
            PhaseCMs = 0;
            PhaseDMs = 0;
            PhaseBClassifyMs = 0;
            PhaseBPreBuildMs = 0;
            PhaseBMainLoopMs = 0;
        }

        internal static float EstimatedSavedMs()
        {
            return AvgColdStorageSkips * 0.0005f
                 + AvgGroundItemSkips * 0.0005f
                 + AvgCustomInterfaceSkips * 0.002f
                 + AvgMotionSensorSkips * 0.003f
                 + AvgWearableSkips * 0.001f
                 + AvgItemRuleSkips * 0.003f
                 + AvgModOptSkips * 0.0003f
                 + AvgWaterDetectorSkips * 0.001f
                 + AvgDoorSkips * 0.001f
                 + AvgWireSkips * 0.0001f
                 + AvgHasStatusTagCacheHits * 0.0002f
                 + AvgStatusHUDSkips * 0.002f
                 + AvgAfflictionDedupSkips * 0.001f
                 + AvgAnimLODSkipped * 0.005f
                 + AvgAnimLODHalfRate * 0.002f
                 + AvgCharStaggerSkipped * 0.01f;
        }

        internal static void Reset()
        {
            ColdStorageSkips = 0;
            GroundItemSkips = 0;
            CustomInterfaceSkips = 0;
            MotionSensorSkips = 0;
            WearableSkips = 0;
            ItemRuleSkips = 0;
            ModOptSkips = 0;
            WaterDetectorSkips = 0;
            DoorSkips = 0;
            WireSkips = 0;
            HasStatusTagCacheHits = 0;
            StatusHUDSkips = 0;
            AfflictionDedupSkips = 0;
            AnimLODSkipped = 0;
            AnimLODHalfRate = 0;
            CharStaggerSkipped = 0;
            ProxyItems = 0;
            ProxyBatchComputeMs = 0;
            ProxySyncBackMs = 0;
            AvgColdStorageSkips = 0;
            AvgGroundItemSkips = 0;
            AvgCustomInterfaceSkips = 0;
            AvgMotionSensorSkips = 0;
            AvgWearableSkips = 0;
            AvgItemRuleSkips = 0;
            AvgModOptSkips = 0;
            AvgWaterDetectorSkips = 0;
            AvgDoorSkips = 0;
            AvgWireSkips = 0;
            AvgHasStatusTagCacheHits = 0;
            AvgStatusHUDSkips = 0;
            AvgAfflictionDedupSkips = 0;
            AvgAnimLODSkipped = 0;
            AvgAnimLODHalfRate = 0;
            AvgCharStaggerSkipped = 0;
            LadderFixCorrections = 0;
            AvgLadderFixCorrections = 0;
            PlatformFixCorrections = 0;
            AvgPlatformFixCorrections = 0;
            AvgProxyItems = 0;
            AvgProxyBatchComputeMs = 0;
            AvgProxySyncBackMs = 0;
            AvgProxyPhysicsMs = 0;
            AvgSignalGraphAccelSkips = 0;
            AvgSignalGraphTickMs = 0;
            AvgTotalDispatchMs = 0;
            AvgPhaseAMs = 0;
            AvgPhaseProxyMs = 0;
            AvgPhaseBMs = 0;
            AvgPhaseCMs = 0;
            AvgPhaseDMs = 0;
            AvgPhaseBClassifyMs = 0;
            AvgPhaseBPreBuildMs = 0;
            AvgPhaseBMainLoopMs = 0;
        }
    }
}
