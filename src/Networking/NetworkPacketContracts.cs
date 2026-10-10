using System;

namespace APIShared.Networking
{
    /// <summary>Local submission outcome. No value acknowledges peer receipt or gameplay execution.</summary>
    public enum NetworkSendStatus
    {
        /// <summary>The underlying void send API returned normally after local checks.</summary>
        Submitted,
        /// <summary>A required registration, native manager, dispatcher or recipient is unavailable.</summary>
        Unavailable,
        /// <summary>The consumer's logical activation predicate rejected this operation.</summary>
        Disabled,
        /// <summary>The payload or recipient is invalid according to the declared contract.</summary>
        Invalid,
        /// <summary>The serialized payload exceeds the channel's configured limit.</summary>
        TooLarge,
        /// <summary>Serialization or a consumer precondition threw.</summary>
        PreparationFailed,
        /// <summary>The transport threw; do not automatically retry or apply locally.</summary>
        TransportFailed
    }

    /// <summary>Immutable local result; Submitted is not an acknowledgement. Failed sends never apply gameplay locally.</summary>
    public sealed class NetworkSendResult
    {
        private readonly byte[] body;
        internal NetworkSendResult(NetworkSendStatus status, string reason, byte[] serializedBody)
        {
            Status = status;
            Reason = reason ?? string.Empty;
            body = serializedBody == null ? Array.Empty<byte>() : (byte[])serializedBody.Clone();
        }
        /// <summary>Local outcome, without an end-to-end delivery guarantee.</summary>
        public NetworkSendStatus Status { get; }
        /// <summary>Whether local preparation and the underlying send returned normally.</summary>
        public bool Submitted => Status == NetworkSendStatus.Submitted;
        /// <summary>Diagnostic reason, not localized UI text.</summary>
        public string Reason { get; }
        /// <summary>Serialized body size, excluding the two-byte Chore packet ID.</summary>
        public int BodyBytes => body.Length;
        /// <summary>Own copy of the preflight serialization, for diagnostics or consumer acknowledgements.</summary>
        public byte[] CopySerializedBody() => (byte[])body.Clone();
    }

    /// <summary>Immutable failure observation. Diagnostic callbacks run on the initialized Unity thread and are isolated.</summary>
    public sealed class NetworkPacketDiagnostic
    {
        internal NetworkPacketDiagnostic(string owner, string registration, string reason, Exception error)
        { OwnerGuid = owner; RegistrationId = registration; Reason = reason; Error = error; TimestampUtc = DateTime.UtcNow; }
        /// <summary>Consumer's stable BepInEx GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Owner-local stable registration identity.</summary>
        public string RegistrationId { get; }
        /// <summary>Reason the local send or receive was rejected.</summary>
        public string Reason { get; }
        /// <summary>Optional captured exception; exceptions never escape native receive callbacks.</summary>
        public Exception Error { get; }
        /// <summary>Capture time, including millisecond precision when formatted with .fff.</summary>
        public DateTime TimestampUtc { get; }
    }

    /// <summary>Registration-time Chore policy. APIShared snapshots options; delegate targets must remain process-owned.</summary>
    public sealed class ChoreChannelOptions<T> where T : class
    {
        /// <summary>Maximum body plus packet-ID bytes, from 2 through 1200. Oversized sends are rejected, never split.</summary>
        public int MaximumPayloadBytes { get; set; } = 1200;
        /// <summary>Optional logical enable check, evaluated on the caller for sends and synchronously for Chore receives.</summary>
        public Func<bool> IsEnabled { get; set; }
        /// <summary>Optional payload validation on send and receive. Must be deterministic on all peers in receive context.</summary>
        public Func<T, bool> Validate { get; set; }
        /// <summary>Optional Unity-thread diagnostics. Gameplay must not depend on diagnostic delivery.</summary>
        public Action<NetworkPacketDiagnostic> Diagnostic { get; set; }
    }

    /// <summary>Copied control-plane payload and transport identity. Payload player IDs never establish sender authority.</summary>
    public sealed class NetworkMessage<T> where T : class
    {
        internal NetworkMessage(T packet, ulong senderSteamId) { Packet = packet; SenderSteamId = senderSteamId; }
        /// <summary>Consumer-owned snapshot made before dispatch; mutable descendants must also be copied.</summary>
        public T Packet { get; }
        /// <summary>Verified transport sender, not a value read from the payload.</summary>
        public ulong SenderSteamId { get; }
    }

    /// <summary>Control-plane options captured at registration; never use this channel for simulation mutations.</summary>
    public sealed class MessageChannelOptions<T> where T : class
    {
        /// <summary>Optional pure deep-copy function on the receive thread. Default is a MessagePack round trip.</summary>
        public Func<T, T> Snapshot { get; set; }
        /// <summary>Optional logical enable check on the Unity thread for receive, and on the caller for send.</summary>
        public Func<bool> IsEnabled { get; set; }
        /// <summary>Optional receive check on the Unity thread. Recheck session/generation, roster and authority here.</summary>
        public Func<NetworkMessage<T>, bool> Validate { get; set; }
        /// <summary>Optional Unity-thread diagnostics; independent of gameplay.</summary>
        public Action<NetworkPacketDiagnostic> Diagnostic { get; set; }
        /// <summary>Maximum serialized body size. Default has no additional limit; the Extender owns fragmentation.</summary>
        public int MaximumBodyBytes { get; set; } = int.MaxValue;
    }
}
