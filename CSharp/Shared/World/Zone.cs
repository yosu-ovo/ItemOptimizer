using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// A spatial partition of the world. Zones are the fundamental unit for LOD,
    /// parallel dispatch, and state management.
    ///
    /// Key design: zones move with their host entity (submarine, vehicle),
    /// NOT fixed to world coordinates. WorldGrid cells are just a coarse index
    /// for neighbor queries.
    /// </summary>
    public abstract class Zone
    {
        public int Id { get; internal set; }
        public ZoneTier Tier { get; internal set; }

        /// <summary>World-space center of this zone. Updated each frame by the runtime.</summary>
        public Vector2 Position { get; internal set; }

        /// <summary>Approximate radius for neighbor/overlap queries.</summary>
        public float Radius { get; internal set; }

        /// <summary>Zones within interaction range (maintained by ZoneGraph each frame).</summary>
        internal readonly List<Zone> Neighbors = new(4);

        /// <summary>NativeComponents registered to this zone.</summary>
        internal readonly List<NativeComponent> Components = new(64);

        /// <summary>Read-only snapshot built by runtime before parallel tick phase.</summary>
        internal ZoneSnapshot Snapshot;

        /// <summary>Command buffer filled during parallel tick, applied on main thread.</summary>
        internal CommandBuffer Commands;

        /// <summary>
        /// Optional zone-level proxy. When non-null, the runtime calls Proxy.Tick()
        /// instead of ticking individual components. Used for dormant/distant zones
        /// where a coarse approximation replaces per-entity simulation.
        /// </summary>
        public virtual IZoneProxy Proxy => null;

        /// <summary>WorldGrid cell index (maintained by WorldGrid.Move).</summary>
        internal int CellX, CellY;
    }

    /// <summary>
    /// Zone tied to a submarine. The most common zone type.
    /// Position tracks Submarine.WorldPosition. Internal items/characters
    /// are permanently assigned (unless they physically leave the sub).
    /// </summary>
    public class SubmarineZone : Zone
    {
        public Submarine Submarine { get; internal set; }
    }

    /// <summary>
    /// Zone for independently moving vehicles (tanks, aircraft, mini-subs).
    /// Position tracks the vehicle root item. Components are the vehicle's
    /// internal parts (engine, turret, weapons).
    /// </summary>
    public class VehicleZone : Zone
    {
        public Item VehicleRoot { get; internal set; }
    }

    /// <summary>
    /// Zone for a fixed world region (cave system, level feature).
    /// Position is static. Used for level-generated content that doesn't move.
    /// </summary>
    public class RegionZone : Zone
    {
        public Rectangle Bounds { get; internal set; }
    }

    /// <summary>
    /// Zone for loose floating entities (debris clusters, creature groups).
    /// Position is the centroid of the cluster. Entities may drift apart
    /// and get reassigned to other zones.
    /// </summary>
    public class LooseZone : Zone
    {
    }

    /// <summary>
    /// Replaces per-component ticking for an entire zone.
    /// Used when zone.Tier is Passive or Dormant — a single lightweight
    /// computation replaces hundreds of individual component ticks.
    /// </summary>
    public interface IZoneProxy
    {
        void Tick(ref TickContext ctx);
    }
}
