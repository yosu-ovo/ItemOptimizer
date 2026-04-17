using System;
using System.Collections.Generic;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// Per-zone command buffer. Each zone gets its own buffer during the parallel tick phase.
    /// Worker threads write to their zone's buffer without locking (one worker per zone).
    /// After tick, the runtime merges and applies all buffers on the main thread.
    ///
    /// Internally uses typed lists per command type to avoid boxing.
    /// </summary>
    public sealed class CommandBuffer
    {
        // ── Typed storage (one list per command type, zero boxing) ──
        internal readonly List<SignalCmd> Signals = new(16);
        internal readonly List<HullWaterCmd> HullWaters = new(8);
        internal readonly List<SpawnCmd> Spawns = new(4);
        internal readonly List<StatusEffectCmd> StatusEffects = new(8);
        internal readonly List<DamageCmd> Damages = new(4);
        internal readonly List<PhysicsForceCmd> PhysicsForces = new(4);
        internal readonly List<SoundCmd> Sounds = new(4);
        internal readonly List<DeferredCmd> Deferred = new(4);

        /// <summary>Total commands in this buffer.</summary>
        public int Count =>
            Signals.Count + HullWaters.Count + Spawns.Count + StatusEffects.Count +
            Damages.Count + PhysicsForces.Count + Sounds.Count + Deferred.Count;

        // ── Add methods (called by TickContext.Emit*) ──

        public void Add(SignalCmd cmd) => Signals.Add(cmd);
        public void Add(HullWaterCmd cmd) => HullWaters.Add(cmd);
        public void Add(SpawnCmd cmd) => Spawns.Add(cmd);
        public void Add(StatusEffectCmd cmd) => StatusEffects.Add(cmd);
        public void Add(DamageCmd cmd) => Damages.Add(cmd);
        public void Add(PhysicsForceCmd cmd) => PhysicsForces.Add(cmd);
        public void Add(SoundCmd cmd) => Sounds.Add(cmd);
        public void Add(DeferredCmd cmd) => Deferred.Add(cmd);

        // ── Apply all commands (main thread, after merge) ──

        /// <summary>
        /// Apply all commands in this buffer to the live game state.
        /// Called on the main thread after the parallel tick phase.
        /// Mergeable commands are merged before apply.
        /// </summary>
        public void ApplyAll()
        {
            // Mergeable: merge then apply
            ApplyMerged(HullWaters);
            ApplyMerged(PhysicsForces);

            // Non-mergeable: apply directly
            for (int i = 0; i < Signals.Count; i++) Signals[i].Apply();
            for (int i = 0; i < Spawns.Count; i++) Spawns[i].Apply();
            for (int i = 0; i < StatusEffects.Count; i++) StatusEffects[i].Apply();
            for (int i = 0; i < Damages.Count; i++) Damages[i].Apply();
            for (int i = 0; i < Sounds.Count; i++) Sounds[i].Apply();
            for (int i = 0; i < Deferred.Count; i++) Deferred[i].Apply();
        }

        /// <summary>Clear all command lists for reuse next frame.</summary>
        public void Clear()
        {
            Signals.Clear();
            HullWaters.Clear();
            Spawns.Clear();
            StatusEffects.Clear();
            Damages.Clear();
            PhysicsForces.Clear();
            Sounds.Clear();
            Deferred.Clear();
        }

        // ── Merge helpers ──

        /// <summary>
        /// Merge commands with the same MergeKey, then apply.
        /// Uses a temporary dictionary — acceptable because this runs once per zone per frame.
        /// </summary>
        private static void ApplyMerged<T>(List<T> commands) where T : struct, ICommand, IMergeableCommand<T>
        {
            if (commands.Count == 0) return;

            if (commands.Count == 1)
            {
                commands[0].Apply();
                return;
            }

            // Merge by key
            var merged = new Dictionary<ulong, T>(commands.Count);
            for (int i = 0; i < commands.Count; i++)
            {
                var cmd = commands[i];
                var key = cmd.MergeKey;
                if (merged.TryGetValue(key, out var existing))
                    merged[key] = existing.Merge(cmd);
                else
                    merged[key] = cmd;
            }

            foreach (var cmd in merged.Values)
                cmd.Apply();
        }
    }
}
