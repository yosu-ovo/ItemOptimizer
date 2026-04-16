using System.Diagnostics;

namespace ItemOptimizerMod
{
    static class Stats
    {
        // Raw per-frame counters — non-atomic: approximate under parallel dispatch
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

        // ── Parallel dispatch counters ──
        internal static int ParallelItems;
        internal static int MainThreadItems;

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

        // ── Parallel dispatch averages ──
        internal static float AvgParallelItems;
        internal static float AvgMainThreadItems;
        internal static float AvgParallelWallMs;

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

        // ── Per-thread live data (updated each frame, up to 8 threads) ──
        internal static readonly float[] ThreadMs = new float[8];     // index 0=main, 1-7=workers
        internal static readonly int[] ThreadItems = new int[8];
        internal static readonly float[] AvgThreadMs = new float[8];
        internal static readonly int[] AvgThreadItems = new int[8];
        internal static int ActiveThreadCount;
        // Smoothed display thread count — prevents bar flickering
        internal static int DisplayThreadCount;

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

            // Parallel dispatch EMA
            AvgParallelItems = AvgParallelItems * (1f - Smoothing) + ParallelItems * Smoothing;
            AvgMainThreadItems = AvgMainThreadItems * (1f - Smoothing) + MainThreadItems * Smoothing;

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

            for (int i = 0; i < 8; i++)
            {
                AvgThreadMs[i] = AvgThreadMs[i] * (1f - Smoothing) + ThreadMs[i] * Smoothing;
                AvgThreadItems[i] = (int)(AvgThreadItems[i] * (1f - Smoothing) + ThreadItems[i] * Smoothing);
            }

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
            ParallelItems = 0;
            MainThreadItems = 0;
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

        /// <summary>
        /// Called from UpdateAllTakeover.DispatchItemUpdates to record per-thread timing.
        /// Uses fixed-size worker arrays (no ConcurrentDictionary, stable thread count).
        /// </summary>
        internal static void RecordParallelFrame(
            int workerCount, long[] workerTicks, int[] workerItemCounts,
            long mainThreadTicks, int mainThreadItemCount)
        {
            // Slot 0 = main thread
            ThreadMs[0] = (float)(mainThreadTicks * TicksToMs);
            ThreadItems[0] = mainThreadItemCount;

            // Slots 1..workerCount = worker threads (fixed, stable)
            float maxWorkerMs = 0;
            for (int i = 0; i < workerCount && i < 7; i++)
            {
                float ms = (float)(workerTicks[i] * TicksToMs);
                ThreadMs[i + 1] = ms;
                ThreadItems[i + 1] = workerItemCounts[i];
                if (ms > maxWorkerMs) maxWorkerMs = ms;
            }

            int totalSlots = 1 + workerCount;
            ActiveThreadCount = totalSlots;
            DisplayThreadCount = totalSlots; // fixed count, no flickering

            // Clear unused slots
            for (int i = totalSlots; i < 8; i++)
            {
                ThreadMs[i] = 0;
                ThreadItems[i] = 0;
            }

            AvgParallelWallMs = AvgParallelWallMs * (1f - Smoothing) + maxWorkerMs * Smoothing;
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

        /// <summary>Estimated ms saved by parallel dispatch (workers run concurrent with main).</summary>
        internal static float ParallelSavedMs()
        {
            return AvgParallelWallMs;
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
            ParallelItems = 0;
            MainThreadItems = 0;
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
            AvgParallelItems = 0;
            AvgMainThreadItems = 0;
            AvgParallelWallMs = 0;
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
            ActiveThreadCount = 0;
            DisplayThreadCount = 0;
            for (int i = 0; i < 8; i++)
            {
                ThreadMs[i] = 0;
                ThreadItems[i] = 0;
                AvgThreadMs[i] = 0;
                AvgThreadItems[i] = 0;
            }
        }
    }
}
