using System;
using System.Threading;
using SHCDESE.API.Components.Network;

namespace APIShared.ModSettings
{
    // Only copying and posting run on the transport thread. Context/authority checks
    // and the original Extender receiver run on the validated Unity queue.
    internal sealed class LobbySettingsReceiveQueue
    {
        private readonly Func<Action, bool> post;
        private readonly Func<long, bool> validate;
        private readonly Action<LobbyModSettingSyncPacket, ulong?> apply;
        private readonly Action<Exception> report;
        private long generation;

        internal LobbySettingsReceiveQueue(Func<Action, bool> post, Func<long, bool> validate,
            Action<LobbyModSettingSyncPacket, ulong?> apply, Action<Exception> report)
        { this.post = post; this.validate = validate; this.apply = apply; this.report = report; }

        internal long Generation => Interlocked.Read(ref generation);
        internal void Invalidate() => Interlocked.Increment(ref generation);

        internal bool Enqueue(LobbyModSettingSyncPacket packet, ulong? sender)
        {
            if (packet == null) return false;
            long queuedGeneration = Generation;
            var copy = new LobbyModSettingSyncPacket {
                ModName = packet.ModName, PropertyName = packet.PropertyName,
                SerializedValue = packet.SerializedValue == null ? null : (byte[])packet.SerializedValue.Clone(),
                TypeName = packet.TypeName, SourcePlayerId = packet.SourcePlayerId
            };
            return post(() =>
            {
                try
                {
                    if (validate(queuedGeneration)) apply(copy, sender);
                }
                catch (Exception error) { report(error); }
            });
        }
    }
}
