using System;
using System.IO;
using System.Linq;
using Barotrauma;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Centralized mod directory resolution. Works in both LocalMods and Workshop installs.
    /// Uses ContentPackage.Dir as the authoritative source for the mod's filesystem location.
    /// </summary>
    static class ModPaths
    {
        private static string _modDir;

        /// <summary>
        /// The mod's root directory on disk (e.g. LocalMods/ItemOptimizer or workshop/content/...).
        /// Resolved once, cached for the session. Falls back to Assembly.Location, then CWD.
        /// </summary>
        internal static string ModDir
        {
            get
            {
                if (_modDir != null) return _modDir;
                _modDir = ResolveModDir();
                return _modDir;
            }
        }

        private static string ResolveModDir()
        {
            // Primary: find our ContentPackage by name and use its Dir property
            try
            {
                var pkg = ContentPackageManager.EnabledPackages.All
                    .FirstOrDefault(cp => cp.Name != null &&
                        cp.Name.Equals("ItemOptimizer", StringComparison.OrdinalIgnoreCase));

                if (pkg != null && !string.IsNullOrEmpty(pkg.Dir))
                    return pkg.Dir;
            }
            catch { /* ContentPackageManager may not be ready yet */ }

            // Fallback: Assembly.Location (works in LocalMods, unreliable in Workshop)
            try
            {
                var asmDir = Path.GetDirectoryName(typeof(ModPaths).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir) && Directory.Exists(asmDir))
                    return asmDir;
            }
            catch { }

            // Last resort: relative path from game CWD (only works for LocalMods)
            return Path.Combine("LocalMods", "ItemOptimizer");
        }

        /// <summary>Resolve a filename to an absolute path inside the mod directory.</summary>
        internal static string Resolve(string fileName)
        {
            return Path.Combine(ModDir, fileName);
        }

        /// <summary>Resolve a filename to an absolute path inside the Data/ subdirectory. Creates it if needed.</summary>
        internal static string ResolveData(string fileName)
        {
            return ResolveInSubDir("Data", fileName);
        }

        /// <summary>Resolve a path inside a subdirectory of the mod directory. Creates the subdirectory if needed.</summary>
        internal static string ResolveInSubDir(string subDir, string fileName)
        {
            var dir = Path.Combine(ModDir, subDir);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        /// <summary>Reset cached path (for hot-reload scenarios).</summary>
        internal static void Reset()
        {
            _modDir = null;
        }
    }
}
