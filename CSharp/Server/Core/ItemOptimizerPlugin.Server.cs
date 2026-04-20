using System;
using Barotrauma;
using Barotrauma.Networking;
using ItemOptimizerMod.Patches;

namespace ItemOptimizerMod
{
    public sealed partial class ItemOptimizerPlugin
    {
        partial void InitializeServer()
        {
            // Register server-side HashSet dedup transpiler
            if (OptimizerConfig.EnableServerHashSetDedup)
                ServerOptimizer.RegisterPatches(harmony);

            // Register per-system performance timing
            ServerPerfTracker.RegisterPatches(harmony);

            // Wire up per-tick metrics + broadcast callback
            UpdateAllTakeover.OnPreUpdate = MetricRelaySender.BeginTick;
            UpdateAllTakeover.OnPostUpdate = MetricRelaySender.OnPostUpdate;

            // Register sync command handler (client requests sync recording)
            // Register sync config handler (client toggles AllowClientSync in UI)
            try
            {
                var networking = LuaCsSetup.Instance?.Networking;
                networking?.Receive("ItemOpt.SyncCmd", (object[] args) =>
                {
                    if (args.Length > 0 && args[0] is IReadMessage msg)
                        OnSyncCommand(msg);
                });
                networking?.Receive("ItemOpt.SyncCfg", (object[] args) =>
                {
                    if (args.Length > 0 && args[0] is IReadMessage msg)
                        OnSyncCfgReceived(msg);
                });
            }
            catch (Exception e)
            {
                SafeLogger.HandleException(e);
            }

            LuaCsLogger.Log("[ItemOptimizer] Server components initialized " +
                $"(HashSetDedup={OptimizerConfig.EnableServerHashSetDedup}, " +
                $"MetricInterval={OptimizerConfig.MetricSendInterval}s, PerfTracker=ON)");
        }

        private static void OnSyncCommand(IReadMessage msg)
        {
            try
            {
                if (!OptimizerConfig.AllowClientSync)
                {
                    LuaCsLogger.Log("[ItemOptimizer] Sync command rejected: AllowClientSync is disabled on server");
                    return;
                }
                int frames = msg.ReadUInt16();
                SyncRelaySender.Start(frames);
            }
            catch (Exception e)
            {
                SafeLogger.HandleException(e);
            }
        }

        private static void OnSyncCfgReceived(IReadMessage msg)
        {
            try
            {
                bool allow = msg.ReadBoolean();
                OptimizerConfig.AllowClientSync = allow;
                LuaCsLogger.Log($"[ItemOptimizer] AllowClientSync set to {allow} by client");
            }
            catch (Exception e)
            {
                SafeLogger.HandleException(e);
            }
        }

        partial void DisposeServer()
        {
            UpdateAllTakeover.OnPreUpdate = null;
            UpdateAllTakeover.OnPostUpdate = null;
            ServerOptimizer.UnregisterPatches(harmony);
            ServerPerfTracker.Reset();
            MetricRelaySender.Reset();
            SyncRelaySender.Reset();
            ServerMetrics.Reset();
        }
    }
}
