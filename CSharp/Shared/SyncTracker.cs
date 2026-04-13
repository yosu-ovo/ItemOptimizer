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
    /// </summary>
    static class SyncTracker
    {
        // ── Recording state ──
        internal static bool IsRecording;
        internal static int FramesRemaining;
        internal static string OutputPath;

        // ── Snapshot data (received from server, compared on client) ──
        internal static readonly List<EntitySnapshot> LastServerSnapshot = new();
        internal static readonly List<CharacterSnapshot> LastCharacterSnapshot = new();

        // ── Client-side desync log ──
        private static StreamWriter _entityWriter;
        private static StreamWriter _charWriter;
        private static int _frameIndex;
        private static int _totalDesyncs;
        private static int _totalSamples;
        private static int _totalPosDesyncs;
        private static int _totalPosSamples;

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
            // Force-close any previous recording that wasn't cleaned up
            // (e.g. disconnect mid-recording leaves file handles open)
            if (IsRecording || _entityWriter != null || _charWriter != null)
            {
                LuaCsLogger.Log("[ItemOptimizer] SyncTracker: force-closing previous recording");
                ForceClose();
            }

            string basePath = PerfProfiler.ResolvePath(path);
            // Strip extension to create two files
            string dir = Path.GetDirectoryName(basePath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(basePath);

            string entityPath = Path.Combine(dir, name + "_entities.csv");
            string charPath = Path.Combine(dir, name + "_characters.csv");
            OutputPath = entityPath; // primary output for summary

            FramesRemaining = frames;
            _frameIndex = 0;
            _totalDesyncs = 0;
            _totalSamples = 0;
            _totalPosDesyncs = 0;
            _totalPosSamples = 0;

            try
            {
                _entityWriter = new StreamWriter(entityPath, false, Encoding.UTF8, 65536);
                _entityWriter.WriteLine("frame,item_id,type," +
                    "s_flags,c_flags," +
                    "s_value,c_value," +
                    "s_x,s_y,c_x,c_y," +
                    "flag_desync,val_delta,pos_delta");

                _charWriter = new StreamWriter(charPath, false, Encoding.UTF8, 65536);
                _charWriter.WriteLine("frame,char_id,char_name," +
                    "s_x,s_y,c_x,c_y," +
                    "s_vx,s_vy,c_vx,c_vy," +
                    "s_flags,c_flags," +
                    "pos_delta,vel_delta,flag_desync");

                IsRecording = true;
            }
            catch (Exception e)
            {
                LuaCsLogger.HandleException(e, LuaCsMessageOrigin.CSharpMod);
                IsRecording = false;
            }
        }

        /// <summary>
        /// Called on client when server snapshot is received — compare against local state.
        /// </summary>
        internal static void ClientTick()
        {
            if (!IsRecording) return;

            _frameIndex++;
            FramesRemaining--;

            // Diagnostic: log character snapshot info for first 3 frames
            if (_frameIndex <= 3)
            {
                LuaCsLogger.Log($"[ItemOptimizer] SyncTracker frame {_frameIndex}: " +
                    $"entities={LastServerSnapshot.Count}, chars={LastCharacterSnapshot.Count}");
                foreach (var cs in LastCharacterSnapshot)
                {
                    var dbgEntity = Entity.FindEntityByID(cs.CharId);
                    LuaCsLogger.Log($"  char ID={cs.CharId} -> {dbgEntity?.GetType().Name ?? "NULL"} " +
                        $"({(dbgEntity is Character c ? c.Name : "?")})");
                }
            }

            // ── Entity comparison ──
            if (_entityWriter != null)
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

                    _entityWriter.Write(_frameIndex); _entityWriter.Write(',');
                    _entityWriter.Write(ss.ItemId); _entityWriter.Write(',');
                    _entityWriter.Write(typeName); _entityWriter.Write(',');
                    _entityWriter.Write(ss.Flags); _entityWriter.Write(',');
                    _entityWriter.Write(clientFlags); _entityWriter.Write(',');
                    _entityWriter.Write(ss.Value.ToString("F4")); _entityWriter.Write(',');
                    _entityWriter.Write(clientValue.ToString("F4")); _entityWriter.Write(',');
                    _entityWriter.Write(ss.PosX.ToString("F1")); _entityWriter.Write(',');
                    _entityWriter.Write(ss.PosY.ToString("F1")); _entityWriter.Write(',');
                    _entityWriter.Write(clientX.ToString("F1")); _entityWriter.Write(',');
                    _entityWriter.Write(clientY.ToString("F1")); _entityWriter.Write(',');
                    _entityWriter.Write(flagDesync ? 1 : 0); _entityWriter.Write(',');
                    _entityWriter.Write(valDelta.ToString("F4")); _entityWriter.Write(',');
                    _entityWriter.WriteLine(posDelta.ToString("F2"));
                }
            }

            // ── Character comparison ──
            if (_charWriter != null)
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

                    _charWriter.Write(_frameIndex); _charWriter.Write(',');
                    _charWriter.Write(cs.CharId); _charWriter.Write(',');
                    _charWriter.Write(charName); _charWriter.Write(',');
                    _charWriter.Write(cs.PosX.ToString("F1")); _charWriter.Write(',');
                    _charWriter.Write(cs.PosY.ToString("F1")); _charWriter.Write(',');
                    _charWriter.Write(cx.ToString("F1")); _charWriter.Write(',');
                    _charWriter.Write(cy.ToString("F1")); _charWriter.Write(',');
                    _charWriter.Write(cs.VelX.ToString("F3")); _charWriter.Write(',');
                    _charWriter.Write(cs.VelY.ToString("F3")); _charWriter.Write(',');
                    _charWriter.Write(cvx.ToString("F3")); _charWriter.Write(',');
                    _charWriter.Write(cvy.ToString("F3")); _charWriter.Write(',');
                    _charWriter.Write(cs.Flags); _charWriter.Write(',');
                    _charWriter.Write(cFlags); _charWriter.Write(',');
                    _charWriter.Write(posDelta.ToString("F2")); _charWriter.Write(',');
                    _charWriter.Write(velDelta.ToString("F3")); _charWriter.Write(',');
                    _charWriter.WriteLine(flagDesync ? 1 : 0);
                }
            }

            if (FramesRemaining <= 0)
            {
                StopRecording();
            }
        }

        internal static void StopRecording()
        {
            IsRecording = false;
            _entityWriter?.Flush();
            _entityWriter?.Dispose();
            _entityWriter = null;
            _charWriter?.Flush();
            _charWriter?.Dispose();
            _charWriter = null;

            float syncRate = _totalSamples > 0
                ? (1f - (float)_totalDesyncs / _totalSamples) * 100f
                : 100f;
            float posSyncRate = _totalPosSamples > 0
                ? (1f - (float)_totalPosDesyncs / _totalPosSamples) * 100f
                : 100f;

            string summary = $"[ItemOptimizer] Sync done: {_frameIndex} frames, " +
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
            ForceClose();
            LastServerSnapshot.Clear();
            LastCharacterSnapshot.Clear();
        }

        /// <summary>
        /// Unconditionally close file handles + reset state.
        /// Safe to call even when not recording.
        /// </summary>
        internal static void ForceClose()
        {
            IsRecording = false;
            FramesRemaining = 0;
            try { _entityWriter?.Flush(); } catch { }
            try { _entityWriter?.Dispose(); } catch { }
            _entityWriter = null;
            try { _charWriter?.Flush(); } catch { }
            try { _charWriter?.Dispose(); } catch { }
            _charWriter = null;
        }
    }
}
