using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// Read-only snapshot of a zone's observable state, built once per frame
    /// on the main thread before the parallel tick phase.
    ///
    /// Workers read this instead of touching live game objects.
    /// This is the key to thread safety: Tick reads snapshots, writes commands.
    /// </summary>
    public readonly struct ZoneSnapshot
    {
        public readonly Zone Zone;
        public readonly IReadOnlyList<ItemSnapshot> Items;
        public readonly IReadOnlyList<CharacterSnapshot> Characters;
        public readonly IReadOnlyList<Zone> Neighbors;
        public readonly uint Frame;

        public ZoneSnapshot(Zone zone, IReadOnlyList<ItemSnapshot> items,
            IReadOnlyList<CharacterSnapshot> characters, IReadOnlyList<Zone> neighbors, uint frame)
        {
            Zone = zone;
            Items = items;
            Characters = characters;
            Neighbors = neighbors;
            Frame = frame;
        }
    }

    /// <summary>
    /// Snapshot of one item's publicly readable state.
    /// Copied from Item on the main thread, read by workers.
    /// </summary>
    public readonly struct ItemSnapshot
    {
        public readonly ushort ItemId;
        public readonly Vector2 Position;
        public readonly float Condition;
        public readonly bool IsActive;
        public readonly Hull CurrentHull;

        public ItemSnapshot(Item item)
        {
            ItemId = item.ID;
            Position = item.WorldPosition;
            Condition = item.Condition;
            IsActive = item.IsActive;
            CurrentHull = item.CurrentHull;
        }
    }

    /// <summary>
    /// Snapshot of one character's publicly readable state.
    /// Copied from Character on the main thread, read by workers.
    /// </summary>
    public readonly struct CharacterSnapshot
    {
        public readonly ushort CharacterId;
        public readonly Vector2 Position;
        public readonly Vector2 Velocity;
        public readonly bool IsAlive;
        public readonly bool IsIncapacitated;
        public readonly Hull CurrentHull;

        public CharacterSnapshot(Character character)
        {
            CharacterId = character.ID;
            Position = character.WorldPosition;
            Velocity = character.AnimController?.Collider?.LinearVelocity ?? Vector2.Zero;
            IsAlive = !character.IsDead;
            IsIncapacitated = character.IsIncapacitated;
            CurrentHull = character.CurrentHull;
        }
    }

    /// <summary>
    /// Global world snapshot for the Read phase.
    /// Contains cross-zone data that components may need.
    /// </summary>
    public readonly struct WorldSnapshot
    {
        public readonly uint Frame;
        public readonly float DeltaTime;
        public readonly IReadOnlyList<Zone> AllZones;

        public WorldSnapshot(uint frame, float deltaTime, IReadOnlyList<Zone> allZones)
        {
            Frame = frame;
            DeltaTime = deltaTime;
            AllZones = allZones;
        }
    }
}
