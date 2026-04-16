using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using ItemOptimizerMod.Proxy;
using ItemOptimizerMod.SignalGraph;
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
        // ── Public state (read by PerfProfiler, StatsOverlay) ──
        internal static bool Enabled;
        internal static bool DispatchActive;
        [ThreadStatic] internal static bool IsWorkerThread;

        // ── Reflection caches ──
        private static FieldInfo _tickField;
        private static AccessTools.FieldRef<int> _tickFieldRef;
        private static MethodInfo _updateProjSpecific;
        private static MethodInfo _hullUpdateCheats;  // CLIENT-only, may be null
        private static Action<float> _updateProjSpecificDel;       // MapEntity.UpdateAllProjSpecific(float) — fast delegate
        private static Action<float, Camera> _hullUpdateCheatsDel; // Hull.UpdateCheats(float, Camera) — fast delegate
        private static Action<Item, float> _itemUpdateNetPos;   // Item.UpdateNetPosition(float) — fast delegate
        private static Action<Item> _itemApplyWaterForces;      // Item.ApplyWaterForces() — fast delegate
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
        private static bool _hasProxy;
        private static bool _hasSignalGraph;
        private static bool _hasWireSkip;

        // ── Frame generation for active-use cache ──
        private static int _frameGeneration;

        // ── Item classification buffers (reused each frame) ──
        private static readonly List<Item> _mainItems = new(2048);
        private static readonly List<Item> _workerItems = new(2048);

        // ── Gap shuffle buffer (reused each frame, Fisher-Yates) ──
        private static readonly List<Gap> _gapShuffleBuffer = new(512);

        // ── Tick conversion ──
        private static readonly double _ticksToMs = 1000.0 / Stopwatch.Frequency;

        // ── Freeze/throttle state — flat arrays indexed by item.ID (ushort 0–65535) ──
        // Replaces ConditionalWeakTable for O(1) direct-index access with zero locking/hashing.
        private static readonly int[] ThrottleCounters = new int[65536];
        private static readonly sbyte[] HoldableCache = new sbyte[65536];   // 0=unknown, 1=yes, -1=no
        private static readonly sbyte[] WireCache = new sbyte[65536];       // 0=unknown, 1=has wires, -1=no wires
        private static readonly sbyte[] IsWireCache = new sbyte[65536];    // 0=unknown, 1=pure wire item, -1=not wire
        // ActiveUseCache: generation in high 32 bits, result in low bit
        private static readonly int[] ActiveUseCacheGen = new int[65536];
        private static readonly bool[] ActiveUseCacheVal = new bool[65536];

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
            if (_tickField != null)
                _tickFieldRef = AccessTools.StaticFieldRefAccess<int>(_tickField);
            _updateProjSpecific = AccessTools.Method(typeof(MapEntity), "UpdateAllProjSpecific",
                new[] { typeof(float) });
            _hullUpdateCheats = AccessTools.Method(typeof(Hull), "UpdateCheats",
                new[] { typeof(float), typeof(Camera) });

            // Create fast delegates for static utility methods (avoids MethodInfo.Invoke + object[] alloc)
            if (_updateProjSpecific != null)
                _updateProjSpecificDel = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), _updateProjSpecific);
            if (_hullUpdateCheats != null)
                _hullUpdateCheatsDel = (Action<float, Camera>)Delegate.CreateDelegate(typeof(Action<float, Camera>), _hullUpdateCheats);

            // Create fast delegates for hot-path private methods (avoids MethodInfo.Invoke boxing/overhead)
            var netPosMethod = AccessTools.Method(typeof(Item), "UpdateNetPosition", new[] { typeof(float) });
            if (netPosMethod != null)
                _itemUpdateNetPos = (Action<Item, float>)Delegate.CreateDelegate(typeof(Action<Item, float>), netPosMethod);

            var waterForcesMethod = AccessTools.Method(typeof(Item), "ApplyWaterForces");
            if (waterForcesMethod != null)
                _itemApplyWaterForces = (Action<Item>)Delegate.CreateDelegate(typeof(Action<Item>), waterForcesMethod);

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
            ClearItemCaches();

            var updateAll = AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll));
            harmony.Unpatch(updateAll,
                AccessTools.Method(typeof(UpdateAllTakeover), nameof(UpdateAllPrefix)));

            _patchAttached = false;
        }

        /// <summary>Clear flat-array caches. Called on unregister / round transitions.</summary>
        internal static void ClearItemCaches()
        {
            Array.Clear(ThrottleCounters, 0, 65536);
            Array.Clear(HoldableCache, 0, 65536);
            Array.Clear(WireCache, 0, 65536);
            Array.Clear(ActiveUseCacheGen, 0, 65536);
            Array.Clear(ActiveUseCacheVal, 0, 65536);
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
            _hasProxy = ProxyRegistry.HasHandlers;
            _hasSignalGraph = OptimizerConfig.SignalGraphMode > 0 && (SignalGraphEvaluator.IsCompiled || SignalGraphEvaluator.IsDirty);
            _hasWireSkip = OptimizerConfig.EnableWireSkip;
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
                int tick = ++_tickFieldRef();

                int mapInterval = MapEntity.MapEntityUpdateInterval;
                bool isMapFrame = (tick % mapInterval == 0);
                float scaledDt = deltaTime * mapInterval;

                int powerInterval = MapEntity.PoweredUpdateInterval;
                bool isPowerFrame = (tick % powerInterval == 0);
                float poweredDt = deltaTime * powerInterval;

                RefreshFrameFlags();
                PerfProfiler.MapEntityUpdateAllPrefix();

                long totalDispatchStart = Stopwatch.GetTimestamp();

                // ═══ Phase A: Misc (Hull / Structure / Gap / Power) ═══
                long phaseAStart = Stopwatch.GetTimestamp();

                // Gap reset MUST complete for ALL gaps before any gap.Update
                foreach (Gap gap in Gap.GapList)
                    gap.ResetWaterFlowThisFrame();

                // Fisher-Yates shuffle via reused buffer (no LINQ, no allocation)
                _gapShuffleBuffer.Clear();
                foreach (Gap g in Gap.GapList) _gapShuffleBuffer.Add(g);
                for (int i = _gapShuffleBuffer.Count - 1; i > 0; i--)
                {
                    int j = Rand.Int(i + 1);
                    (_gapShuffleBuffer[i], _gapShuffleBuffer[j]) = (_gapShuffleBuffer[j], _gapShuffleBuffer[i]);
                }

                if (isMapFrame && _miscParallelEnabled)
                {
                    // Hulls run on main thread: Hull.UpdateProjSpecific (server) calls
                    // CreateEntityEvent which is NOT thread-safe. Moving Hull out of
                    // Parallel.Invoke prevents entity event list corruption that causes
                    // one-way client disconnects.
                    foreach (Hull hull in Hull.HullList)
                        hull.Update(scaledDt, cam);
                    _hullUpdateCheatsDel?.Invoke(scaledDt, cam);

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
                            foreach (Gap gap in _gapShuffleBuffer)
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
                        _hullUpdateCheatsDel?.Invoke(scaledDt, cam);

                        foreach (Structure structure in Structure.WallList)
                            structure.Update(scaledDt, cam);
                    }

                    foreach (Gap gap in _gapShuffleBuffer)
                        gap.Update(deltaTime, cam);

                    if (isPowerFrame)
                        Powered.UpdatePower(poweredDt);
                }

                // ═══ Phase Proxy: Proxy item batch compute + sync ═══
                Stats.PhaseAMs = (float)((Stopwatch.GetTimestamp() - phaseAStart) * _ticksToMs);
                long phaseProxyStart = Stopwatch.GetTimestamp();
                if (isMapFrame && ProxyRegistry.HasHandlers)
                {
                    ProxyRegistry.Tick(scaledDt);
                    Stats.ProxyBatchComputeMs = (float)(ProxyRegistry.LastBatchComputeTicks * _ticksToMs);
                    Stats.ProxySyncBackMs = (float)(ProxyRegistry.LastSyncBackTicks * _ticksToMs);
                }

                // ═══ Phase B: Items ═══
                Stats.PhaseProxyMs = (float)((Stopwatch.GetTimestamp() - phaseProxyStart) * _ticksToMs);
                long phaseBStart = Stopwatch.GetTimestamp();
                Item.UpdatePendingConditionUpdates(deltaTime);  // every frame, raw dt

                // Rebuild hull→character spatial index (Character.UpdateAll already ran)
                if (isMapFrame)
                    HullCharacterTracker.Rebuild();

                if (isMapFrame)
                    DispatchItemUpdates(scaledDt, cam);

                // ═══ Phase C: PriorityItems (LuaCs) — every frame, raw dt ═══
                Stats.PhaseBMs = (float)((Stopwatch.GetTimestamp() - phaseBStart) * _ticksToMs);
                long phaseCStart = Stopwatch.GetTimestamp();
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
                Stats.PhaseCMs = (float)((Stopwatch.GetTimestamp() - phaseCStart) * _ticksToMs);
                long phaseDStart = Stopwatch.GetTimestamp();
                if (isMapFrame)
                {
                    _updateProjSpecificDel?.Invoke(scaledDt);
                    Entity.Spawner?.Update();
                }

                // ═══ Phase E: Frame-end hooks ═══
                Stats.PhaseDMs = (float)((Stopwatch.GetTimestamp() - phaseDStart) * _ticksToMs);
                Stats.TotalDispatchMs = (float)((Stopwatch.GetTimestamp() - totalDispatchStart) * _ticksToMs);
                PerfProfiler.MapEntityUpdateAllPostfix();
                SyncTracker.UpdateTimeout(deltaTime);
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
            long preBuildStart = Stopwatch.GetTimestamp();
            HasStatusTagCachePatch.PreBuildAll();
            Stats.PhaseBPreBuildMs = (float)((Stopwatch.GetTimestamp() - preBuildStart) * _ticksToMs);

            // ── Signal graph accelerator: evaluate compiled circuit graph ──
            if (_hasSignalGraph)
            {
                SignalGraphEvaluator.Tick(dt);
                Stats.SignalGraphTickMs = SignalGraphEvaluator.LastTickMs;
            }

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
                int skippedCount = 0;
                long proxyPhysicsTicks = 0;

                // ── Single-pass: skip / classify ──
                // Check order optimized: cheapest/most-likely-to-skip first.
                // 1. SignalGraph (flat array ~5ns, skips many)
                // 2. Proxy (dictionary, only when handlers registered)
                // 3. ShouldSkipItem (multiple checks, moderate cost)
                // priorityItems check hoisted out of the hot loop when empty.
                long classifyStart = Stopwatch.GetTimestamp();
                bool hasPriority = priorityItems != null && priorityItems.Count > 0;

                foreach (Item item in Item.ItemList)
                {
                    if (hasPriority && priorityItems.Contains(item)) continue;

                    // Cheapest check first: flat bool[65536] array (~5ns)
                    if (_hasSignalGraph && SignalGraphEvaluator.IsAccelerated(item.ID))
                    {
                        Stats.SignalGraphAccelSkips++;
                        continue;
                    }

                    // Wire skip: pure wire items have no meaningful Update (IsActive=false),
                    // skipping the Item.Update shell overhead for ~1000+ wire items
                    if (_hasWireSkip && IsPureWireItem(item))
                    {
                        Stats.WireSkips++;
                        continue;
                    }

                    // Proxy items: guarded by frame flag (skips Dict lookup when no handlers)
                    if (_hasProxy && item.Prefab != null && ProxyRegistry.TryGetHandler(item.Prefab.Identifier, out var proxyHandler))
                    {
                        ProxyRegistry.AttachIfNew(item);
                        Stats.ProxyItems++;

                        switch (proxyHandler.SkipLevel)
                        {
                            case ProxySkipLevel.Full:
                                continue;
                            case ProxySkipLevel.Lightweight:
                                long pStart = Stopwatch.GetTimestamp();
                                ProxyMinimalUpdate(item, dt);
                                proxyPhysicsTicks += Stopwatch.GetTimestamp() - pStart;
                                continue;
                            case ProxySkipLevel.StatusEffectOnly:
                                break; // fall through to normal dispatch
                        }
                    }

                    // Strategy 2: Ground Item Throttle
                    // All ground items (ParentInventory==null): throttle StatusEffects + Components.
                    // Items with active physics bodies get ProxyMinimalUpdate to preserve physics;
                    // items without bodies (lights, labels, decals) get a pure skip.
                    // Note: ColdStorage never covers ground items (ParentInventory==null → false),
                    // so this is the primary throttle path for ALL non-wire, non-signalgraph ground items.
                    // Skip: whitelist, items with active critical components (running machines, etc.)
                    if (_hasGroundItem && item.ParentInventory == null
                        && !OptimizerConfig.WhitelistLookup.Contains(
                            item.Prefab?.Identifier.Value ?? "")
                        && !HasActiveCriticalComponent(item))
                    {
                        int gid = item.ID;
                        ThrottleCounters[gid]++;
                        if (ThrottleCounters[gid] % OptimizerConfig.GroundItemSkipFrames != 0)
                        {
                            // Only run physics preservation for items with active bodies
                            if (item.body != null && item.body.Enabled)
                                ProxyMinimalUpdate(item, dt);
                            Stats.GroundItemSkips++;
                            skippedCount++;
                            continue;
                        }
                        // Full frame: fall through to normal classify + dispatch
                    }

                    if (ShouldSkipItem(item))
                    {
                        skippedCount++;
                        // Maintain network position sync even for skipped items
                        // (prevents positionBuffer backup on client, keeps server PositionUpdateInterval ticking)
                        if (_itemUpdateNetPos != null && item.body != null && item.body.Enabled)
                            _itemUpdateNetPos(item, dt);
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
                        $"worker={_workerItems.Count}, parallel={_parallelEnabled}");
                }

                // Server metrics: record skipped items this frame
                ServerMetrics.SkippedItems = skippedCount;
                Stats.ProxyPhysicsMs = (float)(proxyPhysicsTicks * _ticksToMs);
                Stats.PhaseBClassifyMs = (float)((Stopwatch.GetTimestamp() - classifyStart) * _ticksToMs);

                // ── Launch workers ──
                Task workerTask = null;
                if (_workerItems.Count > 0)
                {
                    Stats.ParallelItems += _workerItems.Count;
                    var snapshot = new List<Item>(_workerItems);
                    workerTask = Task.Run(() => RunWorkers(snapshot, dt, cam));
                }

                // ── Main thread: update unsafe items ──
                long mainLoopStart = Stopwatch.GetTimestamp();
                Stats.MainThreadItems += _mainItems.Count;
                if (_parallelEnabled)
                {
                    // Per-item timing needed for parallel load-balancing diagnostics
                    foreach (var item in _mainItems)
                    {
                        lastUpdatedItem = item;
                        long startTick = Stopwatch.GetTimestamp();
                        item.Update(dt, cam);
                        MainThreadTicks += Stopwatch.GetTimestamp() - startTick;
                        MainThreadItemCount++;
                    }
                }
                else
                {
                    // Fast path: no per-item timing overhead (~117us saved at 2344 items)
                    foreach (var item in _mainItems)
                    {
                        lastUpdatedItem = item;
                        item.Update(dt, cam);
                    }
                    MainThreadItemCount = _mainItems.Count;
                }
                Stats.PhaseBMainLoopMs = (float)((Stopwatch.GetTimestamp() - mainLoopStart) * _ticksToMs);

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
        //  Proxy minimal update (Lightweight skip level)
        // ────────────────────────────────────────────────

        /// <summary>
        /// Runs essential Item.Update phases for proxy items with Lightweight skip level.
        /// Skips: StatusEffects (phase 4), Component loop (phase 5).
        /// Runs: physics body sync, networking, water forces.
        /// </summary>
        private static void ProxyMinimalUpdate(Item item, float dt)
        {
            if (!item.IsActive || item.IsLayerHidden || item.IsInRemoveQueue) return;

            if (item.body != null && item.body.Enabled)
            {
                // Phase 7a: UpdateTransform — sync physics body to item rect
                if (Math.Abs(item.body.LinearVelocity.X) > 0.01f ||
                    Math.Abs(item.body.LinearVelocity.Y) > 0.01f)
                {
                    item.UpdateTransform();
                }

                // Phase 7b: UpdateNetPosition — network sync
                if (_itemUpdateNetPos != null)
                {
                    _itemUpdateNetPos(item, dt);
                }

                // Phase 7c: Water forces
                if (item.InWater)
                    _itemApplyWaterForces?.Invoke(item);
            }
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

            // Strategy 2: Ground Item Throttle — handled in classify loop (physics-preserving)

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
                        int id2 = item.ID;
                        ThrottleCounters[id2]++;
                        if (ThrottleCounters[id2] % rule.SkipFrames != 0)
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
                // Whitelist: never throttle whitelisted items
                if (OptimizerConfig.WhitelistLookup.Contains(identifier))
                    return false;

                if (IsModOptEligibleCached(item))
                {
                    int id3 = item.ID;
                    ThrottleCounters[id3]++;
                    if (ThrottleCounters[id3] % skipFrames != 0)
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
            int id = item.ID;
            if (ActiveUseCacheGen[id] == _frameGeneration)
                return ActiveUseCacheVal[id];
            ActiveUseCacheGen[id] = _frameGeneration;
            bool result = ColdStorageDetector.IsModOptEligible(item);
            ActiveUseCacheVal[id] = result;
            return result;
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
            int id = item.ID;
            sbyte cached = HoldableCache[id];
            if (cached != 0) return cached > 0;
            HoldableCache[id] = item.GetComponent<Holdable>() != null ? (sbyte)1 : (sbyte)-1;
            return HoldableCache[id] > 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasWireConnections(Item item)
        {
            int id = item.ID;
            sbyte cached = WireCache[id];
            if (cached != 0) return cached > 0;
            var conns = item.Connections;
            if (conns != null)
            {
                foreach (var c in conns)
                {
                    if (c.Wires.Count > 0)
                    {
                        WireCache[id] = 1;
                        return true;
                    }
                }
            }
            WireCache[id] = -1;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPureWireItem(Item item)
        {
            int id = item.ID;
            sbyte cached = IsWireCache[id];
            if (cached != 0) return cached > 0;

            // A "pure wire" item has Wire as its only meaningful component.
            // Wire + ConnectionPanel + ItemContainer are all inert when connected.
            bool hasWire = false;
            bool hasOtherActive = false;
            foreach (var ic in item.Components)
            {
                var typeName = ic.GetType().Name;
                if (typeName == "Wire")
                    hasWire = true;
                else if (typeName != "ConnectionPanel" && typeName != "ItemContainer")
                    hasOtherActive = true;
            }

            bool isPure = hasWire && !hasOtherActive;
            IsWireCache[id] = isPure ? (sbyte)1 : (sbyte)-1;
            return isPure;
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

            // Fallback: inline classification (conservative)
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

        // ────────────────────────────────────────────────
        //  Ground item critical component check
        // ────────────────────────────────────────────────

        /// <summary>
        /// Returns true if this item has a critical component that is currently active.
        /// Items with active critical components must NOT be ground-throttled.
        /// Idle machines (Fabricator not crafting, etc.) CAN be throttled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasActiveCriticalComponent(Item item)
        {
            foreach (var ic in item.Components)
            {
                // Always-critical: submarine infrastructure that must never be throttled
                if (ic is Reactor || ic is Engine || ic is Steering
                    || ic is DockingPort || ic is ElectricalDischarger)
                    return true;

                // Critical when active: power, fluid, structural, interaction
                if (ic is Pump || ic is Door || ic is PowerTransfer
                    || ic is OxygenGenerator || ic is Turret
                    || ic is Controller || ic is TriggerComponent)
                {
                    if (ic.IsActive) return true;
                }

                // Machines: only critical when running (crafting/deconstructing)
                if (ic is Fabricator || ic is Deconstructor)
                {
                    if (ic.IsActive) return true;
                }
            }
            return false;
        }

    }
}
