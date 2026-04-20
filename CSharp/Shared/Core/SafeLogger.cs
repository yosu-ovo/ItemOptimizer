using System;
using System.Reflection;
using Barotrauma;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Compatibility wrapper for LuaCsLogger.HandleException.
    /// Dev LuaCs builds may lack the LuaCsMessageOrigin type entirely,
    /// causing TypeLoadException at JIT time if it appears in ANY method
    /// signature. This class uses pure reflection — zero compile-time
    /// references to LuaCsMessageOrigin.
    /// </summary>
    internal static class SafeLogger
    {
        private static bool _resolved;
        private static MethodInfo _handleExceptionMethod;
        private static object _csharpModOrigin;

        internal static void HandleException(Exception e)
        {
            if (!_resolved) Resolve();

            if (_handleExceptionMethod != null && _csharpModOrigin != null)
            {
                try
                {
                    _handleExceptionMethod.Invoke(null, new object[] { e, _csharpModOrigin });
                    return;
                }
                catch { /* fall through to fallback */ }
            }

            LuaCsLogger.LogError($"[ItemOptimizer] {e}");
        }

        private static void Resolve()
        {
            _resolved = true;
            try
            {
                // Resolve LuaCsMessageOrigin type by name — no compile-time dependency
                var originType = typeof(LuaCsLogger).Assembly.GetType("Barotrauma.LuaCsMessageOrigin");
                if (originType == null) return;

                // Get CSharpMod enum value
                _csharpModOrigin = Enum.Parse(originType, "CSharpMod");

                // Get HandleException(Exception, LuaCsMessageOrigin) method
                _handleExceptionMethod = typeof(LuaCsLogger).GetMethod(
                    "HandleException",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Exception), originType },
                    null);
            }
            catch
            {
                _handleExceptionMethod = null;
                _csharpModOrigin = null;
            }
        }
    }
}
