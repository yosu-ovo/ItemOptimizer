using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// Context passed to NativeComponent.Tick().
    /// Provides read access to zone snapshot and write access to command buffer.
    ///
    /// Passed by ref for zero-copy. Do NOT store or cache this struct.
    /// It is only valid for the duration of a single Tick() call.
    /// </summary>
    public struct TickContext
    {
        /// <summary>Wall-clock delta time for this frame.</summary>
        public readonly float DeltaTime;

        /// <summary>Monotonic frame counter.</summary>
        public readonly uint Frame;

        /// <summary>The zone this component belongs to.</summary>
        public readonly Zone Zone;

        /// <summary>
        /// Read-only snapshot of this zone + neighbors.
        /// Use this for reading world state instead of accessing live objects.
        /// </summary>
        public readonly ZoneSnapshot Snapshot;

        // ── Private: command buffer (worker-local, no lock needed) ──
        private readonly CommandBuffer _commands;
        private readonly Item _host;

        public TickContext(float deltaTime, uint frame, Zone zone,
            ZoneSnapshot snapshot, CommandBuffer commands, Item host)
        {
            DeltaTime = deltaTime;
            Frame = frame;
            Zone = zone;
            Snapshot = snapshot;
            _commands = commands;
            _host = host;
        }

        // ═══════════════════════════════════════════════════════
        //  Command emission API
        //  Each method creates a command struct and adds it to the buffer.
        //  These are the ONLY way a NativeComponent should produce side effects.
        // ═══════════════════════════════════════════════════════

        /// <summary>Send a signal through a connection.</summary>
        public void EmitSignal(Connection target, string value, int stepValue = 0,
            Character sender = null)
        {
            _commands.Add(new SignalCmd(_host, target, value, stepValue, sender));
        }

        /// <summary>Modify a hull's water volume.</summary>
        public void EmitHullWater(Hull target, float delta)
        {
            _commands.Add(new HullWaterCmd(target, delta));
        }

        /// <summary>Spawn an item at a world position.</summary>
        public void EmitSpawn(ItemPrefab prefab, Vector2 worldPos,
            Vector2 velocity = default, Submarine sub = null)
        {
            _commands.Add(new SpawnCmd(prefab, worldPos, velocity, sub));
        }

        /// <summary>Apply status effects from the host item.</summary>
        public void EmitStatusEffect(ActionType type)
        {
            _commands.Add(new StatusEffectCmd(type, null, _host, DeltaTime));
        }

        /// <summary>Apply status effects from a specific component.</summary>
        public void EmitStatusEffect(ActionType type, ItemComponent component)
        {
            _commands.Add(new StatusEffectCmd(type, component, _host, DeltaTime));
        }

        /// <summary>Apply damage to a character.</summary>
        public void EmitDamage(Character target, Vector2 worldPos, float damage,
            string damageType = "damage")
        {
            _commands.Add(new DamageCmd(target, worldPos, damage, damageType));
        }

        /// <summary>Apply a force to a physics body.</summary>
        public void EmitPhysicsForce(PhysicsBody body, Vector2 force)
        {
            _commands.Add(new PhysicsForceCmd(body, force));
        }

        /// <summary>Play a sound (client-side, no-op on server).</summary>
        public void EmitSound(string soundTag, Vector2 worldPos, float volume = 1f, float range = 1000f)
        {
            _commands.Add(new SoundCmd(soundTag, worldPos, volume, range));
        }

        /// <summary>
        /// Escape hatch — defer an arbitrary action to the main thread.
        /// Use sparingly: this bypasses determinism and replay guarantees.
        /// </summary>
        public void DeferToMainThread(System.Action action)
        {
            _commands.Add(new DeferredCmd(action));
        }
    }

    /// <summary>
    /// Context passed to NativeComponent.Read().
    /// Provides read-only access to the world for data gathering before parallel tick.
    /// Called on the main thread — safe to read any game state.
    /// </summary>
    public struct ReadContext
    {
        /// <summary>Wall-clock delta time.</summary>
        public readonly float DeltaTime;

        /// <summary>Monotonic frame counter.</summary>
        public readonly uint Frame;

        /// <summary>The zone this component belongs to.</summary>
        public readonly Zone Zone;

        /// <summary>Global world snapshot (all zones).</summary>
        public readonly WorldSnapshot World;

        public ReadContext(float deltaTime, uint frame, Zone zone, WorldSnapshot world)
        {
            DeltaTime = deltaTime;
            Frame = frame;
            Zone = zone;
            World = world;
        }
    }
}
