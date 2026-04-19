using System.Collections.Generic;
using Barotrauma;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Logs all signals going to/from specific items via SendSignalIntoConnection.
    /// Activated by ioruin trace command. Lightweight: only checks HashSet membership
    /// on the hot path; string formatting only when a match is found.
    /// </summary>
    static class SignalPrefixTrace
    {
        private static HashSet<ushort> _traceIds;
        private static int _framesRemaining;

        internal static bool IsActive => _framesRemaining > 0 && _traceIds != null && _traceIds.Count > 0;

        internal static void Start(HashSet<ushort> itemIds, int frames)
        {
            _traceIds = itemIds;
            _framesRemaining = frames;
        }

        internal static void DecrementFrame()
        {
            if (_framesRemaining > 0)
                _framesRemaining--;
        }

        /// <summary>
        /// Called from SendSignalIntoConnection prefix for EVERY signal delivery.
        /// Only logs if the source or target item is in the trace set.
        /// </summary>
        internal static void LogIfTracked(ushort targetItemId, string targetConnName, string signalValue,
            Item sourceItem, bool wasBlocked)
        {
            if (!IsActive) return;

            ushort srcId = (ushort)(sourceItem?.ID ?? 0);
            bool targetTracked = _traceIds.Contains(targetItemId);
            bool sourceTracked = sourceItem != null && _traceIds.Contains(srcId);

            if (!targetTracked && !sourceTracked) return;

            string srcName = sourceItem?.Prefab?.Identifier.Value ?? "?";
            string targetItemName = (Entity.FindEntityByID(targetItemId) as Item)?.Prefab?.Identifier.Value ?? "?";
            string action = wasBlocked ? "CAPTURED" : "DELIVERED";
            LuaCsLogger.Log($"[IO-SignalTrace] {action}: {srcName}(#{srcId}) → {targetItemName}(#{targetItemId}) [{targetConnName}] val=\"{signalValue}\"");
        }
    }
}
