using System.Collections.Generic;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using ItemOptimizerMod.Patches;
using ItemOptimizerMod.World.Sensors;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// Bridge between mod lifecycle (round start/end) and NativeRuntime.
    /// Step 3: real submarine-based zone partitioning with tier evaluation.
    /// </summary>
    internal static partial class NativeRuntimeBridge
    {
        internal static NativeRuntime Runtime;
        internal static bool IsEnabled;

        /// <summary>
        /// Per-item flag: true when this item's lifecycle is fully managed by a Zone.
        /// Dispatch loop skips these items — NativeRuntime handles their scheduling.
        /// Replaces the old submarine-level _dormantSubFlags approach with per-item precision.
        /// </summary>
        internal static readonly bool[] IsZoneManaged = new bool[65536];

        /// <summary>
        /// Per-submarine zone tier, refreshed each Tick from zone tiers.
        /// Value = (byte)ZoneTier (0=Active..4=Unloaded), default 0.
        /// Used by dispatch loop for tier-based item LOD and by CharacterZoneSkipPatch.
        /// </summary>
        internal static readonly byte[] SubZoneTier = new byte[65536];

        private static readonly List<MotionSensorNative> _registeredSensors = new(64);

        /// <summary>Last startup diagnostic string for ionative command.</summary>
        internal static string LastStartupInfo;

        internal static void OnRoundStart()
        {
            // Clean up stale state from previous round (OnRoundEnd may not have been called)
            if (IsEnabled) OnRoundEnd();

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
                IsZoneManaged[item.ID] = true;

                // Track per-zone count for diagnostics
                string zoneName = targetZone is SubmarineZone szd
                    ? (szd.Submarine?.Info?.Name ?? $"Zone{szd.Id}")
                    : $"Zone{targetZone.Id}";
                zoneCounts.TryGetValue(zoneName, out int cnt);
                zoneCounts[zoneName] = cnt + 1;
            }

            IsEnabled = true;

            // Register client-only components (LightNativeComponent etc.)
            RegisterClientComponents();

            // Register independent tick hook (ensures NativeRuntime ticks even when iotakeover is OFF)
            if (ItemOptimizerPlugin.harmony != null)
                RegisterTickHook(ItemOptimizerPlugin.harmony);

            // Register submarine lifecycle patches (hot-load zones for late-spawned subs)
            if (ItemOptimizerPlugin.harmony != null)
                SubmarineZonePatch.Register(ItemOptimizerPlugin.harmony);

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

            // Unregister independent tick hook
            if (ItemOptimizerPlugin.harmony != null)
                UnregisterTickHook(ItemOptimizerPlugin.harmony);

            // Unregister submarine lifecycle patches
            if (ItemOptimizerPlugin.harmony != null)
                SubmarineZonePatch.Unregister(ItemOptimizerPlugin.harmony);

            for (int i = 0; i < _registeredSensors.Count; i++)
                Runtime.Unregister(_registeredSensors[i]);
            _registeredSensors.Clear();

            Runtime.Reset();
            Runtime = null;
            IsEnabled = false;
            LastStartupInfo = null;

            System.Array.Clear(IsZoneManaged, 0, 65536);
            System.Array.Clear(SubZoneTier, 0, 65536);
        }

        internal static void Tick(float deltaTime, Camera cam)
        {
            DrainSubmarineEvents();
            Runtime?.Tick(deltaTime, cam);
            RefreshSubZoneTiers();
        }

        // ═══ Late submarine hot-loading ═══

        private static void DrainSubmarineEvents()
        {
            if (Runtime == null) return;

            // Remove first (before create) — prevents stale zone on ID reuse
            while (SubmarineZonePatch.PendingRemoved.TryDequeue(out var sub))
                HandleSubmarineRemoved(sub);

            while (SubmarineZonePatch.PendingCreated.TryDequeue(out var sub))
                HandleSubmarineCreated(sub);
        }

        private static void HandleSubmarineCreated(Submarine sub)
        {
            if (sub == null || sub.Removed) return;

            // Guard: already has a zone
            for (int i = 0; i < Runtime.Graph.Zones.Count; i++)
            {
                if (Runtime.Graph.Zones[i] is SubmarineZone sz && sz.Submarine == sub)
                    return;
            }

            var zone = Runtime.Graph.CreateSubmarineZone(sub);
            int compCount = 0;

            // Register MotionSensors on this submarine
            foreach (var item in Item.ItemList)
            {
                if (item == null || item.Removed || item.Submarine != sub) continue;
                var ms = item.GetComponent<MotionSensor>();
                if (ms == null) continue;

                var native = new MotionSensorNative(ms, item);
                Runtime.Register(native, zone);
                _registeredSensors.Add(native);
                IsZoneManaged[item.ID] = true;
                compCount++;
            }

            RegisterClientComponentsForZone(zone, sub);

            LuaCsLogger.Log($"[ItemOptimizer] Zone hot-loaded: {sub.Info?.Name ?? "?"} ({compCount} sensors, zone {zone.Id})");
        }

        private static void HandleSubmarineRemoved(Submarine sub)
        {
            if (sub == null) return;

            SubmarineZone target = null;
            for (int i = 0; i < Runtime.Graph.Zones.Count; i++)
            {
                if (Runtime.Graph.Zones[i] is SubmarineZone sz && sz.Submarine == sub)
                { target = sz; break; }
            }
            if (target == null) return;

            // Unregister all components in this zone
            for (int i = target.Components.Count - 1; i >= 0; i--)
            {
                var comp = target.Components[i];
                if (comp.Host != null)
                    IsZoneManaged[comp.Host.ID] = false;
                _registeredSensors.Remove(comp as MotionSensorNative);
                Runtime.Unregister(comp);
            }

            Runtime.Graph.RemoveZone(target);
            LuaCsLogger.Log($"[ItemOptimizer] Zone removed: {sub.Info?.Name ?? "?"} (zone {target.Id})");
        }

        private static void RefreshSubZoneTiers()
        {
            System.Array.Clear(SubZoneTier, 0, SubZoneTier.Length);
            var graph = Runtime?.Graph;
            if (graph == null) return;
            for (int i = 0; i < graph.Zones.Count; i++)
            {
                if (graph.Zones[i] is SubmarineZone sz
                    && sz.Submarine != null)
                {
                    SubZoneTier[sz.Submarine.ID & 0xFFFF] = (byte)sz.Tier;
                }
            }
        }

        // ═══ Independent Tick Hook (方案C) ═══
        // Postfix on MapEntity.UpdateAll — runs AFTER vanilla or takeover prefix.
        // When UpdateAllTakeover is ON, its dispatch loop already calls Rebuild + Tick.
        // When UpdateAllTakeover is OFF, this postfix provides the tick source.

        private static bool _hasTickHook;

        internal static void RegisterTickHook(Harmony harmony)
        {
            if (_hasTickHook) return;
            var updateAll = AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll),
                new[] { typeof(float), typeof(Camera) });
            if (updateAll == null)
            {
                LuaCsLogger.LogError("[ItemOptimizer] NativeRuntimeBridge: MapEntity.UpdateAll not found for tick hook!");
                return;
            }
            harmony.Patch(updateAll,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(NativeRuntimeBridge), nameof(PostUpdateAllTick))));
            _hasTickHook = true;
            LuaCsLogger.Log("[ItemOptimizer] NativeRuntime independent tick hook registered");
        }

        internal static void UnregisterTickHook(Harmony harmony)
        {
            if (!_hasTickHook) return;
            var updateAll = AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll),
                new[] { typeof(float), typeof(Camera) });
            if (updateAll != null)
                harmony.Unpatch(updateAll,
                    AccessTools.Method(typeof(NativeRuntimeBridge), nameof(PostUpdateAllTick)));
            _hasTickHook = false;
        }

        /// <summary>Client-only registration hook for visual NativeComponents.</summary>
        static partial void RegisterClientComponents();

        /// <summary>Client-only registration hook for visual NativeComponents in a hot-loaded zone.</summary>
        static partial void RegisterClientComponentsForZone(SubmarineZone zone, Submarine sub);

        /// <summary>
        /// Postfix on MapEntity.UpdateAll. Provides independent tick for NativeRuntime
        /// when UpdateAllTakeover is OFF. When takeover is ON, its dispatch loop
        /// already handles Rebuild + Tick — this postfix is a no-op.
        /// </summary>
        private static void PostUpdateAllTick(float deltaTime, Camera cam)
        {
            if (!IsEnabled) return;
            // When takeover is ON, the dispatch loop already called
            // HullCharacterTracker.Rebuild() and NativeRuntimeBridge.Tick()
            if (UpdateAllTakeover.Enabled) return;

            HullCharacterTracker.Rebuild();
            Tick(deltaTime, cam);
            Stats.EndFrame();
        }
    }
}
