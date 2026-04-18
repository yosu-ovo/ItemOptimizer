using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// P0 source-level optimizations for signal components.
    /// Each patch eliminates per-frame heap allocations without changing behavior.
    /// </summary>
    static class SignalOptPatches
    {
        internal static void Register(Harmony harmony)
        {
            // Only register old opt patches when rewrites are disabled — avoids stacking Harmony prefixes
            if (!OptimizerConfig.EnableRelayRewrite)
                RelayOpt.Register(harmony);
            if (!OptimizerConfig.EnableMotionSensorRewrite)
                MotionSensorOpt.Register(harmony);
            if (!OptimizerConfig.EnableWaterDetectorRewrite)
                WaterDetectorOpt.Register(harmony);
        }

        // ════════════════════════════════════════════
        //  1. RelayComponent — eliminate ToString() heap alloc + .Any() LINQ
        // ════════════════════════════════════════════
        internal static class RelayOpt
        {
            // Cached ToString values to avoid per-frame heap allocation
            private static readonly ConditionalWeakTable<RelayComponent, CachedRelayState> _cache = new();

            // Access to protected PowerTransfer members
            private static FieldInfo _isBrokenField;
            private static Action<RelayComponent> _refreshConnections;
            private static Action<RelayComponent> _setAllConnectionsDirty;

            private class CachedRelayState
            {
                public int PrevPowerValue = int.MinValue;
                public string PowerValueStr;
                public int PrevLoadValue = int.MinValue;
                public string LoadValueStr;
            }

            internal static void Register(Harmony harmony)
            {
                var targetType = typeof(RelayComponent);

                _isBrokenField = AccessTools.Field(typeof(PowerTransfer), "isBroken");
                var refreshMethod = AccessTools.Method(typeof(PowerTransfer), "RefreshConnections");
                var dirtyMethod = AccessTools.Method(typeof(PowerTransfer), "SetAllConnectionsDirty");

                if (_isBrokenField == null || refreshMethod == null || dirtyMethod == null)
                {
                    LuaCsLogger.LogError("[ItemOptimizer] RelayOpt: failed to resolve PowerTransfer members");
                    return;
                }

                // Fast delegates instead of MethodInfo.Invoke
                _refreshConnections = (Action<RelayComponent>)Delegate.CreateDelegate(
                    typeof(Action<RelayComponent>), refreshMethod);
                _setAllConnectionsDirty = (Action<RelayComponent>)Delegate.CreateDelegate(
                    typeof(Action<RelayComponent>), dirtyMethod);

                var original = AccessTools.Method(targetType, "Update",
                    new[] { typeof(float), typeof(Camera) });
                if (original == null)
                {
                    LuaCsLogger.LogError("[ItemOptimizer] RelayOpt: could not find RelayComponent.Update");
                    return;
                }

                var prefix = AccessTools.Method(typeof(RelayOpt), nameof(Prefix));
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            }

            private static bool Prefix(RelayComponent __instance, float deltaTime)
            {
                if (!OptimizerConfig.EnableRelayOpt) return true;

                // RefreshConnections() — protected on PowerTransfer, invoked via fast delegate
                _refreshConnections(__instance);

                // state_out: "1" or "0" — string literals, no alloc
                __instance.item.SendSignal(__instance.IsOn ? "1" : "0", "state_out");

                // power_value_out: cache ToString to avoid per-frame heap alloc
                var state = _cache.GetOrCreateValue(__instance);

                int powerVal = (int)Math.Round(-__instance.PowerLoad);
                if (powerVal != state.PrevPowerValue)
                {
                    state.PrevPowerValue = powerVal;
                    state.PowerValueStr = powerVal.ToString();
                }
                __instance.item.SendSignal(state.PowerValueStr, "power_value_out");

                int loadVal = (int)Math.Round(__instance.DisplayLoad);
                if (loadVal != state.PrevLoadValue)
                {
                    state.PrevLoadValue = loadVal;
                    state.LoadValueStr = loadVal.ToString();
                }
                __instance.item.SendSignal(state.LoadValueStr, "load_value_out");

                // isBroken check
                bool isBroken = (bool)_isBrokenField.GetValue(__instance);
                if (isBroken)
                {
                    _setAllConnectionsDirty(__instance);
                    _isBrokenField.SetValue(__instance, false);
                }

                // StatusEffects — public on ItemComponent
                __instance.ApplyStatusEffects(ActionType.OnActive, deltaTime);

                // Overload check: .Count > 0 instead of .Any()
                if (__instance.Voltage > __instance.OverloadVoltage
                    && __instance.CanBeOverloaded
                    && __instance.item.Repairables.Count > 0)
                {
                    __instance.item.Condition = 0.0f;
                }

                return false; // skip original
            }
        }

        // ════════════════════════════════════════════
        //  2. MotionSensor — replace targetCharacters.Any() with .Count > 0
        // ════════════════════════════════════════════
        internal static class MotionSensorOpt
        {
            private static FieldInfo _targetCharactersField;

            internal static void Register(Harmony harmony)
            {
                _targetCharactersField = AccessTools.Field(typeof(MotionSensor), "targetCharacters");
                if (_targetCharactersField == null)
                {
                    LuaCsLogger.LogError("[ItemOptimizer] MotionSensorOpt: could not find targetCharacters field");
                    return;
                }

                // Patch the private TriggersOn(Character, bool, bool, bool) overload
                var original = AccessTools.Method(typeof(MotionSensor), "TriggersOn",
                    new[] { typeof(Character), typeof(bool), typeof(bool), typeof(bool) });
                if (original == null)
                {
                    LuaCsLogger.LogError("[ItemOptimizer] MotionSensorOpt: could not find TriggersOn(4-param)");
                    return;
                }

                var prefix = AccessTools.Method(typeof(MotionSensorOpt), nameof(Prefix));
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            }

            /// <summary>
            /// Replaces the private TriggersOn overload.
            /// Identical logic but uses .Count > 0 instead of .Any() to avoid enumerator allocation.
            /// </summary>
            private static bool Prefix(
                MotionSensor __instance,
                Character character,
                bool triggerFromHumans,
                bool triggerFromPets,
                bool triggerFromMonsters,
                ref bool __result)
            {
                // Rewrite supersedes this patch
                if (OptimizerConfig.EnableMotionSensorRewrite) return true;
                if (!OptimizerConfig.EnableMotionSensorOpt)
                {
                    return true; // run original
                }

                // Replicate original logic exactly
                if (__instance.IgnoreDead && character.IsDead)
                {
                    __result = false;
                    return false;
                }

                if (character.IsPet)
                {
                    if (!triggerFromPets) { __result = false; return false; }
                }
                else if (character.IsHuman || CharacterParams.CompareGroup(character.Group, CharacterPrefab.HumanGroup))
                {
                    if (!triggerFromHumans) { __result = false; return false; }
                }
                else
                {
                    if (!triggerFromMonsters) { __result = false; return false; }
                }

                // Key optimization: .Count > 0 instead of .Any()
                var targetChars = (HashSet<Identifier>)_targetCharactersField.GetValue(__instance);
                if (targetChars.Count > 0)
                {
                    bool matchFound = false;
                    foreach (Identifier target in targetChars)
                    {
                        if (character.MatchesSpeciesNameOrGroup(target) || character.Params.HasTag(target))
                        {
                            matchFound = true;
                            break;
                        }
                    }
                    if (!matchFound) { __result = false; return false; }
                }

                __result = true;
                return false; // skip original
            }
        }

        // ════════════════════════════════════════════
        //  3. WaterDetector — cache Connection references to avoid string dictionary lookups
        // ════════════════════════════════════════════
        internal static class WaterDetectorOpt
        {
            private static FieldInfo _isInWaterField;
            private static FieldInfo _stateSwitchDelayField;
            private static FieldInfo _prevSentValueField;
            private static FieldInfo _waterPercentageSignalField;

            private static readonly ConditionalWeakTable<WaterDetector, CachedConnections> _connCache = new();

            private class CachedConnections
            {
                public Connection SignalOut;
                public Connection WaterPercent;
                public Connection HighPressure;
                public bool Resolved;
            }

            internal static void Register(Harmony harmony)
            {
                var targetType = typeof(WaterDetector);

                _isInWaterField = AccessTools.Field(targetType, "isInWater");
                _stateSwitchDelayField = AccessTools.Field(targetType, "stateSwitchDelay");
                _prevSentValueField = AccessTools.Field(targetType, "prevSentWaterPercentageValue");
                _waterPercentageSignalField = AccessTools.Field(targetType, "waterPercentageSignal");

                if (_isInWaterField == null || _stateSwitchDelayField == null ||
                    _prevSentValueField == null || _waterPercentageSignalField == null)
                {
                    LuaCsLogger.LogError("[ItemOptimizer] WaterDetectorOpt: failed to resolve WaterDetector fields");
                    return;
                }

                var original = AccessTools.Method(targetType, "Update",
                    new[] { typeof(float), typeof(Camera) });
                if (original == null)
                {
                    LuaCsLogger.LogError("[ItemOptimizer] WaterDetectorOpt: could not find WaterDetector.Update");
                    return;
                }

                var prefix = AccessTools.Method(typeof(WaterDetectorOpt), nameof(Prefix));
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            }

            private static void ResolveConnections(WaterDetector instance, CachedConnections cc)
            {
                var connections = instance.item.Connections;
                if (connections == null) { cc.Resolved = true; return; }
                foreach (var conn in connections)
                {
                    switch (conn.Name)
                    {
                        case "signal_out":  cc.SignalOut = conn; break;
                        case "water_%":     cc.WaterPercent = conn; break;
                        case "high_pressure": cc.HighPressure = conn; break;
                    }
                }
                cc.Resolved = true;
            }

            private static bool Prefix(WaterDetector __instance, float deltaTime)
            {
                // Rewrite supersedes this patch
                if (OptimizerConfig.EnableWaterDetectorRewrite) return true;
                if (!OptimizerConfig.EnableWaterDetectorOpt) return true;

                var item = __instance.item;

                // Read private fields
                bool isInWater = (bool)_isInWaterField.GetValue(__instance);
                float stateSwitchDelay = (float)_stateSwitchDelayField.GetValue(__instance);

                // Debounce
                if (stateSwitchDelay > 0.0f)
                {
                    stateSwitchDelay -= deltaTime;
                    _stateSwitchDelayField.SetValue(__instance, stateSwitchDelay);
                }
                else
                {
                    bool prevState = isInWater;
                    isInWater = false;

                    if (item.InWater)
                    {
                        isInWater = true;
                    }
                    else if (item.CurrentHull != null && WaterDetector.GetWaterPercentage(item.CurrentHull) > 0)
                    {
                        if (item.CurrentHull.Surface > item.Rect.Y - item.Rect.Height)
                        {
                            isInWater = true;
                        }
                    }

                    if (prevState != isInWater)
                    {
                        _stateSwitchDelayField.SetValue(__instance, 1.0f);
                    }
                    _isInWaterField.SetValue(__instance, isInWater);
                }

                // Resolve cached connections (once per instance)
                var cc = _connCache.GetOrCreateValue(__instance);
                if (!cc.Resolved)
                {
                    ResolveConnections(__instance, cc);
                }

                // signal_out — use cached Connection to skip dictionary lookup
                string signalOut = isInWater ? __instance.Output : __instance.FalseOutput;
                if (!string.IsNullOrEmpty(signalOut) && cc.SignalOut != null)
                {
                    var sig = new Signal(signalOut, source: item);
                    item.SendSignal(sig, cc.SignalOut);
                }

                // water_% — use cached Connection
                if (item.CurrentHull != null && cc.WaterPercent != null)
                {
                    int waterPercentage = WaterDetector.GetWaterPercentage(item.CurrentHull);
                    int prevSent = (int)_prevSentValueField.GetValue(__instance);
                    string wpSignal = (string)_waterPercentageSignalField.GetValue(__instance);

                    if (prevSent != waterPercentage || wpSignal == null)
                    {
                        prevSent = waterPercentage;
                        wpSignal = prevSent.ToString();
                        _prevSentValueField.SetValue(__instance, prevSent);
                        _waterPercentageSignalField.SetValue(__instance, wpSignal);
                    }

                    var sig2 = new Signal(wpSignal, source: item);
                    item.SendSignal(sig2, cc.WaterPercent);
                }

                // high_pressure — use cached Connection
                if (cc.HighPressure != null)
                {
                    string highPressureOut = (item.CurrentHull == null || item.CurrentHull.LethalPressure > 5.0f) ? "1" : "0";
                    var sig3 = new Signal(highPressureOut, source: item);
                    item.SendSignal(sig3, cc.HighPressure);
                }

                return false; // skip original
            }
        }
    }
}
