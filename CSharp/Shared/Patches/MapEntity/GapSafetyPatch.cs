using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Barotrauma;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Thread-safety patches for Gap updates running inside Parallel.Invoke.
    /// Fixes two issues:
    ///   1. Gap.checkedHulls is a static HashSet shared across threads — replaced with ThreadLocal.
    ///   2. Gap.RefreshOutsideCollider modifies physics bodies — deferred to main thread.
    /// </summary>
    static class GapSafetyPatch
    {
        // ── ThreadLocal replacement for the static checkedHulls HashSet ──
        private static readonly ThreadLocal<HashSet<Hull>> _localCheckedHulls =
            new(() => new HashSet<Hull>());

        // ── Deferred physics actions from parallel Gap updates ──
        internal static readonly ConcurrentQueue<Action> DeferredActions = new();

        private static bool _registered;

        internal static void RegisterPatches(Harmony harmony)
        {
            if (_registered) return;

            // Patch SimulateWaterFlowFromOutsideToConnectedHulls to use thread-local checkedHulls
            var simWaterFlow = AccessTools.Method(typeof(Gap),
                "SimulateWaterFlowFromOutsideToConnectedHulls",
                new[] { typeof(Hull), typeof(float), typeof(float) });
            if (simWaterFlow != null)
            {
                harmony.Patch(simWaterFlow,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GapSafetyPatch),
                        nameof(SimWaterFlowPrefix))));
            }

            // Patch RefreshOutsideCollider to defer physics body changes when off main thread
            var refreshCollider = AccessTools.Method(typeof(Gap), "RefreshOutsideCollider");
            if (refreshCollider != null)
            {
                harmony.Patch(refreshCollider,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GapSafetyPatch),
                        nameof(RefreshOutsideColliderPrefix))));
            }

            _registered = true;
            LuaCsLogger.Log("[ItemOptimizer] GapSafetyPatch: registered (ThreadLocal checkedHulls + deferred collider)");
        }

        /// <summary>Drain deferred actions on the main thread after Parallel.Invoke completes.</summary>
        internal static void DrainDeferred()
        {
            while (DeferredActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e)
                {
                    SafeLogger.HandleException(e);
                }
            }
        }

        // ────────────────────────────────────────────────────────────
        //  Prefix: SimulateWaterFlowFromOutsideToConnectedHulls
        //  Replace usage of static checkedHulls with thread-local version
        // ────────────────────────────────────────────────────────────

        // We skip the original and reimplement it using the thread-local HashSet.
        // The original method is small: Clear + Add + foreach connected gaps → recursive call.
        public static bool SimWaterFlowPrefix(Gap __instance, Hull hull, float maxFlow, float deltaTime)
        {
            var checkedHulls = _localCheckedHulls.Value;
            checkedHulls.Clear();
            checkedHulls.Add(hull);

            foreach (var connectedGap in hull.ConnectedGaps)
            {
                if (connectedGap == __instance || !connectedGap.IsRoomToRoom || connectedGap.open <= 0.0f)
                    continue;
                var otherHull = connectedGap.GetOtherLinkedHull(hull);
                if (otherHull == null) continue;

                // Call the recursive static method via compiled delegate (no boxing/alloc)
                if (_recursiveDelegate == null)
                {
                    _recursiveMethod ??= AccessTools.Method(typeof(Gap),
                        "SimulateWaterFlowFromOutsideToConnectedHullsRecursive");
                    if (_recursiveMethod != null)
                        _recursiveDelegate = (Action<Hull, Gap, HashSet<Hull>, Hull, float, float>)
                            Delegate.CreateDelegate(typeof(Action<Hull, Gap, HashSet<Hull>, Hull, float, float>), _recursiveMethod);
                }
                _recursiveDelegate?.Invoke(otherHull, connectedGap, checkedHulls, hull, maxFlow, deltaTime);
            }

            return false; // skip original
        }

        private static MethodInfo _recursiveMethod;
        private static Action<Hull, Gap, HashSet<Hull>, Hull, float, float> _recursiveDelegate;

        // ────────────────────────────────────────────────────────────
        //  Prefix: RefreshOutsideCollider
        //  When called from a parallel thread, defer the entire call to main thread.
        //  It's only called when IsRoomToRoom state changes — very infrequent.
        // ────────────────────────────────────────────────────────────

        public static bool RefreshOutsideColliderPrefix(Gap __instance, ref bool __result)
        {
            if (Environment.CurrentManagedThreadId == UpdateAllTakeover.MainThreadId)
                return true; // main thread: run original

            // On worker thread: defer to main thread
            var gap = __instance;
            DeferredActions.Enqueue(() => gap.RefreshOutsideCollider());
            __result = false;
            return false; // skip original on worker thread
        }
    }
}
