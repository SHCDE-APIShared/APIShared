using System;
using SHCDESE.API;
using SHCDESE.GameGlobals;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Network;

namespace APIShared.Networking
{
    /// <summary>Common local Chore preflight for existing registered packet types; new consumers should register a ChoreChannel.</summary>
    /// <remarks>
    /// Uses only the public Extender API and retains its wire format. Call from a known game/UI entry point.
    /// Register compatible types and explicit MessagePack formatters unconditionally in the same order on all peers.
    /// The packet, its descendants and serializer output must remain unchanged between preflight and send;
    /// the Extender serializes it again. Missing manager or oversized body is rejected before calling the Extender.
    /// This is not an atomic native queue acknowledgement: the Extender returns void and retains its own fallback.
    /// Never apply locally, automatically retry, or interpret success as receipt by every peer.
    /// </remarks>
    public static class ChoreTransport
    {
        /// <summary>Extender cap including its two-byte packet ID, excluding the native record framing.</summary>
        public const int MaximumPayloadBytes = 1200;

        /// <summary>Classifies a dedicated Chore packet receive by phase and absence of a Steam sender; this is not authenticated player identity.</summary>
        /// <remarks>Use only for packet types whose protocol expressly reserves this path for Chores. Never classify an arbitrary unknown-origin message as synchronized gameplay.</remarks>
        public static bool IsChoreDelivery<T>(ReceiveCustomPacketEventArgs<T> args) where T : class =>
            args != null && args.Phase == EventHookPhase.Post && !args.SenderSteamId.HasValue;

        /// <summary>Checks registration and native Chore-manager availability without promising later submission.</summary>
        public static bool IsAvailable(bool packetHookRegistered)
        {
            if (!packetHookRegistered) return false;
            try { return GameGlobalsManager.Instance.ChoreManagerVA != 0; }
            catch { return false; }
        }

        /// <summary>Preflights and submits an unchanged packet. Only Submitted means the local void send returned normally.</summary>
        public static NetworkSendResult Send<T>(T packet, short packetId, bool packetHookRegistered,
            int maximumPayloadBytes = MaximumPayloadBytes) where T : class =>
            ChoreSendPolicy.Send(packet, packetId, packetHookRegistered,
                value => GameNetworkAPI.Serialize(value), () => GameGlobalsManager.Instance.ChoreManagerVA,
                (value, id) => GameNetworkAPI.SendPacketToAllEx2(value, id, viaChore: true), maximumPayloadBytes);

        /// <summary>Convenience form for existing consumers needing the serialized preflight body and rejection reason.</summary>
        public static bool TrySend<T>(T packet, short packetId, bool packetHookRegistered,
            out byte[] body, out string rejectionReason, int maximumPayloadBytes = MaximumPayloadBytes) where T : class
        {
            NetworkSendResult result = Send(packet, packetId, packetHookRegistered, maximumPayloadBytes);
            body = result.CopySerializedBody();
            rejectionReason = result.Submitted ? null : result.Reason;
            return result.Submitted;
        }
    }
}
