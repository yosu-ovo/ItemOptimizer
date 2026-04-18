using System;
using System.Diagnostics;
using Barotrauma;
using Barotrauma.Items.Components;

namespace ItemOptimizerMod.SignalGraph
{
    /// <summary>
    /// Orchestrates the compiled signal graph: compile, tick each frame,
    /// and provide IsAccelerated() for the UpdateAllTakeover skip path.
    /// </summary>
    static class SignalGraphEvaluator
    {
        // ── Public state ──
        internal static bool IsCompiled;
        internal static int AcceleratedNodeCount;
        internal static int RegisterCount;
        internal static float LastTickMs;
        internal static float AvgTickMs;

        // ── Internal graph ──
        private static CompiledGraph _graph;
        // ── Recompilation throttle ──
        // Wire edits can trigger MarkDirty() many times in rapid succession.
        // Instead of recompiling immediately, defer for a cooldown period
        // to batch multiple changes into a single recompile.
        private static bool _dirty;
        private static int _dirtyCooldown;
        private const int RecompileCooldownFrames = 30; // ~0.5s at 60fps
        internal static bool IsDirty => _dirty;
        private static int _mode; // 0=Off, 1=Accel, 2=Aggressive
        internal static int Mode => _mode;

        // ── O(1) accelerated item lookup — indexed by item.ID (ushort) ──
        private static readonly bool[] _accelerated = new bool[65536];

        // ── Client-side emit suppression (dedicated client should not emit) ──
        internal static bool SuppressEmitOnClient;

        // ── Emit trace (set via ioemit trace) ──
        internal static int EmitTraceFrames;
        internal static int EmitTraceTargetId = -1;

        // ── Condition monitoring — check every N frames ──
        private const int ConditionCheckInterval = 60;
        private static int _conditionCheckCounter;

        private const float EmaSmoothing = 0.05f;
        private static readonly double _ticksToMs = 1000.0 / Stopwatch.Frequency;

        // ════════════════════════════════════
        //  Compile / Rebuild
        // ════════════════════════════════════

        /// <summary>
        /// Build the signal graph from live Item.ItemList.
        /// Called from OnLoadCompleted and after wire changes.
        /// </summary>
        internal static void Compile()
        {
            Reset();

            try
            {
                _graph = SignalGraphBuilder.Build(_mode);
                if (_graph == null || _graph.NodeCount == 0)
                {
                    LuaCsLogger.Log("[ItemOptimizer] SignalGraph: no acceleratable nodes found.");
                    return;
                }

                // Initialize SOA state arrays and assign StateIndex
                NodeEvaluators.Initialize(_graph.Nodes);

                // Populate accelerated lookup (skip PartialOnly nodes — their Update still runs)
                for (int i = 0; i < _graph.Nodes.Length; i++)
                {
                    if (!_graph.Nodes[i].PartialOnly)
                        _accelerated[_graph.Nodes[i].ItemId] = true;
                }

                AcceleratedNodeCount = _graph.NodeCount;
                RegisterCount = _graph.RegisterCount;
                IsCompiled = true;
                _dirty = false;
                _conditionCheckCounter = 0;

                // Suppress emit on dedicated client (server handles authoritative signals).
                // In hosted mode (Client+Server in same process) or dedicated server, emit is needed.
                // Note: GameMain.Client/Server are build-specific; for now, only suppress
                // in pure dedicated-client scenario (SERVER build never suppresses).
#if SERVER
                SuppressEmitOnClient = false;
#else
                // CLIENT build: hosted mode is the common case — always emit.
                // Dedicated-client detection can be refined later if needed.
                SuppressEmitOnClient = false;
#endif

                LuaCsLogger.Log($"[ItemOptimizer] SignalGraph compiled: " +
                    $"{AcceleratedNodeCount} nodes, {RegisterCount} registers, " +
                    $"{_graph.CaptureEdges.Length} capture edges, " +
                    $"{_graph.BackEdges.Length} back-edges, " +
                    $"{_graph.EmitEdges.Length} emit edges (mode={_mode}, " +
                    $"emitSuppressed={SuppressEmitOnClient})");
            }
            catch (Exception ex)
            {
                LuaCsLogger.LogError($"[ItemOptimizer] SignalGraph compile FAILED: {ex.GetType().Name}: {ex.Message}");
                Reset();
            }
        }

        /// <summary>Mark the graph as needing recompilation (e.g., wire changed).
        /// Immediately deactivates the graph so vanilla signals take over during cooldown.
        /// Actual recompile is deferred to batch rapid changes.</summary>
        internal static void MarkDirty()
        {
            _dirtyCooldown = RecompileCooldownFrames;
            if (!_dirty)
            {
                // Deactivate immediately — vanilla signal delivery handles the transition.
                // Without this, SendSignalIntoConnectionPrefix would still intercept signals
                // and push to capture registers that become invalid after recompile.
                IsCompiled = false;
                Array.Clear(_accelerated, 0, 65536);
                _dirty = true;
            }
        }

        internal static void SetMode(int mode)
        {
            _mode = mode;
            if (mode == 0)
                Reset();
        }

        // ════════════════════════════════════
        //  Per-frame tick
        // ════════════════════════════════════

        /// <summary>
        /// Called once per map-entity update frame, BEFORE the item dispatch loop.
        /// Runs the 5-phase signal graph evaluation.
        /// </summary>
        internal static void Tick(float deltaTime)
        {
            if (_mode == 0) return;

            // Dirty-flag recompile with cooldown to batch rapid wire changes.
            // Must be checked BEFORE the IsCompiled gate, otherwise a failed initial
            // compile creates a deadlock where _dirty is never acted upon.
            if (_dirty)
            {
                _dirtyCooldown--;
                if (_dirtyCooldown <= 0)
                {
                    Compile();
                    _dirty = false;
                    if (!IsCompiled) return;
                }
                else
                {
                    // Still cooling down — skip this tick, let vanilla handle signals
                    return;
                }
            }

            if (!IsCompiled || _graph == null) return;

            // Periodic condition check — remove dead items
            _conditionCheckCounter++;
            if (_conditionCheckCounter >= ConditionCheckInterval)
            {
                _conditionCheckCounter = 0;
                if (CheckConditions())
                {
                    // A node's item died — full recompile
                    Compile();
                    if (!IsCompiled) return;
                }
            }

            long tickStart = Stopwatch.GetTimestamp();

            try
            {
                var regs = _graph.Registers;
                var nodes = _graph.Nodes;
                var evalOrder = _graph.EvalOrder;

                // ── Phase 1: Capture external inputs ──
                // Push-based: ReceiveSignalPrefix writes to capture registers between ticks.
                // Registers already contain current-frame values (or null if no signal this frame).
                // No action needed here — capture registers are populated by the prefix hook.

                // ── Phase 2: Inject back-edge delayed values ──
                var backEdges = _graph.BackEdges;
                var backBuf = _graph.BackEdgeBuffer;
                for (int i = 0; i < backEdges.Length; i++)
                    regs[backEdges[i].TargetRegister] = backBuf[i];

                // ── Phase 3: Topological-order evaluation ──
                for (int i = 0; i < evalOrder.Length; i++)
                    NodeEvaluators.Evaluate(ref nodes[evalOrder[i]], regs, deltaTime);

                // ── Phase 3.5: Emit signals to non-accelerated downstream items ──
                // Skip on dedicated client (server handles authoritative signal delivery;
                // client-side emit would override server state via PredictedState)
                if (!SuppressEmitOnClient)
                {
                    var emits = _graph.EmitEdges;
                    bool tracing = EmitTraceFrames > 0;
                    for (int i = 0; i < emits.Length; i++)
                    {
                        ref var emit = ref emits[i];
                        string val = regs[emit.SourceRegister];
                        if (string.IsNullOrEmpty(val))
                        {
                            if (tracing && (EmitTraceTargetId < 0 || EmitTraceTargetId == emit.TargetConnection.Item.ID))
                                LuaCsLogger.Log($"[ItemOptimizer] EmitTrace #{i}: reg[{emit.SourceRegister}]=null/empty → SKIP (target=Item#{emit.TargetConnection.Item.ID} [{emit.TargetConnection.Name}])");
                            continue;
                        }
                        var signal = new Signal(val, source: emit.SourceItem);
                        Connection.SendSignalIntoConnection(signal, emit.TargetConnection);
                        if (tracing && (EmitTraceTargetId < 0 || EmitTraceTargetId == emit.TargetConnection.Item.ID))
                            LuaCsLogger.Log($"[ItemOptimizer] EmitTrace #{i}: reg[{emit.SourceRegister}]=\"{val}\" → Item#{emit.TargetConnection.Item.ID} \"{emit.TargetConnection.Item.Name}\" [{emit.TargetConnection.Name}] (source=Item#{emit.SourceItem.ID})");
                    }
                    if (tracing)
                    {
                        EmitTraceFrames--;
                        if (EmitTraceFrames <= 0)
                            LuaCsLogger.Log("[ItemOptimizer] EmitTrace ended.");
                    }
                }

                // ── Phase 4: Save back-edge values for next frame ──
                for (int i = 0; i < backEdges.Length; i++)
                    backBuf[i] = regs[backEdges[i].SourceRegister];

                // ── Phase 5: Synthesize wire visuals (CLIENT only) ──
#if CLIENT
                SynthesizeWireVisuals(regs);
#endif

                // ── Phase 6: Clear capture registers for next frame ──
                // Push-based capture: registers must be null before ReceiveSignalPrefix
                // writes new values. This ensures "no signal this frame" → null.
                var captureRegIndices = _graph.CaptureRegisterIndices;
                for (int i = 0; i < captureRegIndices.Length; i++)
                    regs[captureRegIndices[i]] = null;

                // Decrement push-capture trace counter
                if (PushTraceFrames > 0)
                    PushTraceFrames--;
            }
            catch (Exception ex)
            {
                LuaCsLogger.LogError($"[ItemOptimizer] SignalGraph tick CRASHED — disabling. {ex.GetType().Name}: {ex.Message}");
                Reset();
                return;
            }

            float elapsed = (float)((Stopwatch.GetTimestamp() - tickStart) * _ticksToMs);
            LastTickMs = elapsed;
            AvgTickMs = AvgTickMs * (1f - EmaSmoothing) + elapsed * EmaSmoothing;
        }

        // ════════════════════════════════════
        //  Query: is this item accelerated?
        // ════════════════════════════════════

        /// <summary>O(1) check if an item is handled by the signal graph.</summary>
        internal static bool IsAccelerated(ushort itemId)
        {
            return _mode > 0 && IsCompiled && _accelerated[itemId];
        }

        // ════════════════════════════════════
        //  Push-based capture (called by ReceiveSignalPrefix)
        // ════════════════════════════════════

        // ── Push-capture trace (limited frames, via iocb trace) ──
        internal static int PushTraceFrames;

        /// <summary>
        /// Called by ReceiveSignalPrefix when a signal arrives at an accelerated item's
        /// input connection. Writes the signal value directly into the capture register.
        /// </summary>
        internal static void PushCaptureSignal(ushort itemId, string connectionName, string value)
        {
            if (_graph?.CaptureInputMap == null) return;
            if (_graph.CaptureInputMap.TryGetValue((itemId, connectionName), out int reg))
            {
                _graph.Registers[reg] = value;
                if (PushTraceFrames > 0)
                    DiagLog.Write($"PushCapture: Item#{itemId} [{connectionName}] = \"{value}\" → reg[{reg}]");
            }
            else if (PushTraceFrames > 0)
            {
                DiagLog.Write($"PushCapture MISS: Item#{itemId} [{connectionName}] = \"{value}\" (no captureInputMap entry)");
            }
        }

        // ════════════════════════════════════
        //  CLIENT: wire visual synthesis
        // ════════════════════════════════════

#if CLIENT
        private static void SynthesizeWireVisuals(string[] regs)
        {
            var wireMap = _graph.RegisterWireMap;
            for (int reg = 0; reg < wireMap.Length; reg++)
            {
                var entry = wireMap[reg];
                if (entry.conn == null || entry.wires == null) continue;

                string val = regs[reg];
                if (val == null) continue;

                var signal = new Signal(val);
                var wires = entry.wires;
                for (int w = 0; w < wires.Length; w++)
                {
                    if (wires[w] != null)
                        wires[w].RegisterSignal(signal, source: entry.conn);
                }
            }
        }
#endif

        // ════════════════════════════════════
        //  Internal helpers
        // ════════════════════════════════════

        /// <summary>Check if any accelerated item has died (condition ≤ 0 or removed).</summary>
        /// <returns>true if recompile needed.</returns>
        private static bool CheckConditions()
        {
            if (_graph == null) return false;
            var nodes = _graph.Nodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                var item = nodes[i].Item;
                if (item == null || item.Removed || item.Condition <= 0)
                    return true;
            }
            return false;
        }

        /// <summary>Full reset — release graph and all state.</summary>
        internal static void Reset()
        {
            IsCompiled = false;
            _dirty = false;
            _graph = null;
            AcceleratedNodeCount = 0;
            RegisterCount = 0;
            LastTickMs = 0;
            AvgTickMs = 0;
            _conditionCheckCounter = 0;
            EmitTraceFrames = 0;
            EmitTraceTargetId = -1;
            Array.Clear(_accelerated, 0, 65536);
            NodeEvaluators.Reset();
        }

        // ════════════════════════════════════
        //  Graph accessor (diagnostics only)
        // ════════════════════════════════════

        /// <summary>Returns the compiled graph for diagnostic inspection. Do not mutate.</summary>
        internal static CompiledGraph GetGraphForDiagnostics() => _graph;

        // ════════════════════════════════════
        //  Diagnostics
        // ════════════════════════════════════

        internal static string GetDiagnostics()
        {
            if (!IsCompiled || _graph == null)
                return "SignalGraph: not compiled";

            int captureCount = _graph.CaptureEdges.Length;
            int backEdgeCount = _graph.BackEdges.Length;

            // Count per type
            int boolCount = 0, notCount = 0, scCount = 0, eqCount = 0, gtCount = 0;
            int arithCount = 0, memCount = 0, roundCount = 0;
            int relayCount = 0, delayCount = 0, partialCount = 0;
            foreach (var n in _graph.Nodes)
            {
                switch (n.Type)
                {
                    case SignalNodeType.BoolOp_And:
                    case SignalNodeType.BoolOp_Or: boolCount++; break;
                    case SignalNodeType.Logic_Not: notCount++; break;
                    case SignalNodeType.Compare_SignalCheck: scCount++; break;
                    case SignalNodeType.Compare_Equals: eqCount++; break;
                    case SignalNodeType.Compare_Greater: gtCount++; break;
                    case SignalNodeType.Arith_Add:
                    case SignalNodeType.Arith_Sub:
                    case SignalNodeType.Arith_Mul:
                    case SignalNodeType.Arith_Div: arithCount++; break;
                    case SignalNodeType.Logic_Memory: memCount++; break;
                    case SignalNodeType.Math_Round: roundCount++; break;
                    case SignalNodeType.Logic_Relay: relayCount++; break;
                    case SignalNodeType.Logic_Delay: delayCount++; break;
                }
                if (n.PartialOnly) partialCount++;
            }

            return $"SignalGraph: {AcceleratedNodeCount} nodes, {RegisterCount} regs, " +
                $"{captureCount} captures, {backEdgeCount} back-edges, " +
                $"{_graph.EmitEdges.Length} emits\n" +
                $"  BoolOp={boolCount} Not={notCount} SignalCheck={scCount} " +
                $"Equals={eqCount} Greater={gtCount}\n" +
                $"  Arith={arithCount} Memory={memCount} Round={roundCount}\n" +
                $"  Relay={relayCount} Delay={delayCount} Partial={partialCount}\n" +
                $"  AvgTickMs={AvgTickMs:F3}";
        }

        internal struct EmitDiagLine
        {
            public string text;
            public Microsoft.Xna.Framework.Color color;
        }

        internal struct EmitDiagResult
        {
            public int Total;
            public EmitDiagLine[] Lines;
        }

        /// <summary>Get diagnostic info about emit edges, optionally filtered by target item ID.</summary>
        internal static EmitDiagResult GetEmitDiagnostics(int filterTargetId = -1)
        {
            if (!IsCompiled || _graph == null)
                return new EmitDiagResult { Total = 0, Lines = new[] { new EmitDiagLine { text = "  SignalGraph not compiled.", color = Microsoft.Xna.Framework.Color.Red } } };

            var emits = _graph.EmitEdges;
            var regs = _graph.Registers;
            var lines = new System.Collections.Generic.List<EmitDiagLine>();

            for (int i = 0; i < emits.Length; i++)
            {
                ref var emit = ref emits[i];
                int targetId = emit.TargetConnection?.Item?.ID ?? -1;
                if (filterTargetId >= 0 && targetId != filterTargetId) continue;

                string val = emit.SourceRegister >= 0 && emit.SourceRegister < regs.Length
                    ? (regs[emit.SourceRegister] ?? "(null)")
                    : "(invalid reg)";
                string targetName = emit.TargetConnection?.Item?.Name ?? "?";
                string connName = emit.TargetConnection?.Name ?? "?";
                string srcName = emit.SourceItem?.Name ?? "?";
                int srcId = emit.SourceItem?.ID ?? -1;

                bool isAccel = targetId >= 0 && _accelerated[targetId];
                var color = isAccel ? Microsoft.Xna.Framework.Color.Red : Microsoft.Xna.Framework.Color.LimeGreen;

                lines.Add(new EmitDiagLine
                {
                    text = $"  #{i}: reg[{emit.SourceRegister}]=\"{val}\" | {srcName}(#{srcId}) → {targetName}(#{targetId}) [{connName}]" +
                           (isAccel ? " !! ACCEL TARGET !!" : ""),
                    color = color
                });

                if (lines.Count >= 50) break; // limit output
            }

            return new EmitDiagResult { Total = emits.Length, Lines = lines.ToArray() };
        }
    }
}
