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
    /// Complete replacement for PowerTransfer.Update() via Harmony Prefix.
    /// Targets JunctionBox and other non-Relay PowerTransfer components.
    ///
    /// Key optimizations vs vanilla:
    /// 1. FieldRefAccess for isBroken, overloadCooldownTimer, extraLoad/extraLoadSetTime,
    ///    prevSentPowerValue/powerSignal/prevSentLoadValue/loadSignal — no boxing
    /// 2. Cached Connection references for power_value_out / load_value_out
    /// 3. .Count > 0 instead of .Any() in UpdateOvervoltage
    /// 4. Full vanilla UpdateOvervoltage logic (damage + fire)
    /// </summary>
    static class PowerTransferRewrite
    {
        // ── FieldRef accessors ──
        private static readonly AccessTools.FieldRef<PowerTransfer, bool> Ref_isBroken =
            AccessTools.FieldRefAccess<PowerTransfer, bool>("isBroken");
        private static readonly AccessTools.FieldRef<PowerTransfer, float> Ref_overloadCooldownTimer =
            AccessTools.FieldRefAccess<PowerTransfer, float>("overloadCooldownTimer");
        private static readonly AccessTools.FieldRef<PowerTransfer, float> Ref_extraLoad =
            AccessTools.FieldRefAccess<PowerTransfer, float>("extraLoad");
        private static readonly AccessTools.FieldRef<PowerTransfer, float> Ref_extraLoadSetTime =
            AccessTools.FieldRefAccess<PowerTransfer, float>("extraLoadSetTime");
        // SendSignals fields (vanilla already caches ToString, we just access via FieldRef)
        private static readonly AccessTools.FieldRef<PowerTransfer, int> Ref_prevSentPowerValue =
            AccessTools.FieldRefAccess<PowerTransfer, int>("prevSentPowerValue");
        private static readonly AccessTools.FieldRef<PowerTransfer, string> Ref_powerSignal =
            AccessTools.FieldRefAccess<PowerTransfer, string>("powerSignal");
        private static readonly AccessTools.FieldRef<PowerTransfer, int> Ref_prevSentLoadValue =
            AccessTools.FieldRefAccess<PowerTransfer, int>("prevSentLoadValue");
        private static readonly AccessTools.FieldRef<PowerTransfer, string> Ref_loadSignal =
            AccessTools.FieldRefAccess<PowerTransfer, string>("loadSignal");
        private static readonly AccessTools.FieldRef<PowerTransfer, float> Ref_powerLoad =
            AccessTools.FieldRefAccess<PowerTransfer, float>("powerLoad");

        // ── FieldRef for Item.hasStatusEffectsOfType (skip no-op ApplyStatusEffects) ──
        private static readonly AccessTools.FieldRef<Item, bool[]> Ref_hasStatusEffects =
            AccessTools.FieldRefAccess<Item, bool[]>("hasStatusEffectsOfType");

        // ── Delegate for protected RefreshConnections / SetAllConnectionsDirty ──
        private static Action<PowerTransfer> _refreshConnections;
        private static Action<PowerTransfer> _setAllConnectionsDirty;

        // ── powerOut accessor (avoid FirstOrDefault LINQ) ──
        private static readonly AccessTools.FieldRef<Powered, List<Connection>> Ref_powerOuts =
            AccessTools.FieldRefAccess<Powered, List<Connection>>("powerOuts");

        // ── Cached connections per instance ──
        private struct ConnCache
        {
            public Connection PowerValueOut;
            public Connection LoadValueOut;
            public bool HasOnActiveEffects;
            public bool Resolved;
        }
        private static readonly ConnCache[] CachedConns = new ConnCache[65536];

        private static MethodInfo _originalMethod;
        private static HarmonyMethod _prefixMethod;
        internal static bool IsRegistered { get; private set; }

        internal static void Register(Harmony harmony)
        {
            var refreshMethod = AccessTools.Method(typeof(PowerTransfer), "RefreshConnections");
            var dirtyMethod = AccessTools.Method(typeof(PowerTransfer), "SetAllConnectionsDirty");

            if (refreshMethod == null || dirtyMethod == null)
            {
                LuaCsLogger.LogError("[ItemOptimizer] PowerTransferRewrite: failed to resolve protected methods");
                return;
            }

            _refreshConnections = (Action<PowerTransfer>)Delegate.CreateDelegate(
                typeof(Action<PowerTransfer>), refreshMethod);
            _setAllConnectionsDirty = (Action<PowerTransfer>)Delegate.CreateDelegate(
                typeof(Action<PowerTransfer>), dirtyMethod);

            _originalMethod = AccessTools.Method(typeof(PowerTransfer), nameof(PowerTransfer.Update));
            if (_originalMethod == null)
            {
                LuaCsLogger.LogError("[ItemOptimizer] PowerTransferRewrite: could not find PowerTransfer.Update");
                return;
            }

            _prefixMethod = new HarmonyMethod(AccessTools.Method(typeof(PowerTransferRewrite), nameof(Prefix)));
            harmony.Patch(_originalMethod, prefix: _prefixMethod);
            IsRegistered = true;
            LuaCsLogger.Log("[ItemOptimizer] PowerTransferRewrite registered");
        }

        internal static void Unregister(Harmony harmony)
        {
            if (_originalMethod != null && _prefixMethod != null)
                harmony.Unpatch(_originalMethod, _prefixMethod.method);
            IsRegistered = false;
        }

        internal static void Reset()
        {
            Array.Clear(CachedConns, 0, CachedConns.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref ConnCache ResolveConnections(PowerTransfer pt)
        {
            int id = pt.item.ID;
            ref var cc = ref CachedConns[id];
            if (cc.Resolved) return ref cc;

            var connections = pt.item.Connections;
            if (connections != null)
            {
                foreach (var conn in connections)
                {
                    switch (conn.Name)
                    {
                        case "power_value_out": cc.PowerValueOut = conn; break;
                        case "load_value_out": cc.LoadValueOut = conn; break;
                    }
                }
            }
            cc.Resolved = true;
            cc.HasOnActiveEffects = Ref_hasStatusEffects != null
                && Ref_hasStatusEffects(pt.item)[(int)ActionType.OnActive];
            return ref cc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Connection GetPowerOut(PowerTransfer pt)
        {
            var outs = Ref_powerOuts(pt);
            return outs.Count > 0 ? outs[0] : null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SendViaConn(Item item, string signal, Connection conn, string fallbackName)
        {
            if (conn != null)
                item.SendSignal(new Signal(signal, source: item), conn);
            else
                item.SendSignal(signal, fallbackName);
        }

        public static bool Prefix(PowerTransfer __instance, float deltaTime)
        {
            if (!OptimizerConfig.EnablePowerTransferRewrite) return true;

            // RelayComponent has its own override and rewrite — don't intercept it here
            if (__instance is RelayComponent) return true;

            var item = __instance.item;

            // RefreshConnections
            _refreshConnections(__instance);

            // UpdateExtraLoad (inlined — trivial)
            ref float extraLoad = ref Ref_extraLoad(__instance);
            ref float extraLoadSetTime = ref Ref_extraLoadSetTime(__instance);
            if (Timing.TotalTime > extraLoadSetTime + 1.0)
            {
                if (extraLoad > 0)
                    extraLoad = Math.Max(extraLoad - 1000.0f * deltaTime, 0);
                else
                    extraLoad = Math.Min(extraLoad + 1000.0f * deltaTime, 0);
            }

            if (!__instance.CanTransfer) return false;

            // isBroken check
            ref bool isBroken = ref Ref_isBroken(__instance);
            if (isBroken)
            {
                _setAllConnectionsDirty(__instance);
                isBroken = false;
            }

            // SendSignals — using FieldRef to access vanilla's cached signal fields + cached Connection
            ref var cc = ref ResolveConnections(__instance);

            // StatusEffects — skip if no OnActive effects defined (most junction boxes)
            if (cc.HasOnActiveEffects)
                __instance.ApplyStatusEffects(ActionType.OnActive, deltaTime);

            float powerReadingOut = 0;
            float loadReadingOut = extraLoad;
            float powerLoad = Ref_powerLoad(__instance);
            if (powerLoad < 0)
            {
                powerReadingOut = -powerLoad;
                loadReadingOut = 0;
            }

            var pOut = GetPowerOut(__instance);
            if (pOut != null && pOut.Grid != null)
            {
                powerReadingOut = pOut.Grid.Power;
                loadReadingOut = pOut.Grid.Load;
            }

            ref int prevPower = ref Ref_prevSentPowerValue(__instance);
            ref string powerSignal = ref Ref_powerSignal(__instance);
            if (prevPower != (int)powerReadingOut || powerSignal == null)
            {
                prevPower = (int)Math.Round(powerReadingOut);
                powerSignal = prevPower.ToString();
            }

            ref int prevLoad = ref Ref_prevSentLoadValue(__instance);
            ref string loadSignal = ref Ref_loadSignal(__instance);
            if (prevLoad != (int)loadReadingOut || loadSignal == null)
            {
                prevLoad = (int)Math.Round(loadReadingOut);
                loadSignal = prevLoad.ToString();
            }

            SendViaConn(item, powerSignal, cc.PowerValueOut, "power_value_out");
            SendViaConn(item, loadSignal, cc.LoadValueOut, "load_value_out");

            // UpdateOvervoltage — .Count > 0 instead of .Any(), full vanilla logic
            if (item.Repairables.Count > 0 && __instance.CanBeOverloaded)
            {
                float maxOverVoltage = Math.Max(__instance.OverloadVoltage, 1.0f);
                bool overload = __instance.Voltage > maxOverVoltage
                    && GameMain.GameSession is not { RoundDuration: < 5 };
                __instance.Overload = overload;

                if (overload && GameMain.NetworkMember is not { IsClient: true })
                {
                    ref float cooldown = ref Ref_overloadCooldownTimer(__instance);
                    if (cooldown > 0.0f)
                    {
                        cooldown -= deltaTime;
                    }
                    else
                    {
                        float prevCondition = item.Condition;
                        if (Rand.Range(0.0f, 1.0f) < 0.01f)
                        {
                            float conditionFactor = MathHelper.Lerp(5.0f, 1.0f, item.Condition / item.MaxCondition);
                            item.Condition -= deltaTime * Rand.Range(10.0f, 500.0f) * conditionFactor;
                        }

                        if (item.Condition <= 0.0f && prevCondition > 0.0f)
                        {
                            cooldown = 5.0f;
#if CLIENT
                            SoundPlayer.PlaySound("zap", item.WorldPosition, hullGuess: item.CurrentHull);
                            Vector2 baseVel = Rand.Vector(300.0f);
                            for (int i = 0; i < 10; i++)
                            {
                                var particle = GameMain.ParticleManager.CreateParticle("spark", item.WorldPosition,
                                    baseVel + Rand.Vector(100.0f), 0.0f, item.CurrentHull);
                                if (particle != null) particle.Size *= Rand.Range(0.5f, 1.0f);
                            }
#endif
                            float currentIntensity = GameMain.GameSession?.EventManager != null
                                ? GameMain.GameSession.EventManager.CurrentIntensity : 0.5f;

                            if (__instance.FireProbability > 0.0f &&
                                Rand.Range(0.0f, 1.0f) < MathHelper.Lerp(
                                    __instance.FireProbability, __instance.FireProbability * 0.1f, currentIntensity))
                            {
                                new FireSource(item.WorldPosition);
                            }
                        }
                    }
                }
            }
            else
            {
                __instance.Overload = false;
            }

            return false;
        }
    }
}
