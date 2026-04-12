using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
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
