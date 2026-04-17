using Barotrauma;
using Barotrauma.Items.Components;
using ItemOptimizerMod.Patches;

namespace ItemOptimizerMod.World.Sensors
{
    /// <summary>
    /// NativeComponent wrapper for MotionSensor.
    /// Calls RunDetection (separate codepath from Prefix) for detection logic,
    /// then emits signals and status effects via CommandBuffer for thread-safe parallel execution.
    ///
    /// Step 4 (Parallel mode): all side effects go through CommandBuffer (ctx.EmitSignal/EmitStatusEffect).
    /// Commands are applied on main thread in Phase 5 after parallel tick completes.
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

            // Emit signal every frame via CommandBuffer — stepValue: 1 matches Prefix exactly
            string signalOut = _sensor.MotionDetected ? _sensor.Output : _sensor.FalseOutput;
            if (!string.IsNullOrEmpty(signalOut))
            {
                var conn = MotionSensorRewrite.GetStateOutConnection(_sensor);
                if (conn != null)
                    ctx.EmitSignal(conn, signalOut, stepValue: 1);
            }

            // Status effect when detected — component-level via CommandBuffer
            if (_sensor.MotionDetected)
                ctx.EmitStatusEffect(ActionType.OnUse, _sensor);
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
