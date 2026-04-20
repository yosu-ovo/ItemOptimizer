using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using ItemOptimizerMod.World;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Transpiler for Item.Update(): replaces the two component callvirts
    /// (ItemComponent.Update and ItemComponent.UpdateBroken) with unified
    /// dispatch methods that route known component types to our optimized
    /// implementations and apply zone-based skipping for unknown types.
    ///
    /// Replaces 6 separate Harmony prefix patches (WaterDetector, MotionSensor,
    /// PowerTransfer, Relay, PowerContainer, ButtonTerminal) with a single
    /// IL-level interception point — zero per-component Harmony trampoline overhead.
    /// </summary>
    static class ComponentDispatchTranspiler
    {
        // ── Reflection targets ──
        private static readonly MethodInfo _originalUpdate = AccessTools.Method(
            typeof(ItemComponent), nameof(ItemComponent.Update),
            new[] { typeof(float), typeof(Camera) });

        private static readonly MethodInfo _originalUpdateBroken = AccessTools.Method(
            typeof(ItemComponent), nameof(ItemComponent.UpdateBroken),
            new[] { typeof(float), typeof(Camera) });

        private static readonly MethodInfo _dispatchUpdate = AccessTools.Method(
            typeof(ComponentDispatchTranspiler), nameof(DispatchUpdate));

        private static readonly MethodInfo _dispatchUpdateBroken = AccessTools.Method(
            typeof(ComponentDispatchTranspiler), nameof(DispatchUpdateBroken));

        internal static bool CanPatch =>
            _originalUpdate != null && _originalUpdateBroken != null &&
            _dispatchUpdate != null && _dispatchUpdateBroken != null;

        // ════════════════════════════════════════════
        //  Transpiler
        // ════════════════════════════════════════════

        /// <summary>
        /// Replaces:
        ///   callvirt ItemComponent::Update(float, Camera)
        /// With:
        ///   ldarg.0   // push 'this' (Item)
        ///   call ComponentDispatchTranspiler::DispatchUpdate(ItemComponent, float, Camera, Item)
        ///
        /// Same for UpdateBroken → DispatchUpdateBroken.
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (!CanPatch)
            {
                LuaCsLogger.LogError("[ItemOptimizer] ComponentDispatchTranspiler: reflection failed, skipping");
                foreach (var instr in instructions) yield return instr;
                yield break;
            }

            int replacedUpdate = 0;
            int replacedBroken = 0;

            foreach (var instr in instructions)
            {
                if ((instr.opcode == OpCodes.Callvirt || instr.opcode == OpCodes.Call) &&
                    instr.operand is MethodInfo mi)
                {
                    if (mi == _originalUpdate)
                    {
                        // Stack before: [component, deltaTime, cam]
                        // Insert ldarg.0 (this = Item) then replace callvirt with static call
                        // Stack becomes: [component, deltaTime, cam, item]
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, _dispatchUpdate);
                        replacedUpdate++;
                        continue;
                    }

                    if (mi == _originalUpdateBroken)
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, _dispatchUpdateBroken);
                        replacedBroken++;
                        continue;
                    }
                }

                yield return instr;
            }

            if (replacedUpdate > 0 || replacedBroken > 0)
                LuaCsLogger.Log($"[ItemOptimizer] ComponentDispatchTranspiler: replaced {replacedUpdate} Update + {replacedBroken} UpdateBroken call(s)");
            else
                LuaCsLogger.LogError("[ItemOptimizer] ComponentDispatchTranspiler: no component Update/UpdateBroken calls found in Item.Update IL");
        }

        // ════════════════════════════════════════════
        //  Dispatch — called from transpiled IL
        // ════════════════════════════════════════════

        /// <summary>
        /// Unified dispatch for component.Update(dt, cam).
        /// Known types route to optimized Execute methods; unknown types
        /// get zone-based skip logic then fall through to vanilla.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DispatchUpdate(ItemComponent component, float dt, Camera cam, Item item)
        {
            // ── Known component rewrites ──
            // Order: most frequent types first (power > sensors > misc)
            // RelayComponent check BEFORE PowerTransfer (Relay extends PowerTransfer)
            switch (component)
            {
                case RelayComponent rc:
                    RelayRewrite.Execute(rc, dt);
                    return;
                case PowerTransfer pt:
                    PowerTransferRewrite.Execute(pt, dt);
                    return;
                case PowerContainer pc:
                    PowerContainerRewrite.Execute(pc, dt);
                    return;
                case WaterDetector wd:
                    WaterDetectorRewrite.Execute(wd, dt);
                    return;
                case MotionSensor ms:
                    MotionSensorRewrite.Execute(ms, dt);
                    return;
                case ButtonTerminal bt:
                    ButtonTerminalPatch.Execute(bt, dt);
                    return;
            }

            // ── Unknown component: zone-based skip ──
            int subId = item.Submarine != null ? (int)item.Submarine.ID & 0xFFFF : 0;
            byte tier = NativeRuntimeBridge.SubZoneTier[subId];

            if (tier >= 2)
            {
                // Dormant+: only critical components update
                if (!IsCriticalComponent(component))
                {
                    Stats.ComponentSkips++;
                    return;
                }
            }
            else if (tier >= 1)
            {
                // Passive: skip inert components
                if (IsInertComponent(component))
                {
                    Stats.InertComponentSkips++;
                    return;
                }
            }

            component.Update(dt, cam);
        }

        /// <summary>
        /// Unified dispatch for component.UpdateBroken(dt, cam).
        /// Applies same zone-based skip for non-critical components.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DispatchUpdateBroken(ItemComponent component, float dt, Camera cam, Item item)
        {
            // UpdateBroken is relatively rare (item.Condition <= 0).
            // Apply zone skip but don't dispatch to rewrites — broken items
            // should use vanilla behavior for safety.
            int subId = item.Submarine != null ? (int)item.Submarine.ID & 0xFFFF : 0;
            byte tier = NativeRuntimeBridge.SubZoneTier[subId];

            if (tier >= 2 && !IsCriticalComponent(component))
            {
                Stats.ComponentSkips++;
                return;
            }

            component.UpdateBroken(dt, cam);
        }

        // ════════════════════════════════════════════
        //  Component classification
        // ════════════════════════════════════════════

        /// <summary>
        /// Critical components that must always update regardless of zone tier.
        /// These affect gameplay correctness (power, propulsion, life support).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsCriticalComponent(ItemComponent c)
        {
            return c is Reactor
                || c is Engine
                || c is Steering
                || c is Pump
                || c is OxygenGenerator
                || c is DockingPort
                || c is Fabricator
                || c is Deconstructor
                || c is ElectricalDischarger
                || c is Turret;
        }

        /// <summary>
        /// Inert components that can be safely skipped in Passive+ zones.
        /// These are UI-only or have negligible gameplay impact when skipped.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsInertComponent(ItemComponent c)
        {
            return c is ConnectionPanel
                || c is ItemLabel
                || c is CustomInterface
                || c is Wire;
        }
    }
}
