using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Server-side: collects per-tick metrics and broadcasts to all clients at 2Hz.
    /// Only runs on server (registered by ItemOptimizerPlugin.Server partial).
    /// </summary>
    static class MetricRelaySender
    {
        private static float _sendAccum;
        private static readonly Stopwatch _tickStopwatch = new Stopwatch();
        private static bool _tickTimingActive;
        private static int _broadcastCount;

        /// <summary>
        /// Called at the start of UpdateAllTakeover.Prefix to begin tick timing.
        /// </summary>
        internal static void BeginTick()
        {
            _tickStopwatch.Restart();
            _tickTimingActive = true;
        }

        /// <summary>
        /// Called after UpdateAllTakeover.Prefix completes item updates.
        /// Collects metrics, applies EWMA, and periodically broadcasts to clients.
        /// </summary>
        internal static void OnPostUpdate(float dt)
        {
            if (!_tickTimingActive) return;
            _tickStopwatch.Stop();
            _tickTimingActive = false;

            // Collect raw metrics (MapEntity portion only — total tick will be recomputed after perf data)
            ServerMetrics.EntityCount = Item.ItemList.Count + Character.CharacterList.Count;

            // Server-specific: read from GameServer
            var server = GameMain.Server;
            if (server != null)
            {
                var clients = server.ConnectedClients;
                ServerMetrics.ClientCount = clients.Count;
                ServerMetrics.TickRate = server.ServerSettings.TickRate;

                if (clients.Count > 0)
                {
                    float totalPending = 0;
                    float totalEvents = 0;
                    foreach (var c in clients)
                    {
                        totalPending += c.PendingPositionUpdates.Count;
                        totalEvents += c.UnreceivedEntityEventCount;
                    }
                    ServerMetrics.PendingPosAvg = totalPending / clients.Count;
                    ServerMetrics.EventQueueAvg = totalEvents / clients.Count;
                }
                else
                {
                    ServerMetrics.PendingPosAvg = 0;
                    ServerMetrics.EventQueueAvg = 0;
                }
            }

            // Per-system perf timing (MapEntity ms = our tick stopwatch = items+hulls+etc)
            float mapEntityMs = (float)_tickStopwatch.Elapsed.TotalMilliseconds;
            ServerPerfTracker.EndTick(mapEntityMs);

            // Copy perf data to shared metrics for relay
            ServerMetrics.PerfGameSession = ServerPerfTracker.AvgGameSessionMs;
            ServerMetrics.PerfCharacter = ServerPerfTracker.AvgCharacterMs;
            ServerMetrics.PerfStatusEffect = ServerPerfTracker.AvgStatusEffectMs;
            ServerMetrics.PerfMapEntity = ServerPerfTracker.AvgMapEntityMs;
            ServerMetrics.PerfRagdoll = ServerPerfTracker.AvgRagdollMs;
            ServerMetrics.PerfPhysics = ServerPerfTracker.AvgPhysicsMs;
            ServerMetrics.PerfNetworking = ServerPerfTracker.AvgNetworkingMs;

            // TickMs = total of all subsystems (not just MapEntity)
            ServerMetrics.TickMs = ServerMetrics.PerfGameSession + ServerMetrics.PerfCharacter
                + ServerMetrics.PerfStatusEffect + ServerMetrics.PerfMapEntity
                + ServerMetrics.PerfRagdoll + ServerMetrics.PerfPhysics
                + ServerMetrics.PerfNetworking;

            // EWMA + health (must run AFTER TickMs is set to the real total)
            ServerMetrics.ServerEndTick();

            // Sync tracking (only active when iosync is running)
            if (SyncRelaySender.Active)
                SyncRelaySender.OnTick(dt);

            // Send at configured interval
            _sendAccum += dt;
            if (_sendAccum < OptimizerConfig.MetricSendInterval) return;
            _sendAccum = 0;

            BroadcastMetrics();
        }

        private static void BroadcastMetrics()
        {
            try
            {
                var networking = LuaCsSetup.Instance?.Networking;
                if (networking == null) return;

                _broadcastCount++;
                // Diagnostic: log first 3 broadcasts to verify server-side is running
                if (_broadcastCount <= 3)
                {
                    var server = GameMain.Server;
                    int clientCount = server?.ConnectedClients?.Count ?? -1;
                    LuaCsLogger.Log($"[ItemOptimizer] MetricRelaySender.Broadcast #{_broadcastCount}: " +
                        $"TickMs={ServerMetrics.AvgTickMs:F1}, clients={clientCount}, " +
                        $"networking={networking.GetType().Name}");
                }

                var msg = networking.Start("ItemOpt.Metrics");
                msg.WriteSingle(ServerMetrics.AvgTickMs);
                msg.WriteByte((byte)Math.Min(ServerMetrics.ClientCount, 255));
                msg.WriteUInt16((ushort)Math.Min(ServerMetrics.EntityCount, 65535));
                msg.WriteSingle(ServerMetrics.AvgPendingPos);
                msg.WriteSingle(ServerMetrics.AvgEventQueue);
                msg.WriteUInt16((ushort)Math.Min(ServerMetrics.SkippedItems, 65535));
                msg.WriteByte((byte)Math.Min(ServerMetrics.TickRate, 255));
                msg.WriteByte((byte)Math.Clamp(ServerMetrics.HealthScore, 0, 100));
                // Per-system perf breakdown (7 floats = 28 bytes)
                msg.WriteSingle(ServerMetrics.PerfGameSession);
                msg.WriteSingle(ServerMetrics.PerfCharacter);
                msg.WriteSingle(ServerMetrics.PerfStatusEffect);
                msg.WriteSingle(ServerMetrics.PerfMapEntity);
                msg.WriteSingle(ServerMetrics.PerfRagdoll);
                msg.WriteSingle(ServerMetrics.PerfPhysics);
                msg.WriteSingle(ServerMetrics.PerfNetworking);
                networking.SendToClient(msg); // broadcast to all
            }
            catch (Exception e)
            {
                // Don't crash the server for metrics
                SafeLogger.HandleException(e);
            }
        }

        internal static void Reset()
        {
            _sendAccum = 0;
            _broadcastCount = 0;
            _tickTimingActive = false;
            _tickStopwatch.Reset();
        }
    }
}
