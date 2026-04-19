using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;
using System.Reflection;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Complete replacement for WaterDetector.Update() via Harmony Prefix.
    /// Merges WaterDetectorPatch (frame-skip) + WaterDetectorOpt (cached Connections)
    /// into a single zero-alloc prefix.
    ///
    /// Key optimizations vs vanilla:
    /// 1. FieldRefAccess for all 4 private fields — no FieldInfo.GetValue/SetValue boxing
    /// 2. Cached Connection references — skip 3x per-frame string dictionary lookups
    /// 3. Single GetWaterPercentage call — vanilla calls it twice
    /// 4. Frame-skip with cached signal replay — maintains wiring continuity
    /// 5. Struct-based flat state array — no ConditionalWeakTable / GC pressure
    /// </summary>
    static class WaterDetectorRewrite
    {
        // ── FieldRef accessors (zero-reflection, zero-boxing) ──
        private static readonly AccessTools.FieldRef<WaterDetector, bool> Ref_isInWater =
            AccessTools.FieldRefAccess<WaterDetector, bool>("isInWater");
        private static readonly AccessTools.FieldRef<WaterDetector, float> Ref_stateSwitchDelay =
            AccessTools.FieldRefAccess<WaterDetector, float>("stateSwitchDelay");
        private static readonly AccessTools.FieldRef<WaterDetector, int> Ref_prevSentWaterPercentageValue =
            AccessTools.FieldRefAccess<WaterDetector, int>("prevSentWaterPercentageValue");
        private static readonly AccessTools.FieldRef<WaterDetector, string> Ref_waterPercentageSignal =
            AccessTools.FieldRefAccess<WaterDetector, string>("waterPercentageSignal");

        // ── Cached connections per instance (flat array indexed by item.ID) ──
        private struct ConnCache
        {
            public Connection SignalOut;
            public Connection WaterPercent;
            public Connection HighPressure;
            public bool Resolved;
        }
        private static readonly ConnCache[] CachedConns = new ConnCache[65536];

        // ── Per-instance replay state for frame-skip ──
        private struct ReplayState
        {
            public int FrameCounter;
            public string LastSignalOut;
            public string LastWaterPct;
            public string LastHighPressure;
        }
        private static readonly ReplayState[] States = new ReplayState[65536];

        private static MethodInfo _originalMethod;
        private static HarmonyMethod _prefixMethod;
        internal static bool IsRegistered { get; private set; }

        internal static void Register(Harmony harmony)
        {
            _originalMethod = AccessTools.Method(typeof(WaterDetector), nameof(WaterDetector.Update));
            if (_originalMethod == null)
            {
                LuaCsLogger.LogError("[ItemOptimizer] WaterDetectorRewrite: could not find WaterDetector.Update");
                return;
            }

            _prefixMethod = new HarmonyMethod(AccessTools.Method(typeof(WaterDetectorRewrite), nameof(Prefix)));
            harmony.Patch(_originalMethod, prefix: _prefixMethod);
            IsRegistered = true;
            LuaCsLogger.Log("[ItemOptimizer] WaterDetectorRewrite registered");
        }

        internal static void Unregister(Harmony harmony)
        {
            if (_originalMethod != null && _prefixMethod != null)
                harmony.Unpatch(_originalMethod, _prefixMethod.method);
            IsRegistered = false;
        }

        /// <summary>Reset cached state (called on round end / mod reload).</summary>
        internal static void Reset()
        {
            Array.Clear(CachedConns, 0, CachedConns.Length);
            Array.Clear(States, 0, States.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref ConnCache ResolveConnections(WaterDetector wd)
        {
            int id = wd.item.ID;
            ref var cc = ref CachedConns[id];
            if (cc.Resolved) return ref cc;

            var connections = wd.item.Connections;
            if (connections != null)
            {
                foreach (var conn in connections)
                {
                    switch (conn.Name)
                    {
                        case "signal_out":    cc.SignalOut = conn; break;
                        case "water_%":       cc.WaterPercent = conn; break;
                        case "high_pressure": cc.HighPressure = conn; break;
                    }
                }
            }
            cc.Resolved = true;
            return ref cc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SendViaConn(Item item, string signal, Connection conn)
        {
            if (conn != null)
                item.SendSignal(new Signal(signal, source: item), conn);
            else
                item.SendSignal(signal, "signal_out"); // fallback
        }

        /// <summary>
        /// Complete replacement for WaterDetector.Update().
        /// Returns false to skip the original method.
        /// </summary>
        public static bool Prefix(WaterDetector __instance, float deltaTime)
        {
            if (!OptimizerConfig.EnableWaterDetectorRewrite) return true;

            var item = __instance.item;
            int id = item.ID;
            ref var state = ref States[id];
            ref var cc = ref ResolveConnections(__instance);

            // ── Frame-skip throttle ──
            // First frame (FrameCounter == 0) always runs full computation
            // to initialize replay state before any skip frame can occur.
            bool isRealFrame = state.FrameCounter == 0
                || (state.FrameCounter % OptimizerConfig.WaterDetectorSkipFrames) == 0;
            state.FrameCounter++;

            if (isRealFrame)
            {
                // ── Full computation frame ──

                // Debounce logic (matches vanilla exactly)
                ref float stateSwitchDelay = ref Ref_stateSwitchDelay(__instance);
                ref bool isInWater = ref Ref_isInWater(__instance);

                if (stateSwitchDelay > 0.0f)
                {
                    stateSwitchDelay -= deltaTime;
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
                        stateSwitchDelay = 1.0f; // StateSwitchInterval
                    }
                }

                // Compute signal_out
                string signalOut = isInWater ? __instance.Output : __instance.FalseOutput;
                state.LastSignalOut = signalOut;

                // Compute water_%  (single call — vanilla calls GetWaterPercentage twice)
                if (item.CurrentHull != null)
                {
                    int waterPercentage = WaterDetector.GetWaterPercentage(item.CurrentHull);
                    ref int prevSent = ref Ref_prevSentWaterPercentageValue(__instance);
                    ref string wpSignal = ref Ref_waterPercentageSignal(__instance);

                    if (prevSent != waterPercentage || wpSignal == null)
                    {
                        prevSent = waterPercentage;
                        wpSignal = waterPercentage.ToString();
                    }
                    state.LastWaterPct = wpSignal;
                }
                else
                {
                    state.LastWaterPct = null;
                }

                // Compute high_pressure
                state.LastHighPressure = (item.CurrentHull == null || item.CurrentHull.LethalPressure > 5.0f) ? "1" : "0";
            }
            else
            {
                // ── Skip frame: still decrement debounce timer via FieldRef ──
                ref float stateSwitchDelay = ref Ref_stateSwitchDelay(__instance);
                if (stateSwitchDelay > 0.0f)
                    stateSwitchDelay -= deltaTime;

                Stats.WaterDetectorSkips++;
            }

            // ── Send signals every frame (using cached values + cached connections) ──
            // Vanilla: signal_out only sent if non-empty; water_% and high_pressure sent unconditionally.
            if (!string.IsNullOrEmpty(state.LastSignalOut))
            {
                if (cc.SignalOut != null)
                    item.SendSignal(new Signal(state.LastSignalOut, source: item), cc.SignalOut);
                else
                    item.SendSignal(state.LastSignalOut, "signal_out");
            }

            if (state.LastWaterPct != null)
            {
                if (cc.WaterPercent != null)
                    item.SendSignal(new Signal(state.LastWaterPct, source: item), cc.WaterPercent);
                else
                    item.SendSignal(state.LastWaterPct, "water_%");
            }

            // Vanilla sends high_pressure unconditionally every frame
            string hp = state.LastHighPressure ?? "0";
            if (cc.HighPressure != null)
                item.SendSignal(new Signal(hp, source: item), cc.HighPressure);
            else
                item.SendSignal(hp, "high_pressure");

            return false;
        }
    }
}
