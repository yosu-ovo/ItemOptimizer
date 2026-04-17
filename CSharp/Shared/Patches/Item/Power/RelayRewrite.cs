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
    /// Complete replacement for RelayComponent.Update() via Harmony Prefix.
    /// Supersedes the Client-only RelayOpt in SignalOptPatches.
    ///
    /// Key optimizations vs vanilla:
    /// 1. FieldRefAccess for isBroken — no FieldInfo boxing
    /// 2. Cached Connection references — skip 3x per-frame string dictionary lookups
    /// 3. Cached ToString for power_value_out / load_value_out
    /// 4. .Count > 0 instead of .Any() — no LINQ enumerator alloc
    /// 5. Full UpdateOvervoltage logic (matches vanilla exactly)
    /// 6. Flat struct array — no ConditionalWeakTable / GC pressure
    /// </summary>
    static class RelayRewrite
    {
        // ── FieldRef accessors ──
        private static readonly AccessTools.FieldRef<PowerTransfer, bool> Ref_isBroken =
            AccessTools.FieldRefAccess<PowerTransfer, bool>("isBroken");
        private static readonly AccessTools.FieldRef<PowerTransfer, float> Ref_overloadCooldownTimer =
            AccessTools.FieldRefAccess<PowerTransfer, float>("overloadCooldownTimer");

        // ── Delegates for protected methods ──
        private static Action<RelayComponent> _refreshConnections;
        private static Action<RelayComponent> _setAllConnectionsDirty;

        // ── FieldRef for Item.hasStatusEffectsOfType (skip no-op ApplyStatusEffects) ──
        private static readonly AccessTools.FieldRef<Item, bool[]> Ref_hasStatusEffects =
            AccessTools.FieldRefAccess<Item, bool[]>("hasStatusEffectsOfType");

        // ── Cached connections per instance (flat array indexed by item.ID) ──
        private struct ConnCache
        {
            public Connection StateOut;
            public Connection PowerValueOut;
            public Connection LoadValueOut;
            public bool HasOnActiveEffects;
            public bool Resolved;
        }
        private static readonly ConnCache[] CachedConns = new ConnCache[65536];

        // ── Per-instance cached ToString state ──
        private struct SignalCache
        {
            public int PrevPowerValue;
            public string PowerValueStr;
            public int PrevLoadValue;
            public string LoadValueStr;
            public bool Initialized;
        }
        private static readonly SignalCache[] CachedSignals = new SignalCache[65536];

        private static MethodInfo _originalMethod;
        private static HarmonyMethod _prefixMethod;
        internal static bool IsRegistered { get; private set; }

        internal static void Register(Harmony harmony)
        {
            var refreshMethod = AccessTools.Method(typeof(PowerTransfer), "RefreshConnections");
            var dirtyMethod = AccessTools.Method(typeof(PowerTransfer), "SetAllConnectionsDirty");

            if (refreshMethod == null || dirtyMethod == null)
            {
                LuaCsLogger.LogError("[ItemOptimizer] RelayRewrite: failed to resolve PowerTransfer protected methods");
                return;
            }

            _refreshConnections = (Action<RelayComponent>)Delegate.CreateDelegate(
                typeof(Action<RelayComponent>), refreshMethod);
            _setAllConnectionsDirty = (Action<RelayComponent>)Delegate.CreateDelegate(
                typeof(Action<RelayComponent>), dirtyMethod);

            _originalMethod = AccessTools.Method(typeof(RelayComponent), nameof(RelayComponent.Update));
            if (_originalMethod == null)
            {
                LuaCsLogger.LogError("[ItemOptimizer] RelayRewrite: could not find RelayComponent.Update");
                return;
            }

            _prefixMethod = new HarmonyMethod(AccessTools.Method(typeof(RelayRewrite), nameof(Prefix)));
            harmony.Patch(_originalMethod, prefix: _prefixMethod);
            IsRegistered = true;
            LuaCsLogger.Log("[ItemOptimizer] RelayRewrite registered");
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
            Array.Clear(CachedSignals, 0, CachedSignals.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref ConnCache ResolveConnections(RelayComponent rc)
        {
            int id = rc.item.ID;
            ref var cc = ref CachedConns[id];
            if (cc.Resolved) return ref cc;

            var connections = rc.item.Connections;
            if (connections != null)
            {
                foreach (var conn in connections)
                {
                    switch (conn.Name)
                    {
                        case "state_out": cc.StateOut = conn; break;
                        case "power_value_out": cc.PowerValueOut = conn; break;
                        case "load_value_out": cc.LoadValueOut = conn; break;
                    }
                }
            }
            cc.Resolved = true;
            cc.HasOnActiveEffects = Ref_hasStatusEffects != null
                && Ref_hasStatusEffects(rc.item)[(int)ActionType.OnActive];
            return ref cc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SendViaConn(Item item, string signal, Connection conn, string fallbackName)
        {
            if (conn != null)
            {
                if (!conn.IsConnectedToSomething()) return;
                item.SendSignal(new Signal(signal, source: item), conn);
            }
            else
            {
                item.SendSignal(signal, fallbackName);
            }
        }

        public static bool Prefix(RelayComponent __instance, float deltaTime)
        {
            if (!OptimizerConfig.EnableRelayRewrite) return true;

            var item = __instance.item;
            int id = item.ID;

            // RefreshConnections — protected, called via fast delegate
            _refreshConnections(__instance);

            ref var cc = ref ResolveConnections(__instance);

            // 1. state_out: string literal, no alloc
            SendViaConn(item, __instance.IsOn ? "1" : "0", cc.StateOut, "state_out");

            // 2. power_value_out: cached ToString
            ref var sc = ref CachedSignals[id];
            int powerVal = (int)Math.Round(-__instance.PowerLoad);
            if (!sc.Initialized || powerVal != sc.PrevPowerValue)
            {
                sc.PrevPowerValue = powerVal;
                sc.PowerValueStr = powerVal.ToString();
            }
            SendViaConn(item, sc.PowerValueStr, cc.PowerValueOut, "power_value_out");

            // 3. load_value_out: cached ToString
            int loadVal = (int)Math.Round(__instance.DisplayLoad);
            if (!sc.Initialized || loadVal != sc.PrevLoadValue)
            {
                sc.PrevLoadValue = loadVal;
                sc.LoadValueStr = loadVal.ToString();
                sc.Initialized = true;
            }
            SendViaConn(item, sc.LoadValueStr, cc.LoadValueOut, "load_value_out");

            // isBroken check via FieldRef (zero-boxing)
            ref bool isBroken = ref Ref_isBroken(__instance);
            if (isBroken)
            {
                _setAllConnectionsDirty(__instance);
                isBroken = false;
            }

            // StatusEffects — skip if no OnActive effects defined (most relays)
            if (cc.HasOnActiveEffects)
                __instance.ApplyStatusEffects(ActionType.OnActive, deltaTime);

            // Full UpdateOvervoltage — matches vanilla exactly
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
                            cooldown = 5.0f; // OverloadCooldown
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
