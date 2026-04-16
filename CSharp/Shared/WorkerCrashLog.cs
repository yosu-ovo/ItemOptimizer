using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Barotrauma;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Records worker-thread exceptions, auto-quarantines crashing items,
    /// and persists a single rolling log file for diagnostics.
    /// </summary>
    static class WorkerCrashLog
    {
        private const string LogFileName = "worker_crash.log";
        private const int MaxLogSizeBytes = 2 * 1024 * 1024; // 2 MB cap

        // Items that crashed on worker → moved to main thread for the rest of the session
        private static readonly ConcurrentDictionary<string, int> _quarantine
            = new(StringComparer.Ordinal);

        // Pending entries written from worker threads, flushed to disk on main thread
        private static readonly ConcurrentQueue<string> _pendingEntries = new();

        private static string _logPath;

        internal static void Initialize()
        {
            var modDir = Path.GetDirectoryName(typeof(WorkerCrashLog).Assembly.Location);
            _logPath = Path.Combine(modDir ?? ".", LogFileName);
            _quarantine.Clear();
            while (_pendingEntries.TryDequeue(out _)) { }
        }

        internal static void Reset()
        {
            _quarantine.Clear();
            while (_pendingEntries.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Check if an item identifier is quarantined (crashed before).
        /// Called from IsSafeForWorker on the main thread — very fast ConcurrentDictionary lookup.
        /// </summary>
        internal static bool IsQuarantined(string identifier)
        {
            return _quarantine.ContainsKey(identifier);
        }

        internal static int QuarantineCount => _quarantine.Count;

        /// <summary>
        /// Record a crash from a worker thread. Thread-safe.
        /// The item is quarantined immediately so it won't be dispatched to workers again.
        /// </summary>
        internal static void RecordCrash(Item item, Exception ex, int workerSlot)
        {
            string identifier = item?.Prefab?.Identifier.Value ?? "unknown";
            string package = item?.Prefab?.ContentPackage?.Name ?? "unknown";
            ushort itemId = item?.ID ?? 0;

            // Quarantine: increment crash count
            _quarantine.AddOrUpdate(identifier, 1, (_, count) => count + 1);

            // Build detailed entry (will be flushed to disk on main thread)
            var sb = new StringBuilder(512);
            sb.AppendLine("────────────────────────────────────────");
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Worker #{workerSlot} CRASH");
            sb.AppendLine($"  Item: {identifier} (ID={itemId})");
            sb.AppendLine($"  Package: {package}");
            sb.AppendLine($"  Active: {item?.IsActive}, InRemoveQueue: {item?.IsInRemoveQueue}");

            // Item state snapshot
            if (item != null)
            {
                try
                {
                    sb.AppendLine($"  Condition: {item.Condition:F1}/{item.MaxCondition:F1}");
                    sb.AppendLine($"  ParentInventory: {item.ParentInventory?.Owner?.ToString() ?? "null"}");
                    sb.AppendLine($"  Body: {(item.body != null ? $"enabled={item.body.Enabled}" : "null")}");
                    sb.AppendLine($"  Components: {item.Components?.Count ?? 0}");
                    if (item.Components != null)
                    {
                        foreach (var ic in item.Components)
                            sb.AppendLine($"    - {ic.GetType().Name} (IsActive={ic.IsActive})");
                    }
                }
                catch
                {
                    sb.AppendLine("  [failed to read item state]");
                }
            }

            sb.AppendLine($"  Exception: {ex.GetType().FullName}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
            if (ex.InnerException != null)
            {
                sb.AppendLine($"  Inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                sb.AppendLine(ex.InnerException.StackTrace);
            }

            _pendingEntries.Enqueue(sb.ToString());
        }

        /// <summary>
        /// Flush pending log entries to disk. Called from main thread (UpdateAllPostfix).
        /// </summary>
        internal static void FlushToDisk()
        {
            if (_pendingEntries.IsEmpty) return;

            try
            {
                // Collect all pending entries
                var entries = new List<string>();
                while (_pendingEntries.TryDequeue(out var entry))
                    entries.Add(entry);

                if (entries.Count == 0) return;

                // Truncate if file is too large (keep tail)
                if (File.Exists(_logPath))
                {
                    var fi = new FileInfo(_logPath);
                    if (fi.Length > MaxLogSizeBytes)
                    {
                        // Read existing, keep last 75% worth of content
                        var existing = File.ReadAllText(_logPath);
                        int keepFrom = existing.Length / 4;
                        int newlinePos = existing.IndexOf('\n', keepFrom);
                        if (newlinePos > 0)
                            existing = "[...truncated...]\n" + existing.Substring(newlinePos + 1);
                        File.WriteAllText(_logPath, existing);
                    }
                }

                // Append new entries
                using var writer = File.AppendText(_logPath);
                foreach (var entry in entries)
                    writer.Write(entry);

                var qCount = _quarantine.Count;
                if (qCount > 0)
                {
                    LuaCsLogger.Log($"[ItemOptimizer] {entries.Count} worker crash(es) logged to {LogFileName}, " +
                        $"{qCount} item(s) quarantined to main thread");
                }
            }
            catch (Exception e)
            {
                // Don't let logging failures crash the game
                DebugConsole.ThrowError($"[ItemOptimizer] Failed to write crash log: {e.Message}");
            }
        }

        /// <summary>
        /// Write a session header when parallel dispatch starts.
        /// </summary>
        internal static void WriteSessionHeader()
        {
            try
            {
                using var writer = File.AppendText(_logPath);
                writer.WriteLine();
                writer.WriteLine("════════════════════════════════════════════════════════");
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Session started");
                writer.WriteLine($"  Parallel workers: {OptimizerConfig.ParallelWorkerCount}");
                writer.WriteLine($"  Mod hash: {OptimizerConfig.GetModSetHash()}");
                writer.WriteLine("════════════════════════════════════════════════════════");
            }
            catch { }
        }
    }
}
