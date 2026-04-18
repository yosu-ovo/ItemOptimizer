using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using ItemOptimizerMod.World;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Caps the number of ALT interaction labels to prevent O(n^4) overlap prevention lag.
    /// Vanilla PreventInteractionLabelOverlap uses maxIterations = n^2 with an O(n^2) inner loop.
    /// With 800+ items this causes multi-second freezes when ALT is pressed.
    /// </summary>
    static class InteractionLabelPatch
    {
        private static FieldInfo _interactablesField;

        internal static void Register(Harmony harmony)
        {
            var targetType = typeof(InteractionLabelManager);
            _interactablesField = AccessTools.Field(targetType, "interactablesInRange");

            if (_interactablesField == null)
            {
                LuaCsLogger.LogError("[ItemOptimizer] InteractionLabelPatch: " +
                    "could not find InteractionLabelManager.interactablesInRange field");
                return;
            }

            var original = AccessTools.Method(targetType, "RefreshInteractablesInRange");
            if (original == null)
            {
                LuaCsLogger.LogError("[ItemOptimizer] InteractionLabelPatch: " +
                    "could not find InteractionLabelManager.RefreshInteractablesInRange method");
                return;
            }

            var postfix = AccessTools.Method(typeof(InteractionLabelPatch), nameof(Postfix));
            harmony.Patch(original, postfix: new HarmonyMethod(postfix));
        }

        private static void Postfix()
        {
            if (!OptimizerConfig.EnableInteractionLabelOpt) return;

            var list = _interactablesField.GetValue(null) as List<Item>;
            if (list == null || list.Count == 0) return;

            // Remove zone-managed items — they are engine-controlled, not player-interactable
            list.RemoveAll(item => NativeRuntimeBridge.IsZoneManaged[item.ID]);

            int max = OptimizerConfig.InteractionLabelMaxCount;
            if (list.Count <= max) return;

            // Keep only the nearest N items
            var character = Character.Controlled;
            if (character == null) return;

            var pos = character.WorldPosition;
            list.Sort((a, b) =>
                Vector2.DistanceSquared(a.WorldPosition, pos)
                .CompareTo(Vector2.DistanceSquared(b.WorldPosition, pos)));
            list.RemoveRange(max, list.Count - max);
        }
    }
}
