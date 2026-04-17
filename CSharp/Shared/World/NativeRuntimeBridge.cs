using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using ItemOptimizerMod.Patches;
using ItemOptimizerMod.World.Sensors;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// Bridge between mod lifecycle (round start/end) and NativeRuntime.
    /// Step 2: all sensors go into a single default zone (no spatial partitioning yet).
    /// </summary>
    internal static class NativeRuntimeBridge
    {
        internal static NativeRuntime Runtime;
        internal static bool IsEnabled;

        private static readonly List<MotionSensorNative> _registeredSensors = new(64);

        internal static void OnRoundStart()
        {
            if (!OptimizerConfig.EnableNativeRuntime) return;

            Runtime = new NativeRuntime();

            // Step 2: single default zone covering everything
            var defaultZone = new SubmarineZone
            {
                Id = 0,
                Submarine = Submarine.MainSub,
                Position = Submarine.MainSub?.WorldPosition ?? Vector2.Zero,
                Radius = float.MaxValue,
                Tier = ZoneTier.Active,
            };
            Runtime.Graph.Zones.Add(defaultZone);

            // Register all MotionSensor items
            _registeredSensors.Clear();
            foreach (var item in Item.ItemList)
            {
                if (item == null || item.Removed) continue;
                var ms = item.GetComponent<MotionSensor>();
                if (ms == null) continue;

                var native = new MotionSensorNative(ms, item);
                Runtime.Register(native, defaultZone);
                _registeredSensors.Add(native);
            }

            IsEnabled = true;
            LuaCsLogger.Log($"[ItemOptimizer] NativeRuntime started: {_registeredSensors.Count} sensors registered");
        }

        internal static void OnRoundEnd()
        {
            if (!IsEnabled) return;

            for (int i = 0; i < _registeredSensors.Count; i++)
                Runtime.Unregister(_registeredSensors[i]);
            _registeredSensors.Clear();

            Runtime.Reset();
            Runtime = null;
            IsEnabled = false;
        }

        internal static void Tick(float deltaTime, Camera cam)
        {
            Runtime?.Tick(deltaTime, cam);
        }
    }
}
