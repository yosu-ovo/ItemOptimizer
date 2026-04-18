using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Validates toggle changes and computes cascade adjustments.
    /// Dependency rules:
    ///   NativeRuntime ON → requires MotionSensorRewrite ON
    ///   MotionSensorRewrite OFF → cascade NativeRuntime OFF
    /// </summary>
    static class ToggleValidator
    {
        /// <summary>
        /// Check if a toggle change is valid. Returns cascade actions needed,
        /// or null if the change should be blocked (with reason logged).
        /// Empty list = change is valid with no cascades needed.
        /// </summary>
        internal static List<(string toggle, bool value)> ValidateChange(string toggle, bool newValue)
        {
            var cascades = new List<(string, bool)>();

            switch (toggle)
            {
                case "native_runtime" when newValue:
                    // NativeRuntime ON → requires MotionSensorRewrite
                    if (!OptimizerConfig.EnableMotionSensorRewrite)
                    {
                        Log("Cannot enable NativeRuntime: MotionSensorRewrite is OFF " +
                            "(RunDetection depends on Rewrite code)", Color.Red);
                        return null;
                    }
                    break;

                case "motion_rewrite" when !newValue:
                    // MotionSensorRewrite OFF → cascade NativeRuntime OFF
                    if (OptimizerConfig.EnableNativeRuntime || World.NativeRuntimeBridge.IsEnabled)
                    {
                        cascades.Add(("native_runtime", false));
                        Log("NativeRuntime auto-disabled (depends on MotionSensorRewrite)", Color.Yellow);
                    }
                    break;
            }

            return cascades;
        }

        /// <summary>
        /// Validate ionative console command. Returns true if allowed.
        /// </summary>
        internal static bool CanEnableNativeRuntime()
        {
            if (!OptimizerConfig.EnableMotionSensorRewrite)
            {
                Log("Cannot enable NativeRuntime: MotionSensorRewrite is OFF", Color.Red);
                return false;
            }
            return true;
        }

        private static void Log(string msg, Color color)
        {
            DebugConsole.NewMessage($"[ItemOptimizer] {msg}", color);
        }
    }
}
