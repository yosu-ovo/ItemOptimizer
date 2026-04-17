using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// Coarse spatial grid over the entire level.
    /// Each cell tracks which zones are currently in it.
    ///
    /// Purpose: fast neighbor queries ("which zones are near point X?")
    /// NOT for entity-level spatial queries — that's the zone's internal spatial hash.
    ///
    /// Zones move between cells as their host entity (submarine, vehicle) moves.
    /// Cell reassignment is O(1) per zone per frame, and only happens when
    /// a zone crosses a cell boundary (rare for most entities).
    /// </summary>
    public class WorldGrid
    {
        /// <summary>World-space size of each cell (in display units).</summary>
        public readonly float CellSize;

        /// <summary>Grid dimensions.</summary>
        public readonly int Width, Height;

        /// <summary>World-space origin (top-left of the grid).</summary>
        public readonly Vector2 Origin;

        // cells[y * Width + x] = list of zones in that cell
        private readonly List<Zone>[] _cells;

        public WorldGrid(Vector2 origin, float worldWidth, float worldHeight, float cellSize = 4000f)
        {
            Origin = origin;
            CellSize = cellSize;
            Width = Math.Max(1, (int)Math.Ceiling(worldWidth / cellSize));
            Height = Math.Max(1, (int)Math.Ceiling(worldHeight / cellSize));
            _cells = new List<Zone>[Width * Height];
            for (int i = 0; i < _cells.Length; i++)
                _cells[i] = new List<Zone>(4);
        }

        /// <summary>
        /// Place a zone into the grid based on its current position.
        /// Called once when a zone is created.
        /// </summary>
        public void Insert(Zone zone)
        {
            var (cx, cy) = WorldToCell(zone.Position);
            zone.CellX = cx;
            zone.CellY = cy;
            GetCell(cx, cy)?.Add(zone);
        }

        /// <summary>
        /// Remove a zone from the grid.
        /// Called when a zone is destroyed or unloaded.
        /// </summary>
        public void Remove(Zone zone)
        {
            GetCell(zone.CellX, zone.CellY)?.Remove(zone);
        }

        /// <summary>
        /// Update a zone's cell assignment if it has moved.
        /// Called each frame for moving zones (submarines, vehicles).
        /// Returns true if the zone changed cells.
        /// </summary>
        public bool UpdatePosition(Zone zone)
        {
            var (cx, cy) = WorldToCell(zone.Position);
            if (cx == zone.CellX && cy == zone.CellY)
                return false;

            GetCell(zone.CellX, zone.CellY)?.Remove(zone);
            zone.CellX = cx;
            zone.CellY = cy;
            GetCell(cx, cy)?.Add(zone);
            return true;
        }

        /// <summary>
        /// Query all zones within a radius of a world position.
        /// Returns zones in the target cell and all adjacent cells within range.
        /// </summary>
        public void QueryNear(Vector2 worldPos, float radius, List<Zone> results)
        {
            results.Clear();
            int cellRadius = Math.Max(1, (int)Math.Ceiling(radius / CellSize));
            var (cx, cy) = WorldToCell(worldPos);

            for (int dy = -cellRadius; dy <= cellRadius; dy++)
            {
                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    var cell = GetCell(cx + dx, cy + dy);
                    if (cell == null) continue;
                    for (int i = 0; i < cell.Count; i++)
                        results.Add(cell[i]);
                }
            }
        }

        /// <summary>
        /// Query the zone(s) at an exact world position (single cell).
        /// Used for projectile hit detection.
        /// </summary>
        public void QueryAt(Vector2 worldPos, List<Zone> results)
        {
            results.Clear();
            var (cx, cy) = WorldToCell(worldPos);
            var cell = GetCell(cx, cy);
            if (cell != null)
            {
                for (int i = 0; i < cell.Count; i++)
                    results.Add(cell[i]);
            }
        }

        // ── Internals ──

        private (int x, int y) WorldToCell(Vector2 worldPos)
        {
            int cx = (int)((worldPos.X - Origin.X) / CellSize);
            int cy = (int)((worldPos.Y - Origin.Y) / CellSize);
            return (Math.Clamp(cx, 0, Width - 1), Math.Clamp(cy, 0, Height - 1));
        }

        private List<Zone> GetCell(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
                return null;
            return _cells[y * Width + x];
        }
    }
}
