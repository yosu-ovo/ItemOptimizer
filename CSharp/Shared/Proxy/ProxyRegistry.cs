using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Barotrauma;

namespace ItemOptimizerMod.Proxy
{
    /// <summary>
    /// Controls which phases of Item.Update are skipped for proxy items.
    /// </summary>
    public enum ProxySkipLevel
    {
        /// <summary>Skip Item.Update entirely. Handler does everything.</summary>
        Full,
        /// <summary>Skip StatusEffects + component loop. Keep physics/networking/water.</summary>
        Lightweight,
        /// <summary>Skip only StatusEffects. Components run normally.</summary>
        StatusEffectOnly,
    }

    /// <summary>
    /// Handler for a specific proxy item type.
    /// BatchCompute runs on worker threads (read-only item access, write only to handler-local state).
    /// SyncBack runs on main thread (writes to vanilla Item/Component properties).
    /// </summary>
    public interface IProxyHandler
    {
        /// <summary>Number of currently managed items.</summary>
        int Count { get; }

        /// <summary>Which phases of Item.Update to skip. Default: Lightweight.</summary>
        ProxySkipLevel SkipLevel => ProxySkipLevel.Lightweight;

        /// <summary>
        /// Compute phase — may run on a worker thread.
        /// MUST NOT write to any Barotrauma object (Item, Component, LightSource, etc.).
        /// Only read item state and write to handler-internal arrays.
        /// </summary>
        void BatchCompute(float deltaTime);

        /// <summary>
        /// Sync phase — always runs on main thread.
        /// Write computed results back to vanilla objects (LightSource, Hull, etc.).
        /// </summary>
        void SyncBack();

        /// <summary>Called on main thread when an item with this handler's prefab ID is first encountered.</summary>
        void Attach(Item item);

        /// <summary>Called on main thread during cleanup or when an item is removed.</summary>
        void Detach(Item item);
    }

    /// <summary>
    /// Central registry for proxy item handlers.
    /// Called by UpdateAllTakeover to dispatch proxy items outside the vanilla Update loop.
    /// </summary>
    public static class ProxyRegistry
    {
        // ── Registration ──

        private static readonly Dictionary<Identifier, IProxyHandler> _handlerMap = new();
        private static readonly List<IProxyHandler> _handlerList = new();
        private static readonly HashSet<Identifier> _proxyIds = new();

        // ── Attachment tracking (avoid duplicate OnAttach) ──
        private static readonly HashSet<ushort> _attachedIds = new();

        // ── Scheduling thresholds ──
        private const int SerialThreshold = 50;   // below this total count: skip Task overhead
        private const int MinParallelCount = 16;   // handler needs this many items to get its own Task

        // ── Timing (written by Tick, read by Stats) ──
        public static long LastBatchComputeTicks;
        public static long LastSyncBackTicks;

        public static bool HasHandlers => _handlerList.Count > 0;
        public static int TotalCount => _handlerList.Sum(h => h.Count);

        /// <summary>Register a handler for items with the given prefab identifier.</summary>
        public static void Register(Identifier prefabId, IProxyHandler handler)
        {
            _handlerMap[prefabId] = handler;
            _proxyIds.Add(prefabId);
            if (!_handlerList.Contains(handler))
                _handlerList.Add(handler);
            LuaCsLogger.Log($"[ItemOptimizer:Proxy] Registered handler for '{prefabId}' (skipLevel={handler.SkipLevel})");
        }

        /// <summary>
        /// Register a handler by duck-typing — no compile-time IProxyHandler reference needed.
        /// The handler object must have: int Count { get; }, void BatchCompute(float),
        /// void SyncBack(), void Attach(Item), void Detach(Item).
        /// Used by external mods to avoid cross-AssemblyLoadContext type resolution failures.
        /// </summary>
        public static void RegisterDynamic(Identifier prefabId, object handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            if (handler is IProxyHandler direct)
            {
                Register(prefabId, direct);
                return;
            }

            var adapter = new ReflectionProxyAdapter(handler);
            Register(prefabId, adapter);
            LuaCsLogger.Log($"[ItemOptimizer:Proxy] Dynamic registration for '{prefabId}' (reflection adapter)");
        }

        /// <summary>Get the skip level for a proxy prefab. Returns Full if not found.</summary>
        public static ProxySkipLevel GetSkipLevel(Identifier prefabId)
        {
            return _handlerMap.TryGetValue(prefabId, out var handler)
                ? handler.SkipLevel
                : ProxySkipLevel.Full;
        }

        /// <summary>Adapter that wraps an arbitrary object with matching method signatures as IProxyHandler.</summary>
        private sealed class ReflectionProxyAdapter : IProxyHandler
        {
            private readonly object _target;
            private readonly PropertyInfo _countProp;
            private readonly PropertyInfo _skipLevelProp;
            private readonly MethodInfo _batchCompute;
            private readonly MethodInfo _syncBack;
            private readonly MethodInfo _attach;
            private readonly MethodInfo _detach;

            public ReflectionProxyAdapter(object target)
            {
                _target = target;
                var type = target.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

                _countProp = type.GetProperty("Count", flags)
                    ?? throw new ArgumentException($"Handler {type.Name} missing 'int Count' property");
                _batchCompute = type.GetMethod("BatchCompute", flags, null, new[] { typeof(float) }, null)
                    ?? throw new ArgumentException($"Handler {type.Name} missing 'void BatchCompute(float)'");
                _syncBack = type.GetMethod("SyncBack", flags, null, Type.EmptyTypes, null)
                    ?? throw new ArgumentException($"Handler {type.Name} missing 'void SyncBack()'");
                _attach = type.GetMethod("Attach", flags, null, new[] { typeof(Item) }, null)
                    ?? throw new ArgumentException($"Handler {type.Name} missing 'void Attach(Item)'");
                _detach = type.GetMethod("Detach", flags, null, new[] { typeof(Item) }, null)
                    ?? throw new ArgumentException($"Handler {type.Name} missing 'void Detach(Item)'");

                // Optional: SkipLevel property (defaults to Full for backward compat)
                _skipLevelProp = type.GetProperty("SkipLevel", flags);
            }

            public int Count => (int)_countProp.GetValue(_target);
            public ProxySkipLevel SkipLevel => _skipLevelProp != null
                ? (ProxySkipLevel)_skipLevelProp.GetValue(_target)
                : ProxySkipLevel.Full;
            public void BatchCompute(float deltaTime) => _batchCompute.Invoke(_target, new object[] { deltaTime });
            public void SyncBack() => _syncBack.Invoke(_target, null);
            public void Attach(Item item) => _attach.Invoke(_target, new object[] { item });
            public void Detach(Item item) => _detach.Invoke(_target, new object[] { item });
        }

        /// <summary>Unregister a handler. Detaches all items.</summary>
        public static void Unregister(Identifier prefabId)
        {
            if (_handlerMap.TryGetValue(prefabId, out var handler))
            {
                _handlerMap.Remove(prefabId);
                _proxyIds.Remove(prefabId);

                var toDetach = new List<ushort>();
                foreach (var item in Item.ItemList)
                {
                    if (item.Prefab?.Identifier == prefabId && _attachedIds.Contains(item.ID))
                    {
                        handler.Detach(item);
                        toDetach.Add(item.ID);
                    }
                }
                foreach (var id in toDetach)
                    _attachedIds.Remove(id);

                if (!_handlerMap.ContainsValue(handler))
                    _handlerList.Remove(handler);

                LuaCsLogger.Log($"[ItemOptimizer:Proxy] Unregistered handler for '{prefabId}'");
            }
        }

        /// <summary>
        /// Combined check: is this a proxy prefab? If so, return the handler.
        /// Eliminates the need for separate IsProxy + GetSkipLevel calls.
        /// </summary>
        public static bool TryGetHandler(Identifier prefabId, out IProxyHandler handler)
        {
            return _handlerMap.TryGetValue(prefabId, out handler);
        }

        /// <summary>Fast check: is this item prefab managed by a proxy?</summary>
        public static bool IsProxy(Identifier prefabId)
        {
            return _proxyIds.Contains(prefabId);
        }

        /// <summary>Attach item to its handler if not already attached.</summary>
        public static void AttachIfNew(Item item)
        {
            if (_attachedIds.Contains(item.ID)) return;
            if (_handlerMap.TryGetValue(item.Prefab.Identifier, out var handler))
            {
                handler.Attach(item);
                _attachedIds.Add(item.ID);
            }
        }

        /// <summary>
        /// Main tick entry — called by UpdateAllTakeover BEFORE DispatchItemUpdates.
        /// Phase 0a: parallel BatchCompute, Phase 0b: sequential SyncBack.
        /// </summary>
        public static void Tick(float deltaTime)
        {
            if (_handlerList.Count == 0) return;

            CleanupRemoved();

            int total = 0;
            foreach (var h in _handlerList)
                total += h.Count;

            if (total == 0) return;

            // ── Phase 0a: BatchCompute ──
            long t0 = Stopwatch.GetTimestamp();

            if (total < SerialThreshold || _handlerList.Count == 1)
            {
                foreach (var h in _handlerList)
                    h.BatchCompute(deltaTime);
            }
            else
            {
                var heavy = new List<IProxyHandler>();
                var light = new List<IProxyHandler>();
                foreach (var h in _handlerList)
                {
                    if (h.Count >= MinParallelCount)
                        heavy.Add(h);
                    else
                        light.Add(h);
                }

                var tasks = new List<Task>(heavy.Count + 1);

                foreach (var h in heavy)
                {
                    var handler = h;
                    var dt = deltaTime;
                    tasks.Add(Task.Run(() => handler.BatchCompute(dt)));
                }

                if (light.Count > 0)
                {
                    var lightCopy = light.ToArray();
                    var dt = deltaTime;
                    tasks.Add(Task.Run(() =>
                    {
                        foreach (var h in lightCopy)
                            h.BatchCompute(dt);
                    }));
                }

                try
                {
                    Task.WaitAll(tasks.ToArray());
                }
                catch (AggregateException ae)
                {
                    foreach (var e in ae.Flatten().InnerExceptions)
                        LuaCsLogger.LogError($"[ItemOptimizer:Proxy] Worker error: {e.Message}\n{e.StackTrace}");
                }
            }

            long t1 = Stopwatch.GetTimestamp();
            LastBatchComputeTicks = t1 - t0;

            // ── Phase 0b: SyncBack (main thread) ──
            foreach (var h in _handlerList)
                h.SyncBack();

            LastSyncBackTicks = Stopwatch.GetTimestamp() - t1;
        }

        /// <summary>Detach items that have been removed from the game.</summary>
        private static void CleanupRemoved()
        {
            if (_attachedIds.Count == 0) return;

            List<ushort> toRemove = null;
            foreach (var id in _attachedIds)
            {
                var entity = Entity.FindEntityByID(id);
                if (entity is not Item item || item.Removed)
                {
                    toRemove ??= new List<ushort>();
                    toRemove.Add(id);
                }
            }

            if (toRemove == null) return;
            foreach (var id in toRemove)
            {
                var entity = Entity.FindEntityByID(id);
                if (entity is Item item)
                {
                    if (_handlerMap.TryGetValue(item.Prefab.Identifier, out var handler))
                        handler.Detach(item);
                }
                _attachedIds.Remove(id);
            }
        }

        /// <summary>Detach all items but keep handler registrations. Called when proxy system is toggled off.</summary>
        public static void DetachAllItems()
        {
            foreach (var item in Item.ItemList)
            {
                if (_attachedIds.Contains(item.ID) &&
                    _handlerMap.TryGetValue(item.Prefab.Identifier, out var handler))
                {
                    handler.Detach(item);
                }
            }
            _attachedIds.Clear();
            LastBatchComputeTicks = 0;
            LastSyncBackTicks = 0;
        }

        /// <summary>Detach all items and clear all handlers. Called on Dispose.</summary>
        public static void DetachAll()
        {
            foreach (var item in Item.ItemList)
            {
                if (_attachedIds.Contains(item.ID) &&
                    _handlerMap.TryGetValue(item.Prefab.Identifier, out var handler))
                {
                    handler.Detach(item);
                }
            }
            _attachedIds.Clear();
            _handlerMap.Clear();
            _handlerList.Clear();
            _proxyIds.Clear();
            LastBatchComputeTicks = 0;
            LastSyncBackTicks = 0;
        }
    }
}
