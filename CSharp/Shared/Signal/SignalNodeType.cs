using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;

namespace ItemOptimizerMod.SignalGraph
{
    enum SignalNodeType : byte
    {
        // Boolean operators (poll-driven: record in ReceiveSignal, emit in Update)
        BoolOp_And,
        BoolOp_Or,

        // Unary (ReceiveSignal-driven: inverts immediately)
        Logic_Not,

        // Comparison (ReceiveSignal-driven: immediate output)
        Compare_SignalCheck,
        Compare_Equals,
        Compare_Greater,

        // Arithmetic (poll-driven: record inputs, emit in Update)
        Arith_Add,
        Arith_Sub,
        Arith_Mul,
        Arith_Div,

        // State
        Logic_Memory,

        // Math (ReceiveSignal-driven: immediate transform)
        Math_Round,

        // ── Aggressive mode (mode=2) types ──

        // Relay: control (toggle/set_state) + 6-channel conditional passthrough, partial-only (Update still runs for power)
        Logic_Relay,

        // Delay: frame-based signal queue, fully compiled (Update skipped)
        Logic_Delay,
    }

    struct SignalNode
    {
        public SignalNodeType Type;
        public ushort ItemId;              // item.ID for back-ref / skip lookup
        public Item Item;                  // back-ref for IO bridge
        public ItemComponent Component;    // back-ref for state init

        /// <summary>Input register indices. -1 = unconnected.</summary>
        public int[] InputRegs;

        /// <summary>Output register indices.</summary>
        public int[] OutputRegs;

        /// <summary>Index into per-type SOA state arrays.</summary>
        public int StateIndex;

        /// <summary>If true, signal is compiled into graph but item.Update() still runs (e.g. relay for power grid).</summary>
        public bool PartialOnly;
    }

    /// <summary>
    /// An external input edge: a non-accelerated item's output connection
    /// whose LastSentSignal.value is captured into a register each frame.
    /// </summary>
    struct CaptureEdge
    {
        public Connection SourceConnection;  // the upstream (non-accel) output connection
        public int TargetRegister;           // register index to write into
    }

    /// <summary>
    /// A back-edge in a cycle: carries last frame's value to break the cycle.
    /// </summary>
    struct BackEdge
    {
        public int SourceRegister;  // register to read at end of frame
        public int TargetRegister;  // register to write at start of next frame
    }

    /// <summary>
    /// An emit edge: an accelerated node's output register that feeds into
    /// a non-accelerated downstream item. The evaluator delivers the register
    /// value to the target Connection via SendSignalIntoConnection each frame.
    /// This is the reverse of CaptureEdge.
    /// </summary>
    struct EmitEdge
    {
        public int SourceRegister;          // register containing the output signal
        public Connection TargetConnection; // the non-accelerated item's input connection
        public Item SourceItem;             // for Signal.source (the accelerated item)
    }

    /// <summary>
    /// Result of graph compilation — immutable once built, used every frame.
    /// </summary>
    class CompiledGraph
    {
        public string[] Registers;
        public SignalNode[] Nodes;
        public int[] EvalOrder;            // indices into Nodes[], topological order
        public CaptureEdge[] CaptureEdges;
        public EmitEdge[] EmitEdges;       // outputs from accel nodes to non-accel items
        public BackEdge[] BackEdges;
        public string[] BackEdgeBuffer;    // previous frame values for back-edges

        /// <summary>Wire visuals: register index → (Connection, Wire[]) for CLIENT synthesis.</summary>
        public (Connection conn, Wire[] wires)[] RegisterWireMap;

        /// <summary>
        /// Push-based capture map: (accelerated item ID, input connection name) → capture register index.
        /// Used by ReceiveSignalPrefix to write intercepted signals directly to the register.
        /// </summary>
        public Dictionary<(ushort, string), int> CaptureInputMap;

        /// <summary>
        /// Indices of all capture registers, for efficient clearing after evaluation.
        /// </summary>
        public int[] CaptureRegisterIndices;

        public int NodeCount => Nodes.Length;
        public int RegisterCount => Registers.Length;
    }
}
