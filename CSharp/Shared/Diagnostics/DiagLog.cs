using System;
using System.IO;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Writes diagnostic lines to {ModDir}/diag.log.
    /// Thread-safe via lock. Auto-creates file, appends with timestamps.
    /// </summary>
    static class DiagLog
    {
        private static readonly object _lock = new();
        private static string _path;

        private static string GetPath()
        {
            if (_path != null) return _path;
            _path = ModPaths.ResolveData("diag.log");
            return _path;
        }

        public static void Write(string message)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
                lock (_lock)
                {
                    File.AppendAllText(GetPath(), line);
                }
            }
            catch { /* swallow — diagnostics must not crash the game */ }
        }

        /// <summary>Clear the log file.</summary>
        public static void Clear()
        {
            try
            {
                lock (_lock)
                {
                    File.WriteAllText(GetPath(), $"[{DateTime.Now:HH:mm:ss.fff}] === DiagLog cleared ===\n");
                }
            }
            catch { }
        }

        /// <summary>Write multiple lines at once (more efficient for bulk dumps).</summary>
        public static void WriteBlock(string header, string[] lines)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] ── {header} ──\n");
                foreach (var line in lines)
                    sb.Append($"  {line}\n");
                lock (_lock)
                {
                    File.AppendAllText(GetPath(), sb.ToString());
                }
            }
            catch { }
        }
    }
}
