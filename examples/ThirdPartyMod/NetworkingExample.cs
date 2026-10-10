using System;
using APIShared;
using APIShared.Networking;
using MessagePack;
using MessagePack.Formatters;

namespace ThirdPartyMod
{
    // Call Register unconditionally from your existing Unity-thread LibraryLoaded
    // handler, after the Extender registered its built-in packets. Keep the returned
    // channel in your static runtime. It never depends on a plugin Update/OnDestroy.
    internal static class NetworkingExample
    {
        internal static ChoreChannel<BuildingModePacket> Register(ModApiClient api,
            Func<BuildingModePacket, bool> validateOwnedBuilding,
            Action<BuildingModePacket> applyDesiredState,
            Func<bool> featureEnabled,
            Action<string> log)
        {
            return api.Network.RegisterChore("building-mode", applyDesiredState,
                new ChoreChannelOptions<BuildingModePacket>
                {
                    MaximumPayloadBytes = 1200,
                    IsEnabled = featureEnabled,
                    // Check active session, protocol, current ownership and global-ID
                    // resolution on every peer. Local selection is never consulted here.
                    Validate = packet => packet.Protocol == 1 && packet.BuildingGlobalId > 0 &&
                        validateOwnedBuilding(packet),
                    Diagnostic = failure => log(failure.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                        " UTC " + failure.RegistrationId + ": " + failure.Reason)
                });
        }

        internal static bool OnButton(ChoreChannel<BuildingModePacket> channel,
            int playerId, int globalId, bool desiredEnabled, out string reason)
        {
            var packet = new BuildingModePacket
            { Protocol = 1, PlayerId = playerId, BuildingGlobalId = globalId, DesiredEnabled = desiredEnabled };
            NetworkSendResult result = channel.Send(packet);
            reason = result.Reason;
            // Do not applyDesiredState here: the receiver also runs on the sender.
            // Submitted is local submission, not an acknowledgement from every peer.
            return result.Submitted;
        }

        // A normal message uses a different channel. Supply a pure deep-copy callback
        // or use the default MessagePack round trip. The validator and receiver run
        // on Unity after dispatch, so session/generation/authority are checked there.
        internal static MessageChannel<BuildingModePacket> RegisterStatus(ModApiClient api,
            Func<NetworkMessage<BuildingModePacket>, bool> isCurrentHostMessage,
            Action<NetworkMessage<BuildingModePacket>> updatePresentation)
        {
            return api.Network.RegisterMessage("building-status", updatePresentation,
                new MessageChannelOptions<BuildingModePacket>
                {
                    Validate = isCurrentHostMessage,
                    Snapshot = p => new BuildingModePacket
                    { Protocol = p.Protocol, PlayerId = p.PlayerId, BuildingGlobalId = p.BuildingGlobalId, DesiredEnabled = p.DesiredEnabled }
                });
        }
    }

    [MessagePackObject]
    [MessagePackFormatter(typeof(BuildingModeFormatter))]
    public sealed class BuildingModePacket
    {
        [Key(0)] public int Protocol;
        [Key(1)] public int PlayerId;
        [Key(2)] public int BuildingGlobalId;
        [Key(3)] public bool DesiredEnabled;
    }

    public sealed class BuildingModeFormatter : IMessagePackFormatter<BuildingModePacket>
    {
        public void Serialize(ref MessagePackWriter writer, BuildingModePacket p, MessagePackSerializerOptions options)
        {
            if (p == null) { writer.WriteNil(); return; }
            writer.WriteArrayHeader(4);
            writer.Write(p.Protocol); writer.Write(p.PlayerId);
            writer.Write(p.BuildingGlobalId); writer.Write(p.DesiredEnabled);
        }
        public BuildingModePacket Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil()) return null;
            int count = reader.ReadArrayHeader();
            var p = new BuildingModePacket();
            for (int index = 0; index < count; index++)
            {
                switch (index)
                {
                    case 0: p.Protocol = reader.ReadInt32(); break;
                    case 1: p.PlayerId = reader.ReadInt32(); break;
                    case 2: p.BuildingGlobalId = reader.ReadInt32(); break;
                    case 3: p.DesiredEnabled = reader.ReadBoolean(); break;
                    default: reader.Skip(); break;
                }
            }
            return p;
        }
    }
}
