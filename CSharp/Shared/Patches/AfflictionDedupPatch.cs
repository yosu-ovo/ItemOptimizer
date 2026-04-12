using System.Collections.Generic;
using Barotrauma;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Skips redundant affliction application when an affliction is already at max strength.
    ///
    /// Many mod weapons apply the same buff/debuff every frame via OnActive StatusEffects
    /// (e.g. tsm_straight_grip_buff strength=2, maxstrength=1). Once the affliction is at max,
    /// each call still triggers: Lua event → affliction lookup → GetResistance (O(all_afflictions))
    /// → RecalculateVitality (O(all_afflictions)). This patch detects the saturated case and skips.
    /// </summary>
    static class AfflictionDedupPatch
    {
        /// <summary>
        /// Prefix for CharacterHealth.ApplyAffliction.
        /// If the character already has this affliction at max strength and the new application
        /// would not increase it, skip entirely.
        /// </summary>
        public static bool Prefix(CharacterHealth __instance, Affliction affliction)
        {
            if (!OptimizerConfig.EnableAfflictionDedup) return true;
            if (affliction == null) return true;

            var prefab = affliction.Prefab;
            if (prefab == null) return true;

            // Only skip for buff afflictions — they're safe because being at max means no change.
            // For damage/debuffs, even at max they might have side effects (stun reset, duration reset).
            if (!prefab.IsBuff) return true;

            // Check if already at max
            float maxStrength = prefab.MaxStrength;
            if (maxStrength <= 0f) return true;

            var existing = __instance.GetAffliction(prefab.Identifier);
            if (existing == null) return true;

            // If existing strength is at or very near max, and new strength is positive (a re-application),
            // the result would be clamped to max — no change.
            if (existing.Strength >= maxStrength * 0.99f && affliction.Strength > 0f)
            {
                Stats.AfflictionDedupSkips++;
                return false;
            }

            return true;
        }
    }
}
