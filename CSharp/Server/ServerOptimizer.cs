using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Server-side optimization: Transpiler patches on GameServer.ClientWriteIngame
    /// to replace O(n) Queue.Contains with O(1) HashSet lookups.
    /// </summary>
    static class ServerOptimizer
    {
        // ── Shadow HashSet for Queue.Contains dedup ──
        // Per-call cached: built from Queue contents on first Contains call per queue instance,
        // incrementally updated on Enqueue.
        [ThreadStatic] private static HashSet<Entity> _containsShadow;
        [ThreadStatic] private static Queue<Entity> _shadowSource;

        private static bool _registered;

        internal static void RegisterPatches(Harmony harmony)
        {
            if (_registered) return;

            var target = AccessTools.Method(typeof(GameServer), "ClientWriteIngame");
            if (target == null)
            {
                LuaCsLogger.Log("[ItemOptimizer] ServerOptimizer: GameServer.ClientWriteIngame not found, skipping");
                return;
            }

            harmony.Patch(target,
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(ServerOptimizer), nameof(Transpiler))));

            _registered = true;
            LuaCsLogger.Log("[ItemOptimizer] ServerOptimizer: HashSet dedup transpiler registered");
        }

        internal static void UnregisterPatches(Harmony harmony)
        {
            if (!_registered) return;
            var target = AccessTools.Method(typeof(GameServer), "ClientWriteIngame");
            if (target != null)
                harmony.Unpatch(target, AccessTools.Method(typeof(ServerOptimizer), nameof(Transpiler)));
            _registered = false;
        }

        /// <summary>
        /// O(1) replacement for Queue&lt;Entity&gt;.Contains().
        /// Rebuilds the shadow from queue contents when the queue instance changes
        /// OR when the queue size diverges (indicating dequeues between calls).
        /// </summary>
        public static bool FastContains(Queue<Entity> queue, Entity entity)
        {
            if (_shadowSource != queue || _containsShadow == null || _containsShadow.Count != queue.Count)
            {
                _containsShadow ??= new HashSet<Entity>(256);
                _containsShadow.Clear();
                foreach (var e in queue) _containsShadow.Add(e);
                _shadowSource = queue;
            }
            return _containsShadow.Contains(entity);
        }

        /// <summary>
        /// Replacement for Queue&lt;Entity&gt;.Enqueue() that also updates the shadow HashSet.
        /// </summary>
        public static void FastEnqueue(Queue<Entity> queue, Entity entity)
        {
            queue.Enqueue(entity);
            if (_shadowSource == queue && _containsShadow != null)
                _containsShadow.Add(entity);
        }

        // ── Transpiler ──

        private static readonly MethodInfo _queueContains =
            typeof(Queue<Entity>).GetMethod("Contains", new[] { typeof(Entity) });
        private static readonly MethodInfo _queueEnqueue =
            typeof(Queue<Entity>).GetMethod("Enqueue", new[] { typeof(Entity) });
        private static readonly MethodInfo _fastContains =
            AccessTools.Method(typeof(ServerOptimizer), nameof(FastContains));
        private static readonly MethodInfo _fastEnqueue =
            AccessTools.Method(typeof(ServerOptimizer), nameof(FastEnqueue));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int containsReplaced = 0;
            int enqueueReplaced = 0;

            foreach (var inst in instructions)
            {
                if (inst.Calls(_queueContains))
                {
                    // Replace: callvirt Queue<Entity>.Contains(Entity)
                    // With:    call ServerOptimizer.FastContains(Queue<Entity>, Entity)
                    // Stack is identical: [queue, entity] → [bool]
                    yield return new CodeInstruction(OpCodes.Call, _fastContains);
                    containsReplaced++;
                }
                else if (inst.Calls(_queueEnqueue))
                {
                    // Replace: callvirt Queue<Entity>.Enqueue(Entity)
                    // With:    call ServerOptimizer.FastEnqueue(Queue<Entity>, Entity)
                    // Stack is identical: [queue, entity] → []
                    yield return new CodeInstruction(OpCodes.Call, _fastEnqueue);
                    enqueueReplaced++;
                }
                else
                {
                    yield return inst;
                }
            }

            LuaCsLogger.Log($"[ItemOptimizer] ServerOptimizer transpiler: replaced {containsReplaced} Contains + {enqueueReplaced} Enqueue calls");
        }
    }
}
