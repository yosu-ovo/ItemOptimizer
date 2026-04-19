using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.SignalGraph
{
    /// <summary>
    /// Harmony patches for the signal graph accelerator:
    /// A. Suppress SendSignalIntoConnection for accelerated items (push-capture + block)
    /// B. Detect wire connect/disconnect to trigger graph recompilation
    /// </summary>
    static class SignalGraphPatches
    {
        private static bool _registered;

        // Cached originals for unpatching
        private static MethodInfo _sendSignalIntoConnOriginal;
        private static MethodInfo _wireConnectOriginal;
        private static MethodInfo _wireTryConnectOriginal;
        private static MethodInfo _wireRemoveConnItem;
        private static MethodInfo _wireRemoveConnConn;
        private static MethodInfo _wireClearConns;
        private static MethodInfo _connDisconnectWire;

        internal static void Register(Harmony harmony)
        {
            if (_registered) return;

            // ── Patch A: SendSignalIntoConnection interception ──
            // Patching the static method Connection.SendSignalIntoConnection instead of
            // virtual ItemComponent.ReceiveSignal. The old approach failed because Harmony
            // prefixes on a base virtual method do NOT intercept overridden methods in
            // subclasses (DelayComponent, OrComponent, etc. all override ReceiveSignal).
            // SendSignalIntoConnection is the single entry point for all signal delivery,
            // so patching it captures ALL signals regardless of component type.
            _sendSignalIntoConnOriginal = AccessTools.Method(
                typeof(Connection), nameof(Connection.SendSignalIntoConnection),
                new[] { typeof(Signal), typeof(Connection) });

            if (_sendSignalIntoConnOriginal != null)
            {
                harmony.Patch(_sendSignalIntoConnOriginal,
                    prefix: new HarmonyMethod(typeof(SignalGraphPatches), nameof(SendSignalIntoConnectionPrefix))
                        { priority = Priority.First });
            }

            // ── Patch B: Wire change detection ──
            _wireConnectOriginal = AccessTools.Method(typeof(Wire), nameof(Wire.Connect));
            _wireTryConnectOriginal = AccessTools.Method(typeof(Wire), "TryConnect");
            _wireRemoveConnItem = AccessTools.Method(typeof(Wire), "RemoveConnection", new[] { typeof(Item) });
            _wireRemoveConnConn = AccessTools.Method(typeof(Wire), "RemoveConnection", new[] { typeof(Connection) });
            _wireClearConns = AccessTools.Method(typeof(Wire), "ClearConnections");
            _connDisconnectWire = AccessTools.Method(typeof(Connection), "DisconnectWire", new[] { typeof(Wire) });

            var wirePostfix = new HarmonyMethod(typeof(SignalGraphPatches), nameof(OnWireChanged));

            if (_wireConnectOriginal != null)
                harmony.Patch(_wireConnectOriginal, postfix: wirePostfix);
            if (_wireTryConnectOriginal != null)
                harmony.Patch(_wireTryConnectOriginal, postfix: wirePostfix);
            if (_wireRemoveConnItem != null)
                harmony.Patch(_wireRemoveConnItem, postfix: wirePostfix);
            if (_wireRemoveConnConn != null)
                harmony.Patch(_wireRemoveConnConn, postfix: wirePostfix);
            if (_wireClearConns != null)
                harmony.Patch(_wireClearConns, postfix: wirePostfix);
            if (_connDisconnectWire != null)
                harmony.Patch(_connDisconnectWire, postfix: wirePostfix);

            _registered = true;
            LuaCsLogger.Log("[ItemOptimizer] SignalGraphPatches registered (SendSignalIntoConnection prefix).");
        }

        internal static void Unregister(Harmony harmony)
        {
            if (!_registered) return;

            if (_sendSignalIntoConnOriginal != null)
                harmony.Unpatch(_sendSignalIntoConnOriginal,
                    AccessTools.Method(typeof(SignalGraphPatches), nameof(SendSignalIntoConnectionPrefix)));

            var wirePostfixMethod = AccessTools.Method(typeof(SignalGraphPatches), nameof(OnWireChanged));
            if (_wireConnectOriginal != null)
                harmony.Unpatch(_wireConnectOriginal, wirePostfixMethod);
            if (_wireTryConnectOriginal != null)
                harmony.Unpatch(_wireTryConnectOriginal, wirePostfixMethod);
            if (_wireRemoveConnItem != null)
                harmony.Unpatch(_wireRemoveConnItem, wirePostfixMethod);
            if (_wireRemoveConnConn != null)
                harmony.Unpatch(_wireRemoveConnConn, wirePostfixMethod);
            if (_wireClearConns != null)
                harmony.Unpatch(_wireClearConns, wirePostfixMethod);
            if (_connDisconnectWire != null)
                harmony.Unpatch(_connDisconnectWire, wirePostfixMethod);

            _registered = false;
        }

        // ════════════════════════════════════
        //  Patch A: SendSignalIntoConnection interception
        // ════════════════════════════════════

        /// <summary>
        /// Intercepts Connection.SendSignalIntoConnection to:
        /// 1. Push the signal into the capture register for graph evaluation
        /// 2. Block the entire call (prevents vanilla ReceiveSignal + StatusEffects)
        ///
        /// This replaces the old ReceiveSignal prefix which failed because Harmony
        /// prefixes on base virtual methods don't intercept overridden subclass methods.
        /// </summary>
        private static bool SendSignalIntoConnectionPrefix(Signal signal, Connection conn)
        {
            // Fast path: if graph not active, run vanilla
            if (!SignalGraphEvaluator.IsCompiled)
            {
                // Still trace if active (graph off but trace on)
                if (SignalPrefixTrace.IsActive && conn != null)
                    SignalPrefixTrace.LogIfTracked(conn.Item.ID, conn.Name, signal.value, signal.source, wasBlocked: false);
                return true;
            }

            // Check if the TARGET item is accelerated — O(1) array lookup
            if (conn == null || !SignalGraphEvaluator.IsAccelerated(conn.Item.ID))
            {
                if (SignalPrefixTrace.IsActive && conn != null)
                    SignalPrefixTrace.LogIfTracked(conn.Item.ID, conn.Name, signal.value, signal.source, wasBlocked: false);
                return true; // not accelerated, run vanilla
            }

            // Trace: log captured signal
            if (SignalPrefixTrace.IsActive)
                SignalPrefixTrace.LogIfTracked(conn.Item.ID, conn.Name, signal.value, signal.source, wasBlocked: true);

            // Push the signal into the capture register for this input connection.
            SignalGraphEvaluator.PushCaptureSignal(conn.Item.ID, conn.Name, signal.value);

            // Block: skip vanilla ReceiveSignal dispatch and StatusEffect processing.
            // The signal graph handles evaluation; vanilla processing would double-eval.
            return false;
        }

        // ════════════════════════════════════
        //  Patch B: Wire change detection
        // ════════════════════════════════════

        /// <summary>Postfix on all wire connect/disconnect methods — marks graph dirty.</summary>
        private static void OnWireChanged()
        {
            SignalGraphEvaluator.MarkDirty();
        }
    }
}
