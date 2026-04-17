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
    /// MapEntity.UpdateAll time exceeds the threshold, logs the top items to CSV.
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
        }

        internal static void OnFrameEnd()
        {
            if (!Enabled) return;
            long now = Stopwatch.GetTimestamp();
            double totalMs = (now - _frameStartTick) * TicksToMs;

            if (totalMs >= ThresholdMs && now - _lastLogTick > _cooldownTicks)
            {
                _lastLogTick = now;
                LogSpike(totalMs);
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

        private static void LogSpike(double totalMs)
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
                var sb = new StringBuilder(4096);
                if (needHeader)
                    sb.AppendLine("timestamp,frame_total_ms,rank,identifier,name,package,time_ms,count");

                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var sorted = FrameItems.OrderByDescending(kv => kv.Value.ticks);

                int rank = 0;
                foreach (var kv in sorted)
                {
                    if (++rank > 20) break;
                    double ms = kv.Value.ticks * TicksToMs;
                    string name = kv.Key;
                    string pkg = "?";
                    var prefab = FindPrefab(kv.Key);
                    if (prefab != null)
                    {
                        name = prefab.Name?.Value ?? kv.Key;
                        pkg = prefab.ContentPackage?.Name ?? "?";
                    }
                    sb.Append(CsvEscape(ts)).Append(',');
                    sb.Append(totalMs.ToString("F1")).Append(',');
                    sb.Append(rank).Append(',');
                    sb.Append(CsvEscape(kv.Key)).Append(',');
                    sb.Append(CsvEscape(name)).Append(',');
                    sb.Append(CsvEscape(pkg)).Append(',');
                    sb.Append(ms.ToString("F3")).Append(',');
                    sb.AppendLine(kv.Value.count.ToString());
                }

                File.AppendAllText(_logPath, sb.ToString());

                // Console summary — top 5
                DebugConsole.NewMessage(
                    $"[ItemOptimizer] Spike: {totalMs:F1}ms (top items logged to spike_log.csv)",
                    Color.Red);
                rank = 0;
                foreach (var kv in sorted)
                {
                    if (++rank > 5) break;
                    double ms = kv.Value.ticks * TicksToMs;
                    string name = kv.Key;
                    var prefab = FindPrefab(kv.Key);
                    if (prefab != null) name = prefab.Name?.Value ?? kv.Key;
                    DebugConsole.NewMessage($"  #{rank} {name} ({kv.Key}): {ms:F2}ms x{kv.Value.count}", Color.Yellow);
                }
            }
            catch { /* swallow IO errors during gameplay */ }
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
            if (s.Contains(',') || s.Contains('"'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
