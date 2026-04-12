using System.Diagnostics;

namespace ItemOptimizerMod
{
    static class Stats
    {
        internal static int ColdStorageSkips;
        internal static int GroundItemSkips;
        internal static int CustomInterfaceSkips;
        internal static int MotionSensorSkips;
        internal static int WearableSkips;
        internal static int ItemRuleSkips;
        internal static int ModOptSkips;
        internal static int WaterDetectorSkips;
        internal static int DoorSkips;
        internal static int HasStatusTagCacheHits;
        internal static int StatusHUDSkips;
        internal static int AfflictionDedupSkips;

        // ── Parallel dispatch counters ──
        internal static int ParallelItems;
        internal static int MainThreadItems;

        internal static float AvgColdStorageSkips;
        internal static float AvgGroundItemSkips;
        internal static float AvgCustomInterfaceSkips;
        internal static float AvgMotionSensorSkips;
        internal static float AvgWearableSkips;
        internal static float AvgItemRuleSkips;
        internal static float AvgModOptSkips;
        internal static float AvgWaterDetectorSkips;
        internal static float AvgDoorSkips;
        internal static float AvgHasStatusTagCacheHits;
        internal static float AvgStatusHUDSkips;
        internal static float AvgAfflictionDedupSkips;

        // ── Parallel dispatch averages ──
        internal static float AvgParallelItems;
        internal static float AvgMainThreadItems;
        internal static float AvgParallelWallMs;

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
            AvgHasStatusTagCacheHits = AvgHasStatusTagCacheHits * (1f - Smoothing) + HasStatusTagCacheHits * Smoothing;
            AvgStatusHUDSkips = AvgStatusHUDSkips * (1f - Smoothing) + StatusHUDSkips * Smoothing;
            AvgAfflictionDedupSkips = AvgAfflictionDedupSkips * (1f - Smoothing) + AfflictionDedupSkips * Smoothing;

            // Parallel dispatch EMA
            AvgParallelItems = AvgParallelItems * (1f - Smoothing) + ParallelItems * Smoothing;
            AvgMainThreadItems = AvgMainThreadItems * (1f - Smoothing) + MainThreadItems * Smoothing;

            for (int i = 0; i < 8; i++)
            {
                AvgThreadMs[i] = AvgThreadMs[i] * (1f - Smoothing) + ThreadMs[i] * Smoothing;
                AvgThreadItems[i] = (int)(AvgThreadItems[i] * (1f - Smoothing) + ThreadItems[i] * Smoothing);
            }

            // Invalidate per-frame caches
            Patches.HasStatusTagCachePatch.OnNewFrame();
            Patches.ItemUpdatePatch.NewFrame();
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
            HasStatusTagCacheHits = 0;
            StatusHUDSkips = 0;
            AfflictionDedupSkips = 0;
            ParallelItems = 0;
            MainThreadItems = 0;
        }

        /// <summary>
        /// Called from ParallelDispatchPatch.UpdateAllPostfix to record per-thread timing.
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
                 + AvgHasStatusTagCacheHits * 0.0002f
                 + AvgStatusHUDSkips * 0.002f
                 + AvgAfflictionDedupSkips * 0.001f;
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
            HasStatusTagCacheHits = 0;
            StatusHUDSkips = 0;
            AfflictionDedupSkips = 0;
            ParallelItems = 0;
            MainThreadItems = 0;
            AvgColdStorageSkips = 0;
            AvgGroundItemSkips = 0;
            AvgCustomInterfaceSkips = 0;
            AvgMotionSensorSkips = 0;
            AvgWearableSkips = 0;
            AvgItemRuleSkips = 0;
            AvgModOptSkips = 0;
            AvgWaterDetectorSkips = 0;
            AvgDoorSkips = 0;
            AvgHasStatusTagCacheHits = 0;
            AvgStatusHUDSkips = 0;
            AvgAfflictionDedupSkips = 0;
            AvgParallelItems = 0;
            AvgMainThreadItems = 0;
            AvgParallelWallMs = 0;
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
