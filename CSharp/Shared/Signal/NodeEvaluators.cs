using System;
using System.Globalization;
using Barotrauma;
using Barotrauma.Items.Components;

namespace ItemOptimizerMod.SignalGraph
{
    /// <summary>
    /// SOA state arrays + static evaluation functions for each signal node type.
    /// No virtual dispatch — switch on SignalNodeType.
    /// </summary>
    static class NodeEvaluators
    {
        // ── BoolOp (And / Or) SOA ──
        private static float[] _bool_tsr0, _bool_tsr1;
        private static float[] _bool_timeFrame;
        private static string[] _bool_output, _bool_falseOutput;
        private static int _boolCount;

        // ── Not SOA ──
        private static bool[] _not_continuousOutput;
        private static int _notCount;

        // ── SignalCheck SOA ──
        private static string[] _sc_target, _sc_output, _sc_falseOutput;
        private static int _scCount;

        // ── Equals SOA ──
        private static string[] _eq_recv0, _eq_recv1;
        private static float[] _eq_tsr0, _eq_tsr1, _eq_timeFrame;
        private static string[] _eq_output, _eq_falseOutput;
        private static int _eqCount;

        // ── Greater SOA ──
        private static float[] _gt_val0, _gt_val1;
        private static float[] _gt_tsr0, _gt_tsr1, _gt_timeFrame;
        private static string[] _gt_output, _gt_falseOutput;
        private static int _gtCount;

        // ── Arithmetic SOA (Add/Sub/Mul/Div share layout) ──
        private static string[] _arith_recv0, _arith_recv1;
        private static float[] _arith_tsr0, _arith_tsr1, _arith_timeFrame;
        private static float[] _arith_clampMin, _arith_clampMax;
        private static int _arithCount;

        // ── Memory SOA ──
        private static string[] _mem_value;
        private static bool[] _mem_writable;
        private static int _memCount;

        // ── Round SOA ──
        private static int _roundCount;

        // ── Relay SOA (mode=2) ──
        private static RelayComponent[] _relay_comp;
        private static int _relayCount;

        // ── Delay SOA (mode=2) ──
        // Ring buffer: _delay_values[nodeIdx] = string[capacity], _delay_timers[nodeIdx] = int[capacity]
        private static string[][] _delay_values;
        private static int[][] _delay_timers;
        private static int[] _delay_ticks;         // delayTicks per node
        private static int[] _delay_capacity;       // queue capacity per node
        private static int[] _delay_head;            // read pointer (oldest entry)
        private static int[] _delay_count;           // current queue size
        private static bool[] _delay_resetOnRecv;
        private static bool[] _delay_resetOnDiff;
        private static string[] _delay_lastValue;    // last enqueued value (for resetOnDiff)
        private static int _delayCount;

        // ═══════════════════════════════════
        //  Initialize: allocate SOA arrays and assign StateIndex
        // ═══════════════════════════════════

        public static void Initialize(SignalNode[] nodes)
        {
            _boolCount = 0; _notCount = 0; _scCount = 0;
            _eqCount = 0; _gtCount = 0; _arithCount = 0;
            _memCount = 0; _roundCount = 0;
            _relayCount = 0; _delayCount = 0;

            // Count per type
            foreach (ref var n in nodes.AsSpan())
            {
                switch (n.Type)
                {
                    case SignalNodeType.BoolOp_And:
                    case SignalNodeType.BoolOp_Or:
                        n.StateIndex = _boolCount++;
                        break;
                    case SignalNodeType.Logic_Not:
                        n.StateIndex = _notCount++;
                        break;
                    case SignalNodeType.Compare_SignalCheck:
                        n.StateIndex = _scCount++;
                        break;
                    case SignalNodeType.Compare_Equals:
                        n.StateIndex = _eqCount++;
                        break;
                    case SignalNodeType.Compare_Greater:
                        n.StateIndex = _gtCount++;
                        break;
                    case SignalNodeType.Arith_Add:
                    case SignalNodeType.Arith_Sub:
                    case SignalNodeType.Arith_Mul:
                    case SignalNodeType.Arith_Div:
                        n.StateIndex = _arithCount++;
                        break;
                    case SignalNodeType.Logic_Memory:
                        n.StateIndex = _memCount++;
                        break;
                    case SignalNodeType.Math_Round:
                        n.StateIndex = _roundCount++;
                        break;
                    case SignalNodeType.Logic_Relay:
                        n.StateIndex = _relayCount++;
                        break;
                    case SignalNodeType.Logic_Delay:
                        n.StateIndex = _delayCount++;
                        break;
                }
            }

            // Allocate
            _bool_tsr0 = new float[_boolCount];
            _bool_tsr1 = new float[_boolCount];
            _bool_timeFrame = new float[_boolCount];
            _bool_output = new string[_boolCount];
            _bool_falseOutput = new string[_boolCount];

            _not_continuousOutput = new bool[_notCount];

            _sc_target = new string[_scCount];
            _sc_output = new string[_scCount];
            _sc_falseOutput = new string[_scCount];

            _eq_recv0 = new string[_eqCount];
            _eq_recv1 = new string[_eqCount];
            _eq_tsr0 = new float[_eqCount];
            _eq_tsr1 = new float[_eqCount];
            _eq_timeFrame = new float[_eqCount];
            _eq_output = new string[_eqCount];
            _eq_falseOutput = new string[_eqCount];

            _gt_val0 = new float[_gtCount];
            _gt_val1 = new float[_gtCount];
            _gt_tsr0 = new float[_gtCount];
            _gt_tsr1 = new float[_gtCount];
            _gt_timeFrame = new float[_gtCount];
            _gt_output = new string[_gtCount];
            _gt_falseOutput = new string[_gtCount];

            _arith_recv0 = new string[_arithCount];
            _arith_recv1 = new string[_arithCount];
            _arith_tsr0 = new float[_arithCount];
            _arith_tsr1 = new float[_arithCount];
            _arith_timeFrame = new float[_arithCount];
            _arith_clampMin = new float[_arithCount];
            _arith_clampMax = new float[_arithCount];

            _mem_value = new string[_memCount];
            _mem_writable = new bool[_memCount];

            _relay_comp = new RelayComponent[_relayCount];

            _delay_values = new string[_delayCount][];
            _delay_timers = new int[_delayCount][];
            _delay_ticks = new int[_delayCount];
            _delay_capacity = new int[_delayCount];
            _delay_head = new int[_delayCount];
            _delay_count = new int[_delayCount];
            _delay_resetOnRecv = new bool[_delayCount];
            _delay_resetOnDiff = new bool[_delayCount];
            _delay_lastValue = new string[_delayCount];

            // Sync initial state from vanilla components
            foreach (ref var n in nodes.AsSpan())
                SyncFromComponent(ref n);
        }

        /// <summary>Read initial state from the vanilla component into SOA arrays.</summary>
        private static void SyncFromComponent(ref SignalNode node)
        {
            int si = node.StateIndex;
            switch (node.Type)
            {
                case SignalNodeType.BoolOp_And:
                case SignalNodeType.BoolOp_Or:
                    if (node.Component is BooleanOperatorComponent boc)
                    {
                        _bool_timeFrame[si] = boc.TimeFrame;
                        _bool_output[si] = boc.Output ?? "1";
                        _bool_falseOutput[si] = boc.FalseOutput ?? "0";
                        _bool_tsr0[si] = Math.Max(boc.TimeFrame * 2f, 0.1f);
                        _bool_tsr1[si] = Math.Max(boc.TimeFrame * 2f, 0.1f);
                    }
                    break;

                case SignalNodeType.Logic_Not:
                    if (node.Component is NotComponent nc)
                    {
                        _not_continuousOutput[si] = nc.ContinuousOutput;
                    }
                    break;

                case SignalNodeType.Compare_SignalCheck:
                    if (node.Component is SignalCheckComponent scc)
                    {
                        _sc_target[si] = scc.TargetSignal ?? "";
                        _sc_output[si] = scc.Output ?? "1";
                        _sc_falseOutput[si] = scc.FalseOutput ?? "0";
                    }
                    break;

                case SignalNodeType.Compare_Equals:
                    if (node.Component is EqualsComponent ec)
                    {
                        _eq_timeFrame[si] = ec.TimeFrame;
                        _eq_output[si] = ec.Output ?? "1";
                        _eq_falseOutput[si] = ec.FalseOutput ?? "0";
                        _eq_tsr0[si] = Math.Max(ec.TimeFrame * 2f, 0.1f);
                        _eq_tsr1[si] = Math.Max(ec.TimeFrame * 2f, 0.1f);
                    }
                    break;

                case SignalNodeType.Compare_Greater:
                    if (node.Component is GreaterComponent gc)
                    {
                        _gt_timeFrame[si] = gc.TimeFrame;
                        _gt_output[si] = gc.Output ?? "1";
                        _gt_falseOutput[si] = gc.FalseOutput ?? "0";
                        _gt_tsr0[si] = Math.Max(gc.TimeFrame * 2f, 0.1f);
                        _gt_tsr1[si] = Math.Max(gc.TimeFrame * 2f, 0.1f);
                    }
                    break;

                case SignalNodeType.Arith_Add:
                case SignalNodeType.Arith_Sub:
                case SignalNodeType.Arith_Mul:
                case SignalNodeType.Arith_Div:
                    if (node.Component is ArithmeticComponent ac)
                    {
                        _arith_timeFrame[si] = ac.TimeFrame;
                        _arith_clampMin[si] = ac.ClampMin;
                        _arith_clampMax[si] = ac.ClampMax;
                        _arith_tsr0[si] = Math.Max(ac.TimeFrame * 2f, 0.1f);
                        _arith_tsr1[si] = Math.Max(ac.TimeFrame * 2f, 0.1f);
                    }
                    break;

                case SignalNodeType.Logic_Memory:
                    if (node.Component is MemoryComponent mc)
                    {
                        _mem_value[si] = mc.Value ?? "";
                        _mem_writable[si] = mc.Writeable;
                    }
                    break;

                case SignalNodeType.Logic_Relay:
                    if (node.Component is RelayComponent rc)
                    {
                        _relay_comp[si] = rc;
                    }
                    break;

                case SignalNodeType.Logic_Delay:
                    if (node.Component is DelayComponent dc)
                    {
                        int ticks = (int)(dc.Delay / Timing.Step);
                        int capacity = Math.Max(ticks, 1) * 2;
                        _delay_ticks[si] = ticks;
                        _delay_capacity[si] = capacity;
                        _delay_values[si] = new string[capacity];
                        _delay_timers[si] = new int[capacity];
                        _delay_head[si] = 0;
                        _delay_count[si] = 0;
                        _delay_resetOnRecv[si] = dc.ResetWhenSignalReceived;
                        _delay_resetOnDiff[si] = dc.ResetWhenDifferentSignalReceived;
                        _delay_lastValue[si] = null;
                    }
                    break;
            }
        }

        // ═══════════════════════════════════
        //  Evaluate: main dispatch (no virtual calls)
        // ═══════════════════════════════════

        public static void Evaluate(ref SignalNode node, string[] regs, float dt)
        {
            switch (node.Type)
            {
                case SignalNodeType.BoolOp_And:
                    EvalBoolOp(node.StateIndex, node.InputRegs, node.OutputRegs, regs, dt, requiredInputs: 2);
                    break;
                case SignalNodeType.BoolOp_Or:
                    EvalBoolOp(node.StateIndex, node.InputRegs, node.OutputRegs, regs, dt, requiredInputs: 1);
                    break;
                case SignalNodeType.Logic_Not:
                    EvalNot(node.StateIndex, node.InputRegs, node.OutputRegs, regs);
                    break;
                case SignalNodeType.Compare_SignalCheck:
                    EvalSignalCheck(node.StateIndex, node.InputRegs, node.OutputRegs, regs);
                    break;
                case SignalNodeType.Compare_Equals:
                    EvalEquals(node.StateIndex, node.InputRegs, node.OutputRegs, regs, dt);
                    break;
                case SignalNodeType.Compare_Greater:
                    EvalGreater(node.StateIndex, node.InputRegs, node.OutputRegs, regs, dt);
                    break;
                case SignalNodeType.Arith_Add:
                    EvalArith(node.Type, node.StateIndex, node.InputRegs, node.OutputRegs, regs, dt);
                    break;
                case SignalNodeType.Arith_Sub:
                    EvalArith(node.Type, node.StateIndex, node.InputRegs, node.OutputRegs, regs, dt);
                    break;
                case SignalNodeType.Arith_Mul:
                    EvalArith(node.Type, node.StateIndex, node.InputRegs, node.OutputRegs, regs, dt);
                    break;
                case SignalNodeType.Arith_Div:
                    EvalArith(node.Type, node.StateIndex, node.InputRegs, node.OutputRegs, regs, dt);
                    break;
                case SignalNodeType.Logic_Memory:
                    EvalMemory(node.StateIndex, node.InputRegs, node.OutputRegs, regs);
                    break;
                case SignalNodeType.Math_Round:
                    EvalRound(node.InputRegs, node.OutputRegs, regs);
                    break;
                case SignalNodeType.Logic_Relay:
                    EvalRelay(node.StateIndex, node.InputRegs, node.OutputRegs, regs);
                    break;
                case SignalNodeType.Logic_Delay:
                    EvalDelay(node.StateIndex, node.InputRegs, node.OutputRegs, regs);
                    break;
            }
        }

        // ═══════════════════════════════════
        //  Per-type evaluators
        // ═══════════════════════════════════

        private static void EvalBoolOp(int si, int[] inRegs, int[] outRegs, string[] regs, float dt, int requiredInputs)
        {
            string in0 = inRegs[0] >= 0 ? regs[inRegs[0]] : null;
            string in1 = inRegs[1] >= 0 ? regs[inRegs[1]] : null;

            // set_output: dynamically overrides the Output property (vanilla ReceiveSignal behavior)
            if (inRegs.Length > 2 && inRegs[2] >= 0)
            {
                string setOut = regs[inRegs[2]];
                if (setOut != null)
                    _bool_output[si] = setOut;
            }

            if (in0 != null && in0 != "0")
                _bool_tsr0[si] = 0f;
            else
                _bool_tsr0[si] += dt;

            if (in1 != null && in1 != "0")
                _bool_tsr1[si] = 0f;
            else
                _bool_tsr1[si] += dt;

            float tf = _bool_timeFrame[si];
            int received = (_bool_tsr0[si] <= tf ? 1 : 0) + (_bool_tsr1[si] <= tf ? 1 : 0);

            string output = received >= requiredInputs ? _bool_output[si] : _bool_falseOutput[si];
            if (outRegs[0] >= 0)
                regs[outRegs[0]] = string.IsNullOrEmpty(output) ? null : output;
        }

        private static void EvalNot(int si, int[] inRegs, int[] outRegs, string[] regs)
        {
            string input = inRegs[0] >= 0 ? regs[inRegs[0]] : null;

            if (input != null)
            {
                // Inversion: "0" or empty → "1", anything else → "0"
                if (outRegs[0] >= 0)
                    regs[outRegs[0]] = (input == "0" || input.Length == 0) ? "1" : "0";
            }
            else if (_not_continuousOutput[si])
            {
                // No signal → output "1"
                if (outRegs[0] >= 0)
                    regs[outRegs[0]] = "1";
            }
            else
            {
                if (outRegs[0] >= 0)
                    regs[outRegs[0]] = null;
            }
        }

        private static void EvalSignalCheck(int si, int[] inRegs, int[] outRegs, string[] regs)
        {
            // Handle control inputs (set_output, set_targetsignal)
            if (inRegs.Length > 1 && inRegs[1] >= 0 && regs[inRegs[1]] != null)
                _sc_output[si] = regs[inRegs[1]];
            if (inRegs.Length > 2 && inRegs[2] >= 0 && regs[inRegs[2]] != null)
                _sc_target[si] = regs[inRegs[2]];

            string input = inRegs[0] >= 0 ? regs[inRegs[0]] : null;
            if (input == null)
            {
                if (outRegs[0] >= 0) regs[outRegs[0]] = null;
                return;
            }

            bool match = input == _sc_target[si];
            string output = match ? _sc_output[si] : _sc_falseOutput[si];
            if (outRegs[0] >= 0)
                regs[outRegs[0]] = string.IsNullOrEmpty(output) ? null : output;
        }

        private static void EvalEquals(int si, int[] inRegs, int[] outRegs, string[] regs, float dt)
        {
            string in0 = inRegs[0] >= 0 ? regs[inRegs[0]] : null;
            string in1 = inRegs[1] >= 0 ? regs[inRegs[1]] : null;

            // set_output: dynamically overrides Output property
            if (inRegs.Length > 2 && inRegs[2] >= 0)
            {
                string setOut = regs[inRegs[2]];
                if (setOut != null)
                    _eq_output[si] = setOut;
            }

            // Unlike BoolOp, "0" IS a valid signal for equality comparison.
            // Vanilla EqualsComponent resets timeSinceReceived on ANY signal value.
            if (in0 != null) { _eq_recv0[si] = in0; _eq_tsr0[si] = 0f; }
            else _eq_tsr0[si] += dt;

            if (in1 != null) { _eq_recv1[si] = in1; _eq_tsr1[si] = 0f; }
            else _eq_tsr1[si] += dt;

            float tf = _eq_timeFrame[si];
            int received = (_eq_tsr0[si] <= tf ? 1 : 0) + (_eq_tsr1[si] <= tf ? 1 : 0);

            bool state = received >= 2 && _eq_recv0[si] == _eq_recv1[si];
            string output = state ? _eq_output[si] : _eq_falseOutput[si];
            if (outRegs[0] >= 0)
                regs[outRegs[0]] = string.IsNullOrEmpty(output) ? null : output;
        }

        private static void EvalGreater(int si, int[] inRegs, int[] outRegs, string[] regs, float dt)
        {
            string in0 = inRegs[0] >= 0 ? regs[inRegs[0]] : null;
            string in1 = inRegs[1] >= 0 ? regs[inRegs[1]] : null;

            // set_output: dynamically overrides Output property
            if (inRegs.Length > 2 && inRegs[2] >= 0)
            {
                string setOut = regs[inRegs[2]];
                if (setOut != null)
                    _gt_output[si] = setOut;
            }

            // Unlike BoolOp, "0" IS a valid numerical input for comparison.
            // Vanilla GreaterComponent resets timeSinceReceived on ANY signal value.
            if (in0 != null)
            {
                float.TryParse(in0, NumberStyles.Any, CultureInfo.InvariantCulture, out _gt_val0[si]);
                _gt_tsr0[si] = 0f;
            }
            else _gt_tsr0[si] += dt;

            if (in1 != null)
            {
                float.TryParse(in1, NumberStyles.Any, CultureInfo.InvariantCulture, out _gt_val1[si]);
                _gt_tsr1[si] = 0f;
            }
            else _gt_tsr1[si] += dt;

            float tf = _gt_timeFrame[si];
            int received = (_gt_tsr0[si] <= tf ? 1 : 0) + (_gt_tsr1[si] <= tf ? 1 : 0);

            bool state = received >= 2 && _gt_val0[si] > _gt_val1[si];
            string output = state ? _gt_output[si] : _gt_falseOutput[si];
            if (outRegs[0] >= 0)
                regs[outRegs[0]] = string.IsNullOrEmpty(output) ? null : output;
        }

        private static void EvalArith(SignalNodeType type, int si, int[] inRegs, int[] outRegs, string[] regs, float dt)
        {
            string in0 = inRegs[0] >= 0 ? regs[inRegs[0]] : null;
            string in1 = inRegs[1] >= 0 ? regs[inRegs[1]] : null;

            // set_output: Arith components don't use output/falseOutput the same way,
            // but the connection exists — just capture it silently for now.

            // Unlike BoolOp, "0" IS a valid numerical input for arithmetic.
            // Vanilla ArithmeticComponent resets timeSinceReceived on ANY signal value.
            if (in0 != null) { _arith_recv0[si] = in0; _arith_tsr0[si] = 0f; }
            else _arith_tsr0[si] += dt;

            if (in1 != null) { _arith_recv1[si] = in1; _arith_tsr1[si] = 0f; }
            else _arith_tsr1[si] += dt;

            float tf = _arith_timeFrame[si];
            int received = (_arith_tsr0[si] <= tf ? 1 : 0) + (_arith_tsr1[si] <= tf ? 1 : 0);

            if (received < 2)
            {
                if (outRegs[0] >= 0) regs[outRegs[0]] = null;
                return;
            }

            if (!float.TryParse(_arith_recv0[si], NumberStyles.Any, CultureInfo.InvariantCulture, out float a)) a = 0f;
            if (!float.TryParse(_arith_recv1[si], NumberStyles.Any, CultureInfo.InvariantCulture, out float b)) b = 0f;

            float result = type switch
            {
                SignalNodeType.Arith_Add => a + b,
                SignalNodeType.Arith_Sub => a - b,
                SignalNodeType.Arith_Mul => a * b,
                SignalNodeType.Arith_Div => b != 0f ? a / b : 0f,
                _ => 0f
            };

            float cMin = _arith_clampMin[si];
            float cMax = _arith_clampMax[si];
            if (cMax > cMin)
                result = Math.Clamp(result, cMin, cMax);

            if (outRegs[0] >= 0)
                regs[outRegs[0]] = result.ToString("G", CultureInfo.InvariantCulture);
        }

        private static void EvalMemory(int si, int[] inRegs, int[] outRegs, string[] regs)
        {
            // signal_store / lock_state (input index 1 or 2)
            if (inRegs.Length > 1 && inRegs[1] >= 0 && regs[inRegs[1]] != null)
                _mem_writable[si] = regs[inRegs[1]] == "1";
            if (inRegs.Length > 2 && inRegs[2] >= 0 && regs[inRegs[2]] != null)
                _mem_writable[si] = regs[inRegs[2]] == "1";

            // signal_in write
            if (_mem_writable[si] && inRegs[0] >= 0 && regs[inRegs[0]] != null)
                _mem_value[si] = regs[inRegs[0]];

            // Output every frame
            if (outRegs[0] >= 0)
                regs[outRegs[0]] = _mem_value[si];
        }

        private static void EvalRound(int[] inRegs, int[] outRegs, string[] regs)
        {
            string input = inRegs[0] >= 0 ? regs[inRegs[0]] : null;
            if (input == null)
            {
                if (outRegs[0] >= 0) regs[outRegs[0]] = null;
                return;
            }

            if (float.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out float val))
            {
                float rounded = MathF.Round(val);
                if (rounded == -0f) rounded = 0f;
                if (outRegs[0] >= 0)
                    regs[outRegs[0]] = rounded.ToString("G", CultureInfo.InvariantCulture);
            }
            else
            {
                if (outRegs[0] >= 0) regs[outRegs[0]] = input;
            }
        }

        /// <summary>
        /// Relay: 6-channel conditional passthrough + control update.
        /// InputRegs: [toggle, set_state, signal_in, signal_in1..5]
        /// OutputRegs: [signal_out, signal_out1..5]
        ///
        /// IMPORTANT: Passthrough runs BEFORE control (toggle/set_state) updates IsOn.
        /// In vanilla, Delay.Update() sends signals that immediately reach Relay.ReceiveSignal
        /// for signal_in*, using the PREVIOUS frame's IsOn. Or.Update() runs later and updates
        /// set_state → IsOn for the NEXT frame. This one-frame lag is load-bearing for circuits
        /// like double-click detectors. We preserve it by evaluating passthrough first.
        /// </summary>
        private static void EvalRelay(int si, int[] inRegs, int[] outRegs, string[] regs)
        {
            // ── 1. Passthrough FIRST using previous frame's IsOn ──
            // In vanilla, Delay.Update() → SendSignal → Relay.ReceiveSignal(signal_in1)
            // uses IsOn from the previous frame (Or hasn't sent set_state yet).
            // We must preserve this one-frame lag for circuits that depend on it
            // (e.g. double-click detectors where set_state and signal_in arrive together).
            bool isOn = _relay_comp[si].IsOn;

            // inRegs[2..7] = signal_in, signal_in1..5
            // outRegs[0..5] = signal_out, signal_out1..5
            int channels = Math.Min(outRegs.Length, 6);
            for (int ch = 0; ch < channels; ch++)
            {
                if (outRegs[ch] < 0) continue;
                int inIdx = ch + 2; // offset past toggle/set_state
                if (isOn && inIdx < inRegs.Length && inRegs[inIdx] >= 0)
                    regs[outRegs[ch]] = regs[inRegs[inIdx]];
                else
                    regs[outRegs[ch]] = null;
            }

            // ── 2. THEN update IsOn for next frame ──
            // inRegs[0] = toggle, inRegs[1] = set_state
            string toggle = inRegs[0] >= 0 ? regs[inRegs[0]] : null;
            string setState = inRegs[1] >= 0 ? regs[inRegs[1]] : null;

            if (toggle != null && toggle != "0")
                _relay_comp[si].IsOn = !_relay_comp[si].IsOn;
            if (setState != null)
                _relay_comp[si].IsOn = setState != "0";
        }

        /// <summary>
        /// Delay: frame-based signal queue. Replicates vanilla DelayComponent logic.
        /// InputRegs: [signal_in, set_delay]
        /// OutputRegs: [signal_out]
        /// Ring buffer: _delay_values/timers[si][0..capacity-1], head = oldest entry.
        /// Each entry has a timer (frames until output) and the signal value.
        /// </summary>
        private static void EvalDelay(int si, int[] inRegs, int[] outRegs, string[] regs)
        {
            // Handle set_delay input (index 1)
            if (inRegs.Length > 1 && inRegs[1] >= 0)
            {
                string setDelay = regs[inRegs[1]];
                if (setDelay != null &&
                    float.TryParse(setDelay, NumberStyles.Any, CultureInfo.InvariantCulture, out float newDelay))
                {
                    newDelay = Math.Clamp(newDelay, 0f, 60f);
                    int newTicks = (int)(newDelay / Timing.Step);
                    if (newTicks != _delay_ticks[si])
                    {
                        _delay_ticks[si] = newTicks;
                        int newCap = Math.Max(newTicks, 1) * 2;
                        _delay_capacity[si] = newCap;
                        _delay_values[si] = new string[newCap];
                        _delay_timers[si] = new int[newCap];
                        _delay_head[si] = 0;
                        _delay_count[si] = 0;
                        _delay_lastValue[si] = null;
                    }
                }
            }

            // Enqueue new signal
            string signalIn = inRegs[0] >= 0 ? regs[inRegs[0]] : null;
            if (signalIn != null)
            {
                int count = _delay_count[si];
                int cap = _delay_capacity[si];

                if (_delay_resetOnRecv[si])
                {
                    _delay_head[si] = 0;
                    _delay_count[si] = 0;
                    count = 0;
                    _delay_lastValue[si] = null;
                }

                if (_delay_resetOnDiff[si] && count > 0)
                {
                    // Check if incoming signal differs from the oldest queued value
                    int headIdx = _delay_head[si];
                    if (_delay_values[si][headIdx] != signalIn)
                    {
                        _delay_head[si] = 0;
                        _delay_count[si] = 0;
                        count = 0;
                        _delay_lastValue[si] = null;
                    }
                }

                if (count < cap)
                {
                    int writeIdx = (_delay_head[si] + count) % cap;
                    _delay_values[si][writeIdx] = signalIn;
                    _delay_timers[si][writeIdx] = _delay_ticks[si];
                    _delay_count[si] = count + 1;
                    _delay_lastValue[si] = signalIn;
                }
            }

            // Decrement timers and dequeue expired signals
            string output = null;
            int qCount = _delay_count[si];
            int head = _delay_head[si];
            int capacity = _delay_capacity[si];

            for (int i = 0; i < qCount; i++)
            {
                int idx = (head + i) % capacity;
                _delay_timers[si][idx]--;
            }

            // Dequeue from head while timer <= 0
            while (_delay_count[si] > 0)
            {
                int headIdx = _delay_head[si];
                if (_delay_timers[si][headIdx] <= 0)
                {
                    output = _delay_values[si][headIdx];
                    _delay_values[si][headIdx] = null;
                    _delay_head[si] = (headIdx + 1) % capacity;
                    _delay_count[si]--;
                }
                else
                {
                    break;
                }
            }

            if (outRegs[0] >= 0)
                regs[outRegs[0]] = output;
        }

        /// <summary>Reset all SOA arrays.</summary>
        public static void Reset()
        {
            _bool_tsr0 = _bool_tsr1 = _bool_timeFrame = null;
            _bool_output = _bool_falseOutput = null;
            _not_continuousOutput = null;
            _sc_target = _sc_output = _sc_falseOutput = null;
            _eq_recv0 = _eq_recv1 = _eq_output = _eq_falseOutput = null;
            _eq_tsr0 = _eq_tsr1 = _eq_timeFrame = null;
            _gt_val0 = _gt_val1 = _gt_tsr0 = _gt_tsr1 = _gt_timeFrame = null;
            _gt_output = _gt_falseOutput = null;
            _arith_recv0 = _arith_recv1 = null;
            _arith_tsr0 = _arith_tsr1 = _arith_timeFrame = null;
            _arith_clampMin = _arith_clampMax = null;
            _mem_value = null; _mem_writable = null;
            _relay_comp = null;
            _delay_values = null; _delay_timers = null;
            _delay_ticks = null; _delay_capacity = null;
            _delay_head = null; _delay_count = null;
            _delay_resetOnRecv = null; _delay_resetOnDiff = null;
            _delay_lastValue = null;
            _boolCount = _notCount = _scCount = _eqCount = _gtCount = _arithCount = _memCount = _roundCount = 0;
            _relayCount = 0; _delayCount = 0;
        }

        // ═══════════════════════════════════
        //  Diagnostics
        // ═══════════════════════════════════

        internal struct BoolOpDiag
        {
            public float tsr0, tsr1, timeFrame;
            public string output, falseOutput;
            public bool valid;
        }

        internal static BoolOpDiag GetBoolOpDiag(int stateIdx)
        {
            if (_bool_output == null || stateIdx < 0 || stateIdx >= _boolCount)
                return default;
            return new BoolOpDiag
            {
                tsr0 = _bool_tsr0[stateIdx],
                tsr1 = _bool_tsr1[stateIdx],
                timeFrame = _bool_timeFrame[stateIdx],
                output = _bool_output[stateIdx],
                falseOutput = _bool_falseOutput[stateIdx],
                valid = true
            };
        }
    }
}
