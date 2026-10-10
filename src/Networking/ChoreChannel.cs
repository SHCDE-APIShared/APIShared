using System;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;

namespace APIShared.Networking
{
    /// <summary>Process-rooted channel for one compatible gameplay packet type; it sends and receives without changing its wire schema.</summary>
    public sealed class ChoreChannel<T> where T : class
    {
        private readonly R3PacketEventHook<T> hook;
        private readonly IDisposable subscription;
        private readonly Func<bool> enabled;
        private readonly Func<T, bool> validate;
        private readonly int limit;
        private readonly Action<string, Exception> report;

        internal ChoreChannel(string owner, string id, Action<T> execute, ChoreChannelOptions<T> options)
        {
            enabled = options.IsEnabled;
            validate = options.Validate;
            limit = options.MaximumPayloadBytes;
            report = PacketDiagnostics.Create(owner, id, options.Diagnostic);
            var receiver = new PacketReceivePolicy<T>(enabled, report);
            hook = GameNetworkAPI.Instance.GetPacketEventFor<T>();
            subscription = hook.GetBaseHook().Observable.Subscribe(args =>
            {
                if (args?.Phase != EventHookPhase.Post) return;
                ulong? sender = args.SenderSteamId.HasValue ? args.SenderSteamId.Value.m_SteamID : (ulong?)null;
                receiver.Chore(args.Packet, sender, validate, execute);
            });
        }
        /// <summary>Extender-assigned ID. This is stable only when all peers have matching registration order.</summary>
        public short PacketId => hook.GetPacketId();
        /// <summary>Current technical transport preconditions, excluding consumer activation and payload validation.</summary>
        public bool IsAvailable => ChoreTransport.IsAvailable(hook != null);
        /// <summary>Validates and submits without applying locally. Keep packet and nested values unchanged through this call.</summary>
        public NetworkSendResult Send(T packet)
        {
            NetworkSendResult result;
            try
            {
                if (enabled != null && !enabled()) result = ChoreSendPolicy.Result(NetworkSendStatus.Disabled, "Channel is disabled.");
                else if (packet == null || (validate != null && !validate(packet))) result = ChoreSendPolicy.Result(NetworkSendStatus.Invalid, "Payload validation rejected the packet.");
                else result = ChoreTransport.Send(packet, PacketId, hook != null, limit);
            }
            catch (Exception error) { result = ChoreSendPolicy.Result(NetworkSendStatus.PreparationFailed, error.Message); }
            if (!result.Submitted) report(result.Reason, null);
            return result;
        }
    }
}
