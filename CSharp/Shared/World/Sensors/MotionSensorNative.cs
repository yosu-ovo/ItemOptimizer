using Barotrauma;
using Barotrauma.Items.Components;
using ItemOptimizerMod.Patches;

namespace ItemOptimizerMod.World.Sensors
{
    /// <summary>
    /// NativeComponent wrapper for MotionSensor.
    /// Calls RunDetection (separate codepath from Prefix) for detection logic,
    /// then emits signals and status effects via direct API calls matching the Prefix exactly.
    ///
    /// Step 2 (DirectMode): bypasses CommandBuffer for signal/SE emission to guarantee
    /// identical behavior to the Harmony Prefix path. The CommandBuffer path will be used
    /// in Step 4 (Parallel mode) after proper validation.
    ///
    /// When registered, sets IsNativeManaged[id]=true so the Harmony Prefix
    /// returns false immediately (skip) — no double processing.
    /// </summary>
    internal sealed class MotionSensorNative : NativeComponent
    {
        private readonly MotionSensor _sensor;

        public MotionSensorNative(MotionSensor sensor, Item host)
        {
            _sensor = sensor;
            Host = host;
        }

        /// <summary>
        /// Active/Nearby/Passive: every frame (signal continuity — skipping causes doors to flicker).
        /// Dormant: skip (runtime already skips dormant zones, this is a safety net).
        /// </summary>
        public override bool ShouldTick(ZoneTier tier, uint frame) => tier < ZoneTier.Dormant;

        public override void Tick(ref TickContext ctx)
        {
            // Detection (sets _sensor.MotionDetected, handles timer gate + full scan)
            MotionSensorRewrite.RunDetection(_sensor, ctx.DeltaTime);

            // Emit signal every frame — direct call matching Prefix exactly:
            //   item.SendSignal(new Signal(signalOut, 1), conn)
            string signalOut = _sensor.MotionDetected ? _sensor.Output : _sensor.FalseOutput;
            if (!string.IsNullOrEmpty(signalOut))
            {
                var conn = MotionSensorRewrite.GetStateOutConnection(_sensor);
                if (conn != null)
                    Host.SendSignal(new Signal(signalOut, 1), conn);
                else
                    Host.SendSignal(new Signal(signalOut, 1), "state_out");
            }

            // Status effect when detected — component-level call matching Prefix:
            //   __instance.ApplyStatusEffects(ActionType.OnUse, deltaTime)
            if (_sensor.MotionDetected)
                _sensor.ApplyStatusEffects(ActionType.OnUse, ctx.DeltaTime);
        }

        public override void OnRegistered()
        {
            MotionSensorRewrite.IsNativeManaged[Host.ID] = true;
        }

        public override void OnUnregistered()
        {
            MotionSensorRewrite.IsNativeManaged[Host.ID] = false;
        }
    }
}
