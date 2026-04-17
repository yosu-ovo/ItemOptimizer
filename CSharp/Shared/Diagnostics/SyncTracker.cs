using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Server–client synchronization tracker.
    /// Captures entity states + positions AND character positions from server,
    /// compares against client local state, logs desyncs to CSV.
    ///
    /// Refactored to match PerfProfiler pattern: StringBuilder buffer, flush at end.
    /// </summary>
    static class SyncTracker
    {
        // ── Recording state ──
        internal static bool IsRecording;
        internal static int MaxFrames;
        internal static string OutputPath;

        // ── Snapshot data (received from server, compared on client) ──
        internal static readonly List<EntitySnapshot> LastServerSnapshot = new();
        internal static readonly List<CharacterSnapshot> LastCharacterSnapshot = new();

        // ── Client-side CSV buffers (StringBuilder, flushed at end) ──
        private static StringBuilder _entityBuffer;
        private static StringBuilder _charBuffer;
        private static int _frameIndex;
        private static int _totalDesyncs;
        private static int _totalSamples;
        private static int _totalPosDesyncs;
        private static int _totalPosSamples;
        private static string _entityPath;
        private static string _charPath;

        // ── Timeout: auto-stop if no ticks received for N seconds ──
        private static float _timeSinceLastTick;
        private const float TimeoutSeconds = 5f;

        internal struct EntitySnapshot
        {
            public ushort ItemId;
            public byte Type;          // 0=Door, 1=Pump, 2=MotionSensor, 3=Reactor, 4=Engine
            public byte Flags;         // bit0=isOpen/isActive etc
            public float Value;        // openState / flowPercentage / fissionRate etc
            public float PosX;         // item WorldPosition.X
            public float PosY;         // item WorldPosition.Y
        }

        internal struct CharacterSnapshot
        {
            public ushort CharId;
            public float PosX;         // WorldPosition.X
            public float PosY;         // WorldPosition.Y
            public float VelX;         // LinearVelocity.X
            public float VelY;         // LinearVelocity.Y
            public byte Flags;         // bit0=IsDead, bit1=IsUnconscious, bit2=InWater
        }

        // Entity types for snapshot
        internal const byte TypeDoor = 0;
        internal const byte TypePump = 1;
        internal const byte TypeMotionSensor = 2;
        internal const byte TypeReactor = 3;
        internal const byte TypeEngine = 4;

        /// <summary>Start recording on the client side.</summary>
        internal static void StartRecording(int frames, string path)
        {
            // Force-close any previous recording
            if (IsRecording)
            {
                LuaCsLogger.Log("[ItemOptimizer] SyncTracker: force-flushing previous recording");
                FlushAndStop();
            }

            string basePath = PerfProfiler.ResolvePath(path);
            string dir = Path.GetDirectoryName(basePath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(basePath);

            _entityPath = Path.Combine(dir, name + "_entities.csv");
            _charPath = Path.Combine(dir, name + "_characters.csv");
            OutputPath = _entityPath;

            MaxFrames = Math.Clamp(frames, 1, 3600);
            _frameIndex = 0;
            _totalDesyncs = 0;
            _totalSamples = 0;
            _totalPosDesyncs = 0;
            _totalPosSamples = 0;
            _timeSinceLastTick = 0;

            // Initialize StringBuilder buffers with CSV headers
            _entityBuffer = new StringBuilder(1024 * 64);
            _entityBuffer.AppendLine("frame,item_id,type," +
                "s_flags,c_flags," +
                "s_value,c_value," +
                "s_x,s_y,c_x,c_y," +
                "flag_desync,val_delta,pos_delta");

            _charBuffer = new StringBuilder(1024 * 16);
            _charBuffer.AppendLine("frame,char_id,char_name," +
                "s_x,s_y,c_x,c_y," +
                "s_vx,s_vy,c_vx,c_vy," +
                "s_flags,c_flags," +
                "pos_delta,vel_delta,flag_desync");

            IsRecording = true;
        }

        /// <summary>
        /// Called each game update frame to check timeout.
        /// Should be called from the main update loop when recording is active.
        /// </summary>
        internal static void UpdateTimeout(float dt)
        {
            if (!IsRecording) return;

            _timeSinceLastTick += dt;
            if (_timeSinceLastTick > TimeoutSeconds && _frameIndex > 0)
            {
                LuaCsLogger.Log($"[ItemOptimizer] SyncTracker: timeout ({TimeoutSeconds}s without data), flushing {_frameIndex} frames");
                FlushAndStop();
            }
        }

        /// <summary>
        /// Called on client when server snapshot is received — compare against local state.
        /// Client counts received ticks; stops after MaxFrames received.
        /// </summary>
        internal static void ClientTick()
        {
            if (!IsRecording) return;

            _frameIndex++;
            _timeSinceLastTick = 0;

            // ── Entity comparison ──
            if (_entityBuffer != null)
            {
                foreach (var ss in LastServerSnapshot)
                {
                    var entity = Entity.FindEntityByID(ss.ItemId);
                    if (entity is not Item item) continue;

                    byte clientFlags = 0;
                    float clientValue = 0;
                    string typeName = "";

                    switch (ss.Type)
                    {
                        case TypeDoor:
                            typeName = "door";
                            var door = item.GetComponent<Door>();
                            if (door != null)
                            {
                                clientFlags = (byte)(door.IsOpen ? 1 : 0);
                                clientValue = door.OpenState;
                            }
                            break;
                        case TypePump:
                            typeName = "pump";
                            var pump = item.GetComponent<Pump>();
                            if (pump != null)
                            {
                                clientFlags = (byte)(pump.IsActive ? 1 : 0);
                                clientValue = pump.FlowPercentage;
                            }
                            break;
                        case TypeMotionSensor:
                            typeName = "motion";
                            var ms = item.GetComponent<MotionSensor>();
                            if (ms != null)
                            {
                                clientFlags = (byte)(ms.MotionDetected ? 1 : 0);
                                clientValue = 0;
                            }
                            break;
                        case TypeReactor:
                            typeName = "reactor";
                            var reactor = item.GetComponent<Reactor>();
                            if (reactor != null)
                            {
                                clientFlags = (byte)(reactor.IsActive ? 1 : 0);
                                clientValue = reactor.FissionRate;
                            }
                            break;
                        case TypeEngine:
                            typeName = "engine";
                            var engine = item.GetComponent<Engine>();
                            if (engine != null)
                            {
                                clientFlags = (byte)(engine.IsActive ? 1 : 0);
                                clientValue = engine.Force;
                            }
                            break;
                        default:
                            typeName = $"type{ss.Type}";
                            break;
                    }

                    float clientX = item.WorldPosition.X;
                    float clientY = item.WorldPosition.Y;
                    float valDelta = Math.Abs(ss.Value - clientValue);
                    float posDelta = MathF.Sqrt(
                        (ss.PosX - clientX) * (ss.PosX - clientX) +
                        (ss.PosY - clientY) * (ss.PosY - clientY));
                    bool flagDesync = ss.Flags != clientFlags;

                    _totalSamples++;
                    if (flagDesync || valDelta > 0.01f) _totalDesyncs++;
                    _totalPosSamples++;
                    if (posDelta > 1f) _totalPosDesyncs++;

                    _entityBuffer.Append(_frameIndex).Append(',');
                    _entityBuffer.Append(ss.ItemId).Append(',');
                    _entityBuffer.Append(typeName).Append(',');
                    _entityBuffer.Append(ss.Flags).Append(',');
                    _entityBuffer.Append(clientFlags).Append(',');
                    _entityBuffer.Append(ss.Value.ToString("F4")).Append(',');
                    _entityBuffer.Append(clientValue.ToString("F4")).Append(',');
                    _entityBuffer.Append(ss.PosX.ToString("F1")).Append(',');
                    _entityBuffer.Append(ss.PosY.ToString("F1")).Append(',');
                    _entityBuffer.Append(clientX.ToString("F1")).Append(',');
                    _entityBuffer.Append(clientY.ToString("F1")).Append(',');
                    _entityBuffer.Append(flagDesync ? 1 : 0).Append(',');
                    _entityBuffer.Append(valDelta.ToString("F4")).Append(',');
                    _entityBuffer.AppendLine(posDelta.ToString("F2"));
                }
            }

            // ── Character comparison ──
            if (_charBuffer != null)
            {
                foreach (var cs in LastCharacterSnapshot)
                {
                    var entity = Entity.FindEntityByID(cs.CharId);
                    if (entity is not Character character) continue;

                    float cx = character.WorldPosition.X;
                    float cy = character.WorldPosition.Y;
                    float cvx = 0, cvy = 0;
                    if (character.AnimController?.Collider != null)
                    {
                        var vel = character.AnimController.Collider.LinearVelocity;
                        cvx = vel.X;
                        cvy = vel.Y;
                    }
                    byte cFlags = 0;
                    if (character.IsDead) cFlags |= 1;
                    if (character.IsUnconscious) cFlags |= 2;
                    if (character.InWater) cFlags |= 4;

                    float posDelta = MathF.Sqrt(
                        (cs.PosX - cx) * (cs.PosX - cx) +
                        (cs.PosY - cy) * (cs.PosY - cy));
                    float velDelta = MathF.Sqrt(
                        (cs.VelX - cvx) * (cs.VelX - cvx) +
                        (cs.VelY - cvy) * (cs.VelY - cvy));
                    bool flagDesync = cs.Flags != cFlags;

                    string charName = character.Name?.Replace(',', '_') ?? "?";

                    _charBuffer.Append(_frameIndex).Append(',');
                    _charBuffer.Append(cs.CharId).Append(',');
                    _charBuffer.Append(charName).Append(',');
                    _charBuffer.Append(cs.PosX.ToString("F1")).Append(',');
                    _charBuffer.Append(cs.PosY.ToString("F1")).Append(',');
                    _charBuffer.Append(cx.ToString("F1")).Append(',');
                    _charBuffer.Append(cy.ToString("F1")).Append(',');
                    _charBuffer.Append(cs.VelX.ToString("F3")).Append(',');
                    _charBuffer.Append(cs.VelY.ToString("F3")).Append(',');
                    _charBuffer.Append(cvx.ToString("F3")).Append(',');
                    _charBuffer.Append(cvy.ToString("F3")).Append(',');
                    _charBuffer.Append(cs.Flags).Append(',');
                    _charBuffer.Append(cFlags).Append(',');
                    _charBuffer.Append(posDelta.ToString("F2")).Append(',');
                    _charBuffer.Append(velDelta.ToString("F3")).Append(',');
                    _charBuffer.Append(flagDesync ? "1" : "0").AppendLine();
                }
            }

            if (_frameIndex >= MaxFrames)
            {
                FlushAndStop();
            }
        }

        /// <summary>Flush buffers to file and stop recording. Safe to call multiple times.</summary>
        internal static void FlushAndStop()
        {
            if (!IsRecording && _entityBuffer == null) return;

            IsRecording = false;

            // Flush entity CSV
            if (_entityBuffer != null && _entityPath != null)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_entityPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(_entityPath, _entityBuffer.ToString());
                }
                catch (Exception e)
                {
                    LuaCsLogger.LogError($"[ItemOptimizer] Failed to write entity CSV: {e.Message}");
                }
            }

            // Flush character CSV
            if (_charBuffer != null && _charPath != null)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_charPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(_charPath, _charBuffer.ToString());
                }
                catch (Exception e)
                {
                    LuaCsLogger.LogError($"[ItemOptimizer] Failed to write character CSV: {e.Message}");
                }
            }

            _entityBuffer = null;
            _charBuffer = null;

            float syncRate = _totalSamples > 0
                ? (1f - (float)_totalDesyncs / _totalSamples) * 100f
                : 100f;
            float posSyncRate = _totalPosSamples > 0
                ? (1f - (float)_totalPosDesyncs / _totalPosSamples) * 100f
                : 100f;

            string summary = $"[ItemOptimizer] Sync done: {_frameIndex}/{MaxFrames} frames, " +
                $"state sync: {syncRate:F1}% ({_totalDesyncs}/{_totalSamples} desyncs), " +
                $"pos sync: {posSyncRate:F1}% ({_totalPosDesyncs}/{_totalPosSamples} off>1px) " +
                $"-> {OutputPath}";
            LuaCsLogger.Log(summary);

#if CLIENT
            DebugConsole.NewMessage(summary,
                syncRate >= 99f && posSyncRate >= 99f
                    ? Microsoft.Xna.Framework.Color.LimeGreen
                    : syncRate >= 95f
                        ? Microsoft.Xna.Framework.Color.Yellow
                        : Microsoft.Xna.Framework.Color.Red);
#endif
        }

        // ── Server-side: build snapshot of trackable entities ──

        internal static List<EntitySnapshot> ServerBuildEntitySnapshot()
        {
            var list = new List<EntitySnapshot>(64);

            foreach (var item in Item.ItemList)
            {
                var door = item.GetComponent<Door>();
                if (door != null)
                {
                    list.Add(new EntitySnapshot
                    {
                        ItemId = item.ID, Type = TypeDoor,
                        Flags = (byte)(door.IsOpen ? 1 : 0),
                        Value = door.OpenState,
                        PosX = item.WorldPosition.X, PosY = item.WorldPosition.Y
                    });
                    continue;
                }

                var pump = item.GetComponent<Pump>();
                if (pump != null)
                {
                    list.Add(new EntitySnapshot
                    {
                        ItemId = item.ID, Type = TypePump,
                        Flags = (byte)(pump.IsActive ? 1 : 0),
                        Value = pump.FlowPercentage,
                        PosX = item.WorldPosition.X, PosY = item.WorldPosition.Y
                    });
                    continue;
                }

                var ms = item.GetComponent<MotionSensor>();
                if (ms != null)
                {
                    list.Add(new EntitySnapshot
                    {
                        ItemId = item.ID, Type = TypeMotionSensor,
                        Flags = (byte)(ms.MotionDetected ? 1 : 0),
                        Value = 0,
                        PosX = item.WorldPosition.X, PosY = item.WorldPosition.Y
                    });
                    continue;
                }

                var reactor = item.GetComponent<Reactor>();
                if (reactor != null)
                {
                    list.Add(new EntitySnapshot
                    {
                        ItemId = item.ID, Type = TypeReactor,
                        Flags = (byte)(reactor.IsActive ? 1 : 0),
                        Value = reactor.FissionRate,
                        PosX = item.WorldPosition.X, PosY = item.WorldPosition.Y
                    });
                    continue;
                }

                var engine = item.GetComponent<Engine>();
                if (engine != null)
                {
                    list.Add(new EntitySnapshot
                    {
                        ItemId = item.ID, Type = TypeEngine,
                        Flags = (byte)(engine.IsActive ? 1 : 0),
                        Value = engine.Force,
                        PosX = item.WorldPosition.X, PosY = item.WorldPosition.Y
                    });
                }
            }

            return list;
        }

        // ── Server-side: build character position snapshot ──

        internal static List<CharacterSnapshot> ServerBuildCharacterSnapshot()
        {
            var list = new List<CharacterSnapshot>(16);

            foreach (var character in Character.CharacterList)
            {
                if (character.Removed) continue;

                float vx = 0, vy = 0;
                if (character.AnimController?.Collider != null)
                {
                    var vel = character.AnimController.Collider.LinearVelocity;
                    vx = vel.X;
                    vy = vel.Y;
                }

                byte flags = 0;
                if (character.IsDead) flags |= 1;
                if (character.IsUnconscious) flags |= 2;
                if (character.InWater) flags |= 4;

                list.Add(new CharacterSnapshot
                {
                    CharId = character.ID,
                    PosX = character.WorldPosition.X,
                    PosY = character.WorldPosition.Y,
                    VelX = vx, VelY = vy,
                    Flags = flags
                });
            }

            return list;
        }

        internal static void Reset()
        {
            IsRecording = false;
            MaxFrames = 0;
            _entityBuffer = null;
            _charBuffer = null;
            _frameIndex = 0;
            _timeSinceLastTick = 0;
            LastServerSnapshot.Clear();
            LastCharacterSnapshot.Clear();
        }
    }
}
