namespace ItemOptimizerMod.World
{
    /// <summary>
    /// LOD tier for zones. Determines update frequency and simulation fidelity.
    /// Lower value = higher fidelity. Assigned by ZoneEvaluator each frame based on
    /// distance to players, visibility, and importance.
    /// </summary>
    public enum ZoneTier : byte
    {
        /// <summary>Player's zone or immediately adjacent. Full fidelity every frame.</summary>
        Active = 0,

        /// <summary>Visible or nearby zones. Full fidelity, some subsystems may reduce frequency.</summary>
        Nearby = 1,

        /// <summary>Same submarine but far from player, or adjacent submarine. Reduced frequency (every N frames).</summary>
        Passive = 2,

        /// <summary>Distant zones. Frozen state, only critical updates (damage response). No tick.</summary>
        Dormant = 3,

        /// <summary>Extremely far. Serialized to disk, no memory footprint. Restored on approach.</summary>
        Unloaded = 4,
    }
}
