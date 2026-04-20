using System;
using System.Reflection;
using Barotrauma;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Compatibility wrapper for LuaCsLogger.HandleException.
    /// Older LuaCs builds lack HandleException(Exception, LuaCsMessageOrigin),
    /// causing MissingMethodException at JIT time. This class uses a cached
    /// delegate so the callsite never touches the missing method directly.
    /// </summary>
    internal static class SafeLogger
    {
        private static bool _resolved;
        private static Action<Exception, LuaCsMessageOrigin> _handleException;

        internal static void HandleException(Exception e, LuaCsMessageOrigin origin)
        {
            if (!_resolved) Resolve();

            if (_handleException != null)
            {
                _handleException(e, origin);
            }
            else
            {
                // Fallback: log as error string
                LuaCsLogger.LogError($"[ItemOptimizer] {e}");
            }
        }

        private static void Resolve()
        {
            _resolved = true;
            try
            {
                var method = typeof(LuaCsLogger).GetMethod(
                    "HandleException",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Exception), typeof(LuaCsMessageOrigin) },
                    null);

                if (method != null)
                {
                    _handleException = (Action<Exception, LuaCsMessageOrigin>)
                        Delegate.CreateDelegate(typeof(Action<Exception, LuaCsMessageOrigin>), method);
                }
            }
            catch
            {
                _handleException = null;
            }
        }
    }
}
