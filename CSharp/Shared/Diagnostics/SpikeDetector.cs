using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Always-on spike detector. When enabled, attaches lightweight per-item timing
    /// to Item.Update. Each frame, accumulates timing per item type. If the total
    /// MapEntity.UpdateAll time exceeds the threshold, logs the top items to CSV
    /// along with phase-level timing breakdown and GC diagnostics.
    /// Overhead: ~1-2ms/frame from Harmony prefix/postfix on every Item.Update call.
    /// </summary>
    static class SpikeDetector
    {
        internal static bool Enabled;
        internal static float ThresholdMs = 30f;

        private static long _frameStartTick;
        private static long _lastLogTick;
        private static long _cooldownTicks;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        // Per-item accumulation for current frame
        private static readonly ConcurrentDictionary<string, (long ticks, int count)> FrameItems = new(4, 512);
        [ThreadStatic]
        private static long _itemStartTick;

        // GC tracking — sampled at frame start
        private static int _gcGen0Start;
        private static int _gcGen1Start;
        private static int _gcGen2Start;

        // Harmony
        private static Harmony _harmony;
        private static System.Reflection.MethodInfo _itemUpdateMethod;
        private static HarmonyMethod _itemPrefix;
        private static HarmonyMethod _itemPostfix;
        private static bool _patchesAttached;

        private static string _logPath;
        private const long MaxLogSize = 50 * 1024 * 1024; // 50MB

        internal static void Initialize(Harmony harmony)
        {
            _harmony = harmony;
            _itemUpdateMethod = AccessTools.Method(typeof(Item), nameof(Item.Update));
            _itemPrefix = new HarmonyMethod(typeof(SpikeDetector), nameof(ItemPrefix));
            _itemPostfix = new HarmonyMethod(typeof(SpikeDetector), nameof(ItemPostfix));
            _cooldownTicks = Stopwatch.Frequency * 5; // 5 second cooldown

            _logPath = ModPaths.ResolveData("spike_log.csv");
        }

        internal static void SetEnabled(bool enabled)
        {
            if (enabled == Enabled && enabled == _patchesAttached) return;
            Enabled = enabled;

            if (enabled && !_patchesAttached)
            {
                _harmony.Patch(_itemUpdateMethod, prefix: _itemPrefix, postfix: _itemPostfix);
                _patchesAttached = true;
                DebugConsole.NewMessage(
                    $"[ItemOptimizer] Spike detector ON (threshold={ThresholdMs}ms, ~1-2ms overhead)",
                    Color.Yellow);
            }
            else if (!enabled && _patchesAttached)
            {
                _harmony.Unpatch(_itemUpdateMethod, _itemPrefix.method);
                _harmony.Unpatch(_itemUpdateMethod, _itemPostfix.method);
                _patchesAttached = false;
                DebugConsole.NewMessage("[ItemOptimizer] Spike detector OFF", Color.Yellow);
            }
        }

        // ── Called from MapEntityUpdateAll hooks in PerfProfiler ──

        internal static void OnFrameStart()
        {
            if (!Enabled) return;
            _frameStartTick = Stopwatch.GetTimestamp();
            FrameItems.Clear();

            // Snapshot GC counts at frame start
            _gcGen0Start = GC.CollectionCount(0);
            _gcGen1Start = GC.CollectionCount(1);
            _gcGen2Start = GC.CollectionCount(2);
        }

        internal static void OnFrameEnd()
        {
            if (!Enabled) return;
            long now = Stopwatch.GetTimestamp();
            double totalMs = (now - _frameStartTick) * TicksToMs;

            if (totalMs >= ThresholdMs && now - _lastLogTick > _cooldownTicks)
            {
                _lastLogTick = now;
                int gcGen0 = GC.CollectionCount(0) - _gcGen0Start;
                int gcGen1 = GC.CollectionCount(1) - _gcGen1Start;
                int gcGen2 = GC.CollectionCount(2) - _gcGen2Start;
                LogSpike(totalMs, gcGen0, gcGen1, gcGen2);
            }
        }

        // ── Harmony callbacks on Item.Update (only attached when Enabled) ──

        public static void ItemPrefix()
        {
            _itemStartTick = Stopwatch.GetTimestamp();
        }

        public static void ItemPostfix(Item __instance)
        {
            long elapsed = Stopwatch.GetTimestamp() - _itemStartTick;
            string id = __instance.Prefab?.Identifier.Value ?? "unknown";
            FrameItems.AddOrUpdate(id,
                _ => (elapsed, 1),
                (_, existing) => (existing.ticks + elapsed, existing.count + 1));
        }

        // ── Logging ──

        private static ItemPrefab FindPrefab(string identifier)
        {
            foreach (var p in ItemPrefab.Prefabs)
                if (p.Identifier.Value == identifier)
                    return p;
            return null;
        }

        private static void LogSpike(double totalMs, int gcGen0, int gcGen1, int gcGen2)
        {
            try
            {
                // Rotate log if too large
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxLogSize)
                {
                    var bakPath = _logPath + ".bak";
                    if (File.Exists(bakPath)) File.Delete(bakPath);
                    File.Move(_logPath, bakPath);
                }

                bool needHeader = !File.Exists(_logPath) || new FileInfo(_logPath).Length == 0;
                var sb = new StringBuilder(8192);
                if (needHeader)
                {
                    sb.AppendLine("timestamp,frame_total_ms,type,key,value");
                }

                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                // ── Phase timing breakdown ──
                AppendRow(sb, ts, totalMs, "phase", "PhaseA_ms", Stats.PhaseAMs.ToString("F2"));
                AppendRow(sb, ts, totalMs, "phase", "PhaseB_ms", Stats.PhaseBMs.ToString("F2"));
                AppendRow(sb, ts, totalMs, "phase", "PhaseC_ms", Stats.PhaseCMs.ToString("F2"));
                AppendRow(sb, ts, totalMs, "phase", "PhaseD_ms", Stats.PhaseDMs.ToString("F2"));

                // ── Phase B sub-breakdown ──
                AppendRow(sb, ts, totalMs, "sub_phase", "PreBuild_ms", Stats.PhaseBPreBuildMs.ToString("F2"));
                AppendRow(sb, ts, totalMs, "sub_phase", "SignalGraph_ms", Stats.SignalGraphTickMs.ToString("F2"));
                AppendRow(sb, ts, totalMs, "sub_phase", "NativeRT_ms", Stats.PhaseBNativeRtMs.ToString("F2"));
                AppendRow(sb, ts, totalMs, "sub_phase", "Classify_ms", Stats.PhaseBClassifyMs.ToString("F2"));
                AppendRow(sb, ts, totalMs, "sub_phase", "MainLoop_ms", Stats.PhaseBMainLoopMs.ToString("F2"));

                // ── GC diagnostics ──
                AppendRow(sb, ts, totalMs, "gc", "Gen0", gcGen0.ToString());
                AppendRow(sb, ts, totalMs, "gc", "Gen1", gcGen1.ToString());
                AppendRow(sb, ts, totalMs, "gc", "Gen2", gcGen2.ToString());
                AppendRow(sb, ts, totalMs, "gc", "TotalMemoryMB",
                    (GC.GetTotalMemory(false) / (1024.0 * 1024.0)).ToString("F1"));

                // ── Skip chain counters (current frame, not EMA) ──
                AppendRow(sb, ts, totalMs, "skip", "SignalGraphAccel", Stats.SignalGraphAccelSkips.ToString());
                AppendRow(sb, ts, totalMs, "skip", "Wire", Stats.WireSkips.ToString());
                AppendRow(sb, ts, totalMs, "skip", "Zone", Stats.ZoneSkips.ToString());
                AppendRow(sb, ts, totalMs, "skip", "ZonePassive", Stats.ZonePassiveSkips.ToString());
                AppendRow(sb, ts, totalMs, "skip", "Proxy", Stats.ProxyItems.ToString());
                AppendRow(sb, ts, totalMs, "skip", "GroundItem", Stats.GroundItemSkips.ToString());
                AppendRow(sb, ts, totalMs, "skip", "ColdStorage", Stats.ColdStorageSkips.ToString());

                // ── Unaccounted time ──
                double itemSumMs = 0;
                foreach (var kv in FrameItems)
                    itemSumMs += kv.Value.ticks * TicksToMs;
                double unaccountedMs = totalMs - Stats.PhaseAMs - itemSumMs - Stats.PhaseCMs - Stats.PhaseDMs;
                // infrastructure = PreBuild + SignalGraph + Classify + NativeRT + HullCharTracker
                double infraMs = Stats.PhaseBMs - Stats.PhaseBMainLoopMs;
                AppendRow(sb, ts, totalMs, "diag", "ItemSum_ms", itemSumMs.ToString("F2"));
                AppendRow(sb, ts, totalMs, "diag", "Infra_ms", infraMs.ToString("F2"));
                AppendRow(sb, ts, totalMs, "diag", "Unaccounted_ms", unaccountedMs.ToString("F2"));

                // ── Top 20 items (same as before but in new format) ──
                var sorted = FrameItems.OrderByDescending(kv => kv.Value.ticks);
                int rank = 0;
                foreach (var kv in sorted)
                {
                    if (++rank > 20) break;
                    double ms = kv.Value.ticks * TicksToMs;
                    string pkg = "?";
                    var prefab = FindPrefab(kv.Key);
                    if (prefab != null) pkg = prefab.ContentPackage?.Name ?? "?";
                    // value = "time_ms|count|package"
                    AppendRow(sb, ts, totalMs, "item", kv.Key,
                        $"{ms:F3}|{kv.Value.count}|{pkg}");
                }

                File.AppendAllText(_logPath, sb.ToString());

                // Console summary
                string gcLabel = (gcGen2 > 0) ? $" GC2!={gcGen2}" :
                                 (gcGen1 > 0) ? $" GC1={gcGen1}" :
                                 (gcGen0 > 0) ? $" GC0={gcGen0}" : "";
                DebugConsole.NewMessage(
                    $"[ItemOptimizer] Spike: {totalMs:F1}ms " +
                    $"[A={Stats.PhaseAMs:F1} B={Stats.PhaseBMs:F1} C={Stats.PhaseCMs:F1} D={Stats.PhaseDMs:F1}]" +
                    $" items={itemSumMs:F1}ms infra={infraMs:F1}ms{gcLabel}" +
                    $" mem={GC.GetTotalMemory(false) / (1024 * 1024)}MB",
                    gcGen2 > 0 ? Color.OrangeRed : Color.Red);

                // Top 3 items
                rank = 0;
                foreach (var kv in sorted)
                {
                    if (++rank > 3) break;
                    double ms = kv.Value.ticks * TicksToMs;
                    string name = kv.Key;
                    var prefab = FindPrefab(kv.Key);
                    if (prefab != null) name = prefab.Name?.Value ?? kv.Key;
                    DebugConsole.NewMessage(
                        $"  #{rank} {name}: {ms:F2}ms x{kv.Value.count}", Color.Yellow);
                }
            }
            catch { /* swallow IO errors during gameplay */ }
        }

        /// <summary>Append one row in the unified CSV format.</summary>
        private static void AppendRow(StringBuilder sb, string ts, double totalMs,
            string type, string key, string value)
        {
            sb.Append(CsvEscape(ts)).Append(',');
            sb.Append(totalMs.ToString("F1")).Append(',');
            sb.Append(type).Append(',');
            sb.Append(CsvEscape(key)).Append(',');
            sb.AppendLine(CsvEscape(value));
        }

        internal static void ClearLog()
        {
            try
            {
                if (File.Exists(_logPath)) File.Delete(_logPath);
                DebugConsole.NewMessage("[ItemOptimizer] Spike log cleared", Color.LimeGreen);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"[ItemOptimizer] Failed to clear spike log: {e.Message}");
            }
        }

        internal static void Reset()
        {
            SetEnabled(false);
            FrameItems.Clear();
        }

        private static string CsvEscape(string s)
        {
            if (s == null) return "";
            if (s.Contains(',') || s.Contains('"') || s.Contains('|'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
