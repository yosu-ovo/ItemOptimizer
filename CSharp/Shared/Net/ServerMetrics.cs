using System;

namespace ItemOptimizerMod
{
    enum HealthLevel { Good, Warning, Critical }

    /// <summary>
    /// Shared data structure for server metrics.
    /// Server-side: populated by MetricRelaySender each tick.
    /// Client-side: populated by MetricRelayReceiver from network messages.
    /// </summary>
    static class ServerMetrics
    {
        // ── Raw per-tick values (server writes directly) ──
        internal static float TickMs;
        internal static int ClientCount;
        internal static int EntityCount;
        internal static float PendingPosAvg;
        internal static float EventQueueAvg;
        internal static int SkippedItems;
        internal static int TickRate;

        // ── EWMA-smoothed (server computes, sent to client) ──
        internal static float AvgTickMs;
        internal static float AvgPendingPos;
        internal static float AvgEventQueue;

        // ── Health score (0-100) ──
        internal static int HealthScore;
        internal static HealthLevel Health;

        // ── Client-side flag: true once first network message received ──
        internal static bool HasServerData;
        // ── Client-side flag: true if perf breakdown data is available ──
        internal static bool HasPerfData;

        // ── Per-system timing breakdown (ms, EWMA-smoothed on server, sent to client) ──
        internal static float PerfGameSession;
        internal static float PerfCharacter;
        internal static float PerfStatusEffect;
        internal static float PerfMapEntity;
        internal static float PerfRagdoll;
        internal static float PerfPhysics;
        internal static float PerfNetworking;

        private const float Smoothing = 0.1f;

        /// <summary>
        /// Called on server each tick after metrics are collected.
        /// Applies EWMA smoothing and computes health score.
        /// </summary>
        internal static void ServerEndTick()
        {
            AvgTickMs = AvgTickMs * (1f - Smoothing) + TickMs * Smoothing;
            AvgPendingPos = AvgPendingPos * (1f - Smoothing) + PendingPosAvg * Smoothing;
            AvgEventQueue = AvgEventQueue * (1f - Smoothing) + EventQueueAvg * Smoothing;
            ComputeHealth();
        }

        private static void ComputeHealth()
        {
            // The server simulation runs at 60Hz (16.67ms budget per tick).
            // Even though network writes may be at 20Hz, simulation overruns cause
            // accumulator lag → rubber-banding. Thresholds tuned accordingly:
            //   <10ms = 100, 10-16ms = linear 100→60, >16ms = linear 60→0 at 33ms
            float tickScore;
            if (AvgTickMs < 10f)
                tickScore = 100f;
            else if (AvgTickMs < 16.67f)
                tickScore = 100f - (AvgTickMs - 10f) * 6f; // 10→100, 16.67→60
            else
                tickScore = Math.Max(0f, 60f - (AvgTickMs - 16.67f) * 3.67f); // 16.67→60, ~33→0

            // Position queue score: <20 = 100, 20-100 = linear 100→0
            float posScore;
            if (AvgPendingPos < 20f)
                posScore = 100f;
            else
                posScore = Math.Max(0f, 100f - (AvgPendingPos - 20f) * 1.25f);

            // Event queue score: <10 = 100, 10-50 = linear 100→0
            float eventScore;
            if (AvgEventQueue < 10f)
                eventScore = 100f;
            else
                eventScore = Math.Max(0f, 100f - (AvgEventQueue - 10f) * 2.5f);

            // Weighted composite
            float composite = tickScore * 0.5f + posScore * 0.3f + eventScore * 0.2f;
            HealthScore = (int)Math.Round(Math.Clamp(composite, 0f, 100f));

            Health = HealthScore >= 70
                ? HealthLevel.Good
                : HealthScore >= 40
                    ? HealthLevel.Warning
                    : HealthLevel.Critical;
        }

        internal static void Reset()
        {
            TickMs = 0;
            ClientCount = 0;
            EntityCount = 0;
            PendingPosAvg = 0;
            EventQueueAvg = 0;
            SkippedItems = 0;
            TickRate = 0;
            AvgTickMs = 0;
            AvgPendingPos = 0;
            AvgEventQueue = 0;
            HealthScore = 0;
            Health = HealthLevel.Good;
            HasServerData = false;
            HasPerfData = false;
            PerfGameSession = 0;
            PerfCharacter = 0;
            PerfStatusEffect = 0;
            PerfMapEntity = 0;
            PerfRagdoll = 0;
            PerfPhysics = 0;
            PerfNetworking = 0;
        }
    }
}
