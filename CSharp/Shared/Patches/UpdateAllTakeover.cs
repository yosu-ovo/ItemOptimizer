using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Complete takeover of MapEntity.UpdateAll via Harmony prefix (return false).
    /// Replaces ItemUpdatePatch (freeze/throttle) and ParallelDispatchPatch (worker dispatch)
    /// with direct item.Update() calls — zero per-item Harmony overhead.
    /// </summary>
    static class UpdateAllTakeover
    {
        // ── Public state (read by ThreadSafetyPatches, PerfProfiler, StatsOverlay) ──
        internal static bool Enabled;
        internal static bool DispatchActive;
        [ThreadStatic] internal static bool IsWorkerThread;

        // ── Reflection caches ──
        private static FieldInfo _tickField;
        private static MethodInfo _updateProjSpecific;
        private static MethodInfo _hullUpdateCheats;  // CLIENT-only, may be null
        private static MethodInfo _itemUpdateNetPos;   // Item.UpdateNetPosition(float) — partial void, private
        private static bool _reflectionOk;

        // ── Patch state ──
        private static bool _patchAttached;

        // ── Per-frame pre-computed flags (replaces ItemUpdatePatch.NewFrame) ──
        private static bool _hasColdStorage;
        private static bool _hasGroundItem;
        private static bool _hasRules;
        private static bool _hasModOpt;
        private static bool _parallelEnabled;
        private static bool _miscParallelEnabled;

        // ── Frame generation for active-use cache ──
        private static int _frameGeneration;

        // ── Item classification buffers (reused each frame) ──
        private static readonly List<Item> _mainItems = new(2048);
        private static readonly List<Item> _workerItems = new(2048);
        private static readonly object[] _netPosArgs = new object[1]; // reused for UpdateNetPosition reflection call

        // ── Freeze/throttle state (migrated from ItemUpdatePatch) ──
        private static readonly ConditionalWeakTable<Item, StrongBox<int>> ThrottleCounters = new();
        private static readonly ConditionalWeakTable<Item, StrongBox<int>> HoldableCache = new();
        private static readonly ConditionalWeakTable<Item, ActiveUseCache> ActiveUseCacheTable = new();

        private class ActiveUseCache
        {
            public int Generation;
            public bool NotInActiveUse;
        }

        // ── Parallel dispatch state (migrated from ParallelDispatchPatch) ──
        internal static int MainThreadId;
        private static int _workerCount;
        private static readonly long[] WorkerTicks = new long[7];
        private static readonly int[] WorkerItemCounts = new int[7];
        internal static long MainThreadTicks;
        internal static int MainThreadItemCount;
        private static bool _diagLogged;

        // ── Server-side callbacks (set by ItemOptimizerPlugin.Server, null on client) ──
        internal static Action OnPreUpdate;
        internal static Action<float> OnPostUpdate;

        // Legacy fallback component check (only used when no pre-scan)
        private static readonly HashSet<string> _unsafeTypeNames = new(StringComparer.Ordinal)
        {
            "StatusMonitor"
        };

        // ────────────────────────────────────────────────
        //  Registration
        // ────────────────────────────────────────────────

        internal static void Register(Harmony harmony)
        {
            if (_patchAttached) return;

            MainThreadId = Environment.CurrentManagedThreadId;

            // Reflection lookups
            _tickField = typeof(MapEntity).GetField("mapEntityUpdateTick",
                BindingFlags.NonPublic | BindingFlags.Static);
            _updateProjSpecific = AccessTools.Method(typeof(MapEntity), "UpdateAllProjSpecific",
                new[] { typeof(float) });
            _hullUpdateCheats = AccessTools.Method(typeof(Hull), "UpdateCheats",
                new[] { typeof(float), typeof(Camera) });
            _itemUpdateNetPos = AccessTools.Method(typeof(Item), "UpdateNetPosition",
                new[] { typeof(float) });

            _reflectionOk = _tickField != null;
            if (!_reflectionOk)
            {
                LuaCsLogger.Log("[ItemOptimizer] UpdateAllTakeover: FAILED to find mapEntityUpdateTick — fallback to vanilla.");
                return;
            }
            if (_updateProjSpecific == null)
                LuaCsLogger.Log("[ItemOptimizer] UpdateAllTakeover: WARNING — UpdateAllProjSpecific not found (server build?).");

            // Initialize worker crash log if parallel is configured
            if (OptimizerConfig.EnableParallelDispatch)
            {
                WorkerCrashLog.Initialize();
                WorkerCrashLog.WriteSessionHeader();
            }

            // Single prefix on MapEntity.UpdateAll — this is our ONLY entry point
            var updateAll = AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll));
            harmony.Patch(updateAll,
                prefix: new HarmonyMethod(typeof(UpdateAllTakeover), nameof(UpdateAllPrefix))
                    { priority = Priority.First });

            _patchAttached = true;
            Enabled = false;  // await OnLoadCompleted
            _diagLogged = false;

            LuaCsLogger.Log($"[ItemOptimizer] UpdateAllTakeover registered. " +
                $"tickField={_tickField != null}, projSpecific={_updateProjSpecific != null}, " +
                $"hullCheats={_hullUpdateCheats != null}, netPos={_itemUpdateNetPos != null}");
        }

        internal static void Unregister(Harmony harmony)
        {
            if (!_patchAttached) return;
            Enabled = false;
            DispatchActive = false;
            OnPreUpdate = null;
            OnPostUpdate = null;
            WorkerCrashLog.Reset();

            var updateAll = AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll));
            harmony.Unpatch(updateAll,
                AccessTools.Method(typeof(UpdateAllTakeover), nameof(UpdateAllPrefix)));

            _patchAttached = false;
        }

        // ────────────────────────────────────────────────
        //  Frame flag refresh (replaces ItemUpdatePatch.NewFrame)
        // ────────────────────────────────────────────────

        internal static void RefreshFrameFlags()
        {
            _frameGeneration++;
            _hasColdStorage = OptimizerConfig.EnableColdStorageSkip;
            _hasGroundItem = OptimizerConfig.EnableGroundItemThrottle;
            _hasRules = OptimizerConfig.RuleLookup.Count > 0;
            _hasModOpt = OptimizerConfig.ModOptLookup.Count > 0;
            _parallelEnabled = OptimizerConfig.EnableParallelDispatch;
            _miscParallelEnabled = OptimizerConfig.EnableMiscParallel;
        }

        // ════════════════════════════════════════════════
        //  THE PREFIX — replaces entire MapEntity.UpdateAll
        // ════════════════════════════════════════════════

        public static bool UpdateAllPrefix(float deltaTime, Camera cam)
        {
            if (!Enabled || !_reflectionOk) return true;  // fallback to vanilla

            try
            {
                // ═══ Server-side: begin tick timing ═══
                OnPreUpdate?.Invoke();

                // ═══ Phase 0: Frame setup ═══
                int tick = (int)_tickField.GetValue(null) + 1;
                _tickField.SetValue(null, tick);

                int mapInterval = MapEntity.MapEntityUpdateInterval;
                bool isMapFrame = (tick % mapInterval == 0);
                float scaledDt = deltaTime * mapInterval;

                int powerInterval = MapEntity.PoweredUpdateInterval;
                bool isPowerFrame = (tick % powerInterval == 0);
                float poweredDt = deltaTime * powerInterval;

                RefreshFrameFlags();
                PerfProfiler.MapEntityUpdateAllPrefix();

                // ═══ Phase A: Misc (Hull / Structure / Gap / Power) ═══

                // Gap reset MUST complete for ALL gaps before any gap.Update
                foreach (Gap gap in Gap.GapList)
                    gap.ResetWaterFlowThisFrame();

                // Shuffle gaps on main thread (Rand.Int must be main-thread)
                var shuffledGaps = Gap.GapList.OrderBy(g => Rand.Int(int.MaxValue)).ToList();

                if (isMapFrame && _miscParallelEnabled)
                {
                    // Hulls run on main thread: Hull.UpdateProjSpecific (server) calls
                    // CreateEntityEvent which is NOT thread-safe. Moving Hull out of
                    // Parallel.Invoke prevents entity event list corruption that causes
                    // one-way client disconnects.
                    foreach (Hull hull in Hull.HullList)
                        hull.Update(scaledDt, cam);
                    _hullUpdateCheats?.Invoke(null, new object[] { scaledDt, cam });

                    // Structure/Gap/Power are safe for parallel — they never call CreateEntityEvent.
                    Parallel.Invoke(
                        // Branch 1: Structures (serial)
                        () =>
                        {
                            foreach (Structure structure in Structure.WallList)
                                structure.Update(scaledDt, cam);
                        },
                        // Branch 2: Gaps (serial, already shuffled, raw deltaTime)
                        () =>
                        {
                            foreach (Gap gap in shuffledGaps)
                                gap.Update(deltaTime, cam);
                        },
                        // Branch 3: Power
                        () =>
                        {
                            if (isPowerFrame)
                                Powered.UpdatePower(poweredDt);
                        }
                    );

                    // Drain deferred physics actions from Gap parallel execution
                    GapSafetyPatch.DrainDeferred();
                }
                else
                {
                    // Sequential path (original behavior)
                    if (isMapFrame)
                    {
                        foreach (Hull hull in Hull.HullList)
                            hull.Update(scaledDt, cam);
                        _hullUpdateCheats?.Invoke(null, new object[] { scaledDt, cam });

                        foreach (Structure structure in Structure.WallList)
                            structure.Update(scaledDt, cam);
                    }

                    foreach (Gap gap in shuffledGaps)
                        gap.Update(deltaTime, cam);

                    if (isPowerFrame)
                        Powered.UpdatePower(poweredDt);
                }

                // ═══ Phase B: Items ═══
                Item.UpdatePendingConditionUpdates(deltaTime);  // every frame, raw dt

                if (isMapFrame)
                    DispatchItemUpdates(scaledDt, cam);

                // ═══ Phase C: PriorityItems (LuaCs) — every frame, raw dt ═══
                var priorityItemsC = LuaCsSetup.Instance?.Game?.UpdatePriorityItems;
                if (priorityItemsC != null)
                {
                    foreach (var item in priorityItemsC)
                    {
                        if (item.Removed) continue;
                        item.Update(deltaTime, cam);
                    }
                }

                // ═══ Phase D: Tail ═══
                if (isMapFrame)
                {
                    _updateProjSpecific?.Invoke(null, new object[] { scaledDt });
                    Entity.Spawner?.Update();
                }

                // ═══ Phase E: Frame-end hooks ═══
                PerfProfiler.MapEntityUpdateAllPostfix();
                CharacterStaggerPatch.IncrementFrame();
                Stats.EndFrame();

                // ═══ Server-side: collect + broadcast metrics ═══
                OnPostUpdate?.Invoke(deltaTime);

                return false;  // skip original method entirely
            }
            catch (Exception ex)
            {
                // Auto-fallback: disable takeover, let vanilla run next frame
                LuaCsLogger.Log($"[ItemOptimizer] UpdateAllTakeover CRASHED — disabling. {ex.GetType().Name}: {ex.Message}");
                DebugConsole.ThrowError($"[ItemOptimizer] UpdateAllTakeover disabled due to error: {ex.Message}", ex);
                Enabled = false;
                return true;
            }
        }

        // ────────────────────────────────────────────────
        //  Item dispatch: freeze/throttle + classify + parallel
        // ────────────────────────────────────────────────

        private static void DispatchItemUpdates(float dt, Camera cam)
        {
            DispatchActive = true;

            // Pre-build HasStatusTag cache so parallel workers only do lock-free reads
            HasStatusTagCachePatch.PreBuildAll();

            _mainItems.Clear();
            _workerItems.Clear();
            MainThreadTicks = 0;
            MainThreadItemCount = 0;
            _workerCount = Math.Max(1, Math.Min(OptimizerConfig.ParallelWorkerCount, 6));
            for (int i = 0; i < _workerCount; i++)
            {
                WorkerTicks[i] = 0;
                WorkerItemCounts[i] = 0;
            }

            var priorityItems = LuaCsSetup.Instance?.Game?.UpdatePriorityItems;
            Item lastUpdatedItem = null;

            try
            {
                _netPosArgs[0] = dt;
                int skippedCount = 0;

                // ── Single-pass: skip / classify ──
                foreach (Item item in Item.ItemList)
                {
                    if (priorityItems != null && priorityItems.Contains(item)) continue;

                    if (ShouldSkipItem(item))
                    {
                        skippedCount++;
                        // Maintain network position sync even for skipped items
                        // (prevents positionBuffer backup on client, keeps server PositionUpdateInterval ticking)
                        if (_itemUpdateNetPos != null && item.body != null && item.body.Enabled)
                            _itemUpdateNetPos.Invoke(item, _netPosArgs);
                        continue;
                    }

                    if (_parallelEnabled && IsSafeForWorker(item))
                        _workerItems.Add(item);
                    else
                        _mainItems.Add(item);
                }

                // One-time classification diagnostic
                if (!_diagLogged && Item.ItemList.Count > 0)
                {
                    _diagLogged = true;
                    LuaCsLogger.Log($"[ItemOptimizer] Takeover dispatch: " +
                        $"total={Item.ItemList.Count}, main={_mainItems.Count}, " +
                        $"worker={_workerItems.Count}, parallel={_parallelEnabled}, " +
                        $"scan={( ThreadSafetyAnalyzer.IsScanComplete ? $"safe={ThreadSafetyAnalyzer.CountSafe},cond={ThreadSafetyAnalyzer.CountConditional},unsafe={ThreadSafetyAnalyzer.CountUnsafe}" : "none" )}");
                }

                // Server metrics: record skipped items this frame
                ServerMetrics.SkippedItems = skippedCount;

                // ── Launch workers ──
                Task workerTask = null;
                if (_workerItems.Count > 0)
                {
                    Stats.ParallelItems += _workerItems.Count;
                    var snapshot = new List<Item>(_workerItems);
                    workerTask = Task.Run(() => RunWorkers(snapshot, dt, cam));
                }

                // ── Main thread: update unsafe items ──
                Stats.MainThreadItems += _mainItems.Count;
                foreach (var item in _mainItems)
                {
                    lastUpdatedItem = item;
                    long startTick = Stopwatch.GetTimestamp();
                    item.Update(dt, cam);
                    MainThreadTicks += Stopwatch.GetTimestamp() - startTick;
                    MainThreadItemCount++;
                }

                // ── Wait for workers ──
                if (workerTask != null)
                {
                    try
                    {
                        workerTask.Wait();
                    }
                    catch (AggregateException ae)
                    {
                        foreach (var e in ae.Flatten().InnerExceptions)
                            DebugConsole.ThrowError($"[ItemOptimizer] Worker error: {e.Message}", e);
                    }
                    WorkerCrashLog.FlushToDisk();
                }

                Stats.RecordParallelFrame(_workerCount, WorkerTicks, WorkerItemCounts,
                    MainThreadTicks, MainThreadItemCount);
            }
            catch (InvalidOperationException e)
            {
                GameAnalyticsManager.AddErrorEventOnce(
                    "MapEntity.UpdateAll:ItemUpdateInvalidOperation",
                    GameAnalyticsManager.ErrorSeverity.Critical,
                    $"Error while updating item {lastUpdatedItem?.Name ?? "null"}: {e.Message}");
                throw new InvalidOperationException(
                    $"Error while updating item {lastUpdatedItem?.Name ?? "null"}", innerException: e);
            }
            finally
            {
                DispatchActive = false;
            }
        }

        // ────────────────────────────────────────────────
        //  Worker execution (migrated from ParallelDispatchPatch)
        // ────────────────────────────────────────────────

        private static void RunWorkers(List<Item> items, float dt, Camera cam)
        {
            int threadCount = _workerCount;
            int totalItems = items.Count;
            if (totalItems == 0) return;

            int chunkSize = (totalItems + threadCount - 1) / threadCount;
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int slotIndex = t;
                int start = t * chunkSize;
                int end = Math.Min(start + chunkSize, totalItems);
                if (start >= totalItems)
                {
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

                    WorkerTicks[slotIndex] = Stopwatch.GetTimestamp() - startTick;
                    WorkerItemCounts[slotIndex] = count;
                    IsWorkerThread = false;
                });
            }

            Task.WaitAll(tasks);
        }

        // ────────────────────────────────────────────────
        //  Freeze / Throttle (migrated from ItemUpdatePatch)
        // ────────────────────────────────────────────────

        /// <summary>Returns true if this item should be SKIPPED this frame.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldSkipItem(Item item)
        {
            // Strategy 1: Cold Storage Skip
            if (_hasColdStorage && ColdStorageDetector.IsInColdStorage(item))
            {
                Stats.ColdStorageSkips++;
                return true;
            }

            // Strategy 2: Ground Item Throttle
            // Only throttle truly idle ground items — skip items with wire connections,
            // as they are part of signal chains (relays, logic gates, sensors, etc.)
            // and throttling them breaks signal propagation causing door rubber-banding.
            if (_hasGroundItem
                && item.ParentInventory == null
                && IsHoldable(item)
                && !HasWireConnections(item))
            {
                var counter = ThrottleCounters.GetOrCreateValue(item);
                counter.Value++;
                if (counter.Value % OptimizerConfig.GroundItemSkipFrames != 0)
                {
                    Stats.GroundItemSkips++;
                    return true;
                }
            }

            // Fast exit: no rules and no mod optimization
            if (!_hasRules && !_hasModOpt) return false;

            string identifier = item.Prefab?.Identifier.Value;
            if (identifier == null) return false;

            // Strategy 5: Per-Item Rules
            if (_hasRules && OptimizerConfig.RuleLookup.TryGetValue(identifier, out var rule))
            {
                if (CheckRuleCondition(item, rule.Condition))
                {
                    if (rule.Action == ItemRuleAction.Skip)
                    {
                        Stats.ItemRuleSkips++;
                        return true;
                    }
                    if (rule.Action == ItemRuleAction.Throttle)
                    {
                        var counter = ThrottleCounters.GetOrCreateValue(item);
                        counter.Value++;
                        if (counter.Value % rule.SkipFrames != 0)
                        {
                            Stats.ItemRuleSkips++;
                            return true;
                        }
                    }
                }
                return false;  // rule matched — skip ModOpt for this item
            }

            // Strategy 6: Mod Optimization
            if (_hasModOpt && OptimizerConfig.ModOptLookup.TryGetValue(identifier, out var skipFrames))
            {
                if (IsModOptEligibleCached(item))
                {
                    var counter = ThrottleCounters.GetOrCreateValue(item);
                    counter.Value++;
                    if (counter.Value % skipFrames != 0)
                    {
                        Stats.ModOptSkips++;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsModOptEligibleCached(Item item)
        {
            var cache = ActiveUseCacheTable.GetOrCreateValue(item);
            if (cache.Generation == _frameGeneration)
                return cache.NotInActiveUse;
            cache.Generation = _frameGeneration;
            cache.NotInActiveUse = ColdStorageDetector.IsModOptEligible(item);
            return cache.NotInActiveUse;
        }

        private static bool CheckRuleCondition(Item item, string condition)
        {
            switch (condition)
            {
                case "coldStorage": return ColdStorageDetector.IsInColdStorage(item);
                case "notInActiveUse": return ColdStorageDetector.IsNotInActiveUse(item);
                case "always":
                default: return true;
            }
        }

        private static bool IsHoldable(Item item)
        {
            var cached = HoldableCache.GetOrCreateValue(item);
            if (cached.Value != 0) return cached.Value > 0;
            cached.Value = item.GetComponent<Holdable>() != null ? 1 : -1;
            return cached.Value > 0;
        }

        /// <summary>
        /// Cached check for whether an item has any wire connections.
        /// Items with wires are part of signal chains and must NOT be throttled.
        /// </summary>
        private static readonly ConditionalWeakTable<Item, StrongBox<int>> WireCache = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasWireConnections(Item item)
        {
            var cached = WireCache.GetOrCreateValue(item);
            if (cached.Value != 0) return cached.Value > 0;
            var conns = item.Connections;
            if (conns != null)
            {
                foreach (var c in conns)
                {
                    if (c.Wires.Count > 0)
                    {
                        cached.Value = 1;
                        return true;
                    }
                }
            }
            cached.Value = -1;
            return false;
        }

        // ────────────────────────────────────────────────
        //  Worker safety classification (migrated from ParallelDispatchPatch)
        // ────────────────────────────────────────────────

        private static bool IsSafeForWorker(Item item)
        {
            // Runtime state guards
            if (!item.IsActive || item.IsLayerHidden || item.IsInRemoveQueue) return false;

            // Held by character → writes to character state
            if (item.ParentInventory?.Owner is Character) return false;

            // Null prefab guard
            if (item.Prefab == null) return false;

            // Quarantined: this item crashed on a worker before
            if (WorkerCrashLog.IsQuarantined(item.Prefab.Identifier.Value)) return false;

            // Pre-scan path (preferred)
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

            // Fallback: legacy inline classification (no scan run)
            if (item.body != null && item.body.Enabled) return false;
            var connections = item.Connections;
            if (connections != null)
                foreach (var conn in connections)
                    if (conn.Wires.Count > 0) return false;
            foreach (var ic in item.Components)
                if (IsUnsafeComponentFallback(ic)) return false;
            return true;
        }

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
