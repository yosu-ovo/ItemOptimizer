using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using ItemOptimizerMod.Patches;
using ItemOptimizerMod.SignalGraph;
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
                string path = args.Length > 1 ? args[1] : $"io_record_{DiagnosticHeader.ModVersion}.csv";
                PerfProfiler.StartRecord(frames, path, targetFilter: null);
                DebugConsole.NewMessage($"[ItemOptimizer] Recording {frames} frames -> {PerfProfiler.ResolvePath(path)}", Color.LimeGreen);
            });

            Add("iodump", "iodump [path]: Dump single frame of per-item timing to CSV.", args =>
            {
                string path = args.Length > 0 ? args[0] : $"io_dump_{DiagnosticHeader.ModVersion}.csv";
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
                string path = args.Length > 2 ? args[2] : $"io_record_targeted_{DiagnosticHeader.ModVersion}.csv";
                PerfProfiler.StartRecord(frames, path, targetFilter: ids);
                DebugConsole.NewMessage($"[ItemOptimizer] Recording {frames} frames for {ids.Count} items -> {PerfProfiler.ResolvePath(path)}", Color.LimeGreen);
            });

            Add("ioground", "ioground [path]: List items with ParentInventory==null, grouped by type. Optional: save to file.", args =>
            {
                var groups = new Dictionary<string, (int total, int holdable, string pkg)>();
                foreach (var item in Item.ItemList)
                {
                    if (item.ParentInventory != null) continue;
                    string id = item.Prefab?.Identifier.Value ?? "unknown";
                    string pkg = item.Prefab?.ContentPackage?.Name ?? "Unknown";
                    bool canHold = item.GetComponent<Holdable>() != null;
                    if (!groups.TryGetValue(id, out var g))
                        g = (0, 0, pkg);
                    g.total++;
                    if (canHold) g.holdable++;
                    groups[id] = g;
                }

                int totalAll = 0, totalHoldable = 0;
                var sorted = groups.OrderByDescending(kv => kv.Value.total);

                // If path argument given, write to file
                if (args.Length > 0)
                {
                    string path = PerfProfiler.ResolvePath(args[0]);
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("identifier,count,holdable,package");
                    foreach (var kv in sorted)
                    {
                        sb.Append(kv.Key).Append(',');
                        sb.Append(kv.Value.total).Append(',');
                        sb.Append(kv.Value.holdable > 0 ? "Yes" : "No").Append(',');
                        sb.AppendLine(kv.Value.pkg);
                        totalAll += kv.Value.total;
                        totalHoldable += kv.Value.holdable;
                    }
                    sb.AppendLine($"# Total: {totalAll} items, {totalHoldable} holdable, {totalAll - totalHoldable} fixed");
                    try
                    {
                        string dir = System.IO.Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                        System.IO.File.WriteAllText(path, sb.ToString());
                        DebugConsole.NewMessage($"[ItemOptimizer] Ground items saved to {path} ({totalAll} items)", Color.LimeGreen);
                    }
                    catch (Exception ex)
                    {
                        DebugConsole.NewMessage($"[ItemOptimizer] Failed to write: {ex.Message}", Color.Red);
                    }
                    return;
                }

                DebugConsole.NewMessage($"[ItemOptimizer] ── Ground Items (ParentInventory==null) ──", Color.Cyan);
                foreach (var kv in sorted)
                {
                    string tag = kv.Value.holdable > 0 ? " [Holdable]" : " [Fixed]";
                    var color = kv.Value.holdable > 0 ? Color.LimeGreen : Color.Gray;
                    DebugConsole.NewMessage($"  {kv.Key}: {kv.Value.total}{tag} ({kv.Value.pkg})", color);
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
                DebugConsole.NewMessage($"  OptimizerConfig.EnableServerHashSetDedup: {OptimizerConfig.EnableServerHashSetDedup}", Color.White);
                DebugConsole.NewMessage($"  ServerMetrics.HasServerData: {ServerMetrics.HasServerData}", Color.White);
                DebugConsole.NewMessage($"  ServerMetrics.HasPerfData: {ServerMetrics.HasPerfData}", Color.White);
                DebugConsole.NewMessage($"  ServerMetrics.AvgTickMs: {ServerMetrics.AvgTickMs:F2}", Color.White);
                DebugConsole.NewMessage($"  SyncTracker.IsRecording: {SyncTracker.IsRecording}", Color.White);
                DebugConsole.NewMessage($"  SyncTracker.MaxFrames: {SyncTracker.MaxFrames}", Color.White);

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

            Add("iosgraph", "iosgraph: Show signal graph accelerator diagnostics.", args =>
            {
                string[] modeNames = { "Off", "Accelerate", "Aggressive" };
                int mode = OptimizerConfig.SignalGraphMode;
                string modeName = mode >= 0 && mode < modeNames.Length ? modeNames[mode] : "?";
                DebugConsole.NewMessage($"[ItemOptimizer] SignalGraph mode={mode} ({modeName})", Color.Cyan);
                DebugConsole.NewMessage($"[ItemOptimizer] {SignalGraphEvaluator.GetDiagnostics()}", Color.Cyan);
            });

            Add("iotakeover", "iotakeover [on|off]: Toggle UpdateAllTakeover master switch. OFF = pure vanilla MapEntity.UpdateAll.", args =>
            {
                if (args.Length > 0)
                {
                    string a = args[0].ToLowerInvariant();
                    if (a == "on" || a == "1") UpdateAllTakeover.Enabled = true;
                    else if (a == "off" || a == "0") UpdateAllTakeover.Enabled = false;
                }
                else
                {
                    UpdateAllTakeover.Enabled = !UpdateAllTakeover.Enabled;
                }
                DebugConsole.NewMessage(
                    $"[ItemOptimizer] UpdateAllTakeover: {(UpdateAllTakeover.Enabled ? "ON" : "OFF (pure vanilla)")}",
                    UpdateAllTakeover.Enabled ? Color.LimeGreen : Color.Red);
            });

            Add("iotrace", "iotrace [frames] [sensorId]: Enable sensor trace logging for N frames (default 60). Optional: specific sensor ID.", args =>
            {
                int frames = 60;
                if (args.Length > 0 && int.TryParse(args[0], out int f)) frames = f;
                int targetId = -1;
                if (args.Length > 1 && int.TryParse(args[1], out int tid)) targetId = tid;
                MotionSensorRewrite.TraceFrames = frames;
                MotionSensorRewrite.TraceTargetId = targetId;
                string targetStr = targetId >= 0 ? $", target=Sensor#{targetId}" : " (all sensors)";
                DebugConsole.NewMessage(
                    $"[ItemOptimizer] Sensor trace: {frames} frames{targetStr}. Rewrite registered={MotionSensorRewrite.IsRegistered}, " +
                    $"EnableMotionSensorRewrite={OptimizerConfig.EnableMotionSensorRewrite}, " +
                    $"EnableHullSpatialIndex={OptimizerConfig.EnableHullSpatialIndex}, " +
                    $"Takeover={UpdateAllTakeover.Enabled}",
                    Color.Yellow);
            });

            Add("iowire", "iowire [itemId]: Show all connections and wires for an item.", args =>
            {
                if (args.Length == 0 || !int.TryParse(args[0], out int targetId))
                {
                    DebugConsole.NewMessage("[ItemOptimizer] Usage: iowire <itemId>", Color.Red);
                    return;
                }
                Item target = null;
                foreach (var it in Item.ItemList)
                {
                    if (it.ID == targetId) { target = it; break; }
                }
                if (target == null)
                {
                    DebugConsole.NewMessage($"[ItemOptimizer] Item ID={targetId} not found.", Color.Red);
                    return;
                }
                DebugConsole.NewMessage($"[ItemOptimizer] Item ID={targetId} \"{target.Name}\" components: {string.Join(", ", target.Components.Select(c => c.GetType().Name))}", Color.Cyan);
                if (target.Connections == null || target.Connections.Count == 0)
                {
                    DebugConsole.NewMessage("  No connections.", Color.Gray);
                    return;
                }
                foreach (var conn in target.Connections)
                {
                    string wireInfo = conn.Wires.Count == 0 ? "(no wires)" : "";
                    DebugConsole.NewMessage($"  [{conn.Name}] wires={conn.Wires.Count} {wireInfo}", Color.White);
                    foreach (var wire in conn.Wires)
                    {
                        if (wire == null) continue;
                        var otherConn = wire.OtherConnection(conn);
                        string otherStr = otherConn != null
                            ? $"→ Item#{otherConn.Item.ID} \"{otherConn.Item.Name}\" [{otherConn.Name}]"
                            : "→ (dangling)";
                        DebugConsole.NewMessage($"    wire#{wire.Item.ID}: {otherStr}", Color.LimeGreen);
                    }
                }
            });

            Add("iohull", "iohull [on|off]: Toggle hull spatial index for motion sensors.", args =>
            {
                if (args.Length > 0)
                {
                    string a = args[0].ToLowerInvariant();
                    if (a == "on" || a == "1") OptimizerConfig.EnableHullSpatialIndex = true;
                    else if (a == "off" || a == "0") OptimizerConfig.EnableHullSpatialIndex = false;
                }
                else
                {
                    OptimizerConfig.EnableHullSpatialIndex = !OptimizerConfig.EnableHullSpatialIndex;
                }
                DebugConsole.NewMessage(
                    $"[ItemOptimizer] HullSpatialIndex: {(OptimizerConfig.EnableHullSpatialIndex ? "ON" : "OFF")}",
                    OptimizerConfig.EnableHullSpatialIndex ? Color.LimeGreen : Color.Yellow);
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
                // iosync stop → flush and stop current recording
                if (args.Length > 0 && args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    if (SyncTracker.IsRecording)
                    {
                        SyncTracker.FlushAndStop();
                        DebugConsole.NewMessage("[ItemOptimizer] Sync recording stopped manually", Color.Yellow);
                    }
                    else
                    {
                        DebugConsole.NewMessage("[ItemOptimizer] Sync: no active recording", Color.Yellow);
                    }
                    return;
                }

                int frames = 300;
                if (args.Length > 0 && int.TryParse(args[0], out int f)) frames = f;
                string path = args.Length > 1 ? args[1] : "io_sync.csv";

                // Send start command to server FIRST, then start client recording.
                // This ensures server is already sending by the time client is ready.
                try
                {
                    var networking = LuaCsSetup.Instance?.Networking;
                    if (networking == null)
                    {
                        DebugConsole.NewMessage(
                            "[ItemOptimizer] Not connected to server — sync recording requires multiplayer",
                            Color.Red);
                        return;
                    }

                    var msg = networking.Start("ItemOpt.SyncCmd");
                    msg.WriteUInt16((ushort)frames);
                    networking.Send(msg);

                    // Start client-side recording AFTER sending server command
                    SyncTracker.StartRecording(frames, path);

                    float secs = frames * 0.1f;
                    DebugConsole.NewMessage(
                        $"[ItemOptimizer] Sync recording: {frames} frames at 10Hz (~{secs:F0}s) -> {PerfProfiler.ResolvePath(path)}",
                        Color.LimeGreen);
                    DebugConsole.NewMessage(
                        $"[ItemOptimizer] Use 'iosync stop' to abort early. Auto-timeout after 5s of no data.",
                        Color.Cyan);
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

            Add("ioemit", "ioemit [targetItemId|trace <frames> [targetId]]: List emit edges or trace emit delivery.", args =>
            {
                if (!SignalGraphEvaluator.IsCompiled)
                {
                    DebugConsole.NewMessage("[ItemOptimizer] SignalGraph not compiled.", Color.Red);
                    return;
                }

                // ioemit trace <frames> [targetId]
                if (args.Length > 0 && args[0].Equals("trace", StringComparison.OrdinalIgnoreCase))
                {
                    int frames = 30;
                    if (args.Length > 1 && int.TryParse(args[1], out int f)) frames = f;
                    int traceTarget = -1;
                    if (args.Length > 2 && int.TryParse(args[2], out int tt)) traceTarget = tt;
                    SignalGraphEvaluator.EmitTraceFrames = frames;
                    SignalGraphEvaluator.EmitTraceTargetId = traceTarget;
                    string targetStr = traceTarget >= 0 ? $" for target Item#{traceTarget}" : " (all targets)";
                    DebugConsole.NewMessage($"[ItemOptimizer] Emit trace: {frames} frames{targetStr}", Color.Yellow);
                    return;
                }

                int filterTargetId = -1;
                if (args.Length > 0 && int.TryParse(args[0], out int tid)) filterTargetId = tid;

                var diag = SignalGraphEvaluator.GetEmitDiagnostics(filterTargetId);
                DebugConsole.NewMessage($"[ItemOptimizer] ── Emit Edges ({diag.Total} total, showing {diag.Lines.Length}) ──", Color.Cyan);
                foreach (var line in diag.Lines)
                    DebugConsole.NewMessage(line.text, line.color);
                DebugConsole.NewMessage($"[ItemOptimizer] EmitSuppressed={SignalGraphEvaluator.SuppressEmitOnClient}", Color.Cyan);
            });

            Add("iocb", "iocb [trace <frames>|clear]: CircuitBox boundary diagnostics (writes to diag.log).", args =>
            {
                // iocb trace <frames> — enable PushCaptureSignal logging for N frames
                if (args.Length > 0 && args[0].Equals("trace", StringComparison.OrdinalIgnoreCase))
                {
                    int frames = 120;
                    if (args.Length > 1 && int.TryParse(args[1], out int f)) frames = f;
                    SignalGraphEvaluator.PushTraceFrames = frames;
                    DiagLog.Write($"=== PushCapture trace started: {frames} frames ===");
                    DebugConsole.NewMessage($"[ItemOptimizer] Push-capture trace: {frames} frames → diag.log", Color.Yellow);
                    return;
                }

                // iocb clear — clear diag.log
                if (args.Length > 0 && args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    DiagLog.Clear();
                    DebugConsole.NewMessage("[ItemOptimizer] diag.log cleared.", Color.Yellow);
                    return;
                }

                if (!SignalGraphEvaluator.IsCompiled)
                {
                    DebugConsole.NewMessage("[ItemOptimizer] SignalGraph not compiled.", Color.Red);
                    return;
                }

                var graph = SignalGraphEvaluator.GetGraphForDiagnostics();
                if (graph == null) { DebugConsole.NewMessage("No graph.", Color.Red); return; }

                DiagLog.Write("=== iocb dump start ===");

                // CB-internal nodes
                var lines = new System.Collections.Generic.List<string>();
                int cbCount = 0;
                for (int i = 0; i < graph.Nodes.Length; i++)
                {
                    ref var node = ref graph.Nodes[i];
                    var item = node.Item;
                    if (item?.ParentInventory?.Owner is Item cbOwner &&
                        cbOwner.GetComponent<CircuitBox>() != null)
                    {
                        cbCount++;
                        bool isAccel = SignalGraphEvaluator.IsAccelerated(node.ItemId);
                        string inRegs = string.Join(",", node.InputRegs);
                        string outRegs = string.Join(",", node.OutputRegs);
                        string inVals = string.Join(",", Array.ConvertAll(node.InputRegs, r => r >= 0 ? $"\"{graph.Registers[r] ?? "null"}\"" : "-"));
                        string outVals = string.Join(",", Array.ConvertAll(node.OutputRegs, r => r >= 0 ? $"\"{graph.Registers[r] ?? "null"}\"" : "-"));
                        lines.Add($"[{i}] Item#{node.ItemId} \"{item.Name}\" type={node.Type} accel={isAccel} partial={node.PartialOnly}");
                        lines.Add($"     inRegs=[{inRegs}] outRegs=[{outRegs}]");
                        lines.Add($"     inVals=[{inVals}] outVals=[{outVals}]");
                    }
                }
                DiagLog.WriteBlock($"CB-Internal Nodes ({cbCount})", lines.ToArray());

                // CaptureInputMap (all entries, not just CB)
                lines.Clear();
                if (graph.CaptureInputMap != null)
                {
                    foreach (var kv in graph.CaptureInputMap)
                    {
                        string val = kv.Value >= 0 && kv.Value < graph.Registers.Length
                            ? $"\"{graph.Registers[kv.Value] ?? "null"}\""
                            : "(invalid reg)";
                        var findItem = Entity.FindEntityByID(kv.Key.Item1) as Item;
                        string itemName = findItem?.Name ?? "?";
                        bool isCB = findItem?.ParentInventory?.Owner is Item ownerIt &&
                                    ownerIt.GetComponent<CircuitBox>() != null;
                        lines.Add($"({kv.Key.Item1},{kv.Key.Item2}) → reg[{kv.Value}]={val}  \"{itemName}\" {(isCB ? "[CB-INTERNAL]" : "")}");
                    }
                }
                else
                    lines.Add("CaptureInputMap is NULL!");
                DiagLog.WriteBlock($"CaptureInputMap ({lines.Count} entries)", lines.ToArray());

                // CaptureEdges
                lines.Clear();
                for (int i = 0; i < graph.CaptureEdges.Length; i++)
                {
                    ref var ce = ref graph.CaptureEdges[i];
                    var srcConn = ce.SourceConnection;
                    string src = srcConn != null
                        ? $"Item#{srcConn.Item.ID} \"{srcConn.Item.Name}\" [{srcConn.Name}]"
                        : "?";
                    lines.Add($"#{i}: {src} → reg[{ce.TargetRegister}]");
                }
                DiagLog.WriteBlock($"CaptureEdges ({graph.CaptureEdges.Length})", lines.ToArray());

                // CB boundary emit edges
                lines.Clear();
                int cbEmitCount = 0;
                for (int i = 0; i < graph.EmitEdges.Length; i++)
                {
                    ref var emit = ref graph.EmitEdges[i];
                    if (emit.SourceItem?.ParentInventory?.Owner is Item cbOw &&
                        cbOw.GetComponent<CircuitBox>() != null)
                    {
                        string val = emit.SourceRegister >= 0 && emit.SourceRegister < graph.Registers.Length
                            ? $"\"{graph.Registers[emit.SourceRegister] ?? "null"}\""
                            : "(invalid)";
                        string tgt = emit.TargetConnection != null
                            ? $"Item#{emit.TargetConnection.Item.ID} \"{emit.TargetConnection.Item.Name}\" [{emit.TargetConnection.Name}]"
                            : "?";
                        lines.Add($"#{i}: reg[{emit.SourceRegister}]={val} | {emit.SourceItem.Name}(#{emit.SourceItem.ID}) → {tgt}");
                        cbEmitCount++;
                    }
                }
                DiagLog.WriteBlock($"CB Boundary Emit Edges ({cbEmitCount}/{graph.EmitEdges.Length} total)", lines.ToArray());

                // EvalOrder
                lines.Clear();
                for (int i = 0; i < graph.EvalOrder.Length; i++)
                {
                    int ni = graph.EvalOrder[i];
                    ref var node = ref graph.Nodes[ni];
                    lines.Add($"[{i}] nodeIdx={ni} Item#{node.ItemId} \"{node.Item?.Name}\" type={node.Type}");
                }
                DiagLog.WriteBlock($"EvalOrder ({graph.EvalOrder.Length})", lines.ToArray());

                DiagLog.Write($"=== iocb dump end: {cbCount} CB nodes, {cbEmitCount} CB emits, mode={SignalGraphEvaluator.Mode} ===");
                DebugConsole.NewMessage($"[ItemOptimizer] CB diag written to diag.log ({cbCount} CB nodes, {cbEmitCount} CB emits)", Color.Cyan);
            });

            Add("iodoor", "iodoor <itemId>: Show Door component state (isOpen, PredictedState, openState).", args =>
            {
                if (args.Length == 0 || !int.TryParse(args[0], out int targetId))
                {
                    DebugConsole.NewMessage("[ItemOptimizer] Usage: iodoor <itemId>", Color.Red);
                    return;
                }
                Item target = null;
                foreach (var it in Item.ItemList)
                {
                    if (it.ID == targetId) { target = it; break; }
                }
                if (target == null)
                {
                    DebugConsole.NewMessage($"[ItemOptimizer] Item ID={targetId} not found.", Color.Red);
                    return;
                }
                var door = target.GetComponent<Door>();
                if (door == null)
                {
                    DebugConsole.NewMessage($"[ItemOptimizer] Item ID={targetId} has no Door component.", Color.Red);
                    return;
                }
                bool isAccel = SignalGraphEvaluator.IsAccelerated((ushort)targetId);
                DebugConsole.NewMessage($"[ItemOptimizer] Door ID={targetId} \"{target.Name}\"", Color.Cyan);
                DebugConsole.NewMessage($"  isOpen={door.IsOpen} PredictedState={door.PredictedState} OpenState={door.OpenState:F3}", Color.White);
                DebugConsole.NewMessage($"  IsStuck={door.IsStuck} IsJammed={door.IsJammed} Body.Enabled={door.Body?.Enabled}", Color.White);
                DebugConsole.NewMessage($"  Accelerated={isAccel} (should be false for doors)", isAccel ? Color.Red : Color.LimeGreen);

                // Show connections
                if (target.Connections != null)
                {
                    foreach (var conn in target.Connections)
                    {
                        if (conn.Name == "set_state" || conn.Name == "toggle")
                        {
                            string lastRecv = conn.LastReceivedSignal.value ?? "(null)";
                            DebugConsole.NewMessage($"  [{conn.Name}] LastReceived=\"{lastRecv}\" wires={conn.Wires.Count}", Color.Yellow);
                        }
                    }
                }
            });

            Add("iocapture", "iocapture: Show CaptureEdge sources and their current LastSentSignal values.", args =>
            {
                var graph = SignalGraphEvaluator.GetGraphForDiagnostics();
                if (graph == null)
                {
                    DebugConsole.NewMessage("[ItemOptimizer] SignalGraph not compiled.", Color.Red);
                    return;
                }
                var captures = graph.CaptureEdges;
                var regs = graph.Registers;
                DebugConsole.NewMessage($"[ItemOptimizer] ── CaptureEdges ({captures.Length}) ──", Color.Cyan);
                for (int i = 0; i < captures.Length && i < 50; i++)
                {
                    ref var cap = ref captures[i];
                    var conn = cap.SourceConnection;
                    string lastSent = conn?.LastSentSignal.value ?? "(null-conn)";
                    string regVal = cap.TargetRegister >= 0 && cap.TargetRegister < regs.Length
                        ? (regs[cap.TargetRegister] ?? "(null)")
                        : "(bad reg)";
                    string itemName = conn?.Item?.Name ?? "?";
                    int itemId = conn?.Item?.ID ?? -1;
                    string connName = conn?.Name ?? "?";
                    DebugConsole.NewMessage(
                        $"  #{i}: {itemName}(#{itemId}) [{connName}] LastSent=\"{lastSent}\" → reg[{cap.TargetRegister}]=\"{regVal}\"",
                        lastSent == regVal ? Color.LimeGreen : Color.Yellow);
                }
            });

            Add("iochain", "iochain <targetItemId>: Trace full signal chain from CaptureEdge → node → EmitEdge for a target item.", args =>
            {
                if (args.Length == 0 || !int.TryParse(args[0], out int targetId))
                {
                    DebugConsole.NewMessage("[ItemOptimizer] Usage: iochain <targetItemId>", Color.Red);
                    return;
                }
                var graph = SignalGraphEvaluator.GetGraphForDiagnostics();
                if (graph == null)
                {
                    DebugConsole.NewMessage("[ItemOptimizer] SignalGraph not compiled.", Color.Red);
                    return;
                }

                var emits = graph.EmitEdges;
                var regs = graph.Registers;
                var nodes = graph.Nodes;
                var captures = graph.CaptureEdges;
                bool found = false;

                for (int i = 0; i < emits.Length; i++)
                {
                    ref var emit = ref emits[i];
                    int emitTargetId = emit.TargetConnection?.Item?.ID ?? -1;
                    if (emitTargetId != targetId) continue;
                    found = true;

                    string val = emit.SourceRegister >= 0 && emit.SourceRegister < regs.Length
                        ? (regs[emit.SourceRegister] ?? "(null)")
                        : "(bad reg)";
                    string targetName = emit.TargetConnection?.Item?.Name ?? "?";
                    string connName = emit.TargetConnection?.Name ?? "?";
                    string srcName = emit.SourceItem?.Name ?? "?";
                    int srcId = emit.SourceItem?.ID ?? -1;

                    DebugConsole.NewMessage($"[ItemOptimizer] ── Chain → {targetName}(#{targetId}) [{connName}] ──", Color.Cyan);
                    DebugConsole.NewMessage($"  EmitEdge #{i}: reg[{emit.SourceRegister}]=\"{val}\" from {srcName}(#{srcId})", Color.White);

                    // Find which node produces this register
                    int producerIdx = -1;
                    for (int n = 0; n < nodes.Length; n++)
                    {
                        if (nodes[n].OutputRegs != null)
                        {
                            foreach (int outReg in nodes[n].OutputRegs)
                            {
                                if (outReg == emit.SourceRegister) { producerIdx = n; break; }
                            }
                        }
                        if (producerIdx >= 0) break;
                    }

                    if (producerIdx >= 0)
                    {
                        ref var node = ref nodes[producerIdx];
                        string nodeName = node.Item?.Name ?? "?";
                        int nodeId = node.ItemId;
                        DebugConsole.NewMessage($"  Producer: {node.Type} {nodeName}(#{nodeId}) stateIdx={node.StateIndex} partial={node.PartialOnly}", Color.Yellow);

                        // Show BoolOp internal state if applicable
                        if (node.Type == SignalNodeType.BoolOp_And || node.Type == SignalNodeType.BoolOp_Or)
                        {
                            var bd = NodeEvaluators.GetBoolOpDiag(node.StateIndex);
                            if (bd.valid)
                            {
                                int reqInputs = node.Type == SignalNodeType.BoolOp_Or ? 1 : 2;
                                int recv = (bd.tsr0 <= bd.timeFrame ? 1 : 0) + (bd.tsr1 <= bd.timeFrame ? 1 : 0);
                                string chosen = recv >= reqInputs ? $"output=\"{bd.output}\"" : $"falseOutput=\"{bd.falseOutput}\"";
                                DebugConsole.NewMessage(
                                    $"    BoolOp: tsr0={bd.tsr0:F4} tsr1={bd.tsr1:F4} timeFrame={bd.timeFrame:F4} " +
                                    $"recv={recv} reqInputs={reqInputs} → {chosen}",
                                    recv >= reqInputs ? Color.LimeGreen : Color.Red);
                                DebugConsole.NewMessage(
                                    $"    BoolOp: output=\"{bd.output ?? "(null)"}\" falseOutput=\"{bd.falseOutput ?? "(null)"}\"",
                                    Color.White);
                            }
                        }

                        // Show input registers
                        if (node.InputRegs != null)
                        {
                            for (int ir = 0; ir < node.InputRegs.Length; ir++)
                            {
                                int inReg = node.InputRegs[ir];
                                if (inReg < 0)
                                {
                                    DebugConsole.NewMessage($"    Input[{ir}]: unconnected", Color.Gray);
                                    continue;
                                }
                                string inVal = inReg < regs.Length ? (regs[inReg] ?? "(null)") : "(bad)";
                                DebugConsole.NewMessage($"    Input[{ir}]: reg[{inReg}]=\"{inVal}\"", Color.White);

                                // Check if this is a CaptureEdge
                                for (int c = 0; c < captures.Length; c++)
                                {
                                    if (captures[c].TargetRegister == inReg)
                                    {
                                        var conn = captures[c].SourceConnection;
                                        string cItemName = conn?.Item?.Name ?? "?";
                                        int cItemId = conn?.Item?.ID ?? -1;
                                        string cConnName = conn?.Name ?? "?";
                                        string lastSent = conn?.LastSentSignal.value ?? "(null)";
                                        DebugConsole.NewMessage(
                                            $"      ← CaptureEdge #{c}: {cItemName}(#{cItemId}) [{cConnName}] LastSent=\"{lastSent}\"",
                                            Color.Magenta);
                                        break;
                                    }
                                }

                                // Check if this comes from another node
                                for (int n2 = 0; n2 < nodes.Length; n2++)
                                {
                                    if (nodes[n2].OutputRegs != null)
                                    {
                                        foreach (int outReg in nodes[n2].OutputRegs)
                                        {
                                            if (outReg == inReg)
                                            {
                                                string n2Name = nodes[n2].Item?.Name ?? "?";
                                                DebugConsole.NewMessage(
                                                    $"      ← Node: {nodes[n2].Type} {n2Name}(#{nodes[n2].ItemId})",
                                                    Color.Orange);
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // Show output registers
                        if (node.OutputRegs != null)
                        {
                            for (int or = 0; or < node.OutputRegs.Length; or++)
                            {
                                int outReg = node.OutputRegs[or];
                                if (outReg < 0) continue;
                                string outVal = outReg < regs.Length ? (regs[outReg] ?? "(null)") : "(bad)";
                                DebugConsole.NewMessage($"    Output[{or}]: reg[{outReg}]=\"{outVal}\"", Color.LimeGreen);
                            }
                        }
                    }
                    else
                    {
                        // Register might be a direct CaptureEdge passthrough
                        for (int c = 0; c < captures.Length; c++)
                        {
                            if (captures[c].TargetRegister == emit.SourceRegister)
                            {
                                var conn = captures[c].SourceConnection;
                                string cItemName = conn?.Item?.Name ?? "?";
                                int cItemId = conn?.Item?.ID ?? -1;
                                string lastSent = conn?.LastSentSignal.value ?? "(null)";
                                DebugConsole.NewMessage(
                                    $"  Direct CaptureEdge #{c}: {cItemName}(#{cItemId}) [{conn?.Name}] LastSent=\"{lastSent}\"",
                                    Color.Magenta);
                                break;
                            }
                        }
                    }
                }

                if (!found)
                    DebugConsole.NewMessage($"[ItemOptimizer] No emit edges targeting Item#{targetId}.", Color.Red);
            });

            Add("ioreg", "ioreg <regIdx>: Show what writes to a register (CaptureEdge, node output, or back-edge).", args =>
            {
                if (args.Length == 0 || !int.TryParse(args[0], out int regIdx))
                {
                    DebugConsole.NewMessage("[ItemOptimizer] Usage: ioreg <registerIndex>", Color.Red);
                    return;
                }
                var graph = SignalGraphEvaluator.GetGraphForDiagnostics();
                if (graph == null)
                {
                    DebugConsole.NewMessage("[ItemOptimizer] SignalGraph not compiled.", Color.Red);
                    return;
                }
                var regs = graph.Registers;
                if (regIdx < 0 || regIdx >= regs.Length)
                {
                    DebugConsole.NewMessage($"[ItemOptimizer] Invalid register index {regIdx} (max={regs.Length - 1}).", Color.Red);
                    return;
                }
                string val = regs[regIdx] ?? "(null)";
                DebugConsole.NewMessage($"[ItemOptimizer] reg[{regIdx}]=\"{val}\"", Color.Cyan);

                // Check CaptureEdges
                var captures = graph.CaptureEdges;
                for (int i = 0; i < captures.Length; i++)
                {
                    if (captures[i].TargetRegister == regIdx)
                    {
                        var conn = captures[i].SourceConnection;
                        string cName = conn?.Item?.Name ?? "?";
                        int cId = conn?.Item?.ID ?? -1;
                        string lastSent = conn?.LastSentSignal.value ?? "(null)";
                        DebugConsole.NewMessage($"  ← CaptureEdge #{i}: {cName}(#{cId}) [{conn?.Name}] LastSent=\"{lastSent}\"", Color.Magenta);
                    }
                }
                // Check node outputs
                var nodes = graph.Nodes;
                for (int n = 0; n < nodes.Length; n++)
                {
                    if (nodes[n].OutputRegs == null) continue;
                    for (int o = 0; o < nodes[n].OutputRegs.Length; o++)
                    {
                        if (nodes[n].OutputRegs[o] == regIdx)
                        {
                            string nName = nodes[n].Item?.Name ?? "?";
                            DebugConsole.NewMessage($"  ← Node[{n}]: {nodes[n].Type} {nName}(#{nodes[n].ItemId}) Output[{o}]", Color.Yellow);
                        }
                    }
                }
                // Check back-edges
                var backEdges = graph.BackEdges;
                var backBuf = graph.BackEdgeBuffer;
                for (int b = 0; b < backEdges.Length; b++)
                {
                    if (backEdges[b].TargetRegister == regIdx)
                    {
                        string bufVal = b < backBuf.Length ? (backBuf[b] ?? "(null)") : "(bad)";
                        DebugConsole.NewMessage($"  ← BackEdge #{b}: src=reg[{backEdges[b].SourceRegister}] buf=\"{bufVal}\"", Color.Orange);
                    }
                }
            });

            Add("ionode", "ionode <itemId>: Inspect a signal graph node's internal state by item ID.", args =>
            {
                if (args.Length == 0 || !int.TryParse(args[0], out int targetId))
                {
                    DebugConsole.NewMessage("[ItemOptimizer] Usage: ionode <itemId>", Color.Red);
                    return;
                }
                var graph = SignalGraphEvaluator.GetGraphForDiagnostics();
                if (graph == null)
                {
                    DebugConsole.NewMessage("[ItemOptimizer] SignalGraph not compiled.", Color.Red);
                    return;
                }
                var nodes = graph.Nodes;
                var regs = graph.Registers;
                bool found = false;
                for (int n = 0; n < nodes.Length; n++)
                {
                    if (nodes[n].ItemId != targetId) continue;
                    found = true;
                    ref var node = ref nodes[n];
                    string name = node.Item?.Name ?? "?";
                    DebugConsole.NewMessage($"[ItemOptimizer] Node[{n}]: {node.Type} {name}(#{node.ItemId}) stateIdx={node.StateIndex} partial={node.PartialOnly}", Color.Cyan);

                    // Eval order position
                    int evalPos = -1;
                    for (int e = 0; e < graph.EvalOrder.Length; e++)
                    {
                        if (graph.EvalOrder[e] == n) { evalPos = e; break; }
                    }
                    DebugConsole.NewMessage($"  EvalOrder position: {evalPos} / {graph.EvalOrder.Length}", Color.White);

                    // Inputs
                    if (node.InputRegs != null)
                    {
                        for (int ir = 0; ir < node.InputRegs.Length; ir++)
                        {
                            int inReg = node.InputRegs[ir];
                            if (inReg < 0)
                            {
                                DebugConsole.NewMessage($"  Input[{ir}]: unconnected", Color.Gray);
                                continue;
                            }
                            string v = inReg < regs.Length ? (regs[inReg] ?? "(null)") : "(bad)";
                            DebugConsole.NewMessage($"  Input[{ir}]: reg[{inReg}]=\"{v}\"", Color.White);
                        }
                    }
                    // Outputs
                    if (node.OutputRegs != null)
                    {
                        for (int or = 0; or < node.OutputRegs.Length; or++)
                        {
                            int outReg = node.OutputRegs[or];
                            if (outReg < 0) continue;
                            string v = outReg < regs.Length ? (regs[outReg] ?? "(null)") : "(bad)";
                            DebugConsole.NewMessage($"  Output[{or}]: reg[{outReg}]=\"{v}\"", Color.LimeGreen);
                        }
                    }
                    // Type-specific state
                    if (node.Type == SignalNodeType.BoolOp_And || node.Type == SignalNodeType.BoolOp_Or)
                    {
                        var bd = NodeEvaluators.GetBoolOpDiag(node.StateIndex);
                        if (bd.valid)
                        {
                            int reqInputs = node.Type == SignalNodeType.BoolOp_Or ? 1 : 2;
                            int recv = (bd.tsr0 <= bd.timeFrame ? 1 : 0) + (bd.tsr1 <= bd.timeFrame ? 1 : 0);
                            DebugConsole.NewMessage(
                                $"  BoolOp state: tsr0={bd.tsr0:F4} tsr1={bd.tsr1:F4} timeFrame={bd.timeFrame:F4} recv={recv} reqInputs={reqInputs}",
                                recv >= reqInputs ? Color.LimeGreen : Color.Red);
                            DebugConsole.NewMessage(
                                $"  BoolOp config: output=\"{bd.output ?? "(null)"}\" falseOutput=\"{bd.falseOutput ?? "(null)"}\"",
                                Color.White);
                        }
                    }
                }
                if (!found)
                    DebugConsole.NewMessage($"[ItemOptimizer] No graph node for Item#{targetId}.", Color.Red);
            });

            Add("ionative", "ionative [on|off|parallel|direct]: Control NativeRuntime. No args = show status.", args =>
            {
                if (args.Length > 0)
                {
                    string a = args[0].ToLowerInvariant();
                    if (a == "on" || a == "1")
                    {
                        if (!ToggleValidator.CanEnableNativeRuntime()) return;
                        OptimizerConfig.EnableNativeRuntime = true;
                        // Always restart to pick up new round data (fixes stale zones after round restart)
                        if (World.NativeRuntimeBridge.IsEnabled)
                            World.NativeRuntimeBridge.OnRoundEnd();
                        World.NativeRuntimeBridge.OnRoundStart();
                    }
                    else if (a == "off" || a == "0")
                    {
                        if (World.NativeRuntimeBridge.IsEnabled)
                            World.NativeRuntimeBridge.OnRoundEnd();
                        OptimizerConfig.EnableNativeRuntime = false;
                    }
                    else if (a == "parallel")
                    {
                        var rt = World.NativeRuntimeBridge.Runtime;
                        if (rt != null)
                        {
                            rt.Mode = World.NativeRuntime.RuntimeMode.Parallel;
                            DebugConsole.NewMessage("[ItemOptimizer] NativeRuntime mode: Parallel", Color.LimeGreen);
                        }
                        else
                            DebugConsole.NewMessage("[ItemOptimizer] NativeRuntime not active.", Color.Red);
                        return;
                    }
                    else if (a == "direct")
                    {
                        var rt = World.NativeRuntimeBridge.Runtime;
                        if (rt != null)
                        {
                            rt.Mode = World.NativeRuntime.RuntimeMode.Direct;
                            DebugConsole.NewMessage("[ItemOptimizer] NativeRuntime mode: Direct", Color.LimeGreen);
                        }
                        else
                            DebugConsole.NewMessage("[ItemOptimizer] NativeRuntime not active.", Color.Red);
                        return;
                    }
                }
                DebugConsole.NewMessage(
                    $"[ItemOptimizer] NativeRuntime: {(World.NativeRuntimeBridge.IsEnabled ? "ON" : "OFF")}",
                    World.NativeRuntimeBridge.IsEnabled ? Color.LimeGreen : Color.Yellow);
                if (World.NativeRuntimeBridge.IsEnabled)
                {
                    var rt = World.NativeRuntimeBridge.Runtime;
                    if (rt != null)
                        DebugConsole.NewMessage(
                            $"[ItemOptimizer]   Mode: {rt.Mode}", Color.White);
                    if (World.NativeRuntimeBridge.LastStartupInfo != null)
                        DebugConsole.NewMessage(
                            $"[ItemOptimizer]   {World.NativeRuntimeBridge.LastStartupInfo}", Color.White);
                    // Show current zone tiers with distance to nearest player
                    if (rt != null)
                    {
                        // Find nearest player position for distance calc
                        Vector2 playerPos = Character.Controlled?.WorldPosition
                            ?? GameMain.GameScreen?.Cam?.GetPosition()
                            ?? Vector2.Zero;

                        foreach (var zone in rt.Graph.Zones)
                        {
                            string name = zone is World.SubmarineZone szz
                                ? (szz.Submarine?.Info?.Name ?? $"Zone{szz.Id}")
                                : $"Zone{zone.Id}";
                            float dist = Vector2.Distance(playerPos, zone.Position);
                            bool playerAboard = zone is World.SubmarineZone sz2
                                && Character.Controlled?.Submarine == sz2.Submarine;
                            string aboardTag = playerAboard ? " [YOU]" : "";
                            var tierColor = zone.Tier <= World.ZoneTier.Nearby ? Color.LimeGreen
                                : zone.Tier == World.ZoneTier.Passive ? Color.Yellow
                                : Color.Gray;
                            DebugConsole.NewMessage(
                                $"[ItemOptimizer]   {name}: tier={zone.Tier}, dist={dist:F0}, components={zone.Components.Count}{aboardTag}",
                                tierColor);
                        }
                    }
                }
            });

            Add("iozone", "iozone [csv <path>]: List all structures with item counts, distance, zone tier.", args =>
            {
                // Gather player position
                Vector2 playerPos = Character.Controlled?.WorldPosition
                    ?? GameMain.GameScreen?.Cam?.GetPosition()
                    ?? Vector2.Zero;

                // Group items by submarine
                var subGroups = new Dictionary<Submarine, int>();
                int looseCount = 0;
                foreach (var item in Item.ItemList)
                {
                    if (item == null || item.Removed) continue;
                    if (item.Submarine != null)
                    {
                        subGroups.TryGetValue(item.Submarine, out int cnt);
                        subGroups[item.Submarine] = cnt + 1;
                    }
                    else
                    {
                        looseCount++;
                    }
                }

                // Build zone lookup if NativeRuntime is available
                Dictionary<Submarine, World.SubmarineZone> subToZone = null;
                if (World.NativeRuntimeBridge.IsEnabled && World.NativeRuntimeBridge.Runtime != null)
                {
                    var zones = World.NativeRuntimeBridge.Runtime.Graph.Zones;
                    subToZone = new Dictionary<Submarine, World.SubmarineZone>(zones.Count);
                    foreach (var z in zones)
                    {
                        if (z is World.SubmarineZone sz && sz.Submarine != null)
                            subToZone[sz.Submarine] = sz;
                    }
                }

                // Build sorted list
                var entries = new List<(string name, string subType, int items, float dist, string tier, int components)>();
                foreach (var kv in subGroups)
                {
                    var sub = kv.Key;
                    string name = sub.Info?.Name ?? $"Sub#{sub.ID}";
                    string subType = sub.Info?.Type.ToString() ?? "Unknown";
                    float dist = Vector2.Distance(playerPos, sub.WorldPosition);
                    string tier = "N/A";
                    int components = 0;
                    if (subToZone != null && subToZone.TryGetValue(sub, out var sz))
                    {
                        tier = sz.Tier.ToString();
                        components = sz.Components.Count;
                    }
                    entries.Add((name, subType, kv.Value, dist, tier, components));
                }
                entries.Sort((a, b) => a.dist.CompareTo(b.dist));

                // CSV export
                if (args.Length >= 1 && args[0].Equals("csv", StringComparison.OrdinalIgnoreCase))
                {
                    string path = args.Length > 1 ? args[1] : "io_zone.csv";
                    path = PerfProfiler.ResolvePath(path);
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("name,type,items,distance,tier,native_components");
                    foreach (var e in entries)
                        sb.AppendLine($"{e.name},{e.subType},{e.items},{e.dist:F0},{e.tier},{e.components}");
                    sb.AppendLine($"[loose],,-,{looseCount},-,N/A,0");
                    try
                    {
                        string dir = System.IO.Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                        System.IO.File.WriteAllText(path, sb.ToString());
                        DebugConsole.NewMessage($"[ItemOptimizer] Zone data saved to {path}", Color.LimeGreen);
                    }
                    catch (Exception ex)
                    {
                        DebugConsole.NewMessage($"[ItemOptimizer] Failed to write: {ex.Message}", Color.Red);
                    }
                    return;
                }

                // Console output
                DebugConsole.NewMessage($"[ItemOptimizer] ── Zone / Structure Distribution ──", Color.Cyan);
                int totalItems = 0;
                foreach (var e in entries)
                {
                    totalItems += e.items;
                    bool isPlayer = e.tier == "Active" || (Character.Controlled?.Submarine?.Info?.Name == e.name);
                    string tag = isPlayer ? " [YOU]" : "";
                    var tierColor = e.tier switch
                    {
                        "Active" => Color.LimeGreen,
                        "Nearby" => Color.Yellow,
                        "Passive" => Color.Orange,
                        "Dormant" => Color.Gray,
                        "Unloaded" => Color.DarkGray,
                        _ => Color.White
                    };
                    DebugConsole.NewMessage(
                        $"  {e.name} ({e.subType}): {e.items} items, dist={e.dist:F0}, tier={e.tier}, native={e.components}{tag}",
                        tierColor);
                }
                DebugConsole.NewMessage($"  [loose]: {looseCount} items (no submarine)", Color.Gray);
                DebugConsole.NewMessage(
                    $"[ItemOptimizer] Total: {totalItems + looseCount} items in {entries.Count} structures + {looseCount} loose",
                    Color.Cyan);
                if (subToZone == null)
                    DebugConsole.NewMessage("[ItemOptimizer] (NativeRuntime OFF — tier data unavailable, use 'ionative on')", Color.Yellow);
            });

            Add("iospatial", "iospatial: Dump hull spatial partition state for motion sensors + character positions.", args =>
            {
                DebugConsole.NewMessage($"[ItemOptimizer] ── Hull Spatial Partition Debug ──", Color.Cyan);

                // Characters
                DebugConsole.NewMessage($"  Characters ({Character.CharacterList.Count}):", Color.Yellow);
                foreach (var c in Character.CharacterList)
                {
                    var hull = c.CurrentHull;
                    string hullStr = hull != null
                        ? $"Hull#{hull.ID} ({hull.RoomName ?? "?"}) sub={hull.Submarine?.Info?.Name ?? "null"}"
                        : "NO HULL";
                    string hullRect = hull != null
                        ? $" rect=({hull.Rect.X},{hull.Rect.Y},{hull.Rect.Width},{hull.Rect.Height})"
                        : "";
                    DebugConsole.NewMessage(
                        $"    {c.Name} pos=({c.WorldPosition.X:F0},{c.WorldPosition.Y:F0}) {hullStr}{hullRect}",
                        c.IsDead ? Color.Gray : Color.White);
                }

                // HullCharacterTracker state
                DebugConsole.NewMessage($"  HullCharacterTracker:", Color.Yellow);
                var noHull = HullCharacterTracker.GetCharactersWithoutHull();
                DebugConsole.NewMessage($"    No-hull characters: {noHull.Count}", Color.White);
                foreach (var c in noHull)
                    DebugConsole.NewMessage($"      {c.Name} pos=({c.WorldPosition.X:F0},{c.WorldPosition.Y:F0})", Color.Gray);

                // Motion sensors
                int sensorCount = 0;
                foreach (var item in Item.ItemList)
                {
                    var ms = item.GetComponent<MotionSensor>();
                    if (ms == null) continue;
                    sensorCount++;
                    if (sensorCount > 10) continue; // limit output

                    // Compute what ResolveSpatialCache would compute
                    var r = item.Rect;
                    Vector2 localCenter = new Vector2(r.X + r.Width / 2f, r.Y - r.Height / 2f);
                    bool hasBody = item.body != null;
                    string posWarn = hasBody ? " [HAS BODY!]" : "";

                    DebugConsole.NewMessage(
                        $"    Sensor '{item.Prefab?.Identifier}' ID={item.ID} sub={item.Submarine?.Info?.Name ?? "null"}{posWarn}",
                        Color.LimeGreen);
                    // Also compute broadRange AABB for verification
                    float brX = Math.Max(100 * 2, 500); // approximate — real rangeX may vary
                    float brY = Math.Max(100 * 2, 500);
                    Vector2 detectLocal = localCenter + ms.TransformedDetectOffset;
                    DebugConsole.NewMessage(
                        $"      rect_center=({localCenter.X:F0},{localCenter.Y:F0}) detectLocal=({detectLocal.X:F0},{detectLocal.Y:F0}) world=({item.WorldPosition.X:F0},{item.WorldPosition.Y:F0})",
                        hasBody ? Color.Red : Color.White);
                    DebugConsole.NewMessage(
                        $"      broadAABB local: X=[{detectLocal.X - brX:F0},{detectLocal.X + brX:F0}] Y=[{detectLocal.Y - brY:F0},{detectLocal.Y + brY:F0}]",
                        Color.White);

                    // Show covered hulls
                    string hullStr = "none (cache not resolved yet)";
                    var hull = item.CurrentHull;
                    if (hull != null)
                        hullStr = $"in Hull#{hull.ID} ({hull.RoomName ?? "?"})";
                    DebugConsole.NewMessage($"      currentHull: {hullStr}", Color.White);

                    // Show spatial cache
                    var (resolved, hullIds, subName) = MotionSensorRewrite.GetSpatialCacheInfo(item.ID);
                    if (resolved)
                    {
                        DebugConsole.NewMessage($"      spatialCache: {hullIds.Length} hulls, sub={subName}", Color.Yellow);
                        if (hullIds.Length <= 20)
                        {
                            foreach (int hid in hullIds)
                            {
                                // Try to find hull name
                                string hName = "?";
                                foreach (Hull h in Hull.HullList)
                                {
                                    if (h.ID == hid) { hName = h.RoomName ?? "unnamed"; break; }
                                }
                                DebugConsole.NewMessage($"        Hull#{hid} ({hName})", Color.Yellow);
                            }
                        }
                        else
                        {
                            DebugConsole.NewMessage($"        (too many to list)", Color.Yellow);
                        }
                    }
                    else
                    {
                        DebugConsole.NewMessage($"      spatialCache: NOT RESOLVED", Color.Red);
                    }
                }
                if (sensorCount > 10)
                    DebugConsole.NewMessage($"    ... and {sensorCount - 10} more sensors", Color.Gray);
                DebugConsole.NewMessage($"  Total sensors: {sensorCount}", Color.Cyan);
            });

            Add("ioreactor", "ioreactor [itemId]: Dump reactor state (fission, turbine, auto-control, signals).", args =>
            {
                Reactor reactor = null;
                Item reactorItem = null;
                if (args.Length > 0 && int.TryParse(args[0], out int targetId))
                {
                    foreach (var it in Item.ItemList)
                    {
                        if (it.ID == targetId) { reactorItem = it; break; }
                    }
                    reactor = reactorItem?.GetComponent<Reactor>();
                }
                else
                {
                    // Find first reactor
                    foreach (var it in Item.ItemList)
                    {
                        var r = it.GetComponent<Reactor>();
                        if (r != null) { reactor = r; reactorItem = it; break; }
                    }
                }
                if (reactor == null || reactorItem == null)
                {
                    DebugConsole.NewMessage("[ItemOptimizer] No reactor found.", Color.Red);
                    return;
                }

                DebugConsole.NewMessage($"[ItemOptimizer] Reactor ID={reactorItem.ID} \"{reactorItem.Name}\"", Color.Cyan);
                DebugConsole.NewMessage($"  FissionRate={reactor.FissionRate:F2} TargetFissionRate={reactor.TargetFissionRate:F2}", Color.White);
                DebugConsole.NewMessage($"  TurbineOutput={reactor.TurbineOutput:F2} TargetTurbineOutput={reactor.TargetTurbineOutput:F2}", Color.White);
                DebugConsole.NewMessage($"  Temperature={reactor.Temperature:F2} AvailableFuel={reactor.AvailableFuel:F2}", Color.White);
                DebugConsole.NewMessage($"  PowerOn={reactor.PowerOn} AutoTemp={reactor.AutoTemp} Load={reactor.Load:F2}", Color.White);
                DebugConsole.NewMessage($"  CurrPowerConsumption={reactor.CurrPowerConsumption:F2}", Color.White);

                // Signal control state: check connections for last received signals
                if (reactorItem.Connections != null)
                {
                    foreach (var conn in reactorItem.Connections)
                    {
                        if (conn.Name == "set_fissionrate" || conn.Name == "set_turbineoutput" || conn.Name == "shutdown")
                        {
                            string lastRecv = conn.LastReceivedSignal.value ?? "(null)";
                            DebugConsole.NewMessage($"  [{conn.Name}] LastReceived=\"{lastRecv}\" wires={conn.Wires.Count}",
                                conn.Wires.Count > 0 ? Color.Yellow : Color.Gray);
                        }
                    }
                }

                // Check if item is in update loop
                bool isActive = reactorItem.IsActive;
                bool isAccel = SignalGraphEvaluator.IsAccelerated((ushort)reactorItem.ID);
                bool inPriority = LuaCsSetup.Instance?.Game?.UpdatePriorityItems?.Contains(reactorItem) ?? false;
                DebugConsole.NewMessage($"  IsActive={isActive} Accelerated={isAccel} PriorityItem={inPriority}",
                    isAccel ? Color.Red : Color.LimeGreen);

                // Check circuitbox controller if wired
                foreach (var conn in reactorItem.Connections)
                {
                    if ((conn.Name == "set_fissionrate" || conn.Name == "set_turbineoutput") && conn.Wires.Count > 0)
                    {
                        foreach (var wire in conn.Wires)
                        {
                            if (wire == null) continue;
                            for (int ci = 0; ci < 2; ci++)
                            {
                                var otherConn = wire.Connections[ci];
                                if (otherConn != null && otherConn != conn && otherConn.Item != null)
                                {
                                    string lastSent = otherConn.LastSentSignal.value ?? "(null)";
                                    DebugConsole.NewMessage(
                                        $"  [{conn.Name}] ← {otherConn.Item.Name}(#{otherConn.Item.ID}) [{otherConn.Name}] LastSent=\"{lastSent}\"",
                                        Color.Yellow);
                                }
                            }
                        }
                    }
                }
            });
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
