using System;
using Barotrauma;
using Barotrauma.Networking;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Client-side: receives server metric broadcasts and populates ServerMetrics fields.
    /// </summary>
    static class MetricRelayReceiver
    {
        private static bool _registered;

        internal static void Register()
        {
            if (_registered) return;
            try
            {
                var networking = LuaCsSetup.Instance?.Networking;
                if (networking == null)
                {
                    LuaCsLogger.Log("[ItemOptimizer] MetricRelayReceiver: Networking not available, skipping");
                    return;
                }
#if CLIENT
                networking.Receive("ItemOpt.Metrics", OnReceive);
                _registered = true;
#endif
            }
            catch (Exception e)
            {
                LuaCsLogger.HandleException(e, LuaCsMessageOrigin.CSharpMod);
            }
        }

        private static void OnReceive(IReadMessage msg)
        {
            try
            {
                ServerMetrics.AvgTickMs = msg.ReadSingle();
                ServerMetrics.ClientCount = msg.ReadByte();
                ServerMetrics.EntityCount = msg.ReadUInt16();
                ServerMetrics.AvgPendingPos = msg.ReadSingle();
                ServerMetrics.AvgEventQueue = msg.ReadSingle();
                ServerMetrics.SkippedItems = msg.ReadUInt16();
                ServerMetrics.TickRate = msg.ReadByte();
                ServerMetrics.HealthScore = msg.ReadByte();

                // Per-system perf breakdown
                ServerMetrics.PerfGameSession = msg.ReadSingle();
                ServerMetrics.PerfCharacter = msg.ReadSingle();
                ServerMetrics.PerfStatusEffect = msg.ReadSingle();
                ServerMetrics.PerfMapEntity = msg.ReadSingle();
                ServerMetrics.PerfRagdoll = msg.ReadSingle();
                ServerMetrics.PerfPhysics = msg.ReadSingle();
                ServerMetrics.PerfNetworking = msg.ReadSingle();
                ServerMetrics.HasPerfData = true;

                ServerMetrics.Health = ServerMetrics.HealthScore >= 70
                    ? HealthLevel.Good
                    : ServerMetrics.HealthScore >= 40
                        ? HealthLevel.Warning
                        : HealthLevel.Critical;
                ServerMetrics.HasServerData = true;
            }
            catch (Exception e)
            {
                LuaCsLogger.HandleException(e, LuaCsMessageOrigin.CSharpMod);
            }
        }

        internal static void Reset()
        {
            _registered = false;
            ServerMetrics.HasServerData = false;
        }
    }
}
