using System;
using System.Collections.Generic;
using Barotrauma;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Server-side: periodically captures entity state + position snapshots
    /// and character positions, broadcasts to clients for desync analysis.
    /// Activated by "iosync" console command — not always running.
    /// </summary>
    static class SyncRelaySender
    {
        private static float _sendAccum;
        private const float SendInterval = 0.1f; // 10Hz sampling
        internal static bool Active;
        internal static int FramesRemaining;

        /// <summary>Called from MetricRelaySender.OnPostUpdate when sync tracking is active.</summary>
        internal static void OnTick(float dt)
        {
            if (!Active) return;

            _sendAccum += dt;
            if (_sendAccum < SendInterval) return;
            _sendAccum = 0;

            var entities = SyncTracker.ServerBuildEntitySnapshot();
            var characters = SyncTracker.ServerBuildCharacterSnapshot();
            BroadcastSnapshots(entities, characters);

            FramesRemaining--;
            if (FramesRemaining <= 0)
            {
                Active = false;
                LuaCsLogger.Log("[ItemOptimizer] SyncRelaySender: recording finished");
            }
        }

        private static void BroadcastSnapshots(
            List<SyncTracker.EntitySnapshot> entities,
            List<SyncTracker.CharacterSnapshot> characters)
        {
            try
            {
                var networking = LuaCsSetup.Instance?.Networking;
                if (networking == null) return;

                // ── Entity snapshots (chunked) ──
                const int entityChunk = 150; // 8 bytes per entity → ~1200B per chunk
                int offset = 0;
                while (offset < entities.Count)
                {
                    int count = Math.Min(entityChunk, entities.Count - offset);
                    var msg = networking.Start("ItemOpt.Sync");
                    msg.WriteByte(0); // packet type = entity
                    msg.WriteUInt16((ushort)count);

                    for (int i = offset; i < offset + count; i++)
                    {
                        var ss = entities[i];
                        msg.WriteUInt16(ss.ItemId);
                        msg.WriteByte(ss.Type);
                        msg.WriteByte(ss.Flags);
                        msg.WriteSingle(ss.Value);
                        msg.WriteSingle(ss.PosX);
                        msg.WriteSingle(ss.PosY);
                    }

                    networking.SendToClient(msg);
                    offset += count;
                }

                // ── Character snapshots (ALWAYS sent — acts as end-of-frame marker) ──
                {
                    var cmsg = networking.Start("ItemOpt.Sync");
                    cmsg.WriteByte(1); // packet type = character
                    int charCount = Math.Min(characters.Count, 200);
                    cmsg.WriteUInt16((ushort)charCount);

                    for (int i = 0; i < charCount; i++)
                    {
                        var cs = characters[i];
                        cmsg.WriteUInt16(cs.CharId);
                        cmsg.WriteSingle(cs.PosX);
                        cmsg.WriteSingle(cs.PosY);
                        cmsg.WriteSingle(cs.VelX);
                        cmsg.WriteSingle(cs.VelY);
                        cmsg.WriteByte(cs.Flags);
                    }

                    networking.SendToClient(cmsg);
                }
            }
            catch (Exception e)
            {
                SafeLogger.HandleException(e);
            }
        }

        internal static void Start(int frames)
        {
            FramesRemaining = frames;
            _sendAccum = 0;
            Active = true;
            LuaCsLogger.Log($"[ItemOptimizer] SyncRelaySender: started ({frames} samples at 10Hz)");
        }

        internal static void Reset()
        {
            Active = false;
            FramesRemaining = 0;
            _sendAccum = 0;
        }
    }
}
