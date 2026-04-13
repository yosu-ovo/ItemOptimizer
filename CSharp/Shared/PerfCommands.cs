using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using ItemOptimizerMod.Patches;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    static class PerfCommands
    {
        private static readonly List<DebugConsole.Command> _registered = new();

        internal static void Register()
        {
            Add("iosnap", "iosnap [path]: Capture mod/item/perf snapshot to JSON.", args =>
            {
                string path = args.Length > 0 ? args[0] : "io_snapshot.json";
                PerfProfiler.StartSnapshot(path);
                DebugConsole.NewMessage($"[ItemOptimizer] Snapshot will be captured next frame -> {PerfProfiler.ResolvePath(path)}", Color.LimeGreen);
            });

            Add("iorecord", "iorecord [frames] [path]: Record N frames of per-item timing to CSV.", args =>
            {
                int frames = 600;
                if (args.Length > 0 && int.TryParse(args[0], out int f)) frames = f;
                string path = args.Length > 1 ? args[1] : "io_record.csv";
                PerfProfiler.StartRecord(frames, path, targetFilter: null);
                DebugConsole.NewMessage($"[ItemOptimizer] Recording {frames} frames -> {PerfProfiler.ResolvePath(path)}", Color.LimeGreen);
            });

            Add("iodump", "iodump [path]: Dump single frame of per-item timing to CSV.", args =>
            {
                string path = args.Length > 0 ? args[0] : "io_dump.csv";
                PerfProfiler.StartRecord(1, path, targetFilter: null);
                DebugConsole.NewMessage($"[ItemOptimizer] Dump will be captured next frame -> {PerfProfiler.ResolvePath(path)}", Color.LimeGreen);
            });

            Add("iorecord_items", "iorecord_items <id1,id2,...> [frames] [path]: Record only specified items.", args =>
            {
                if (args.Length < 1)
                {
                    DebugConsole.NewMessage("Usage: iorecord_items <id1,id2,...> [frames] [path]", Color.Red);
                    return;
                }
                var ids = new HashSet<string>(args[0].Split(','), StringComparer.OrdinalIgnoreCase);
                int frames = 600;
                if (args.Length > 1 && int.TryParse(args[1], out int f)) frames = f;
                string path = args.Length > 2 ? args[2] : "io_record_targeted.csv";
                PerfProfiler.StartRecord(frames, path, targetFilter: ids);
                DebugConsole.NewMessage($"[ItemOptimizer] Recording {frames} frames for {ids.Count} items -> {PerfProfiler.ResolvePath(path)}", Color.LimeGreen);
            });

            Add("ioground", "ioground: List items with ParentInventory==null, grouped by type.", args =>
            {
                var groups = new Dictionary<string, (int total, int holdable)>();
                foreach (var item in Item.ItemList)
                {
                    if (item.ParentInventory != null) continue;
                    string id = item.Prefab?.Identifier.Value ?? "unknown";
                    bool canHold = item.GetComponent<Holdable>() != null;
                    if (!groups.TryGetValue(id, out var g))
                        g = (0, 0);
                    g.total++;
                    if (canHold) g.holdable++;
                    groups[id] = g;
                }

                int totalAll = 0, totalHoldable = 0;
                var sorted = groups.OrderByDescending(kv => kv.Value.total);
                DebugConsole.NewMessage($"[ItemOptimizer] ── Ground Items (ParentInventory==null) ──", Color.Cyan);
                foreach (var kv in sorted)
                {
                    string tag = kv.Value.holdable > 0 ? " [Holdable]" : " [Fixed]";
                    var color = kv.Value.holdable > 0 ? Color.LimeGreen : Color.Gray;
                    DebugConsole.NewMessage($"  {kv.Key}: {kv.Value.total}{tag}", color);
                    totalAll += kv.Value.total;
                    totalHoldable += kv.Value.holdable;
                }
                DebugConsole.NewMessage($"[ItemOptimizer] Total: {totalAll} items, {totalHoldable} holdable, {totalAll - totalHoldable} fixed", Color.Cyan);
            });

            Add("ioground_wired", "ioground_wired: List holdable ground items WITH wire connections (exempted from throttle).", args =>
            {
                int wiredCount = 0;
                int unwiredCount = 0;
                var wiredGroups = new Dictionary<string, int>();

                foreach (var item in Item.ItemList)
                {
                    if (item.ParentInventory != null) continue;
                    if (item.GetComponent<Holdable>() == null) continue;

                    bool hasWires = false;
                    var conns = item.Connections;
                    if (conns != null)
                    {
                        foreach (var c in conns)
                        {
                            if (c.Wires.Count > 0)
                            {
                                hasWires = true;
                                break;
                            }
                        }
                    }

                    if (hasWires)
                    {
                        wiredCount++;
                        string id = item.Prefab?.Identifier.Value ?? "unknown";
                        wiredGroups.TryGetValue(id, out int count);
                        wiredGroups[id] = count + 1;
                    }
                    else
                    {
                        unwiredCount++;
                    }
                }

                DebugConsole.NewMessage($"[ItemOptimizer] ── Holdable Ground Items: Wire Analysis ──", Color.Cyan);
                DebugConsole.NewMessage($"  Wired (EXEMPT from throttle): {wiredCount}", Color.Yellow);
                foreach (var kv in wiredGroups.OrderByDescending(kv => kv.Value))
                    DebugConsole.NewMessage($"    {kv.Key}: {kv.Value}", Color.Yellow);
                DebugConsole.NewMessage($"  Unwired (throttled): {unwiredCount}", Color.LimeGreen);
                DebugConsole.NewMessage($"  Net throttle reduction: {wiredCount} items no longer skipped", Color.Cyan);
            });

            Add("iodiag", "iodiag: Diagnostic info about mod state on both client and server.", args =>
            {
                DebugConsole.NewMessage($"[ItemOptimizer] ── Diagnostic ──", Color.Cyan);
                DebugConsole.NewMessage($"  UpdateAllTakeover.Enabled: {UpdateAllTakeover.Enabled}", Color.White);
                DebugConsole.NewMessage($"  OptimizerConfig.EnableParallelDispatch: {OptimizerConfig.EnableParallelDispatch}", Color.White);
                DebugConsole.NewMessage($"  OptimizerConfig.EnableServerHashSetDedup: {OptimizerConfig.EnableServerHashSetDedup}", Color.White);
                DebugConsole.NewMessage($"  ServerMetrics.HasServerData: {ServerMetrics.HasServerData}", Color.White);
                DebugConsole.NewMessage($"  ServerMetrics.HasPerfData: {ServerMetrics.HasPerfData}", Color.White);
                DebugConsole.NewMessage($"  ServerMetrics.AvgTickMs: {ServerMetrics.AvgTickMs:F2}", Color.White);
                DebugConsole.NewMessage($"  SyncTracker.IsRecording: {SyncTracker.IsRecording}", Color.White);
                DebugConsole.NewMessage($"  SyncTracker.FramesRemaining: {SyncTracker.FramesRemaining}", Color.White);

                var networking = LuaCsSetup.Instance?.Networking;
                DebugConsole.NewMessage($"  LuaCs Networking: {(networking != null ? networking.GetType().Name : "NULL")}", Color.White);
                DebugConsole.NewMessage($"  LuaCs Networking.IsActive: {networking?.IsActive}", Color.White);
                DebugConsole.NewMessage($"  LuaCs Networking.IsSynchronized: {networking?.IsSynchronized}", Color.White);

#if CLIENT
                DebugConsole.NewMessage($"  Build: CLIENT", Color.LimeGreen);
                DebugConsole.NewMessage($"  GameMain.Client: {(GameMain.Client != null ? "connected" : "null")}", Color.White);
                if (GameMain.Client?.ClientPeer != null)
                    DebugConsole.NewMessage($"  ClientPeer type: {GameMain.Client.ClientPeer.GetType().Name}", Color.White);
#endif
#if SERVER
                DebugConsole.NewMessage($"  Build: SERVER", Color.Yellow);
                DebugConsole.NewMessage($"  GameMain.Server: {(GameMain.Server != null ? "running" : "null")}", Color.White);
                DebugConsole.NewMessage($"  OnPreUpdate wired: {UpdateAllTakeover.OnPreUpdate != null}", Color.White);
                DebugConsole.NewMessage($"  OnPostUpdate wired: {UpdateAllTakeover.OnPostUpdate != null}", Color.White);
#endif
            });

            Add("iospike", "iospike [threshold_ms|off|clear]: Toggle spike detector. Default 30ms.", args =>
            {
                if (args.Length == 0)
                {
                    string status = SpikeDetector.Enabled
                        ? $"ON (threshold={SpikeDetector.ThresholdMs}ms)"
                        : "OFF";
                    DebugConsole.NewMessage($"[ItemOptimizer] Spike detector: {status}", Color.Cyan);
                    return;
                }

                string arg = args[0].ToLowerInvariant();
                if (arg == "off")
                {
                    SpikeDetector.SetEnabled(false);
                    OptimizerConfig.EnableSpikeDetector = false;
                }
                else if (arg == "clear")
                {
                    SpikeDetector.ClearLog();
                }
                else if (float.TryParse(arg, out float ms) && ms > 0)
                {
                    SpikeDetector.ThresholdMs = ms;
                    OptimizerConfig.SpikeThresholdMs = ms;
                    OptimizerConfig.EnableSpikeDetector = true;
                    SpikeDetector.SetEnabled(true);
                }
                else
                {
                    DebugConsole.NewMessage("[ItemOptimizer] Usage: iospike [threshold_ms|off|clear]", Color.Yellow);
                }
            });

#if CLIENT
            Add("showserverperf", "showserverperf: Toggle server performance overlay. Shows per-system timing breakdown from the server.", args =>
            {
                ServerPerfOverlay.Visible = !ServerPerfOverlay.Visible;
                DebugConsole.NewMessage(
                    $"[ItemOptimizer] Server perf overlay: {(ServerPerfOverlay.Visible ? "ON" : "OFF")}" +
                    (ServerMetrics.HasServerData ? "" : " (no server data yet — join a multiplayer game)"),
                    ServerPerfOverlay.Visible ? Color.LimeGreen : Color.Yellow);
            });

            Add("iosync", "iosync [frames|stop] [path]: Record server-client sync data. Default 300 frames at 10Hz.", args =>
            {
                // iosync stop → abort current recording
                if (args.Length > 0 && args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    if (SyncTracker.IsRecording)
                    {
                        SyncTracker.StopRecording();
                        DebugConsole.NewMessage("[ItemOptimizer] Sync recording stopped manually", Color.Yellow);
                    }
                    else
                    {
                        // Still force-close in case file handles leaked
                        SyncTracker.ForceClose();
                        DebugConsole.NewMessage("[ItemOptimizer] Sync: no active recording (file handles cleaned up)", Color.Yellow);
                    }
                    return;
                }

                int frames = 300;
                if (args.Length > 0 && int.TryParse(args[0], out int f)) frames = f;
                string path = args.Length > 1 ? args[1] : "io_sync.csv";

                float secs = frames * 0.1f;
                DebugConsole.NewMessage(
                    $"[ItemOptimizer] Sync: {frames} samples at 10Hz = ~{secs:F0}s. Use 'iosync stop' to abort early.",
                    Color.Cyan);

                // Start client-side recording
                SyncTracker.StartRecording(frames, path);

                // Send start command to server via LuaCs networking
                try
                {
                    var networking = LuaCsSetup.Instance?.Networking;
                    if (networking != null)
                    {
                        var msg = networking.Start("ItemOpt.SyncCmd");
                        msg.WriteUInt16((ushort)frames);
                        networking.Send(msg);
                        DebugConsole.NewMessage(
                            $"[ItemOptimizer] Sync recording: {frames} frames -> {PerfProfiler.ResolvePath(path)}",
                            Color.LimeGreen);
                    }
                    else
                    {
                        DebugConsole.NewMessage(
                            "[ItemOptimizer] Not connected to server — sync recording requires multiplayer",
                            Color.Red);
                        SyncTracker.StopRecording();
                    }
                }
                catch (Exception e)
                {
                    LuaCsLogger.HandleException(e, LuaCsMessageOrigin.CSharpMod);
                }
            });
#endif

#if SERVER
            Add("iosync", "iosync [frames]: Start sync snapshot broadcast for connected clients. Default 300 frames.", args =>
            {
                int frames = 300;
                if (args.Length > 0 && int.TryParse(args[0], out int f)) frames = f;
                SyncRelaySender.Start(frames);
            });
#endif
        }

        internal static void Unregister()
        {
            foreach (var cmd in _registered)
                DebugConsole.Commands.Remove(cmd);
            _registered.Clear();
        }

        private static void Add(string name, string help, Action<string[]> onExecute)
        {
            var cmd = new DebugConsole.Command(name, help, onExecute);
            DebugConsole.Commands.Add(cmd);
            _registered.Add(cmd);
        }
    }
}
