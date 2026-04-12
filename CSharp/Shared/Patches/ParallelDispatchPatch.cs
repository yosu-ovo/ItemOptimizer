using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    static class ParallelDispatchPatch
    {
        internal static bool Enabled;

        // ── Per-frame state ──
        internal static bool _dispatchActive;
        private static bool _workerStarted;
        private static float _cachedDt;
        private static Camera _cachedCam;
        private static Task _workerTask;

        private static readonly HashSet<Item> _safeSet = new(2048);
        private static readonly List<Item> _safeSnapshot = new(2048);

        // Items claimed by a prior higher-priority skip prefix (cold storage, throttle, etc.)
        // These must be excluded from worker dispatch to avoid double-processing.
        private static readonly HashSet<Item> _alreadySkipped = new(2048);

        // ── Thread identification ──
        internal static int MainThreadId;
        [ThreadStatic] internal static bool IsWorkerThread;

        // ── Per-thread timing (populated by workers) ──
        // Fixed-size arrays indexed by worker slot (0..WorkerCount-1)
        private static int _workerCount;
        private static readonly long[] WorkerTicks = new long[7];
        private static readonly int[] WorkerItemCounts = new int[7];
        internal static long MainThreadTicks;
        internal static int MainThreadItemCount;

        // ── One-time classification diagnostic ──
        private static bool _diagLogged;

        // ── Patch registration ──
        private static bool _patchesAttached;

        // ── Per-frame main-thread timing ──
        [ThreadStatic] private static long _itemStartTick;

        internal static void Register(Harmony harmony)
        {
            if (_patchesAttached) return;
            MainThreadId = Environment.CurrentManagedThreadId;

            WorkerCrashLog.Initialize();

            var updateAll = AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll));
            harmony.Patch(updateAll,
                prefix: new HarmonyMethod(typeof(ParallelDispatchPatch), nameof(UpdateAllPrefix))
                    { priority = Priority.Low },
                postfix: new HarmonyMethod(typeof(ParallelDispatchPatch), nameof(UpdateAllPostfix))
                    { priority = Priority.Low });

            var itemUpdate = AccessTools.Method(typeof(Item), nameof(Item.Update),
                new[] { typeof(float), typeof(Camera) });
            harmony.Patch(itemUpdate,
                prefix: new HarmonyMethod(typeof(ParallelDispatchPatch), nameof(ItemUpdatePrefix))
                    { priority = Priority.Low },
                postfix: new HarmonyMethod(typeof(ParallelDispatchPatch), nameof(ItemUpdatePostfix))
                    { priority = Priority.Low });

            _patchesAttached = true;
            // Do NOT set Enabled=true here — defer to OnLoadCompleted()
            // to avoid running parallel dispatch before all systems are initialized.
            Enabled = false;
            _diagLogged = false;

            WorkerCrashLog.WriteSessionHeader();
        }

        internal static void Unregister(Harmony harmony)
        {
            if (!_patchesAttached) return;
            Enabled = false;
            _dispatchActive = false;
            WorkerCrashLog.Reset();

            var updateAll = AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll));
            harmony.Unpatch(updateAll,
                AccessTools.Method(typeof(ParallelDispatchPatch), nameof(UpdateAllPrefix)));
            harmony.Unpatch(updateAll,
                AccessTools.Method(typeof(ParallelDispatchPatch), nameof(UpdateAllPostfix)));

            var itemUpdate = AccessTools.Method(typeof(Item), nameof(Item.Update),
                new[] { typeof(float), typeof(Camera) });
            harmony.Unpatch(itemUpdate,
                AccessTools.Method(typeof(ParallelDispatchPatch), nameof(ItemUpdatePrefix)));
            harmony.Unpatch(itemUpdate,
                AccessTools.Method(typeof(ParallelDispatchPatch), nameof(ItemUpdatePostfix)));

            _patchesAttached = false;
        }

        // ──────────────────────────────────────────────
        // MapEntity.UpdateAll Prefix
        // ──────────────────────────────────────────────
        public static void UpdateAllPrefix(float deltaTime, Camera cam)
        {
            if (!Enabled) return;

            // MapEntity.MapEntityUpdateInterval is public static
            int interval = MapEntity.MapEntityUpdateInterval;
            // Items only update on tick-aligned frames; _mapEntityUpdateTick is private
            // but we can detect it: if interval > 1, we check via a simple heuristic.
            // For simplicity, always attempt dispatch — if no items hit our prefix, no cost.

            _cachedDt = deltaTime * interval;
            _cachedCam = cam;
            _dispatchActive = true;
            _workerStarted = false;
            _workerTask = null;
            _safeSet.Clear();
            _safeSnapshot.Clear();
            _alreadySkipped.Clear();
            MainThreadTicks = 0;
            MainThreadItemCount = 0;
            _workerCount = Math.Max(1, Math.Min(OptimizerConfig.ParallelWorkerCount, 6));
            for (int i = 0; i < _workerCount; i++)
            {
                WorkerTicks[i] = 0;
                WorkerItemCounts[i] = 0;
            }

            // Classify all items
            foreach (Item item in Item.ItemList)
            {
                if (IsSafeForWorker(item))
                    _safeSet.Add(item);
            }

            // One-time diagnostic: log classification breakdown
            if (!_diagLogged && Item.ItemList.Count > 0)
            {
                _diagLogged = true;
                if (ThreadSafetyAnalyzer.IsScanComplete)
                {
                    LuaCsLogger.Log($"[ItemOptimizer] Parallel dispatch (pre-scan): " +
                        $"safe={ThreadSafetyAnalyzer.CountSafe}, conditional={ThreadSafetyAnalyzer.CountConditional}, " +
                        $"unsafe={ThreadSafetyAnalyzer.CountUnsafe}, vanilla={ThreadSafetyAnalyzer.CountVanilla}, " +
                        $"safeSet this frame={_safeSet.Count}, quarantined={WorkerCrashLog.QuarantineCount}");
                }
                else
                {
                    int totalItems = Item.ItemList.Count;
                    int vanilla = 0, modInactive = 0, modBody = 0, modHeld = 0;
                    int modWired = 0, modUnsafe = 0, modSafe = 0;
                    foreach (Item item in Item.ItemList)
                    {
                        var cp = item.Prefab?.ContentPackage;
                        if (cp == null || cp == ContentPackageManager.VanillaCorePackage) { vanilla++; continue; }
                        if (!item.IsActive || item.IsLayerHidden || item.IsInRemoveQueue) { modInactive++; continue; }
                        if (item.body != null && item.body.Enabled) { modBody++; continue; }
                        if (item.ParentInventory?.Owner is Character) { modHeld++; continue; }
                        bool wired = false;
                        var conns = item.Connections;
                        if (conns != null)
                        {
                            foreach (var c in conns)
                                if (c.Wires.Count > 0) { wired = true; break; }
                        }
                        if (wired) { modWired++; continue; }
                        bool unsafeCmp = false;
                        foreach (var ic in item.Components)
                            if (IsUnsafeComponentFallback(ic)) { unsafeCmp = true; break; }
                        if (unsafeCmp) { modUnsafe++; continue; }
                        modSafe++;
                    }
                    LuaCsLogger.Log($"[ItemOptimizer] Parallel classification (fallback, no scan): " +
                        $"total={totalItems}, vanilla={vanilla}, " +
                        $"mod(inactive={modInactive}, body={modBody}, held={modHeld}, " +
                        $"wired={modWired}, unsafeCmp={modUnsafe}, SAFE={modSafe})");
                }
            }
        }

        // ──────────────────────────────────────────────
        // Item.Update Prefix (priority Low = runs after default)
        // ──────────────────────────────────────────────
        public static bool ItemUpdatePrefix(Item __instance, bool __runOriginal)
        {
            if (!_dispatchActive) return true;

            // Workers calling Item.Update go through Harmony too — let them run the original
            if (IsWorkerThread) return true;

            // If a prior prefix (cold storage / throttle) already skipped, record it so the
            // worker snapshot excludes this item — prevents double-processing.
            if (!__runOriginal)
            {
                _alreadySkipped.Add(__instance);
                return true;
            }

            // First safe item triggers worker launch
            if (!_workerStarted && _safeSet.Count > 0)
            {
                _workerStarted = true;
                // Exclude items that were already skipped by higher-priority prefixes
                foreach (var item in _safeSet)
                {
                    if (!_alreadySkipped.Contains(item))
                        _safeSnapshot.Add(item);
                }
                float dt = _cachedDt;
                Camera cam = _cachedCam;
                var snapshot = new List<Item>(_safeSnapshot);
                _workerTask = Task.Run(() => RunWorkers(snapshot, dt, cam));
            }

            if (_safeSet.Contains(__instance) && !_alreadySkipped.Contains(__instance))
            {
                Stats.ParallelItems++;
                return false; // worker handles this item
            }

            // Unsafe item — main thread handles it, start timing
            Stats.MainThreadItems++;
            MainThreadItemCount++;
            _itemStartTick = Stopwatch.GetTimestamp();
            return true;
        }

        // ──────────────────────────────────────────────
        // Item.Update Postfix — accumulate main-thread time
        // ──────────────────────────────────────────────
        public static void ItemUpdatePostfix(Item __instance)
        {
            if (!_dispatchActive) return;
            if (IsWorkerThread) return; // workers handle their own timing
            // Only count items that actually ran on main thread (not in safeSet)
            if (_safeSet.Contains(__instance)) return;

            long elapsed = Stopwatch.GetTimestamp() - _itemStartTick;
            MainThreadTicks += elapsed;
        }

        // ──────────────────────────────────────────────
        // MapEntity.UpdateAll Postfix
        // ──────────────────────────────────────────────
        public static void UpdateAllPostfix()
        {
            if (!_dispatchActive) return;
            _dispatchActive = false;

            if (_workerStarted && _workerTask != null)
            {
                try
                {
                    _workerTask.Wait();
                }
                catch (AggregateException ae)
                {
                    foreach (var e in ae.Flatten().InnerExceptions)
                    {
                        DebugConsole.ThrowError(
                            $"[ItemOptimizer] Parallel worker error: {e.Message}", e);
                    }
                }

                // Flush deferred sound plays to main thread
                ThreadSafetyPatches.FlushDeferredSounds();

                // Flush crash log entries to disk (main thread)
                WorkerCrashLog.FlushToDisk();
            }

            // Record per-thread stats (fixed worker count)
            Stats.RecordParallelFrame(_workerCount, WorkerTicks, WorkerItemCounts,
                MainThreadTicks, MainThreadItemCount);
        }

        // ──────────────────────────────────────────────
        // Worker execution — fixed thread count, manual partitioning
        // ──────────────────────────────────────────────
        private static void RunWorkers(List<Item> items, float dt, Camera cam)
        {
            int threadCount = _workerCount;
            int totalItems = items.Count;
            if (totalItems == 0) return;

            // Split items into exactly threadCount chunks
            int chunkSize = (totalItems + threadCount - 1) / threadCount;
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int slotIndex = t;
                int start = t * chunkSize;
                int end = Math.Min(start + chunkSize, totalItems);
                if (start >= totalItems)
                {
                    // No items for this slot
                    WorkerTicks[slotIndex] = 0;
                    WorkerItemCounts[slotIndex] = 0;
                    tasks[t] = Task.CompletedTask;
                    continue;
                }

                tasks[t] = Task.Run(() =>
                {
                    IsWorkerThread = true;
                    long startTick = Stopwatch.GetTimestamp();
                    int count = 0;

                    for (int i = start; i < end; i++)
                    {
                        try
                        {
                            items[i].Update(dt, cam);
                            count++;
                        }
                        catch (Exception e)
                        {
                            WorkerCrashLog.RecordCrash(items[i], e, slotIndex);
                        }
                    }

                    long elapsed = Stopwatch.GetTimestamp() - startTick;
                    WorkerTicks[slotIndex] = elapsed;
                    WorkerItemCounts[slotIndex] = count;

                    IsWorkerThread = false;
                });
            }

            Task.WaitAll(tasks);
        }

        // ──────────────────────────────────────────────
        // Classification (dual-path: pre-scan preferred, fallback inline)
        // ──────────────────────────────────────────────
        private static bool IsSafeForWorker(Item item)
        {
            // Runtime state guards (always run)
            if (!item.IsActive || item.IsLayerHidden || item.IsInRemoveQueue) return false;

            // Vanilla items NEVER go to workers
            if (item.Prefab?.ContentPackage == null ||
                item.Prefab.ContentPackage == ContentPackageManager.VanillaCorePackage)
                return false;

            // ── Skip items that will be skipped by higher-priority prefixes ──
            // If cold storage skip is active and this item is in cold storage,
            // don't bother sending it to a worker — it'll just be skipped there too.
            if (OptimizerConfig.EnableColdStorageSkip && ColdStorageDetector.IsInColdStorage(item))
                return false;

            // Ground item throttle: these are throttled on main thread already.
            // We can't predict which frame they'll be throttled, so skip them entirely.
            if (OptimizerConfig.EnableGroundItemThrottle
                && item.ParentInventory == null
                && item.GetComponent<Holdable>() != null)
                return false;

            // Held by character → writes to character state
            if (item.ParentInventory?.Owner is Character) return false;

            // Quarantined: this item crashed on a worker before → main thread only
            if (WorkerCrashLog.IsQuarantined(item.Prefab.Identifier.Value)) return false;

            // ── Pre-scan path (preferred) ──
            if (ThreadSafetyAnalyzer.IsScanComplete)
            {
                var tier = ThreadSafetyAnalyzer.GetTier(item.Prefab.Identifier.Value);
                if (tier == ThreadSafetyTier.Unsafe) return false;
                if (tier == ThreadSafetyTier.Conditional)
                {
                    if (item.body != null && item.body.Enabled) return false;
                    var conns = item.Connections;
                    if (conns != null)
                        foreach (var c in conns)
                            if (c.Wires.Count > 0) return false;
                }
                return true;
            }

            // ── Fallback: legacy inline classification (no scan run) ──
            if (item.body != null && item.body.Enabled) return false;
            var connections = item.Connections;
            if (connections != null)
                foreach (var conn in connections)
                    if (conn.Wires.Count > 0) return false;
            foreach (var ic in item.Components)
                if (IsUnsafeComponentFallback(ic)) return false;
            return true;
        }

        // Legacy fallback component check (only used when no pre-scan)
        private static readonly HashSet<string> _unsafeTypeNames = new(StringComparer.Ordinal)
        {
            "StatusMonitor"
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsUnsafeComponentFallback(ItemComponent ic)
        {
            return ic is Door
                || ic is Pump
                || ic is Reactor
                || ic is PowerTransfer
                || ic is Turret
                || ic is Fabricator
                || ic is Deconstructor
                || ic is Steering
                || ic is Engine
                || ic is OxygenGenerator
                || ic is MiniMap
                || ic is Sonar
                || ic is DockingPort
                || ic is ElectricalDischarger
                || ic is Controller
                || ic is TriggerComponent
                || ic is Rope
                || ic is EntitySpawnerComponent
                || _unsafeTypeNames.Contains(ic.GetType().Name);
        }
    }
}
