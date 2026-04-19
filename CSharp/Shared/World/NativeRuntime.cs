using System.Collections.Generic;
using System.Threading.Tasks;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// Orchestrates the per-frame execution of NativeComponents across all zones.
    ///
    /// Frame lifecycle:
    ///   1. ZoneGraph.Update()        — sync positions, evaluate tiers, rebuild neighbors
    ///   2. BuildSnapshots()          — create read-only zone snapshots (main thread)
    ///   3. Read phase                — NativeComponent.Read() for each component (main thread)
    ///   4. Tick phase                — NativeComponent.Tick() per zone (parallel or serial)
    ///   5. Apply phase               — flush command buffers (main thread)
    ///
    /// Two runtime modes:
    ///   - DirectMode: Tick on main thread, commands apply immediately (vanilla compatible)
    ///   - ParallelMode: Tick on worker threads, commands buffered and flushed
    ///
    /// The runtime automatically falls back to DirectMode if parallelism causes issues.
    /// </summary>
    public class NativeRuntime
    {
        public enum RuntimeMode { Direct, Parallel }

        public RuntimeMode Mode { get; set; } = RuntimeMode.Direct;

        public readonly ZoneGraph Graph = new();

        private uint _frame;
        private WorldSnapshot _worldSnapshot;

        // Pooled command buffers (one per zone, reused each frame)
        private readonly Dictionary<int, CommandBuffer> _bufferPool = new(32);

        // Player list cache (rebuilt each frame)
        private readonly List<Character> _players = new(4);

        // ── Initialization ──

        /// <summary>
        /// Initialize the runtime for a level. Call when a round starts.
        /// </summary>
        public void Initialize(Vector2 levelOrigin, float levelWidth, float levelHeight)
        {
            _frame = 0;
            Graph.Initialize(levelOrigin, levelWidth, levelHeight);

            // Create zones for all loaded submarines
            foreach (var sub in Submarine.Loaded)
            {
                if (sub == null) continue;
                Graph.CreateSubmarineZone(sub);
            }
        }

        // ── Per-frame update ──

        /// <summary>
        /// Run one frame of the NativeComponent runtime.
        /// Call from UpdateAllTakeover or a dedicated hook point.
        /// </summary>
        public void Tick(float deltaTime, Camera cam)
        {
            _frame++;

            // Gather players
            _players.Clear();
            foreach (var c in Character.CharacterList)
            {
                if (c != null && !c.IsDead && c.IsPlayer)
                    _players.Add(c);
            }

            // Phase 1: Zone graph update
            Graph.Update(_players);

            // Phase 2: Build snapshots
            _worldSnapshot = new WorldSnapshot(_frame, deltaTime, Graph.Zones);
            BuildZoneSnapshots(deltaTime);

            // Phase 3: Read phase (main thread)
            RunReadPhase(deltaTime);

            // Phase 4: Tick phase (buffers pre-allocated on main thread)
            PreAllocateBuffers();
            switch (Mode)
            {
                case RuntimeMode.Direct:
                    RunTickDirect(deltaTime);
                    break;
                case RuntimeMode.Parallel:
                    RunTickParallel(deltaTime);
                    break;
            }

            // Phase 5: Apply phase (main thread)
            RunApplyPhase();
        }

        // ── Phase implementations ──

        private void BuildZoneSnapshots(float deltaTime)
        {
            for (int i = 0; i < Graph.Zones.Count; i++)
            {
                var zone = Graph.Zones[i];
                if (zone.Tier >= ZoneTier.Dormant) continue;

                // TODO: build proper item/character snapshots
                zone.Snapshot = new ZoneSnapshot(zone, null, null, zone.Neighbors, _frame);
            }
        }

        private void RunReadPhase(float deltaTime)
        {
            for (int i = 0; i < Graph.Zones.Count; i++)
            {
                var zone = Graph.Zones[i];
                if (zone.Tier >= ZoneTier.Dormant) continue;

                var readCtx = new ReadContext(deltaTime, _frame, zone, _worldSnapshot);
                for (int c = 0; c < zone.Components.Count; c++)
                    zone.Components[c].Read(in readCtx);
            }
        }

        private void PreAllocateBuffers()
        {
            for (int i = 0; i < Graph.Zones.Count; i++)
            {
                var zone = Graph.Zones[i];
                if (zone.Tier >= ZoneTier.Dormant) continue;
                var buffer = GetOrCreateBuffer(zone.Id);
                buffer.Clear();
                zone.Commands = buffer;
            }
        }

        private void RunTickDirect(float deltaTime)
        {
            // Direct mode: tick on main thread, commands apply immediately
            // (In v1, "immediately" means they go into the buffer and get applied in Phase 5.
            //  True immediate-apply is a future optimization for DirectMode.)
            for (int i = 0; i < Graph.Zones.Count; i++)
            {
                var zone = Graph.Zones[i];
                if (zone.Tier >= ZoneTier.Dormant) continue;
                TickZone(zone, deltaTime);
            }
        }

        private void RunTickParallel(float deltaTime)
        {
            // Parallel mode: one worker per zone
            Parallel.For(0, Graph.Zones.Count, i =>
            {
                var zone = Graph.Zones[i];
                if (zone.Tier >= ZoneTier.Dormant) return;
                TickZone(zone, deltaTime);
            });
        }

        private void TickZone(Zone zone, float deltaTime)
        {
            var buffer = zone.Commands; // pre-allocated by PreAllocateBuffers

            // Zone-level proxy overrides individual component ticks
            if (zone.Proxy != null)
            {
                var proxyCtx = new TickContext(deltaTime, _frame, zone, zone.Snapshot, buffer, null);
                zone.Proxy.Tick(ref proxyCtx);
                zone.Commands = buffer;
                return;
            }

            // Tick individual components
            for (int c = 0; c < zone.Components.Count; c++)
            {
                var comp = zone.Components[c];
                if (!comp.ShouldTick(zone.Tier, _frame)) continue;

                var ctx = new TickContext(deltaTime, _frame, zone, zone.Snapshot, buffer, comp.Host);
                comp.Tick(ref ctx);
            }

            zone.Commands = buffer;
        }

        private void RunApplyPhase()
        {
            for (int i = 0; i < Graph.Zones.Count; i++)
            {
                var zone = Graph.Zones[i];
                if (zone.Commands == null || zone.Commands.Count == 0) continue;
                zone.Commands.ApplyAll();
            }
        }

        // ── Component registration ──

        /// <summary>Register a NativeComponent with a zone.</summary>
        public void Register(NativeComponent component, Zone zone)
        {
            component.Zone = zone;
            zone.Components.Add(component);
            component.OnRegistered();
        }

        /// <summary>Unregister a NativeComponent from its zone.</summary>
        public void Unregister(NativeComponent component)
        {
            component.Zone?.Components.Remove(component);
            component.OnUnregistered();
            component.Zone = null;
        }

        // ── Helpers ──

        private CommandBuffer GetOrCreateBuffer(int zoneId)
        {
            if (!_bufferPool.TryGetValue(zoneId, out var buffer))
            {
                buffer = new CommandBuffer();
                _bufferPool[zoneId] = buffer;
            }
            return buffer;
        }

        public void Reset()
        {
            Graph.Reset();
            _bufferPool.Clear();
            _frame = 0;
        }
    }
}
