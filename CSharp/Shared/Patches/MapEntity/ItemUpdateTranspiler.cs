using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Transpiler for Item.Update(): wraps the two ApplyStatusEffects calls so that
    /// items with no status effects of the given type skip the call entirely —
    /// avoiding argument evaluation overhead and virtual dispatch into the method.
    ///
    /// Pattern: replace  callvirt Item::ApplyStatusEffects(...)
    ///          with     call ItemUpdateTranspiler::FastApplyStatusEffects(...)
    /// The wrapper reads hasStatusEffectsOfType[] and early-returns before calling
    /// the real method. Stack signature is identical (this + 7 args).
    /// </summary>
    static class ItemUpdateTranspiler
    {
        // ── Reflection ──
        private static readonly AccessTools.FieldRef<Item, bool[]> _hasEffectsRef =
            AccessTools.FieldRefAccess<Item, bool[]>("hasStatusEffectsOfType");

        private static readonly MethodInfo _originalApply = AccessTools.Method(
            typeof(Item), nameof(Item.ApplyStatusEffects),
            new[] { typeof(ActionType), typeof(float), typeof(Character),
                    typeof(Limb), typeof(Entity), typeof(bool), typeof(Vector2?) });

        private static readonly MethodInfo _fastApply = AccessTools.Method(
            typeof(ItemUpdateTranspiler), nameof(FastApplyStatusEffects));

        internal static bool CanPatch => _hasEffectsRef != null && _originalApply != null && _fastApply != null;

        // ════════════════════════════════════════════
        //  Transpiler
        // ════════════════════════════════════════════

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (!CanPatch)
            {
                LuaCsLogger.LogError("[ItemOptimizer] ItemUpdateTranspiler: reflection failed, skipping");
                foreach (var instr in instructions) yield return instr;
                yield break;
            }

            int replaced = 0;
            foreach (var instr in instructions)
            {
                // Match: callvirt/call Item::ApplyStatusEffects(ActionType, float, Character, Limb, Entity, bool, Vector2?)
                if ((instr.opcode == OpCodes.Callvirt || instr.opcode == OpCodes.Call) &&
                    instr.operand is MethodInfo mi && mi == _originalApply)
                {
                    // Replace with static call — same stack layout:
                    // [Item, ActionType, float, Character, Limb, Entity, bool, Vector2?]
                    yield return new CodeInstruction(OpCodes.Call, _fastApply);
                    replaced++;
                }
                else
                {
                    yield return instr;
                }
            }

            if (replaced > 0)
                LuaCsLogger.Log($"[ItemOptimizer] ItemUpdateTranspiler: replaced {replaced} ApplyStatusEffects call(s) in Item.Update");
            else
                LuaCsLogger.LogError("[ItemOptimizer] ItemUpdateTranspiler: no ApplyStatusEffects calls found in Item.Update IL");
        }

        // ════════════════════════════════════════════
        //  Fast wrapper — called from transpiled IL
        // ════════════════════════════════════════════

        /// <summary>
        /// Drop-in replacement for Item.ApplyStatusEffects with an early-out
        /// that checks hasStatusEffectsOfType[] before entering the method.
        /// Signature matches the original exactly so the IL stack is compatible.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FastApplyStatusEffects(
            Item item, ActionType type, float deltaTime,
            Character character, Limb limb, Entity useTarget,
            bool isNetworkEvent, Vector2? worldPosition)
        {
            // Fast path: direct field ref + array index (~2ns) vs full method entry + return (~30-50ns)
            if (!_hasEffectsRef(item)[(int)type]) return;

            item.ApplyStatusEffects(type, deltaTime, character, limb, useTarget, isNetworkEvent, worldPosition);
        }
    }
}
