using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.World
{
    // ═══════════════════════════════════════════════════════
    //  Command interfaces
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// A deferred world mutation produced by NativeComponent.Tick().
    /// Commands are data — they describe WHAT should happen, not HOW.
    /// The runtime applies them on the main thread after the parallel tick phase.
    /// </summary>
    public interface ICommand
    {
        /// <summary>Apply this command to the live game state. Main thread only.</summary>
        void Apply();
    }

    /// <summary>
    /// A command that can be merged with another of the same type targeting the same entity.
    /// Example: 20 sprinklers each emit HullWaterCmd(hull, +0.5) → merged to HullWaterCmd(hull, +10).
    /// Only valid for commutative/associative operations (addition, max, min).
    /// </summary>
    public interface IMergeableCommand<T> where T : struct, ICommand
    {
        /// <summary>Key identifying the merge target (e.g., hull ID). Same key = can merge.</summary>
        ulong MergeKey { get; }

        /// <summary>Combine this command with another. Must be associative and commutative.</summary>
        T Merge(T other);
    }

    // ═══════════════════════════════════════════════════════
    //  Concrete command types (readonly structs, zero-alloc)
    // ═══════════════════════════════════════════════════════

    /// <summary>Send a signal through a connection via Item.SendSignal (matches vanilla API).</summary>
    public readonly struct SignalCmd : ICommand
    {
        public readonly Item SourceItem;
        public readonly Connection Target;
        public readonly string Value;
        public readonly int StepValue;
        public readonly Character Sender;

        public SignalCmd(Item sourceItem, Connection target, string value, int stepValue = 0,
            Character sender = null)
        {
            SourceItem = sourceItem;
            Target = target;
            Value = value;
            StepValue = stepValue;
            Sender = sender;
        }

        public void Apply()
        {
            if (SourceItem != null && Target != null)
                SourceItem.SendSignal(new Signal(Value, StepValue, Sender, SourceItem), Target);
            else
                Target?.SendSignal(new Signal(Value, StepValue, Sender, SourceItem));
        }
    }

    /// <summary>Modify a hull's water volume. Mergeable by hull ID (additive).</summary>
    public readonly struct HullWaterCmd : ICommand, IMergeableCommand<HullWaterCmd>
    {
        public readonly Hull Target;
        public readonly float Delta;

        public HullWaterCmd(Hull target, float delta)
        {
            Target = target;
            Delta = delta;
        }

        public ulong MergeKey => (ulong)Target.ID;

        public HullWaterCmd Merge(HullWaterCmd other) =>
            new HullWaterCmd(Target, Delta + other.Delta);

        public void Apply()
        {
            if (Target != null)
                Target.WaterVolume += Delta;
        }
    }

    /// <summary>Spawn an item at a world position.</summary>
    public readonly struct SpawnCmd : ICommand
    {
        public readonly ItemPrefab Prefab;
        public readonly Vector2 WorldPosition;
        public readonly Vector2 Velocity;
        public readonly Submarine Submarine;

        public SpawnCmd(ItemPrefab prefab, Vector2 worldPosition,
            Vector2 velocity = default, Submarine submarine = null)
        {
            Prefab = prefab;
            WorldPosition = worldPosition;
            Velocity = velocity;
            Submarine = submarine;
        }

        public void Apply()
        {
            Entity.Spawner?.AddItemToSpawnQueue(Prefab, WorldPosition, Submarine);
            // TODO: apply initial velocity after spawn
        }
    }

    /// <summary>Apply status effects on a component or item.</summary>
    public readonly struct StatusEffectCmd : ICommand
    {
        public readonly ActionType Type;
        public readonly ItemComponent Component;
        public readonly Item SourceItem;
        public readonly float DeltaTime;

        public StatusEffectCmd(ActionType type, ItemComponent component, Item sourceItem, float deltaTime)
        {
            Type = type;
            Component = component;
            SourceItem = sourceItem;
            DeltaTime = deltaTime;
        }

        public void Apply()
        {
            if (Component != null)
                Component.ApplyStatusEffects(Type, DeltaTime);
            else
                SourceItem?.ApplyStatusEffects(Type, DeltaTime);
        }
    }

    /// <summary>Apply damage to a character.</summary>
    public readonly struct DamageCmd : ICommand
    {
        public readonly Character Target;
        public readonly Vector2 WorldPosition;
        public readonly float Damage;
        public readonly string DamageType;

        public DamageCmd(Character target, Vector2 worldPosition, float damage,
            string damageType = "damage")
        {
            Target = target;
            WorldPosition = worldPosition;
            Damage = damage;
            DamageType = damageType;
        }

        public void Apply()
        {
            // TODO: proper damage application via AfflictionPrefab
            // Target?.AddDamage(WorldPosition, ...);
        }
    }

    /// <summary>Apply a force to a physics body.</summary>
    public readonly struct PhysicsForceCmd : ICommand, IMergeableCommand<PhysicsForceCmd>
    {
        public readonly PhysicsBody Body;
        public readonly Vector2 Force;

        public PhysicsForceCmd(PhysicsBody body, Vector2 force)
        {
            Body = body;
            Force = force;
        }

        public ulong MergeKey => Body != null ? (ulong)Body.GetHashCode() : 0;

        public PhysicsForceCmd Merge(PhysicsForceCmd other) =>
            new PhysicsForceCmd(Body, Force + other.Force);

        public void Apply()
        {
            Body?.ApplyForce(Force);
        }
    }

    /// <summary>
    /// Play a sound at a world position.
    /// Sound identifier is a string (resolved to platform-specific sound by the client runtime).
    /// Shared code does not reference client Sound types directly.
    /// </summary>
    public readonly struct SoundCmd : ICommand
    {
        public readonly string SoundTag;
        public readonly Vector2 WorldPosition;
        public readonly float Volume;
        public readonly float Range;

        public SoundCmd(string soundTag, Vector2 worldPosition, float volume = 1f, float range = 1000f)
        {
            SoundTag = soundTag;
            WorldPosition = worldPosition;
            Volume = volume;
            Range = range;
        }

        public void Apply()
        {
            // Client-only: resolved by client runtime
            // Server: no-op or broadcast to clients
        }
    }

    /// <summary>
    /// Escape hatch: defer an arbitrary action to the main thread.
    /// Use sparingly — this bypasses the command model's determinism guarantees.
    /// For operations that don't fit any typed command.
    /// </summary>
    public readonly struct DeferredCmd : ICommand
    {
        public readonly Action Action;

        public DeferredCmd(Action action)
        {
            Action = action;
        }

        public void Apply()
        {
            Action?.Invoke();
        }
    }
}
