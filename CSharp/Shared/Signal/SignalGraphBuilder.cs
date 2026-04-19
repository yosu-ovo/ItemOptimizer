using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;

namespace ItemOptimizerMod.SignalGraph
{
    /// <summary>
    /// Discovers the signal wiring graph from live Item.ItemList,
    /// allocates registers, performs topological sort, and produces a CompiledGraph.
    /// </summary>
    static class SignalGraphBuilder
    {
        // ── CircuitBox boundary reflection cache ──
        // CB internal types (CircuitBoxInputConnection, etc.) are internal;
        // we access their public fields via cached reflection.
        private static bool _cbReflectReady;
        private static bool _cbReflectFailed;
        private static FieldInfo _cbInputsField;       // CircuitBox.Inputs (ImmutableArray<CBInputConn>)
        private static FieldInfo _cbConnConnField;      // CircuitBoxConnection.Connection (Connection)
        private static FieldInfo _cbExtConnectedTo;     // CircuitBoxInputConnection.ExternallyConnectedTo (List<CBConn>)
        private static FieldInfo _connCBConnsField;     // Connection.CircuitBoxConnections (List<CBConn>)
        private static Type _cbOutputConnType;          // typeof(CircuitBoxOutputConnection)

        private static void InitCBReflection()
        {
            if (_cbReflectReady || _cbReflectFailed) return;
            try
            {
                var asm = typeof(CircuitBox).Assembly;
                var cbConnBase = asm.GetType("Barotrauma.CircuitBoxConnection");
                var cbInputConn = asm.GetType("Barotrauma.CircuitBoxInputConnection");
                _cbOutputConnType = asm.GetType("Barotrauma.CircuitBoxOutputConnection");

                if (cbConnBase == null || cbInputConn == null || _cbOutputConnType == null)
                {
                    _cbReflectFailed = true;
                    LuaCsLogger.LogError("[ItemOptimizer] CB reflection: types not found");
                    return;
                }

                _cbInputsField = typeof(CircuitBox).GetField("Inputs");
                _cbConnConnField = cbConnBase.GetField("Connection");
                _cbExtConnectedTo = cbInputConn.GetField("ExternallyConnectedTo"); // on CircuitBoxInputConnection
                _connCBConnsField = typeof(Connection).GetField("CircuitBoxConnections");

                if (_cbInputsField == null || _cbConnConnField == null ||
                    _cbExtConnectedTo == null || _connCBConnsField == null)
                {
                    _cbReflectFailed = true;
                    LuaCsLogger.LogError("[ItemOptimizer] CB reflection: fields not found");
                    return;
                }

                _cbReflectReady = true;
            }
            catch (Exception ex)
            {
                _cbReflectFailed = true;
                LuaCsLogger.LogError($"[ItemOptimizer] CB reflection init failed: {ex.Message}");
            }
        }

        /// <summary>Get the Connection field from a CircuitBoxConnection object.</summary>
        private static Connection GetCBConnection(object cbConn)
            => (Connection)_cbConnConnField.GetValue(cbConn);

        // ── Supported component types ──
        private static SignalNodeType? Classify(ItemComponent ic, int mode)
        {
            // Mode 1+: Pure logic gates
            switch (ic)
            {
                case AndComponent: return SignalNodeType.BoolOp_And;
                case OrComponent: return SignalNodeType.BoolOp_Or;
                case NotComponent: return SignalNodeType.Logic_Not;
                case SignalCheckComponent: return SignalNodeType.Compare_SignalCheck;
                case GreaterComponent: return SignalNodeType.Compare_Greater;  // before Equals (subclass)
                case EqualsComponent: return SignalNodeType.Compare_Equals;
                case AdderComponent: return SignalNodeType.Arith_Add;
                case SubtractComponent: return SignalNodeType.Arith_Sub;
                case MultiplyComponent: return SignalNodeType.Arith_Mul;
                case DivideComponent: return SignalNodeType.Arith_Div;
                case MemoryComponent: return SignalNodeType.Logic_Memory;
                case FunctionComponent fc when fc.Function == FunctionComponent.FunctionType.Round:
                    return SignalNodeType.Math_Round;
            }

            // Mode 2: Expanded set (aggressive)
            if (mode >= 2)
            {
                switch (ic)
                {
                    case RelayComponent: return SignalNodeType.Logic_Relay;
                    case DelayComponent: return SignalNodeType.Logic_Delay;
                }
            }

            return null;
        }

        /// <summary>Is this node type partial-only (signal compiled but Update still runs)?</summary>
        private static bool IsPartialOnly(SignalNodeType type)
        {
            return type == SignalNodeType.Logic_Relay;
        }

        /// <summary>
        /// Build a compiled signal graph from the currently loaded items.
        /// Returns null if no items qualify for acceleration.
        /// </summary>
        public static CompiledGraph Build(int mode = 1)
        {
            // ────────────────────────────────────
            //  Step 1: Discover acceleratable items
            // ────────────────────────────────────
            var candidates = new Dictionary<ushort, (Item item, ItemComponent comp, SignalNodeType type)>();

            foreach (var item in Item.ItemList)
            {
                if (item.Removed || item.Condition <= 0) continue;
                if (item.Connections == null || item.Connections.Count == 0) continue;

                // Exclude CircuitBox container items (they have no signal logic themselves).
                // CB-internal items are NOT excluded — they are accelerated via the signal graph,
                // with CB boundary connections handled via reflection.
                if (item.GetComponent<CircuitBox>() != null) continue;

                // Exclude items with StatusEffects on connections
                bool hasConnEffects = false;
                foreach (var conn in item.Connections)
                {
                    if (conn.Effects != null && conn.Effects.Count > 0)
                    {
                        hasConnEffects = true;
                        break;
                    }
                }
                if (hasConnEffects) continue;

                // Find a supported signal component
                ItemComponent signalComp = null;
                SignalNodeType? nodeType = null;
                foreach (var ic in item.Components)
                {
                    var t = Classify(ic, mode);
                    if (t.HasValue)
                    {
                        signalComp = ic;
                        nodeType = t;
                        break;
                    }
                }
                if (signalComp == null || !nodeType.HasValue) continue;

                candidates[item.ID] = (item, signalComp, nodeType.Value);
            }

            if (candidates.Count == 0) return null;

            // ── Diagnostic: log CB internal candidates to file ──
            int cbInternalCount = 0;
            foreach (var (cid, (citem, _, ctype)) in candidates)
            {
                if (citem.ParentInventory?.Owner is Item cbOwnerDbg &&
                    cbOwnerDbg.GetComponent<CircuitBox>() != null)
                {
                    cbInternalCount++;
                    DiagLog.Write($"CB-internal candidate: Item#{cid} \"{citem.Name}\" type={ctype} (CB=Item#{cbOwnerDbg.ID})");
                }
            }
            if (cbInternalCount > 0)
                DiagLog.Write($"Found {cbInternalCount} CB-internal candidates (total={candidates.Count}).");

            // ────────────────────────────────────
            //  Step 2: Allocate registers for output connections
            // ────────────────────────────────────
            int nextReg = 0;
            // Map: (item.ID, connection.Name) → register index
            var regMap = new Dictionary<(ushort, string), int>();
            // Map: register index → source Connection (for capture edges & wire visuals)
            var regSourceConn = new Dictionary<int, Connection>();

            // Also map non-accelerated item outputs that feed INTO accelerated items
            // (these become capture edges — discovered in step 3)

            // Initialize CB reflection (needed for boundary tracing)
            InitCBReflection();

            // Allocate registers for accelerated items' outputs.
            // IMPORTANT: Only allocate for connections that the graph evaluator actually writes.
            // PartialOnly nodes (e.g. Relay) have extra outputs like state_out/power_value_out
            // that are emitted by vanilla Update(), not by the graph. If we allocate registers
            // for those, downstream nodes get internal edges to never-written registers instead
            // of capture edges that correctly receive the vanilla Update() signals.
            foreach (var (id, (item, comp, type)) in candidates)
            {
                var graphOutputNames = GetOutputConnectionNames(type);
                foreach (var conn in item.Connections)
                {
                    if (!conn.IsOutput) continue;

                    // Only allocate registers for connections the graph evaluator handles
                    bool isGraphOutput = false;
                    foreach (var name in graphOutputNames)
                    {
                        if (conn.Name == name) { isGraphOutput = true; break; }
                    }
                    if (!isGraphOutput) continue;

                    var key = (id, conn.Name);
                    if (!regMap.ContainsKey(key))
                    {
                        int reg = nextReg++;
                        regMap[key] = reg;
                        regSourceConn[reg] = conn;

                        // CB boundary output mapping: if this item is inside a CircuitBox,
                        // map the CB's external output connection to the same register
                        // so that external consumers trace wires back to the CB and find it.
                        if (_cbReflectReady && item.ParentInventory?.Owner is Item cbOwner)
                        {
                            var cb = cbOwner.GetComponent<CircuitBox>();
                            if (cb != null)
                            {
                                var cbConns = _connCBConnsField.GetValue(conn) as IList;
                                if (cbConns != null)
                                {
                                    foreach (var cbConn in cbConns)
                                    {
                                        if (cbConn.GetType() == _cbOutputConnType)
                                        {
                                            var extConn = GetCBConnection(cbConn);
                                            if (extConn != null)
                                            {
                                                var extKey = (cbOwner.ID, extConn.Name);
                                                if (!regMap.ContainsKey(extKey))
                                                {
                                                    regMap[extKey] = reg;
                                                    // Also add to regSourceConn for wire visuals
                                                    // (the external CB output connection's wires are visible)
                                                    regSourceConn[reg] = extConn;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // ────────────────────────────────────
            //  Step 3: Wire resolution — resolve inputs to register indices
            // ────────────────────────────────────
            var nodes = new List<SignalNode>(candidates.Count);
            var captureEdges = new List<CaptureEdge>();
            var captureInputMap = new Dictionary<(ushort, string), int>();
            // adjacency: output register → list of node indices that consume it
            var dependents = new Dictionary<int, List<int>>();
            // in-degree per node (for topo sort)
            var inDegree = new int[candidates.Count];
            // node index from item.ID
            var itemToNodeIdx = new Dictionary<ushort, int>();

            foreach (var (id, (item, comp, type)) in candidates)
            {
                int nodeIdx = nodes.Count;
                itemToNodeIdx[id] = nodeIdx;

                var inputRegs = ResolveInputRegisters(item, type, candidates, regMap,
                    captureEdges, captureInputMap, ref nextReg, regSourceConn);

                var outputRegs = ResolveOutputRegisters(item, type, regMap);

                nodes.Add(new SignalNode
                {
                    Type = type,
                    ItemId = id,
                    Item = item,
                    Component = comp,
                    InputRegs = inputRegs,
                    OutputRegs = outputRegs,
                    StateIndex = -1, // assigned later by NodeEvaluators
                    PartialOnly = IsPartialOnly(type),
                });
            }

            // Build dependency edges for topological sort
            foreach (var (id, (item, _, _)) in candidates)
            {
                if (!itemToNodeIdx.TryGetValue(id, out int consumerIdx)) continue;
                var node = nodes[consumerIdx];
                foreach (int inReg in node.InputRegs)
                {
                    if (inReg < 0) continue;
                    // Find which node (if any) produces this register
                    foreach (var (otherId, otherNodeIdx) in itemToNodeIdx)
                    {
                        if (otherId == id) continue;
                        var otherNode = nodes[otherNodeIdx];
                        foreach (int outReg in otherNode.OutputRegs)
                        {
                            if (outReg == inReg)
                            {
                                // otherNode → thisNode dependency
                                if (!dependents.ContainsKey(outReg))
                                    dependents[outReg] = new List<int>();
                                dependents[outReg].Add(consumerIdx);
                                inDegree[consumerIdx]++;
                            }
                        }
                    }
                }
            }

            // ────────────────────────────────────
            //  Step 4: Topological sort (Kahn's algorithm)
            // ────────────────────────────────────
            var evalOrder = new List<int>(nodes.Count);
            var queue = new Queue<int>();
            var tempInDegree = (int[])inDegree.Clone();

            for (int i = 0; i < nodes.Count; i++)
            {
                if (tempInDegree[i] == 0)
                    queue.Enqueue(i);
            }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                evalOrder.Add(idx);

                var node = nodes[idx];
                foreach (int outReg in node.OutputRegs)
                {
                    if (!dependents.TryGetValue(outReg, out var deps)) continue;
                    foreach (int depIdx in deps)
                    {
                        tempInDegree[depIdx]--;
                        if (tempInDegree[depIdx] == 0)
                            queue.Enqueue(depIdx);
                    }
                }
            }

            // ────────────────────────────────────
            //  Step 5: Cycle detection — remaining nodes form cycles
            // ────────────────────────────────────
            var backEdges = new List<BackEdge>();
            if (evalOrder.Count < nodes.Count)
            {
                // Nodes not in evalOrder are part of cycles
                var inEvalOrder = new HashSet<int>(evalOrder);
                var cycleNodes = new List<int>();
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (!inEvalOrder.Contains(i))
                        cycleNodes.Add(i);
                }

                // Break cycles by cutting one back-edge per cycle node:
                // for each cycle node, find an input that comes from another cycle node
                // and convert it to a back-edge (1-frame delay)
                foreach (int cIdx in cycleNodes)
                {
                    var cNode = nodes[cIdx];
                    bool edgeCut = false;
                    foreach (int inReg in cNode.InputRegs)
                    {
                        if (inReg < 0) continue;
                        // Check if this input comes from a cycle node
                        foreach (int otherCIdx in cycleNodes)
                        {
                            if (otherCIdx == cIdx) continue;
                            var otherNode = nodes[otherCIdx];
                            foreach (int outReg in otherNode.OutputRegs)
                            {
                                if (outReg == inReg)
                                {
                                    // Convert to back-edge: allocate a delay register
                                    int delayReg = nextReg++;
                                    backEdges.Add(new BackEdge
                                    {
                                        SourceRegister = outReg,
                                        TargetRegister = delayReg
                                    });
                                    // Rewrite the node's input to read from delayReg
                                    for (int r = 0; r < cNode.InputRegs.Length; r++)
                                    {
                                        if (cNode.InputRegs[r] == inReg)
                                        {
                                            cNode.InputRegs[r] = delayReg;
                                            break;
                                        }
                                    }
                                    nodes[cIdx] = cNode;
                                    edgeCut = true;
                                    break;
                                }
                            }
                            if (edgeCut) break;
                        }
                        if (edgeCut) break;
                    }
                }

                // After cutting back-edges, add cycle nodes to eval order
                // (they should now have reduced in-degree; just append them)
                foreach (int cIdx in cycleNodes)
                    evalOrder.Add(cIdx);
            }

            // ────────────────────────────────────
            //  Step 6: Build wire visual map (CLIENT only, but allocated always)
            // ────────────────────────────────────
            var wireMap = new (Connection conn, Wire[] wires)[nextReg];
            foreach (var (reg, conn) in regSourceConn)
            {
                if (reg < nextReg)
                {
                    var wires = conn.Wires.ToArray();
                    wireMap[reg] = (conn, wires);
                }
            }

            // ────────────────────────────────────
            //  Step 6b: Build emit edges (accel output → non-accel downstream)
            // ────────────────────────────────────
            var emitEdges = new List<EmitEdge>();
            foreach (var (id, (item, comp, type)) in candidates)
            {
                var outputNames = GetOutputConnectionNames(type);
                foreach (var outName in outputNames)
                {
                    var key = (id, outName);
                    if (!regMap.TryGetValue(key, out int reg)) continue;

                    // Find the output connection
                    Connection outConn = null;
                    foreach (var conn in item.Connections)
                    {
                        if (conn.IsOutput && conn.Name == outName) { outConn = conn; break; }
                    }
                    if (outConn == null) continue;

                    // Check each wire: if destination is NOT accelerated, create emit edge
                    foreach (var wire in outConn.Wires)
                    {
                        var targetConn = wire.OtherConnection(outConn);
                        if (targetConn == null) continue;
                        if (candidates.ContainsKey(targetConn.Item.ID)) continue; // target is also accelerated, skip

                        emitEdges.Add(new EmitEdge
                        {
                            SourceRegister = reg,
                            TargetConnection = targetConn,
                            SourceItem = item,
                        });
                    }

                    // CB boundary emit: if this output connection bridges through a
                    // CircuitBoxOutputConnection to the CB's external output, trace
                    // external wires to find non-accelerated downstream targets.
                    if (_cbReflectReady)
                    {
                        var cbConns = _connCBConnsField.GetValue(outConn) as IList;
                        if (cbConns != null)
                        {
                            foreach (var cbConn in cbConns)
                            {
                                if (cbConn.GetType() != _cbOutputConnType) continue;

                                var extConn = GetCBConnection(cbConn);
                                if (extConn == null) continue;

                                // Trace external wires from CB's output connection
                                foreach (var wire in extConn.Wires)
                                {
                                    var targetConn = wire.OtherConnection(extConn);
                                    if (targetConn == null) continue;
                                    if (candidates.ContainsKey(targetConn.Item.ID)) continue;

                                    emitEdges.Add(new EmitEdge
                                    {
                                        SourceRegister = reg,
                                        TargetConnection = targetConn,
                                        SourceItem = item,
                                    });
                                }
                            }
                        }
                    }
                }
            }

            // Build capture register index array for efficient clearing
            var captureRegIndices = new HashSet<int>();
            foreach (var ce in captureEdges)
                captureRegIndices.Add(ce.TargetRegister);

            // ────────────────────────────────────
            //  Step 7: Assemble CompiledGraph
            // ────────────────────────────────────
            var graph = new CompiledGraph
            {
                Registers = new string[nextReg],
                Nodes = nodes.ToArray(),
                EvalOrder = evalOrder.ToArray(),
                CaptureEdges = captureEdges.ToArray(),
                EmitEdges = emitEdges.ToArray(),
                BackEdges = backEdges.ToArray(),
                BackEdgeBuffer = new string[backEdges.Count],
                RegisterWireMap = wireMap,
                CaptureInputMap = captureInputMap,
                CaptureRegisterIndices = captureRegIndices.Count > 0
                    ? System.Linq.Enumerable.ToArray(captureRegIndices) : Array.Empty<int>(),
            };

            LuaCsLogger.Log($"[ItemOptimizer] SignalGraph compiled: " +
                $"{graph.NodeCount} nodes, {graph.RegisterCount} registers, " +
                $"{graph.CaptureEdges.Length} capture edges, " +
                $"{graph.EmitEdges.Length} emit edges, " +
                $"{graph.BackEdges.Length} back-edges (cycles)");

            return graph;
        }

        // ────────────────────────────────────────────────
        //  Input register resolution
        // ────────────────────────────────────────────────

        private static readonly string[] BoolOpInputNames = { "signal_in1", "signal_in2", "set_output" };
        private static readonly string[] UnaryInputNames = { "signal_in" };
        private static readonly string[] MemoryInputNames = { "signal_in", "signal_store", "lock_state" };
        private static readonly string[] SignalCheckInputNames = { "signal_in", "set_output", "set_targetsignal" };
        private static readonly string[] RelayInputNames = { "toggle", "set_state", "signal_in", "signal_in1", "signal_in2", "signal_in3", "signal_in4", "signal_in5" };
        private static readonly string[] DelayInputNames = { "signal_in", "set_delay" };

        private static string[] GetInputConnectionNames(SignalNodeType type)
        {
            switch (type)
            {
                case SignalNodeType.BoolOp_And:
                case SignalNodeType.BoolOp_Or:
                case SignalNodeType.Compare_Equals:
                case SignalNodeType.Compare_Greater:
                case SignalNodeType.Arith_Add:
                case SignalNodeType.Arith_Sub:
                case SignalNodeType.Arith_Mul:
                case SignalNodeType.Arith_Div:
                    return BoolOpInputNames;

                case SignalNodeType.Logic_Not:
                case SignalNodeType.Math_Round:
                    return UnaryInputNames;

                case SignalNodeType.Logic_Memory:
                    return MemoryInputNames;

                case SignalNodeType.Compare_SignalCheck:
                    return SignalCheckInputNames;

                case SignalNodeType.Logic_Relay:
                    return RelayInputNames;

                case SignalNodeType.Logic_Delay:
                    return DelayInputNames;

                default:
                    return UnaryInputNames;
            }
        }

        private static readonly string[] SingleOutputNames = { "signal_out" };
        private static readonly string[] RelayOutputNames = { "signal_out", "signal_out1", "signal_out2", "signal_out3", "signal_out4", "signal_out5" };

        private static string[] GetOutputConnectionNames(SignalNodeType type)
        {
            if (type == SignalNodeType.Logic_Relay)
                return RelayOutputNames;
            return SingleOutputNames;
        }

        private static int[] ResolveInputRegisters(
            Item item, SignalNodeType type,
            Dictionary<ushort, (Item, ItemComponent, SignalNodeType)> candidates,
            Dictionary<(ushort, string), int> regMap,
            List<CaptureEdge> captureEdges,
            Dictionary<(ushort, string), int> captureInputMap,
            ref int nextReg,
            Dictionary<int, Connection> regSourceConn)
        {
            var inputNames = GetInputConnectionNames(type);
            var result = new int[inputNames.Length];

            for (int i = 0; i < inputNames.Length; i++)
            {
                result[i] = -1; // default: unconnected

                // Find this input connection on the item
                Connection inputConn = null;
                foreach (var conn in item.Connections)
                {
                    if (!conn.IsOutput && conn.Name == inputNames[i])
                    {
                        inputConn = conn;
                        break;
                    }
                }
                if (inputConn == null) continue;

                // Trace backwards through wires to find the source
                foreach (var wire in inputConn.Wires)
                {
                    var otherConn = wire.OtherConnection(inputConn);
                    if (otherConn == null) continue;

                    var sourceItem = otherConn.Item;
                    var key = (sourceItem.ID, otherConn.Name);

                    if (regMap.TryGetValue(key, out int reg))
                    {
                        // Source is an accelerated item — direct register link
                        result[i] = reg;
                    }
                    else
                    {
                        // Source is NOT accelerated — create a capture edge
                        int captureReg = nextReg++;
                        regMap[key] = captureReg;
                        regSourceConn[captureReg] = otherConn;
                        captureEdges.Add(new CaptureEdge
                        {
                            SourceConnection = otherConn,
                            TargetRegister = captureReg
                        });
                        // Register push-based capture: ReceiveSignalPrefix will write to this reg
                        captureInputMap[(item.ID, inputNames[i])] = captureReg;
                        result[i] = captureReg;
                    }
                }

                // CB boundary input tracing: if wire tracing didn't find a source,
                // check if this item is inside a CircuitBox and receives input
                // through a CB boundary (CircuitBoxInputConnection, no Wire object).
                if (result[i] < 0 && _cbReflectReady)
                {
                    var cbOwner = item.ParentInventory?.Owner as Item;
                    var cb = cbOwner?.GetComponent<CircuitBox>();
                    if (cb != null)
                    {
                        // Iterate CB inputs to find one that routes to this inputConn
                        var cbInputs = _cbInputsField.GetValue(cb) as IEnumerable;
                        if (cbInputs == null)
                        {
                            DiagLog.Write($"CB boundary: cbInputs is null for CB Item#{cbOwner.ID}");
                        }
                        else
                        {
                            int cbInputIdx = 0;
                            foreach (var cbInput in cbInputs)
                            {
                                var extConnectedTo = _cbExtConnectedTo.GetValue(cbInput) as IList;
                                if (extConnectedTo == null)
                                {
                                    DiagLog.Write($"CB boundary: extConnectedTo is null for cbInput#{cbInputIdx}");
                                    cbInputIdx++;
                                    continue;
                                }

                                bool found = false;
                                foreach (var target in extConnectedTo)
                                {
                                    // target is a CircuitBoxNodeConnection;
                                    // its .Connection is the internal item's Connection
                                    var internalConn = GetCBConnection(target);
                                    if (internalConn != inputConn) continue;

                                    // Found the boundary! cbInput.Connection is the CB's external input Connection
                                    var extConn = GetCBConnection(cbInput);
                                    if (extConn == null)
                                    {
                                        DiagLog.Write($"CB boundary: extConn is null for matched cbInput");
                                        break;
                                    }

                                    DiagLog.Write($"CB boundary MATCH: Item#{item.ID} [{inputNames[i]}] ← CB#{cbOwner.ID} ext=[{extConn.Name}] wires={extConn.Wires.Count}");

                                    // Trace external wires from the CB's input connection
                                    foreach (var wire in extConn.Wires)
                                    {
                                        var otherConn = wire.OtherConnection(extConn);
                                        if (otherConn == null) continue;

                                        var extKey = (otherConn.Item.ID, otherConn.Name);
                                        if (regMap.TryGetValue(extKey, out int existingReg))
                                        {
                                            result[i] = existingReg;
                                            DiagLog.Write($"CB boundary: direct reg link → reg[{existingReg}] (source=Item#{otherConn.Item.ID} [{otherConn.Name}])");
                                        }
                                        else
                                        {
                                            // External source not accelerated → capture edge
                                            int captureReg = nextReg++;
                                            regMap[extKey] = captureReg;
                                            regSourceConn[captureReg] = otherConn;
                                            captureEdges.Add(new CaptureEdge
                                            {
                                                SourceConnection = otherConn,
                                                TargetRegister = captureReg
                                            });
                                            // Register push-based capture for CB boundary input
                                            captureInputMap[(item.ID, inputNames[i])] = captureReg;
                                            result[i] = captureReg;
                                            DiagLog.Write($"CB boundary: capture edge → reg[{captureReg}] (source=Item#{otherConn.Item.ID} [{otherConn.Name}], captureKey=({item.ID},{inputNames[i]}))");
                                        }
                                        break;
                                    }
                                    found = true;
                                    break;
                                }
                                if (found) break;
                                cbInputIdx++;
                            }

                            if (result[i] < 0)
                                DiagLog.Write($"CB boundary: NO MATCH found for Item#{item.ID} [{inputNames[i]}] in CB#{cbOwner.ID}");
                        }
                    }
                }
            }

            return result;
        }

        private static int[] ResolveOutputRegisters(
            Item item, SignalNodeType type,
            Dictionary<(ushort, string), int> regMap)
        {
            var outputNames = GetOutputConnectionNames(type);
            var result = new int[outputNames.Length];

            for (int i = 0; i < outputNames.Length; i++)
            {
                var key = (item.ID, outputNames[i]);
                if (regMap.TryGetValue(key, out int reg))
                    result[i] = reg;
                else
                    result[i] = -1;
            }

            return result;
        }
    }
}
