using System.Collections.Generic;
using Barotrauma;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Per-frame Hull → Character mapping for spatial pre-filtering.
    /// Rebuilt once per map frame (before item dispatch) in O(C) where C = character count.
    /// Consumed by MotionSensorRewrite to avoid iterating the global CharacterList.
    /// </summary>
    static class HullCharacterTracker
    {
        // Hull.ID → characters in that hull. Flat array indexed by entity ID (max 65536).
        private static List<Character>[] _hullCharacters = new List<Character>[65536];

        // Characters with no hull (open water, monsters, transitioning through gaps)
        private static readonly List<Character> _noHullCharacters = new List<Character>(32);

        // Track which slots were used this frame to avoid clearing all 65536
        private static readonly List<int> _usedSlots = new List<int>(128);

        private static readonly List<Character> _emptyList = new List<Character>(0);

        /// <summary>
        /// Rebuild the hull→character index. Must be called once per map frame,
        /// after Character.UpdateAll has set CurrentHull on all characters.
        /// Cost: O(CharacterList.Count) ≈ ~1μs for 40 characters.
        /// </summary>
        internal static void Rebuild()
        {
            // Clear only the slots we used last frame
            for (int i = 0; i < _usedSlots.Count; i++)
                _hullCharacters[_usedSlots[i]]?.Clear();
            _usedSlots.Clear();
            _noHullCharacters.Clear();

            var charList = Character.CharacterList;
            for (int i = 0; i < charList.Count; i++)
            {
                Character c = charList[i];
                if (c.Removed) continue;

                Hull hull = c.CurrentHull;
                if (hull == null)
                {
                    _noHullCharacters.Add(c);
                }
                else
                {
                    int hid = hull.ID;
                    var list = _hullCharacters[hid];
                    if (list == null)
                    {
                        list = new List<Character>(4);
                        _hullCharacters[hid] = list;
                    }
                    list.Add(c);
                    _usedSlots.Add(hid);
                }
            }
        }

        /// <summary>Get characters in a specific hull by hull entity ID.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static List<Character> GetCharactersInHull(int hullId)
            => _hullCharacters[hullId] ?? _emptyList;

        /// <summary>Get characters with no hull (open water / gap transitions).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static List<Character> GetCharactersWithoutHull()
            => _noHullCharacters;

        /// <summary>Reset all state (round end / mod reload).</summary>
        internal static void Reset()
        {
            for (int i = 0; i < _usedSlots.Count; i++)
            {
                var list = _hullCharacters[_usedSlots[i]];
                if (list != null) list.Clear();
            }
            _usedSlots.Clear();
            _noHullCharacters.Clear();
        }
    }
}
