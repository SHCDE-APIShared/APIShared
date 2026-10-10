using System;
using System.Collections.Generic;
using MessagePack;

namespace APIShared.Networking
{
    /// <summary>Owner-bound packet registration. Obtain through ApiShared.ForMod(yourGuid).Network.</summary>
    /// <remarks>
    /// Register once, on the Unity thread after the Extender's built-in LibraryLoaded registration,
    /// unconditionally in the same order on every peer. Packet IDs are Extender-assigned by registration
    /// order, not GUID-derived. All peers need compatible packet types, explicit MessagePack formatters
    /// and payload semantics. APIShared does not negotiate missing mods or change the wire format.
    /// Channels and subscriptions are rooted until process exit; toggle features with IsEnabled.
    /// </remarks>
    public sealed class ModNetworkClient
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<Tuple<string, string>, object> Registrations = new Dictionary<Tuple<string, string>, object>();
        private static readonly HashSet<Tuple<Type, string>> PacketKinds = new HashSet<Tuple<Type, string>>();
        internal ModNetworkClient(string ownerGuid) { OwnerGuid = ownerGuid; }
        /// <summary>The caller's stable plugin GUID; arbitrary foreign GUIDs are supported.</summary>
        public string OwnerGuid { get; }

        /// <summary>Posts copied presentation work from a Chore/native callback to Unity without blocking or an inline fallback.</summary>
        /// <remarks>Capture immutable values first; never post simulation mutation or live native pointers. False means no dispatcher is available. Posted failures are isolated.</remarks>
        public bool TryPostPresentation(Action presentation) => Internal.UnityMainThreadDispatch.TryEnqueue(presentation);

        /// <summary>Registers synchronous gameplay execution through the existing Extender Chore pipeline.</summary>
        /// <remarks>
        /// Execute and Validate run synchronously in the simulation context, including on the sender.
        /// Never dispatch gameplay to Unity or read local selection there. Resolve stable IDs and desired
        /// states from the packet. A missing Steam sender classifies this dedicated packet type as Chore;
        /// it is not authenticated player identity. The consumer still owns authorization and determinism.
        /// No replay, acknowledgement, rollback, automatic deduplication or retry is performed.
        /// </remarks>
        public ChoreChannel<T> RegisterChore<T>(string registrationId, Action<T> execute,
            ChoreChannelOptions<T> options = null) where T : class
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            options = options ?? new ChoreChannelOptions<T>();
            if (options.MaximumPayloadBytes < 2 || options.MaximumPayloadBytes > ChoreTransport.MaximumPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(options.MaximumPayloadBytes));
            return Register(registrationId, "chore", () => new ChoreChannel<T>(OwnerGuid, registrationId, execute, options));
        }

        /// <summary>Registers copied, sender-aware control-plane messages with FIFO Unity-thread processing.</summary>
        /// <remarks>
        /// Snapshot is the only consumer callback on the raw receive thread and must only deep-copy the
        /// supplied payload. Default snapshotting uses the packet's explicit MessagePack formatter.
        /// IsEnabled, Validate and receive run after dispatch; revalidate session/generation, sender authority
        /// and roster then. Packets without a verified Steam sender are rejected. No inline or foreign-thread
        /// fallback is used when the Unity dispatcher is absent. Gameplay mutations belong in RegisterChore.
        /// </remarks>
        public MessageChannel<T> RegisterMessage<T>(string registrationId, Action<NetworkMessage<T>> receive,
            MessageChannelOptions<T> options = null) where T : class
        {
            if (receive == null) throw new ArgumentNullException(nameof(receive));
            options = options ?? new MessageChannelOptions<T>();
            if (options.MaximumBodyBytes < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumBodyBytes));
            return Register(registrationId, "message", () => new MessageChannel<T>(OwnerGuid, registrationId, receive, options));
        }

        private TChannel Register<TChannel>(string id, string kind, Func<TChannel> create)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A stable registration ID is required.", nameof(id));
            if (!Internal.UnityMainThreadDispatch.IsMainThread)
                throw new InvalidOperationException("Network registration requires the initialized Unity main thread.");
            Type packetType = typeof(TChannel).GenericTypeArguments[0];
            if (!Attribute.IsDefined(packetType, typeof(MessagePackFormatterAttribute)))
                throw new ArgumentException("Packet types require an explicit [MessagePackFormatter] to avoid unsupported runtime-generated serializers.");
            var key = Tuple.Create(OwnerGuid, id);
            var packetKind = Tuple.Create(packetType, kind);
            lock (Gate)
            {
                if (Registrations.ContainsKey(key)) throw new InvalidOperationException("Network registration already exists: " + OwnerGuid + "/" + id);
                // Two independent simulation handlers for one packet type would apply it twice.
                // A deliberate mixed protocol may register one Chore and one message channel for that type.
                if (PacketKinds.Contains(packetKind)) throw new InvalidOperationException("Packet type already has a " + kind + " channel.");
                TChannel channel = create();
                Registrations.Add(key, channel);
                PacketKinds.Add(packetKind);
                return channel;
            }
        }
    }
}
