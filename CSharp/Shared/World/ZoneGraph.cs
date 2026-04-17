using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// Manages the zone topology: creation, destruction, tier evaluation, neighbor linkage.
    ///
    /// Each frame:
    ///   1. Update zone positions (from host entities)
    ///   2. Update WorldGrid cell assignments
    ///   3. Evaluate tiers (based on distance to players)
    ///   4. Rebuild neighbor lists
    ///
    /// This is the "backbone" that all other systems (NativeComponent runtime, proxy,
    /// LOD, serialization) build on.
    /// </summary>
    public class ZoneGraph
    {
        /// <summary>All active zones.</summary>
        public readonly List<Zone> Zones = new(32);

        /// <summary>Coarse spatial index.</summary>
        public WorldGrid Grid { get; private set; }

        /// <summary>Radius within which zones become neighbors.</summary>
        public float NeighborRadius = 8000f;

        /// <summary>Distance thresholds for tier assignment.</summary>
        public float ActiveRadius = 3000f;
        public float NearbyRadius = 6000f;
        public float PassiveRadius = 15000f;
        public float DormantRadius = 30000f;
        // Beyond DormantRadius → Unloaded (if enabled)

        // Reusable query buffer
        private readonly List<Zone> _queryBuffer = new(16);

        // ── Initialization ──

        /// <summary>
        /// Initialize the zone graph for a level.
        /// Creates a WorldGrid covering the level bounds and builds initial zones
        /// from all loaded submarines.
        /// </summary>
        public void Initialize(Vector2 levelOrigin, float levelWidth, float levelHeight)
        {
            Grid = new WorldGrid(levelOrigin, levelWidth, levelHeight);
            Zones.Clear();
        }

        // ── Zone lifecycle ──

        /// <summary>Create a SubmarineZone for a loaded submarine.</summary>
        public SubmarineZone CreateSubmarineZone(Submarine sub)
        {
            var zone = new SubmarineZone
            {
                Id = Zones.Count,
                Submarine = sub,
                Position = sub.WorldPosition,
                Radius = EstimateSubmarineRadius(sub),
                Tier = ZoneTier.Active,
            };
            Zones.Add(zone);
            Grid?.Insert(zone);
            return zone;
        }

        /// <summary>Create a VehicleZone for an independent vehicle.</summary>
        public VehicleZone CreateVehicleZone(Item vehicleRoot, float radius = 500f)
        {
            var zone = new VehicleZone
            {
                Id = Zones.Count,
                VehicleRoot = vehicleRoot,
                Position = vehicleRoot.WorldPosition,
                Radius = radius,
                Tier = ZoneTier.Active,
            };
            Zones.Add(zone);
            Grid?.Insert(zone);
            return zone;
        }

        /// <summary>Remove a zone (submarine left, destroyed, or unloaded to disk).</summary>
        public void RemoveZone(Zone zone)
        {
            Grid?.Remove(zone);
            Zones.Remove(zone);
        }

        // ── Per-frame update ──

        /// <summary>
        /// Update all zones for the current frame.
        /// Call once per frame on the main thread, before the tick phase.
        /// </summary>
        public void Update(IReadOnlyList<Character> players)
        {
            // Step 1: Sync zone positions with host entities
            for (int i = 0; i < Zones.Count; i++)
            {
                var zone = Zones[i];
                switch (zone)
                {
                    case SubmarineZone sz:
                        if (sz.Submarine != null)
                            sz.Position = sz.Submarine.WorldPosition;
                        break;
                    case VehicleZone vz:
                        if (vz.VehicleRoot != null)
                            vz.Position = vz.VehicleRoot.WorldPosition;
                        break;
                }
            }

            // Step 2: Update grid cell assignments
            if (Grid != null)
            {
                for (int i = 0; i < Zones.Count; i++)
                    Grid.UpdatePosition(Zones[i]);
            }

            // Step 3: Evaluate tiers
            for (int i = 0; i < Zones.Count; i++)
            {
                var zone = Zones[i];
                var oldTier = zone.Tier;
                zone.Tier = EvaluateTier(zone, players);

                if (zone.Tier != oldTier)
                {
                    // Notify components of tier change
                    for (int c = 0; c < zone.Components.Count; c++)
                        zone.Components[c].OnTierChanged(oldTier, zone.Tier);
                }
            }

            // Step 4: Rebuild neighbor lists
            for (int i = 0; i < Zones.Count; i++)
            {
                var zone = Zones[i];
                zone.Neighbors.Clear();

                if (zone.Tier >= ZoneTier.Dormant) continue; // dormant zones don't need neighbors

                Grid?.QueryNear(zone.Position, NeighborRadius, _queryBuffer);
                for (int j = 0; j < _queryBuffer.Count; j++)
                {
                    if (_queryBuffer[j] != zone)
                        zone.Neighbors.Add(_queryBuffer[j]);
                }
            }
        }

        // ── Tier evaluation ──

        private ZoneTier EvaluateTier(Zone zone, IReadOnlyList<Character> players)
        {
            float minDist = float.MaxValue;

            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] == null || players[i].IsDead) continue;
                float dist = Vector2.Distance(zone.Position, players[i].WorldPosition);
                if (dist < minDist) minDist = dist;
            }

            // Player inside this zone's submarine → always Active
            if (zone is SubmarineZone sz)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i]?.Submarine == sz.Submarine)
                        return ZoneTier.Active;
                }
            }

            if (minDist <= ActiveRadius) return ZoneTier.Active;
            if (minDist <= NearbyRadius) return ZoneTier.Nearby;
            if (minDist <= PassiveRadius) return ZoneTier.Passive;
            if (minDist <= DormantRadius) return ZoneTier.Dormant;
            return ZoneTier.Unloaded;
        }

        // ── Helpers ──

        private static float EstimateSubmarineRadius(Submarine sub)
        {
            // Half-diagonal of the sub's bounding box
            var borders = sub.Borders;
            return new Vector2(borders.Width, borders.Height).Length() * 0.5f;
        }

        /// <summary>
        /// Find the zone that contains a world position.
        /// Uses WorldGrid for fast lookup, then checks zone radius.
        /// Returns null if no zone contains the point.
        /// </summary>
        public Zone FindZoneAt(Vector2 worldPos)
        {
            Grid?.QueryAt(worldPos, _queryBuffer);
            for (int i = 0; i < _queryBuffer.Count; i++)
            {
                var zone = _queryBuffer[i];
                if (Vector2.Distance(worldPos, zone.Position) <= zone.Radius)
                    return zone;
            }
            return null;
        }

        public void Reset()
        {
            Zones.Clear();
            // Grid is recreated on next Initialize
        }
    }
}
