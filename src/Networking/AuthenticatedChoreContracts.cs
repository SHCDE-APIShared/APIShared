using System;

namespace APIShared.Networking
{
    /// <summary>One synchronous native Chore delivery with the resolved issuer from the active native pending slot.</summary>
    /// <remarks>The issuer is not read from the packet or the selected viewpoint. Retain no native pointers. This identifies a Vanilla transport participant, not permission to control another player's objects.</remarks>
    public sealed class AuthenticatedChoreMessage<T> where T : class
    {
        internal AuthenticatedChoreMessage(T packet, NativeChoreExecution native)
        { Packet = packet; SenderPlayerId = native.Sender; CommandId = native.CommandId; ScheduledTick = native.ScheduledTick; }
        /// <summary>Deserialized packet. Receivers must not mutate it.</summary>
        public T Packet { get; }
        /// <summary>Transport participant, from 1 through 8, matching the native pending slot and execution context. Managed player remapping has already occurred.</summary>
        public int SenderPlayerId { get; }
        /// <summary>Native command identity; its scope is the current native game session.</summary>
        public int CommandId { get; }
        /// <summary>Final native pending-slot tick, not a local engine-run counter.</summary>
        public int ScheduledTick { get; }
    }

    /// <summary>Options captured once for an authenticated Chore channel.</summary>
    public sealed class AuthenticatedChoreChannelOptions<T> where T : class
    {
        /// <summary>Maximum submitted body plus packet-ID bytes, 2 through 1200. Receive schemas must independently bound their fields.</summary>
        public int MaximumPayloadBytes { get; set; } = ChoreSendPolicy.MaximumPayloadBytes;
        /// <summary>Logical activation, checked on send and after verified native context on receive; receive results must agree on every peer. Rejected Steam/worker deliveries do not invoke this predicate.</summary>
        public Func<bool> IsEnabled { get; set; }
        /// <summary>Optional payload-only validation on send and receive. Do not mutate packets or simulation.</summary>
        public Func<T, bool> ValidatePayload { get; set; }
        /// <summary>Optional synchronous authorization after the native issuer has been verified; must be deterministic on every peer and must not mutate packets or simulation.</summary>
        public Func<AuthenticatedChoreMessage<T>, bool> ValidateReceive { get; set; }
        /// <summary>Optional isolated Unity-thread diagnostics; never simulation mutation.</summary>
        public Action<NetworkPacketDiagnostic> Diagnostic { get; set; }
    }
}
