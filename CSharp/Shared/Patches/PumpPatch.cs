using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Eliminates per-frame LINQ allocation in Pump.Update():
    /// Item.GetComponent&lt;Repairable&gt;() calls Enumerable.First() on a List,
    /// allocating an enumerator each time. This transpiler replaces that call
    /// with a cached lookup via ConditionalWeakTable.
    /// </summary>
    static class PumpPatch
    {
        private static readonly ConditionalWeakTable<Item, StrongBox<Repairable>> _cache = new();

        private static readonly MethodInfo _getComponentRepairable = ResolveGetComponent();

        private static MethodInfo ResolveGetComponent()
        {
            // Find the open generic method Item.GetComponent<T>() (no parameters, 1 generic arg)
            foreach (var m in typeof(Item).GetMethods())
            {
                if (m.Name == "GetComponent" && m.IsGenericMethodDefinition &&
                    m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0)
                {
                    return m.MakeGenericMethod(typeof(Repairable));
                }
            }
            return null;
        }

        private static readonly MethodInfo _getCached =
            AccessTools.Method(typeof(PumpPatch), nameof(GetCachedRepairable));

        /// <summary>
        /// Transpiler: replace callvirt Item::GetComponent&lt;Repairable&gt;()
        /// with call PumpPatch::GetCachedRepairable(Item).
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (_getComponentRepairable == null || _getCached == null)
            {
                LuaCsLogger.LogError("[ItemOptimizer] PumpPatch: could not resolve methods, skipping transpiler");
                foreach (var instr in instructions) yield return instr;
                yield break;
            }

            int replaced = 0;
            foreach (var instr in instructions)
            {
                if ((instr.opcode == OpCodes.Callvirt || instr.opcode == OpCodes.Call) &&
                    instr.operand is MethodInfo mi &&
                    mi.IsGenericMethod &&
                    mi.GetGenericMethodDefinition() == _getComponentRepairable.GetGenericMethodDefinition() &&
                    mi.GetGenericArguments()[0] == typeof(Repairable))
                {
                    // Replace: callvirt Item::GetComponent<Repairable>()
                    // With:    call PumpPatch::GetCachedRepairable(Item)
                    // Stack is the same: [Item] → [Repairable]
                    yield return new CodeInstruction(OpCodes.Call, _getCached);
                    replaced++;
                }
                else
                {
                    yield return instr;
                }
            }

            if (replaced > 0)
                LuaCsLogger.Log($"[ItemOptimizer] PumpPatch: replaced {replaced} GetComponent<Repairable> call(s)");
            else
                LuaCsLogger.LogError("[ItemOptimizer] PumpPatch: no GetComponent<Repairable> calls found in Pump.Update IL");
        }

        /// <summary>
        /// Cached replacement for item.GetComponent&lt;Repairable&gt;().
        /// First call per item does the real lookup; subsequent calls return cached value.
        /// ConditionalWeakTable ensures GC cleanup when item is collected.
        /// </summary>
        public static Repairable GetCachedRepairable(Item item)
        {
            if (!OptimizerConfig.EnablePumpOpt)
                return item.GetComponent<Repairable>();

            var box = _cache.GetOrCreateValue(item);
            // StrongBox<Repairable> default .Value is null
            // Repairable is attached at item load and never changes, so first non-null result is permanent
            if (box.Value == null)
                box.Value = item.GetComponent<Repairable>();
            return box.Value;
        }
    }
}
