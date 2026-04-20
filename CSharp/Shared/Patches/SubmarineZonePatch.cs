using System.Collections.Concurrent;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using ItemOptimizerMod.World;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Harmony patches on Submarine ctor/Remove to detect late-spawned submarines.
    /// Events are queued and drained at a safe point in the tick cycle (before
    /// parallel phase) by NativeRuntimeBridge.DrainSubmarineEvents().
    /// </summary>
    static class SubmarineZonePatch
    {
        private static bool _registered;

        // Deferred queues — postfix/prefix enqueue, Tick drains on main thread
        internal static readonly ConcurrentQueue<Submarine> PendingCreated = new();
        internal static readonly ConcurrentQueue<Submarine> PendingRemoved = new();

        private static ConstructorInfo _ctorOriginal;
        private static MethodInfo _removeOriginal;

        internal static void Register(Harmony harmony)
        {
            if (_registered) return;

            // Submarine has a single public constructor
            _ctorOriginal = typeof(Submarine).GetConstructors()[0];
            if (_ctorOriginal != null)
            {
                harmony.Patch(_ctorOriginal,
                    postfix: new HarmonyMethod(typeof(SubmarineZonePatch), nameof(CtorPostfix)));
            }

            _removeOriginal = AccessTools.Method(typeof(Submarine), nameof(Submarine.Remove));
            if (_removeOriginal != null)
            {
                harmony.Patch(_removeOriginal,
                    prefix: new HarmonyMethod(typeof(SubmarineZonePatch), nameof(RemovePrefix)));
            }

            _registered = true;
            LuaCsLogger.Log("[ItemOptimizer] SubmarineZonePatch registered (ctor postfix + Remove prefix)");
        }

        internal static void Unregister(Harmony harmony)
        {
            if (!_registered) return;

            var ctorPostfix = AccessTools.Method(typeof(SubmarineZonePatch), nameof(CtorPostfix));
            var removePrefix = AccessTools.Method(typeof(SubmarineZonePatch), nameof(RemovePrefix));

            if (_ctorOriginal != null)
                harmony.Unpatch(_ctorOriginal, ctorPostfix);
            if (_removeOriginal != null)
                harmony.Unpatch(_removeOriginal, removePrefix);

            // Drain any leftover events
            while (PendingCreated.TryDequeue(out _)) { }
            while (PendingRemoved.TryDequeue(out _)) { }

            _registered = false;
        }

        private static void CtorPostfix(Submarine __instance)
        {
            if (!NativeRuntimeBridge.IsEnabled) return;
            PendingCreated.Enqueue(__instance);
        }

        private static void RemovePrefix(Submarine __instance)
        {
            if (!NativeRuntimeBridge.IsEnabled) return;
            PendingRemoved.Enqueue(__instance);
        }
    }
}
