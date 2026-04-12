using System.Collections.Generic;
using Barotrauma;

namespace ItemOptimizerMod
{
    static class ColdStorageDetector
    {
        // Per-frame set of items being actively used by characters
        private static readonly HashSet<Item> _activeItems = new();
        private static bool _activeItemsDirty = true;

        /// <summary>Call once per frame to invalidate the active items set.</summary>
        internal static void NewFrame()
        {
            _activeItemsDirty = true;
        }

        /// <summary>
        /// Conservative: item is in a non-character container (locker, crate, etc.)
        /// </summary>
        public static bool IsInColdStorage(Item item)
        {
            if (item.ParentInventory == null) return false;
            if (item.ParentInventory is CharacterInventory) return false;
            return item.GetRootInventoryOwner() is not Character;
        }

        /// <summary>
        /// Aggressive: item is not actively held in a character's hand slots.
        /// Catches items in inventory/backpack/bag slots too.
        /// </summary>
        public static bool IsNotInActiveUse(Item item)
        {
            if (item.ParentInventory == null) return false;

            var rootOwner = item.GetRootInventoryOwner();
            if (rootOwner is not Character character) return true;

            var topItem = item.RootContainer ?? item;
            return !character.HasEquippedItem(topItem,
                predicate: slot => (slot & (InvSlotType.LeftHand | InvSlotType.RightHand)) != InvSlotType.None);
        }

        /// <summary>
        /// For ModOpt: item is eligible for throttling.
        /// Covers both inventory items (not in hands) and submarine-installed items
        /// not currently being used by any character.
        /// </summary>
        public static bool IsModOptEligible(Item item)
        {
            // Items in an inventory
            if (item.ParentInventory != null)
            {
                var rootOwner = item.GetRootInventoryOwner();
                if (rootOwner is not Character character) return true; // in a locker/crate

                var topItem = item.RootContainer ?? item;
                return !character.HasEquippedItem(topItem,
                    predicate: slot => (slot & (InvSlotType.LeftHand | InvSlotType.RightHand)) != InvSlotType.None);
            }

            // Submarine-installed items: throttle if nobody is using them
            if (item.IsClaimedByBallastFlora) return false;

            return !IsBeingUsed(item);
        }

        /// <summary>
        /// Check if item is in the per-frame active items set.
        /// The set is rebuilt once per frame on first access.
        /// </summary>
        private static bool IsBeingUsed(Item item)
        {
            EnsureActiveItemsBuilt();
            return _activeItems.Contains(item);
        }

        /// <summary>Build the active items set once per frame (lazy, on first access).</summary>
        private static void EnsureActiveItemsBuilt()
        {
            if (!_activeItemsDirty) return;
            _activeItemsDirty = false;
            _activeItems.Clear();

            foreach (var character in Character.CharacterList)
            {
                if (character.Removed || character.IsDead) continue;
                if (character.SelectedItem != null)
                    _activeItems.Add(character.SelectedItem);
                if (character.SelectedSecondaryItem != null)
                    _activeItems.Add(character.SelectedSecondaryItem);
            }
        }
    }
}
