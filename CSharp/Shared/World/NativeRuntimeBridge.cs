using System.Collections.Generic;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using ItemOptimizerMod.Patches;
using ItemOptimizerMod.World.Sensors;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// Bridge between mod lifecycle (round start/end) and NativeRuntime.
    /// Step 3: real submarine-based zone partitioning with tier evaluation.
    /// </summary>
    internal static class NativeRuntimeBridge
    {
        internal static NativeRuntime Runtime;
        internal static bool IsEnabled;

        private static readonly List<MotionSensorNative> _registeredSensors = new(64);

        /// <summary>Last startup diagnostic string for ionative command.</summary>
        internal static string LastStartupInfo;

        internal static void OnRoundStart()
        {
            if (!OptimizerConfig.EnableNativeRuntime) return;

            Runtime = new NativeRuntime();

            // Initialize with level bounds for WorldGrid
            InitializeWithLevelBounds();

            // Build Submarine → Zone lookup
            var subToZone = new Dictionary<Submarine, SubmarineZone>(Runtime.Graph.Zones.Count);
            foreach (var zone in Runtime.Graph.Zones)
            {
                if (zone is SubmarineZone sz && sz.Submarine != null)
                    subToZone[sz.Submarine] = sz;
            }

            // Register all MotionSensor items, matched to their submarine's zone
            _registeredSensors.Clear();
            var zoneCounts = new Dictionary<string, int>();
            int skipped = 0;

            foreach (var item in Item.ItemList)
            {
                if (item == null || item.Removed) continue;
                var ms = item.GetComponent<MotionSensor>();
                if (ms == null) continue;

                // Match zone: by submarine > by spatial position > skip
                Zone targetZone = null;
                if (item.Submarine != null && subToZone.TryGetValue(item.Submarine, out var sz))
                    targetZone = sz;
                else
                    targetZone = Runtime.Graph.FindZoneAt(item.WorldPosition);

                if (targetZone == null)
                {
                    skipped++;
                    continue;
                }

                var native = new MotionSensorNative(ms, item);
                Runtime.Register(native, targetZone);
                _registeredSensors.Add(native);

                // Track per-zone count for diagnostics
                string zoneName = targetZone is SubmarineZone szd
                    ? (szd.Submarine?.Info?.Name ?? $"Zone{szd.Id}")
                    : $"Zone{targetZone.Id}";
                zoneCounts.TryGetValue(zoneName, out int cnt);
                zoneCounts[zoneName] = cnt + 1;
            }

            IsEnabled = true;

            // Build diagnostic string
            var sb = new StringBuilder();
            sb.Append($"{_registeredSensors.Count} sensors in {Runtime.Graph.Zones.Count} zones");
            if (zoneCounts.Count > 0)
            {
                sb.Append(" (");
                bool first = true;
                foreach (var kv in zoneCounts)
                {
                    if (!first) sb.Append(", ");
                    sb.Append($"{kv.Key}:{kv.Value}");
                    first = false;
                }
                sb.Append(')');
            }
            if (skipped > 0) sb.Append($" [{skipped} skipped: no zone]");
            LastStartupInfo = sb.ToString();

            LuaCsLogger.Log($"[ItemOptimizer] NativeRuntime started: {LastStartupInfo}");
        }

        private static void InitializeWithLevelBounds()
        {
            // Try to get level bounds for WorldGrid sizing
            var level = Level.Loaded;
            if (level != null)
            {
                // Level.Size is a Point (width, height in pixels)
                // Use generous bounds to cover all submarines including those outside the level
                float w = level.Size.X;
                float h = level.Size.Y;
                float margin = 20000f;
                Runtime.Initialize(
                    new Vector2(-margin, -h - margin),
                    w + margin * 2,
                    h + margin * 2);
            }
            else
            {
                // No level (editor, lobby) — large fallback
                var pos = Submarine.MainSub?.WorldPosition ?? Vector2.Zero;
                Runtime.Initialize(
                    new Vector2(pos.X - 50000, pos.Y - 50000),
                    100000f,
                    100000f);
            }
        }

        internal static void OnRoundEnd()
        {
            if (!IsEnabled) return;

            for (int i = 0; i < _registeredSensors.Count; i++)
                Runtime.Unregister(_registeredSensors[i]);
            _registeredSensors.Clear();

            Runtime.Reset();
            Runtime = null;
            IsEnabled = false;
            LastStartupInfo = null;
        }

        internal static void Tick(float deltaTime, Camera cam)
        {
            Runtime?.Tick(deltaTime, cam);
        }
    }
}
