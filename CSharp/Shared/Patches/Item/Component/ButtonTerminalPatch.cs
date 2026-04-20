using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Eliminates per-frame LINQ allocations in ButtonTerminal.Update().
    /// Vanilla IsActivated property uses nested .Any() with lambda closures on a HashSet
    /// (3 enumerator allocs + 2 closure allocs per frame per terminal).
    /// This patch replaces with zero-alloc foreach + HashSet.Contains.
    /// </summary>
    static class ButtonTerminalPatch
    {
        // Access private auto-property backing fields
        private static readonly AccessTools.FieldRef<ButtonTerminal, HashSet<ItemPrefab>> Ref_activatingPrefabs =
            AccessTools.FieldRefAccess<ButtonTerminal, HashSet<ItemPrefab>>("<ActivatingItemPrefabs>k__BackingField");

        private static readonly AccessTools.FieldRef<ButtonTerminal, ItemContainer> Ref_container =
            AccessTools.FieldRefAccess<ButtonTerminal, ItemContainer>("<Container>k__BackingField");

        internal static void Execute(ButtonTerminal __instance, float deltaTime)
        {
            if (!OptimizerConfig.EnableButtonTerminalOpt)
            {
                __instance.Update(deltaTime, null);
                return;
            }

            // Replicate base.Update() → ApplyStatusEffects(OnActive, deltaTime)
            __instance.ApplyStatusEffects(ActionType.OnActive, deltaTime);

            bool activated = IsActivatedFast(__instance);
            __instance.item.SendSignal(activated ? "1" : "0", "state_out");
        }

        /// <summary>
        /// Zero-alloc replacement for ButtonTerminal.IsActivated property.
        /// Original: ActivatingItemPrefabs.None() || Container.Inventory.AllItems.Any(i => ActivatingItemPrefabs.Any(p => p == i.Prefab))
        /// </summary>
        private static bool IsActivatedFast(ButtonTerminal bt)
        {
            var prefabs = Ref_activatingPrefabs(bt);
            if (prefabs == null || prefabs.Count == 0) return true;

            var container = Ref_container(bt);
            if (container?.Inventory == null) return false;

            foreach (var item in container.Inventory.AllItems)
            {
                if (item != null && prefabs.Contains(item.Prefab))
                    return true;
            }
            return false;
        }
    }
}
