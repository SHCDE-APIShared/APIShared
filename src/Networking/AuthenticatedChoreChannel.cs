using System;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Network;

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
        public int MaximumPayloadBytes { get; set; } = 1200;
        /// <summary>Logical activation, checked on send and after verified native context on receive; receive results must agree on every peer. Rejected Steam/worker deliveries do not invoke this predicate.</summary>
        public Func<bool> IsEnabled { get; set; }
        /// <summary>Optional payload-only validation on send and receive. Do not mutate packets or simulation.</summary>
        public Func<T, bool> ValidatePayload { get; set; }
        /// <summary>Optional synchronous authorization after the native issuer has been verified; must be deterministic on every peer and must not mutate packets or simulation.</summary>
        public Func<AuthenticatedChoreMessage<T>, bool> ValidateReceive { get; set; }
        /// <summary>Optional isolated Unity-thread diagnostics; never simulation mutation.</summary>
        public Action<NetworkPacketDiagnostic> Diagnostic { get; set; }
    }

    /// <summary>Additive sender-aware Chore channel. Does not alter existing ChoreChannel delivery contracts.</summary>
    /// <remarks>Register on Unity after Extender readiness, in matching order on all peers. Delivery requires a native-body-bound dispatcher scope for this exact packet ID and an unchanged native issuer/slot/command/tick before and after validation. Missing/unsupported context and Steam fallback deliveries are rejected. Callbacks run synchronously in the native simulation context, including on the sender. Sender verification is not a grant; recheck the session, authority and object ownership.</remarks>
    public sealed class AuthenticatedChoreChannel<T> where T : class
    {
        private static readonly CaptureAuthenticatedChore CaptureNative = NativeChoreDispatch.TryCapture;
        private readonly R3PacketEventHook<T> hook;
        private readonly IDisposable subscription;
        private readonly Func<bool> enabled;
        private readonly Func<T, bool> validate;
        private readonly int limit;
        private readonly Action<string, Exception> report;

        internal AuthenticatedChoreChannel(string owner, string id, Action<AuthenticatedChoreMessage<T>> execute,
            AuthenticatedChoreChannelOptions<T> options)
        {
            enabled = options.IsEnabled; validate = options.ValidatePayload; limit = options.MaximumPayloadBytes;
            var authorize = options.ValidateReceive;
            report = PacketDiagnostics.Create(owner, id, options.Diagnostic);
            hook = GameNetworkAPI.Instance.GetPacketEventFor<T>();
            subscription = hook.GetBaseHook().Observable.Subscribe(args => AuthenticatedChoreDelivery<T>.Receive(
                args, hook.GetPacketId(), CaptureNative, enabled, validate, authorize, execute, report));
            NativeChoreDispatch.AddDemand(hook.GetPacketId());
        }
        /// <summary>Extender packet ID; matching peer registration order is required.</summary>
        public short PacketId => hook.GetPacketId();
        /// <summary>Unity-thread technical transport and hash-bound native-context availability, not peer agreement or permission. False outside the initialized Unity thread without querying game state.</summary>
        public bool IsAvailable => Internal.UnityMainThreadDispatch.IsMainThread && NativeChoreContext.IsReady &&
            NativeChoreDispatch.IsReady && NativeChoreTransport.IsReady && ChoreTransport.IsAvailable(hook != null);
        /// <summary>Serializes once and submits on Unity through native Chore only; no Steam fallback, local simulation effect or execution acknowledgement.</summary>
        /// <remarks>Requires a real local player ID matching the native enqueue view under the engine monitor. A foreign report viewpoint cannot impersonate that owner; submissions during it are rejected.</remarks>
        public NetworkSendResult Send(T packet)
        {
            try
            {
                // Reject before activation/validation/serialization. These may
                // read game state; the transport's later thread guard is too late.
                if (!Internal.UnityMainThreadDispatch.IsMainThread)
                    return ChoreSendPolicy.Result(NetworkSendStatus.Unavailable, "Authenticated Chore submission requires the initialized Unity thread.");
                if (!IsAvailable) return ChoreSendPolicy.Result(NetworkSendStatus.Unavailable, "Native Chore context unavailable.");
                if (enabled != null && !enabled()) return ChoreSendPolicy.Result(NetworkSendStatus.Disabled, "Channel disabled.");
                if (packet == null || validate != null && !validate(packet)) return ChoreSendPolicy.Result(NetworkSendStatus.Invalid, "Payload rejected.");
                return StrictChoreSendPolicy.Send(packet, PacketId, IsAvailable, GameNetworkAPI.Serialize,
                    NativeChoreTransport.TryQueue, limit);
            }
            catch (Exception error)
            { report("Authenticated Chore preparation failed.", error); return ChoreSendPolicy.Result(NetworkSendStatus.PreparationFailed, error.Message); }
        }
    }

    internal delegate bool CaptureAuthenticatedChore(short expectedPacketId, out NativeChoreExecution native);

    // The shared receive path is directly fixture-tested with the real SE event
    // args. No consumer predicate may run until native-only delivery is proven:
    // a Steam/worker event is not entitled to call an IsEnabled that reads game
    // state merely because the same packet type also has a Chore receiver.
    internal static class AuthenticatedChoreDelivery<T> where T : class
    {
        internal static void Receive(ReceiveCustomPacketEventArgs<T> args, short expectedPacketId,
            CaptureAuthenticatedChore capture, Func<bool> enabled, Func<T, bool> validate,
            Func<AuthenticatedChoreMessage<T>, bool> authorize, Action<AuthenticatedChoreMessage<T>> execute,
            Action<string, Exception> report)
        {
            if (args?.Phase != EventHookPhase.Post || args.Packet == null) return;
            try
            {
                if (args.SenderSteamId.HasValue || args.PacketId != expectedPacketId ||
                    !capture(expectedPacketId, out var native))
                { report("Native Chore issuer unavailable; delivery rejected.", null); return; }
                if (enabled != null && !enabled() || validate != null && !validate(args.Packet)) return;
                var message = new AuthenticatedChoreMessage<T>(args.Packet, native);
                if (authorize != null && !authorize(message)) return;
                if (!capture(expectedPacketId, out var current) || !native.Matches(current))
                { report("Native Chore context changed during validation; delivery rejected.", null); return; }
                execute(message);
            }
            catch (Exception error) { report("Authenticated Chore validation or execution failed.", error); }
        }
    }
}
