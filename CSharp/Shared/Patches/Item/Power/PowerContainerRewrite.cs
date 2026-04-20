using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Complete replacement for PowerContainer.Update() via Harmony Prefix.
    /// Targets Battery, Supercapacitor, GuardianPod, etc.
    ///
    /// Key optimizations vs vanilla:
    /// 1. Cached ToString for all 5 output signals — vanilla allocates 5 strings × N instances per frame
    /// 2. Cached Connection references — skip 5x per-frame string dictionary lookups
    /// 3. Cached powerOut — avoid powerOuts.FirstOrDefault() LINQ alloc
    /// 4. FieldRefAccess for private fields — no reflection boxing
    /// </summary>
    static class PowerContainerRewrite
    {
        // ── FieldRef accessors ──
        private static readonly AccessTools.FieldRef<PowerContainer, float> Ref_adjustedCapacity =
            AccessTools.FieldRefAccess<PowerContainer, float>("adjustedCapacity");
        private static readonly AccessTools.FieldRef<PowerContainer, float> Ref_charge =
            AccessTools.FieldRefAccess<PowerContainer, float>("charge");
        private static readonly AccessTools.FieldRef<PowerContainer, float> Ref_maxRechargeSpeed =
            AccessTools.FieldRefAccess<PowerContainer, float>("maxRechargeSpeed");
        private static readonly AccessTools.FieldRef<PowerContainer, bool> Ref_isRunning =
            AccessTools.FieldRefAccess<PowerContainer, bool>("isRunning");

        // powerOuts from Powered base — to avoid FirstOrDefault LINQ
        private static readonly AccessTools.FieldRef<Powered, List<Connection>> Ref_powerOuts =
            AccessTools.FieldRefAccess<Powered, List<Connection>>("powerOuts");

        // ── Cached connections per instance ──
        private struct ConnCache
        {
            public Connection PowerValueOut;
            public Connection LoadValueOut;
            public Connection Charge;
            public Connection ChargePct;
            public Connection ChargeRate;
            public Connection PowerOut; // the actual power output connection
            public bool Resolved;
        }
        private static readonly ConnCache[] CachedConns = new ConnCache[65536];

        // ── Per-instance cached ToString values ──
        private struct SignalCache
        {
            public int PrevPower;
            public string PowerStr;
            public int PrevLoad;
            public string LoadStr;
            public int PrevCharge;
            public string ChargeStr;
            public int PrevChargePct;
            public string ChargePctStr;
            public int PrevChargeRate;
            public string ChargeRateStr;
            public bool Initialized;
        }
        private static readonly SignalCache[] CachedSignals = new SignalCache[65536];

        internal static bool IsRegistered => true; // dispatched via ComponentDispatchTranspiler

        internal static void Reset()
        {
            Array.Clear(CachedConns, 0, CachedConns.Length);
            Array.Clear(CachedSignals, 0, CachedSignals.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref ConnCache ResolveConnections(PowerContainer pc)
        {
            int id = pc.item.ID;
            ref var cc = ref CachedConns[id];
            if (cc.Resolved) return ref cc;

            var connections = pc.item.Connections;
            if (connections != null)
            {
                foreach (var conn in connections)
                {
                    switch (conn.Name)
                    {
                        case "power_value_out": cc.PowerValueOut = conn; break;
                        case "load_value_out": cc.LoadValueOut = conn; break;
                        case "charge": cc.Charge = conn; break;
                        case "charge_%": cc.ChargePct = conn; break;
                        case "charge_rate": cc.ChargeRate = conn; break;
                    }
                }
            }

            // Cache powerOut to avoid FirstOrDefault LINQ each frame
            var outs = Ref_powerOuts(pc);
            cc.PowerOut = outs.Count > 0 ? outs[0] : null;

            cc.Resolved = true;
            return ref cc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CacheSend(Item item, ref int prev, ref string str, int newVal,
            Connection conn, string fallbackName, ref bool initialized)
        {
            if (!initialized || prev != newVal)
            {
                prev = newVal;
                str = newVal.ToString();
            }
            if (conn != null)
                item.SendSignal(new Signal(str, source: item), conn);
            else
                item.SendSignal(str, fallbackName);
        }

        internal static void Execute(PowerContainer __instance, float deltaTime)
        {
            if (!OptimizerConfig.EnablePowerContainerRewrite)
            {
                __instance.Update(deltaTime, null);
                return;
            }

            var item = __instance.item;
            if (item.Connections == null)
            {
                __instance.IsActive = false;
                return;
            }

            int id = item.ID;

            // adjustedCapacity = GetCapacity() — calls StatManager, must keep
            ref float adjustedCapacity = ref Ref_adjustedCapacity(__instance);
            adjustedCapacity = __instance.GetCapacity();
            Ref_isRunning(__instance) = true;

            float charge = Ref_charge(__instance);
            float chargeRatio = charge / adjustedCapacity;

            if (chargeRatio > 0.0f)
            {
                __instance.ApplyStatusEffects(ActionType.OnActive, deltaTime);
            }

            // Load reading — cached powerOut avoids FirstOrDefault LINQ
            ref var cc = ref ResolveConnections(__instance);
            float loadReading = 0;
            if (cc.PowerOut != null && cc.PowerOut.Grid != null)
            {
                loadReading = cc.PowerOut.Grid.Load;
            }

            // 5 signals with cached ToString — only regenerate string on value change
            ref var sc = ref CachedSignals[id];
            float maxRS = Ref_maxRechargeSpeed(__instance);

            int vPower = (int)Math.Round(__instance.CurrPowerOutput);
            CacheSend(item, ref sc.PrevPower, ref sc.PowerStr, vPower,
                cc.PowerValueOut, "power_value_out", ref sc.Initialized);

            int vLoad = (int)Math.Round(loadReading);
            CacheSend(item, ref sc.PrevLoad, ref sc.LoadStr, vLoad,
                cc.LoadValueOut, "load_value_out", ref sc.Initialized);

            int vCharge = (int)Math.Round(charge);
            CacheSend(item, ref sc.PrevCharge, ref sc.ChargeStr, vCharge,
                cc.Charge, "charge", ref sc.Initialized);

            int vChargePct = (int)Math.Round(charge / adjustedCapacity * 100);
            CacheSend(item, ref sc.PrevChargePct, ref sc.ChargePctStr, vChargePct,
                cc.ChargePct, "charge_%", ref sc.Initialized);

            int vChargeRate = maxRS > 0 ? (int)Math.Round(__instance.RechargeSpeed / maxRS * 100) : 0;
            CacheSend(item, ref sc.PrevChargeRate, ref sc.ChargeRateStr, vChargeRate,
                cc.ChargeRate, "charge_rate", ref sc.Initialized);

            sc.Initialized = true;

            return;
        }
    }
}
