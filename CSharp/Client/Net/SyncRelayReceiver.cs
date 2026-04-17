using System;
using Barotrauma;
using Barotrauma.Networking;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Client-side: receives server entity/character state snapshots for sync analysis.
    /// Protocol: server sends N entity chunks (type=0) then 1 character packet (type=1).
    /// Character packet is the "end of frame" trigger — always sent, even if empty.
    /// </summary>
    static class SyncRelayReceiver
    {
        private static bool _registered;
        private static bool _entityBuffering; // true while accumulating entity chunks
        private static int _diagCount;

        internal static void Register()
        {
            if (_registered) return;
            try
            {
                var networking = LuaCsSetup.Instance?.Networking;
                if (networking == null) return;
#if CLIENT
                networking.Receive("ItemOpt.Sync", OnReceive);
                _registered = true;
#endif
            }
            catch (Exception e)
            {
                LuaCsLogger.HandleException(e, LuaCsMessageOrigin.CSharpMod);
            }
        }

        private static void OnReceive(IReadMessage msg)
        {
            try
            {
                byte packetType = msg.ReadByte();

                if (packetType == 0) // Entity snapshot chunk
                {
                    int count = msg.ReadUInt16();
                    // First chunk of this frame clears previous data
                    if (!_entityBuffering)
                    {
                        SyncTracker.LastServerSnapshot.Clear();
                        _entityBuffering = true;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        SyncTracker.LastServerSnapshot.Add(new SyncTracker.EntitySnapshot
                        {
                            ItemId = msg.ReadUInt16(),
                            Type = msg.ReadByte(),
                            Flags = msg.ReadByte(),
                            Value = msg.ReadSingle(),
                            PosX = msg.ReadSingle(),
                            PosY = msg.ReadSingle()
                        });
                    }
                }
                else if (packetType == 1) // Character snapshot = END OF FRAME
                {
                    int count = msg.ReadUInt16();

                    if (_diagCount < 3)
                    {
                        _diagCount++;
                        LuaCsLogger.Log($"[ItemOptimizer] SyncReceiver: char packet count={count}");
                    }

                    SyncTracker.LastCharacterSnapshot.Clear();

                    for (int i = 0; i < count; i++)
                    {
                        SyncTracker.LastCharacterSnapshot.Add(new SyncTracker.CharacterSnapshot
                        {
                            CharId = msg.ReadUInt16(),
                            PosX = msg.ReadSingle(),
                            PosY = msg.ReadSingle(),
                            VelX = msg.ReadSingle(),
                            VelY = msg.ReadSingle(),
                            Flags = msg.ReadByte()
                        });
                    }

                    // Character packet = frame complete → process comparison
                    _entityBuffering = false;
                    SyncTracker.ClientTick();
                }
            }
            catch (Exception e)
            {
                LuaCsLogger.HandleException(e, LuaCsMessageOrigin.CSharpMod);
            }
        }

        internal static void Reset()
        {
            _registered = false;
            _entityBuffering = false;
            _diagCount = 0;
        }
    }
}
