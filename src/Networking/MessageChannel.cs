using System;
using System.Runtime.InteropServices;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using Steamworks;

namespace APIShared.Networking
{
    /// <summary>Process-rooted control-plane channel. Receive processing is FIFO on Unity; sends require the Unity thread.</summary>
    /// <remarks>
    /// Game broadcast excludes the sender. Lobby broadcast explicitly selects the active lobby even if gameMembers
    /// is already populated. Steam-peer sending uses reliable channel-2 transport directly, without choosing a
    /// game player from a stale list. No method supplies tick synchronization or all-peer acknowledgements.
    /// Retain session IDs/epochs in your existing payload and recheck them in the receive validator.
    /// </remarks>
    public sealed class MessageChannel<T> where T : class
    {
        private readonly R3PacketEventHook<T> hook;
        private readonly IDisposable subscription;
        private readonly Func<bool> enabled;
        private readonly int limit;
        private readonly Action<string, Exception> report;

        internal MessageChannel(string owner, string id, Action<NetworkMessage<T>> receive, MessageChannelOptions<T> options)
        {
            enabled = options.IsEnabled;
            limit = options.MaximumBodyBytes;
            report = PacketDiagnostics.Create(owner, id, options.Diagnostic);
            Func<T, T> snapshot = options.Snapshot ?? (packet => GameNetworkAPI.Deserialize<T>(GameNetworkAPI.Serialize(packet)));
            Func<NetworkMessage<T>, bool> validate = options.Validate;
            var receiver = new PacketReceivePolicy<T>(enabled, report);
            hook = GameNetworkAPI.Instance.GetPacketEventFor<T>();
            subscription = hook.GetBaseHook().Observable.Subscribe(args =>
            {
                if (args?.Phase != EventHookPhase.Post) return;
                ulong? sender = args.SenderSteamId.HasValue ? args.SenderSteamId.Value.m_SteamID : (ulong?)null;
                receiver.Message(args.Packet, sender, snapshot, Internal.UnityMainThreadDispatch.TryEnqueue, validate, receive);
            });
        }

        /// <summary>Extender-assigned ID; matching registration order and formatters are prerequisites on all peers.</summary>
        public short PacketId => hook.GetPacketId();

        /// <summary>Sends to the current game's other peers. instantMessage bypasses the managed outgoing queue, not simulation timing.</summary>
        public NetworkSendResult SendToGame(T packet, bool instantMessage = false) =>
            Send(packet, () => GameNetworkAPI.IsNetworkedEnvironment(),
                envelope => GameNetworkAPI.SendPacketToAllEx(envelope, instantMessage));

        /// <summary>Sends to one current game player (1..8), without selecting a lobby recipient or applying locally.</summary>
        public NetworkSendResult SendToPlayer(T packet, int playerId)
        {
            if (playerId < 1 || playerId > 8) return ChoreSendPolicy.Result(NetworkSendStatus.Invalid, "Player ID must be 1..8.");
            return Send(packet, () => GameNetworkAPI.IsNetworkedEnvironment(),
                envelope => GameNetworkAPI.SendPacketToPlayerIdEx(playerId, envelope));
        }

        /// <summary>Sends reliably to one Steam peer directly. The consumer must authorize and validate the target/session.</summary>
        public NetworkSendResult SendToSteamPeer(T packet, ulong steamId)
        {
            if (steamId == 0) return ChoreSendPolicy.Result(NetworkSendStatus.Invalid, "Steam ID must be nonzero.");
            return Send(packet, () => true, envelope => SendReliable(new CSteamID(steamId), envelope.ToBytes()));
        }

        /// <summary>Broadcasts reliably to active-lobby human peers, excluding self, AI and pending-kick entries.</summary>
        public NetworkSendResult SendToLobby(T packet) =>
            Send(packet, () => Platform_Multiplayer.Instance?.activeLobby?.members != null, envelope =>
            {
                byte[] bytes = envelope.ToBytes();
                foreach (Platform_Multiplayer.MPLobbyMember member in Platform_Multiplayer.Instance.activeLobby.members)
                {
                    if (member == null || member.IsSelf() || member.SkirmishMember || member.dummyToBeKicked) continue;
                    SendReliable(member.id, bytes);
                }
            });

        private NetworkSendResult Send(T packet, Func<bool> available, Action<Platform_Multiplayer.MPData> send)
        {
            if (!Internal.UnityMainThreadDispatch.IsMainThread)
                return ChoreSendPolicy.Result(NetworkSendStatus.Unavailable, "Control-plane sends require the initialized Unity main thread.");
            NetworkSendResult result;
            byte[] body = null;
            try
            {
                if (enabled != null && !enabled()) return ChoreSendPolicy.Result(NetworkSendStatus.Disabled, "Channel is disabled.");
                if (packet == null) return ChoreSendPolicy.Result(NetworkSendStatus.Invalid, "Packet is null.");
                if (!available()) return ChoreSendPolicy.Result(NetworkSendStatus.Unavailable, "Recipient context is unavailable.");
                body = GameNetworkAPI.Serialize(packet);
                if (body == null) return ChoreSendPolicy.Result(NetworkSendStatus.PreparationFailed, "Serializer returned null.");
                if (body.Length > limit) return ChoreSendPolicy.Result(NetworkSendStatus.TooLarge, "Message body exceeds configured limit.", body);
            }
            catch (Exception error) { return ChoreSendPolicy.Result(NetworkSendStatus.PreparationFailed, error.Message, body); }
            try
            {
                send(new Platform_Multiplayer.MPData { packetType = PacketId, dataLength = body.Length, dataOffset = 0, data = body });
                result = ChoreSendPolicy.Result(NetworkSendStatus.Submitted, string.Empty, body);
            }
            catch (Exception error)
            {
                result = ChoreSendPolicy.Result(NetworkSendStatus.TransportFailed, error.Message, body);
                report(result.Reason, error);
            }
            return result;
        }

        private static void SendReliable(CSteamID target, byte[] bytes)
        {
            // Extender's internal enum: Reliable=8, AutoRestartBrokenSession=32.
            // Verify-NetworkContracts validates these values against the installed assembly.
            const int reliableFlags = 8 | 32;
            SteamNetworkingIdentity identity = default(SteamNetworkingIdentity);
            identity.SetSteamID(target);
            GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                EResult result = SteamNetworkingMessages.SendMessageToUser(ref identity, pinned.AddrOfPinnedObject(),
                    (uint)bytes.Length, reliableFlags, 2);
                if (result != EResult.k_EResultOK)
                    throw new InvalidOperationException("Reliable send to " + target.m_SteamID + " failed: " + result);
            }
            finally { pinned.Free(); }
        }
    }
}
